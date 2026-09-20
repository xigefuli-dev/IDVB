    void UploadFloor(int floorIndex) {
        if (floorIndex < 0 || floorIndex >= static_cast<int>(floors_.size())) {
            return;
        }

        FloorSet& targetFloor = floors_[floorIndex];
        const std::size_t availableSlots = kMaxImagesPerFloor -
            std::min(targetFloor.images.size(), kMaxImagesPerFloor);
        if (availableSlots == 0) {
            message_ = L"This floor already contains the maximum number of images.";
            InvalidateRect(window_, nullptr, FALSE);
            return;
        }

        ComPtr<IFileOpenDialog> dialog;
        HRESULT hr = CoCreateInstance(
            CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(dialog.put()));
        if (FAILED(hr)) {
            message_ = L"Unable to open the image picker.";
            InvalidateRect(window_, nullptr, FALSE);
            return;
        }

        DWORD options = 0;
        if (SUCCEEDED(dialog->GetOptions(&options))) {
            dialog->SetOptions(options | FOS_ALLOWMULTISELECT | FOS_FILEMUSTEXIST | FOS_FORCEFILESYSTEM);
        }
        const COMDLG_FILTERSPEC filters[] = {
            {L"Image files", L"*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"},
            {L"All files", L"*.*"},
        };
        dialog->SetFileTypes(static_cast<UINT>(std::size(filters)), filters);
        dialog->SetTitle((L"Select images for " + floors_[floorIndex].label).c_str());

        // Passing nullptr prevents the shell picker from becoming owned by the borderless test window.
        hr = dialog->Show(nullptr);
        RestoreFocus();
        if (hr == HRESULT_FROM_WIN32(ERROR_CANCELLED)) {
            return;
        }
        if (FAILED(hr)) {
            message_ = L"The image picker could not be opened.";
            InvalidateRect(window_, nullptr, FALSE);
            return;
        }

        ComPtr<IShellItemArray> itemArray;
        if (FAILED(dialog->GetResults(itemArray.put()))) {
            message_ = L"No files were returned by the image picker.";
            InvalidateRect(window_, nullptr, FALSE);
            return;
        }

        DWORD count = 0;
        if (FAILED(itemArray->GetCount(&count)) || count == 0) {
            message_ = L"No images were selected.";
            InvalidateRect(window_, nullptr, FALSE);
            return;
        }

        std::vector<std::filesystem::path> paths;
        paths.reserve(std::min<std::size_t>(count, availableSlots));
        for (DWORD i = 0; i < count && paths.size() < availableSlots; ++i) {
            ComPtr<IShellItem> item;
            if (FAILED(itemArray->GetItemAt(i, item.put()))) {
                continue;
            }
            PWSTR rawPath = nullptr;
            const HRESULT pathResult = item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
            if (SUCCEEDED(pathResult) && rawPath != nullptr) {
                try {
                    std::wstring pathString(rawPath);
                    CoTaskMemFree(rawPath);
                    rawPath = nullptr;
                    paths.emplace_back(std::move(pathString));
                } catch (...) {
                    CoTaskMemFree(rawPath);
                    throw;
                }
            } else if (rawPath != nullptr) {
                CoTaskMemFree(rawPath);
            }
        }
        std::sort(paths.begin(), paths.end(), [](const auto& left, const auto& right) {
            return Lowercase(left.wstring()) < Lowercase(right.wstring());
        });

        std::size_t existingBytes = 0;
        for (const Bitmap& image : targetFloor.images) {
            if (image.pixels.size() > kMaxFloorBytes - std::min(existingBytes, kMaxFloorBytes)) {
                existingBytes = kMaxFloorBytes;
                break;
            }
            existingBytes += image.pixels.size();
        }

        std::vector<Bitmap> loaded;
        loaded.reserve(std::min(paths.size(), availableSlots));
        std::size_t loadedBytes = existingBytes;
        for (const std::filesystem::path& path : paths) {
            Bitmap image;
            if (Bitmap::Load(imagingFactory_.get(), path, image) &&
                image.pixels.size() <= kMaxFloorBytes - std::min(loadedBytes, kMaxFloorBytes)) {
                loadedBytes += image.pixels.size();
                loaded.push_back(std::move(image));
                if (loaded.size() >= availableSlots) {
                    break;
                }
            }
        }
        if (loaded.empty()) {
            message_ = L"The selected files could not be decoded as images.";
            InvalidateRect(window_, nullptr, FALSE);
            return;
        }

        const std::size_t firstNewImage = targetFloor.images.size();
        targetFloor.images.insert(
            targetFloor.images.end(),
            std::make_move_iterator(loaded.begin()),
            std::make_move_iterator(loaded.end()));
        floorIndex_ = static_cast<std::size_t>(floorIndex);
        imageIndex_ = firstNewImage;
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
    }

    void ClearAllImages() {
        bool hadImages = false;
        for (FloorSet& floor : floors_) {
            hadImages = hadImages || !floor.images.empty();
            floor.images.clear();
        }

        KillTimer(window_, kRapidTimer);
        mapOpen_ = false;
        floorIndex_ = 0;
        imageIndex_ = 0;
        message_ = hadImages ? L"All loaded screenshot data cleared."
                             : L"There is no loaded screenshot data to clear.";
        InvalidateRect(window_, nullptr, FALSE);
    }

    static void SendXButton1() noexcept {
        INPUT inputs[2]{};
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dwFlags = MOUSEEVENTF_XDOWN;
        inputs[0].mi.mouseData = XBUTTON1;
        inputs[1].type = INPUT_MOUSE;
        inputs[1].mi.dwFlags = MOUSEEVENTF_XUP;
        inputs[1].mi.mouseData = XBUTTON1;
        SendInput(2, inputs, sizeof(INPUT));
    }

    void RestoreFocus() noexcept {
        if (window_ != nullptr) {
            SetForegroundWindow(window_);
            SetFocus(window_);
        }
    }

    FloorSet& CurrentFloor() noexcept {
        return floors_[floorIndex_];
    }

    const Bitmap* CurrentImage() const noexcept {
        if (floorIndex_ >= floors_.size()) {
            return nullptr;
        }
        const FloorSet& floor = floors_[floorIndex_];
        if (imageIndex_ >= floor.images.size()) {
            return nullptr;
        }
        return &floor.images[imageIndex_];
    }

    std::wstring StatusText(const Bitmap* image) const {
        if (!message_.empty()) {
            return message_;
        }
        const FloorSet& floor = floors_[floorIndex_];
        std::wstring result = L"Map: ";
        result += mapOpen_ ? L"OPEN" : L"CLOSED";
        result += L"  |  Display: ";
        result += mapOnly_ ? L"MAP ONLY" : L"FULL SCREEN";
        result += L"  |  Floor: ";
        result += floor.label;
        result += L"  |  Image: ";
        if (image != nullptr) {
            result += std::to_wstring(imageIndex_ + 1);
            result += L"/";
            result += std::to_wstring(floor.images.size());
            result += L"  (";
            result += image->path.substr(image->path.find_last_of(L"\\/") + 1);
            result += L")";
        } else {
            result += L"none";
        }
        result += L"  |  Toggles: ";
        result += std::to_wstring(toggleCount_);
        return result;
    }

    static RECT MapViewport(const RECT& client) {
        const int width = client.right - client.left;
        const int height = client.bottom - client.top;
        RECT map{
            client.left + static_cast<int>(width * 0.3201389),
            client.top + static_cast<int>(height * 0.1166667),
            client.left + static_cast<int>(width * 0.875),
            client.top + static_cast<int>(height * 0.8966667),
        };
        const RECT content = ImageViewport(client);
        map.left = std::max(map.left, content.left);
        map.top = std::max(map.top, content.top);
        map.right = std::min(map.right, content.right);
        map.bottom = std::min(map.bottom, content.bottom);
        if (map.right < map.left) {
            map.right = map.left;
        }
        if (map.bottom < map.top) {
            map.bottom = map.top;
        }
        return map;
    }

    static int ToolbarHeight(const RECT& client) {
        return std::max(80, ScaleValue(92, UiScale(client)));
    }

    static int StatusHeight(const RECT& client) {
        return ScaleValue(64, UiScale(client));
    }

    static RECT ImageViewport(const RECT& client) {
        // The background covers the complete client area.  DrawOverlay paints
        // the toolbar and status bar afterwards, so both bars are composited on
        // top of the image instead of reducing its viewport.
        return client;
    }

    static RECT CoverImageRect(const RECT& available, const Bitmap& image) {
        const int availableWidth = available.right - available.left;
        const int availableHeight = available.bottom - available.top;
        if (!image.valid() || availableWidth <= 0 || availableHeight <= 0) {
            return available;
        }

        const auto ceilRatio = [](std::uint64_t numerator, std::uint64_t denominator) {
            return numerator / denominator + (numerator % denominator == 0 ? 0ULL : 1ULL);
        };

        // Fit to the full available width first.  This keeps the image's left
        // and right edges aligned with the screen at 1920x1080, 2560x1440 and
        // 2560x1600, while preserving its aspect ratio.  A taller image is
        // clipped vertically; a shorter one leaves the already-painted dark
        // background visible above and below it instead of being distorted.
        const int drawWidth = availableWidth;
        const int drawHeight = std::max(1, static_cast<int>(std::min<std::uint64_t>(
            ceilRatio(static_cast<std::uint64_t>(drawWidth) * image.height, image.width),
            static_cast<std::uint64_t>(std::numeric_limits<int>::max()))));

        const int left = available.left;
        const int top = available.top + (availableHeight - drawHeight) / 2;
        return RECT{left, top, left + drawWidth, top + drawHeight};
    }

    static int UiScale(const RECT& client) {
        const int width = client.right - client.left;
        const int height = client.bottom - client.top;
        const double value = std::min(width / 1920.0, height / 1080.0);
        return std::max(1, static_cast<int>(value * 100.0 + 0.5));
    }

    static int ScaleValue(int value, int scalePercent) {
        const std::int64_t scaled = (static_cast<std::int64_t>(value) * scalePercent + 50) / 100;
        return static_cast<int>(std::clamp<std::int64_t>(scaled, 1, std::numeric_limits<int>::max()));
    }

    HINSTANCE module_ = nullptr;
    HWND window_ = nullptr;
    bool classRegistered_ = false;
    bool trackingMouse_ = false;
    int hovered_ = -1;
    int pressed_ = -1;
    bool mapOpen_ = false;
    bool mapOnly_ = false;
    std::size_t floorIndex_ = 0;
    std::size_t imageIndex_ = 0;
    std::uint64_t toggleCount_ = 0;
    std::filesystem::path assetDirectory_;
    std::wstring pipeName_;
    std::atomic<bool> pipeStop_{false};
    std::thread pipeThread_;
    std::wstring message_;
    ComPtr<IWICImagingFactory> imagingFactory_;
    std::vector<FloorSet> floors_;
