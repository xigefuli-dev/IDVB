public:
    explicit OverlayGame(const std::filesystem::path& assetDirectory,
                         std::wstring pipeName = {})
        : module_(reinterpret_cast<HINSTANCE>(GetModuleHandleW(nullptr))),
          assetDirectory_(assetDirectory), pipeName_(std::move(pipeName)) {
        if (module_ == nullptr) {
            throw std::runtime_error("GetModuleHandleW failed");
        }
        const HRESULT factoryResult = CoCreateInstance(
            CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(imagingFactory_.put()));
        if (FAILED(factoryResult)) {
            throw std::runtime_error("Unable to create the Windows Imaging Component factory");
        }

        floors_.reserve(4);
        for (int floor = 1; floor <= 4; ++floor) {
            FloorSet set;
            set.label = std::to_wstring(floor) + L"F";
            const std::wstring prefix = std::to_wstring(floor) + L"f";
            std::size_t floorBytes = 0;
            for (const std::filesystem::path& path : DiscoverFloorImages(assetDirectory_, prefix)) {
                if (set.images.size() >= kMaxImagesPerFloor) {
                    break;
                }
                Bitmap image;
                if (Bitmap::Load(imagingFactory_.get(), path, image) &&
                    image.pixels.size() <= kMaxFloorBytes - std::min(floorBytes, kMaxFloorBytes)) {
                    floorBytes += image.pixels.size();
                    set.images.push_back(std::move(image));
                }
            }
            floors_.push_back(std::move(set));
        }

        RegisterWindowClass();
        CreateMainWindow();
        StartPipeServer();
    }

    ~OverlayGame() {
        StopPipeServer();
        if (window_ != nullptr) {
            KillTimer(window_, kRapidTimer);
            DestroyWindow(window_);
        }
        if (classRegistered_) {
            UnregisterClassW(kWindowClass, module_);
        }
    }

    OverlayGame(const OverlayGame&) = delete;
    OverlayGame& operator=(const OverlayGame&) = delete;

    int Run() {
        MSG message{};
        while (true) {
            const BOOL result = GetMessageW(&message, nullptr, 0, 0);
            if (result <= 0) {
                return result == 0 ? static_cast<int>(message.wParam) : 1;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

private:
    void RegisterWindowClass() {
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.hInstance = module_;
        windowClass.lpfnWndProc = &OverlayGame::WindowProc;
        windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        windowClass.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        windowClass.hbrBackground = nullptr;
        windowClass.lpszClassName = kWindowClass;
        if (RegisterClassExW(&windowClass) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            throw std::runtime_error("RegisterClassExW failed");
        }
        classRegistered_ = true;
    }

    void CreateMainWindow() {
        MONITORINFO monitorInfo{sizeof(monitorInfo)};
        HMONITOR monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == nullptr || !GetMonitorInfoW(monitor, &monitorInfo)) {
            throw std::runtime_error("GetMonitorInfoW failed");
        }

        const RECT& bounds = monitorInfo.rcMonitor;
        const LONG monitorWidth = bounds.right - bounds.left;
        const LONG monitorHeight = bounds.bottom - bounds.top;
        LONG clientWidth = monitorWidth;
        LONG clientHeight = monitorHeight;
        for (const FloorSet& floor : floors_) {
            if (!floor.images.empty() && floor.images.front().valid()) {
                clientWidth = std::min<LONG>(
                    monitorWidth,
                    static_cast<LONG>(floor.images.front().width));
                clientHeight = std::min<LONG>(
                    monitorHeight,
                    static_cast<LONG>(floor.images.front().height));
                break;
            }
        }
        const LONG left = bounds.left + ((monitorWidth - clientWidth) / 2);
        const LONG top = bounds.top + ((monitorHeight - clientHeight) / 2);
        window_ = CreateWindowExW(
            WS_EX_APPWINDOW, kWindowClass, kWindowTitle, WS_POPUP | WS_CLIPCHILDREN,
            left, top, clientWidth, clientHeight,
            nullptr, nullptr, module_, this);
        if (window_ == nullptr) {
            throw std::runtime_error("CreateWindowExW failed");
        }
        ShowWindow(window_, SW_SHOW);
        UpdateWindow(window_);
        SetForegroundWindow(window_);
        SetFocus(window_);
    }

    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
        OverlayGame* self = reinterpret_cast<OverlayGame*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const CREATESTRUCTW* create = reinterpret_cast<const CREATESTRUCTW*>(lParam);
            self = static_cast<OverlayGame*>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            self->window_ = window;
        }
        if (self == nullptr) {
            return DefWindowProcW(window, message, wParam, lParam);
        }

        try {
            return self->HandleMessage(message, wParam, lParam);
        } catch (...) {
            self->message_ = L"An unexpected error occurred while handling the last action.";
            InvalidateRect(window, nullptr, FALSE);
            return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    LRESULT HandleMessage(UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case kPipeCommandMessage:
            return HandlePipeRequest(reinterpret_cast<PipeRequest*>(lParam));

        case WM_ERASEBKGND:
            return 1;

        case WM_PAINT:
            Paint();
            return 0;

        case WM_SIZE:
            InvalidateRect(window_, nullptr, FALSE);
            return 0;

        case WM_MOUSEMOVE:
            UpdateHover(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
            if (!trackingMouse_) {
                TRACKMOUSEEVENT tracking{sizeof(tracking), TME_LEAVE, window_, 0};
                TrackMouseEvent(&tracking);
                trackingMouse_ = true;
            }
            return 0;

        case WM_MOUSELEAVE:
            trackingMouse_ = false;
            {
                const int previous = hovered_;
                hovered_ = -1;
                InvalidateButton(previous);
            }
            return 0;

        case WM_LBUTTONDOWN: {
            const int hit = ButtonAt(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
            const int previous = pressed_;
            pressed_ = hit;
            if (hit >= 0) {
                SetCapture(window_);
            }
            InvalidateButton(previous);
            InvalidateButton(pressed_);
            return 0;
        }

        case WM_LBUTTONUP: {
            const int hit = ButtonAt(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
            const int pressed = pressed_;
            pressed_ = -1;
            if (GetCapture() == window_) {
                ReleaseCapture();
            }
            if (pressed >= 0 && pressed == hit) {
                ActivateButton(static_cast<ButtonId>(pressed));
            }
            InvalidateButton(pressed);
            return 0;
        }

        case WM_KEYDOWN:
            if ((lParam & (1LL << 30)) != 0 && wParam != VK_LEFT && wParam != VK_RIGHT) {
                return 0;
            }
            HandleKey(static_cast<UINT>(wParam));
            return 0;

        case WM_TIMER:
            if (wParam == kRapidTimer) {
                KillTimer(window_, kRapidTimer);
                if (mapOpen_) {
                    ToggleMap();
                }
            }
            return 0;

        case WM_NCHITTEST:
            return HTCLIENT;

        case WM_CLOSE:
            DestroyWindow(window_);
            return 0;

        case WM_DESTROY:
            KillTimer(window_, kRapidTimer);
            if (GetCapture() == window_) {
                ReleaseCapture();
            }
            PostQuitMessage(0);
            return 0;

        default:
            return DefWindowProcW(window_, message, wParam, lParam);
        }
    }


    LRESULT HandlePipeRequest(PipeRequest* request) {
        if (request == nullptr) {
            return 0;
        }
        request->response = ExecutePipeCommand(request->request);
        if (request->completed != nullptr) {
            SetEvent(request->completed);
        }
        return 0;
    }

    std::string StateJson() const {
        const FloorSet* floor = floorIndex_ < floors_.size() ? &floors_[floorIndex_] : nullptr;
        const Bitmap* image = CurrentImage();
        const std::string floorLabel = floor == nullptr ? std::string() : WideToUtf8(floor->label);
        const std::string imagePath = image == nullptr ? std::string() : WideToUtf8(image->path);
        std::ostringstream output;
        output << "{\"mapOpen\":" << (mapOpen_ ? "true" : "false")
               << ",\"mapOnly\":" << (mapOnly_ ? "true" : "false")
               << ",\"floorIndex\":" << floorIndex_
               << ",\"floor\":\"" << JsonEscape(floorLabel) << "\""
               << ",\"imageIndex\":" << (image == nullptr ? 0 : imageIndex_)
               << ",\"imageCount\":" << (floor == nullptr ? 0 : floor->images.size())
               << ",\"imagePath\":\"" << JsonEscape(imagePath) << "\""
               << ",\"toggleCount\":" << toggleCount_
               << ",\"hwnd\":" << reinterpret_cast<std::uintptr_t>(window_)
               << ",\"pid\":" << GetCurrentProcessId()
               << "}";
        return output.str();
    }

    std::string SuccessResponse() const {
        return "{\"ok\":true,\"state\":" + StateJson() + "}";
    }

    std::string ErrorResponse(const std::string& message) const {
        return "{\"ok\":false,\"error\":\"" + JsonEscape(message)
            + "\",\"state\":" + StateJson() + "}";
    }

    std::string ExecutePipeCommand(const std::string& request) {
        const std::string command = JsonString(request, "command");
        if (command == "get_state") {
            return SuccessResponse();
        }
        if (command == "set_map_open") {
            bool open = false;
            if (!JsonBool(request, "open", open)) {
                return ErrorResponse("set_map_open requires args.open.");
            }
            if (mapOpen_ != open) {
                ToggleMap();
            }
            return SuccessResponse();
        }
        if (command == "set_map_only") {
            bool mapOnly = false;
            if (!JsonBool(request, "mapOnly", mapOnly)) {
                return ErrorResponse("set_map_only requires args.mapOnly.");
            }
            if (mapOnly_ != mapOnly) {
                ToggleMask();
            }
            return SuccessResponse();
        }
        if (command == "select_floor") {
            long long floorIndex = -1;
            if (!JsonInteger(request, "floorIndex", floorIndex)) {
                long long oneBasedFloor = -1;
                if (!JsonInteger(request, "floor", oneBasedFloor)) {
                    return ErrorResponse("select_floor requires args.floorIndex or args.floor.");
                }
                floorIndex = oneBasedFloor - 1;
            }
            if (!SelectFloorIndex(floorIndex)) {
                return ErrorResponse("The requested floor does not contain images.");
            }
            return SuccessResponse();
        }
        if (command == "select_image") {
            long long imageIndex = -1;
            if (!JsonInteger(request, "imageIndex", imageIndex)
                || !SelectImageIndex(imageIndex)) {
                return ErrorResponse("select_image requires a valid imageIndex.");
            }
            return SuccessResponse();
        }
        if (command == "next_image") {
            NextImage();
            return SuccessResponse();
        }
        if (command == "previous_image") {
            PreviousImage();
            return SuccessResponse();
        }
        if (command == "clear_images") {
            ClearAllImages();
            return SuccessResponse();
        }
        if (command == "shutdown") {
            const std::string response = SuccessResponse();
            PostMessageW(window_, WM_CLOSE, 0, 0);
            return response;
        }
        return ErrorResponse("Unknown overlay_game command.");
    }

    std::wstring PipePath() const {
        return L"\\\\.\\pipe\\" + pipeName_;
    }

    void StartPipeServer() {
        if (pipeName_.empty()) {
            return;
        }
        pipeStop_.store(false);
        pipeThread_ = std::thread([this]() { PipeLoop(); });
    }

    void StopPipeServer() noexcept {
        pipeStop_.store(true);
        if (!pipeName_.empty()) {
            HANDLE wake = CreateFileW(
                PipePath().c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, 0, nullptr);
            if (wake != INVALID_HANDLE_VALUE) {
                CloseHandle(wake);
            }
        }
        if (pipeThread_.joinable()) {
            pipeThread_.join();
        }
    }

    void PipeLoop() noexcept {
        while (!pipeStop_.load()) {
            HANDLE pipe = CreateNamedPipeW(
                PipePath().c_str(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                static_cast<DWORD>(kMaxPipeRequestBytes),
                static_cast<DWORD>(kMaxPipeRequestBytes),
                1000,
                nullptr);
            if (pipe == INVALID_HANDLE_VALUE) {
                return;
            }

            const BOOL connected = ConnectNamedPipe(pipe, nullptr)
                ? TRUE
                : (GetLastError() == ERROR_PIPE_CONNECTED ? TRUE : FALSE);
            if (!connected) {
                CloseHandle(pipe);
                continue;
            }
            if (pipeStop_.load()) {
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
                break;
            }

            std::string request;
            char buffer[1024]{};
            DWORD bytesRead = 0;
            bool readSucceeded = false;
            while (request.size() < kMaxPipeRequestBytes
                   && ReadFile(pipe, buffer, sizeof(buffer), &bytesRead, nullptr)
                   && bytesRead > 0) {
                request.append(buffer, buffer + bytesRead);
                const std::size_t lineEnd = request.find('\n');
                if (lineEnd != std::string::npos) {
                    request.resize(lineEnd);
                    readSucceeded = true;
                    break;
                }
            }

            if (readSucceeded && !request.empty()) {
                PipeRequest pipeRequest;
                pipeRequest.request = std::move(request);
                pipeRequest.completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
                if (pipeRequest.completed != nullptr
                    && PostMessageW(window_, kPipeCommandMessage, 0,
                                    reinterpret_cast<LPARAM>(&pipeRequest))) {
                    if (WaitForSingleObject(pipeRequest.completed, 10000) != WAIT_OBJECT_0) {
                        pipeRequest.response = ErrorResponse("UI command timed out.");
                    }
                } else {
                    pipeRequest.response = ErrorResponse("overlay_game UI thread is unavailable.");
                }
                if (pipeRequest.completed != nullptr) {
                    CloseHandle(pipeRequest.completed);
                    pipeRequest.completed = nullptr;
                }
                if (pipeRequest.response.empty()) {
                    pipeRequest.response = ErrorResponse("Empty overlay_game response.");
                }
                DWORD bytesWritten = 0;
                const std::string response = pipeRequest.response + "\n";
                WriteFile(pipe, response.data(), static_cast<DWORD>(response.size()),
                          &bytesWritten, nullptr);
                FlushFileBuffers(pipe);
            }

            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
    }

