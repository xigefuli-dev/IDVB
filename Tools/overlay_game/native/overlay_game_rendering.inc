    void Paint() {
        PAINTSTRUCT paint{};
        HDC dc = BeginPaint(window_, &paint);
        if (dc == nullptr) {
            return;
        }

        RECT client{};
        GetClientRect(window_, &client);
        HDC renderDc = dc;
        HDC bufferDc = nullptr;
        HBITMAP bufferBitmap = nullptr;
        HGDIOBJ previousBitmap = nullptr;
        bool bufferReady = false;

        const int width = client.right - client.left;
        const int height = client.bottom - client.top;
        if (width > 0 && height > 0) {
            bufferDc = CreateCompatibleDC(dc);
            if (bufferDc != nullptr) {
                bufferBitmap = CreateCompatibleBitmap(dc, width, height);
                if (bufferBitmap != nullptr) {
                    previousBitmap = SelectObject(bufferDc, bufferBitmap);
                    if (previousBitmap != nullptr && previousBitmap != HGDI_ERROR) {
                        renderDc = bufferDc;
                        bufferReady = true;
                    } else {
                        DeleteObject(bufferBitmap);
                        bufferBitmap = nullptr;
                        DeleteDC(bufferDc);
                        bufferDc = nullptr;
                    }
                } else {
                    DeleteDC(bufferDc);
                    bufferDc = nullptr;
                }
            }
        }

        try {
            Fill(renderDc, client, RGB(25, 31, 42));

            const RECT imageViewport = ImageViewport(client);
            const RECT map = MapViewport(client);
            const Bitmap* image = CurrentImage();
            if (mapOpen_ && image != nullptr && image->valid()) {
                const RECT imageDestination = CoverImageRect(imageViewport, *image);
                if (mapOnly_) {
                    const int state = SaveDC(renderDc);
                    if (state != 0) {
                        IntersectClipRect(renderDc, map.left, map.top, map.right, map.bottom);
                        DrawBitmap(renderDc, imageDestination, *image);
                        RestoreDC(renderDc, state);
                    }
                    FillOutsideMap(renderDc, client, map);
                } else {
                    DrawBitmap(renderDc, imageDestination, *image);
                }
            }

            DrawOverlay(renderDc, client, map, image);
        } catch (...) {
            // Painting must always reach EndPaint, even if a transient allocation fails.
            Fill(renderDc, client, RGB(25, 31, 42));
        }

        if (bufferReady) {
            RECT dirty = paint.rcPaint;
            dirty.left = std::max(dirty.left, client.left);
            dirty.top = std::max(dirty.top, client.top);
            dirty.right = std::min(dirty.right, client.right);
            dirty.bottom = std::min(dirty.bottom, client.bottom);
            if (dirty.right > dirty.left && dirty.bottom > dirty.top) {
                BitBlt(dc, dirty.left, dirty.top, dirty.right - dirty.left, dirty.bottom - dirty.top,
                       renderDc, dirty.left, dirty.top, SRCCOPY);
            }
            SelectObject(bufferDc, previousBitmap);
            DeleteObject(bufferBitmap);
            DeleteDC(bufferDc);
        }
        EndPaint(window_, &paint);
    }

    void DrawOverlay(HDC dc, const RECT& client, const RECT& map, const Bitmap* image) {
        const int scale = UiScale(client);
        const int toolbarHeight = ToolbarHeight(client);
        RECT toolbar{0, 0, client.right, toolbarHeight};
        Fill(dc, toolbar, RGB(14, 20, 29));

        HFONT titleFont = CreateFontW(-ScaleValue(22, scale), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                                      DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                      CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
        if (titleFont != nullptr) {
            HFONT previous = static_cast<HFONT>(SelectObject(dc, titleFont));
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB(235, 242, 250));
            RECT title{ScaleValue(24, scale), ScaleValue(8, scale), client.right - ScaleValue(24, scale),
                       ScaleValue(38, scale)};
            DrawTextW(dc, L"Identity Vision Bridge", -1, &title, DT_LEFT | DT_SINGLELINE | DT_VCENTER);
            SelectObject(dc, previous);
            DeleteObject(titleFont);
        }

        for (const ToolButton& button : Buttons(client)) {
            DrawButton(dc, button, scale);
        }

        HPEN mapPen = CreatePen(PS_SOLID, std::max(1, ScaleValue(2, scale)), RGB(168, 197, 225));
        if (mapPen != nullptr) {
            HPEN previous = static_cast<HPEN>(SelectObject(dc, mapPen));
            HBRUSH previousBrush = static_cast<HBRUSH>(SelectObject(dc, GetStockObject(HOLLOW_BRUSH)));
            Rectangle(dc, map.left, map.top, map.right, map.bottom);
            SelectObject(dc, previousBrush);
            SelectObject(dc, previous);
            DeleteObject(mapPen);
        }

        const int statusHeight = StatusHeight(client);
        RECT statusBackground{0, client.bottom - statusHeight, client.right, client.bottom};
        Fill(dc, statusBackground, RGB(14, 20, 29));
        HFONT statusFont = CreateFontW(-ScaleValue(15, scale), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                       DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                       CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
        if (statusFont != nullptr) {
            HFONT previous = static_cast<HFONT>(SelectObject(dc, statusFont));
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB(208, 220, 233));
            RECT status{ScaleValue(24, scale), client.bottom - statusHeight + ScaleValue(8, scale),
                        client.right - ScaleValue(24, scale), client.bottom - ScaleValue(8, scale)};
            const std::wstring text = StatusText(image);
            DrawTextW(dc, text.c_str(), -1, &status, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS);
            SelectObject(dc, previous);
            DeleteObject(statusFont);
        }
    }

    void DrawButton(HDC dc, const ToolButton& button, int scale) {
        const bool hovered = hovered_ == static_cast<int>(button.id);
        const bool pressed = pressed_ == static_cast<int>(button.id);
        const COLORREF fill = pressed ? RGB(48, 72, 96) : hovered ? RGB(61, 91, 120) : RGB(39, 58, 78);
        const COLORREF border = hovered ? RGB(154, 194, 232) : RGB(104, 140, 175);

        HBRUSH brush = CreateSolidBrush(fill);
        HPEN pen = CreatePen(PS_SOLID, std::max(1, ScaleValue(1, scale)), border);
        if (brush == nullptr || pen == nullptr) {
            if (brush != nullptr) DeleteObject(brush);
            if (pen != nullptr) DeleteObject(pen);
            return;
        }
        HBRUSH previousBrush = static_cast<HBRUSH>(SelectObject(dc, brush));
        HPEN previousPen = static_cast<HPEN>(SelectObject(dc, pen));
        const int radius = std::max(4, ScaleValue(6, scale));
        RoundRect(dc, button.bounds.left, button.bounds.top, button.bounds.right, button.bounds.bottom, radius, radius);
        SelectObject(dc, previousPen);
        SelectObject(dc, previousBrush);
        DeleteObject(pen);
        DeleteObject(brush);

        HFONT font = CreateFontW(-ScaleValue(14, scale), 0, 0, 0, FW_MEDIUM, FALSE, FALSE, FALSE,
                                 DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                 CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
        if (font == nullptr) {
            return;
        }
        HFONT previousFont = static_cast<HFONT>(SelectObject(dc, font));
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(244, 248, 252));
        RECT text = button.bounds;
        DrawTextW(dc, button.text.c_str(), -1, &text,
                  DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        SelectObject(dc, previousFont);
        DeleteObject(font);
    }

    static void Fill(HDC dc, const RECT& rectangle, COLORREF color) {
        HBRUSH brush = CreateSolidBrush(color);
        if (brush != nullptr) {
            FillRect(dc, &rectangle, brush);
            DeleteObject(brush);
        }
    }

    static void FillOutsideMap(HDC dc, const RECT& client, const RECT& map) {
        Fill(dc, RECT{client.left, client.top, client.right, map.top}, RGB(25, 31, 42));
        Fill(dc, RECT{client.left, map.bottom, client.right, client.bottom}, RGB(25, 31, 42));
        Fill(dc, RECT{client.left, map.top, map.left, map.bottom}, RGB(25, 31, 42));
        Fill(dc, RECT{map.right, map.top, client.right, map.bottom}, RGB(25, 31, 42));
    }

    static void DrawBitmap(HDC dc, const RECT& destination, const Bitmap& image) {
        if (!image.valid()) {
            return;
        }
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(info.bmiHeader);
        info.bmiHeader.biWidth = static_cast<LONG>(image.width);
        info.bmiHeader.biHeight = -static_cast<LONG>(image.height);
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        const int previousMode = SetStretchBltMode(dc, HALFTONE);
        SetBrushOrgEx(dc, 0, 0, nullptr);
        StretchDIBits(dc, destination.left, destination.top,
                      destination.right - destination.left, destination.bottom - destination.top,
                      0, 0, static_cast<int>(image.width), static_cast<int>(image.height),
                      image.pixels.data(), &info, DIB_RGB_COLORS, SRCCOPY);
        SetStretchBltMode(dc, previousMode);
    }

    std::vector<ToolButton> Buttons(const RECT& client) const {
        const int scale = UiScale(client);
        const int y = ScaleValue(43, scale);
        const int height = ScaleValue(38, scale);
        const int gap = ScaleValue(7, scale);
        int x = ScaleValue(24, scale);
        std::vector<ToolButton> result;
        result.reserve(11);

        auto add = [&](ButtonId id, int width, const wchar_t* text) {
            const int actualWidth = ScaleValue(width, scale);
            result.push_back(ToolButton{id, RECT{x, y, x + actualWidth, y + height}, text});
            x += actualWidth + gap;
        };
        add(ButtonId::ToggleMap, 205, L"Open / Close  [Space]");
        add(ButtonId::RapidToggle, 184, L"Rapid toggle  [50 ms]");
        add(ButtonId::Previous, 116, L"Previous  [Left]");
        add(ButtonId::Next, 116, L"Next  [Right]");
        add(ButtonId::SwitchFloor, 142, L"Floor  [F]");
        add(ButtonId::ToggleMask, 142, L"Map-only  [M]");
        add(ButtonId::UploadFloor1, 118, L"Upload 1F");
        add(ButtonId::UploadFloor2, 118, L"Upload 2F");
        add(ButtonId::UploadFloor3, 118, L"Upload 3F");
        add(ButtonId::UploadFloor4, 118, L"Upload 4F");
        add(ButtonId::ClearAllImages, 194, L"Clear all screenshots");
        return result;
    }

    int ButtonAt(int x, int y) const {
        RECT client{};
        GetClientRect(window_, &client);
        for (const ToolButton& button : Buttons(client)) {
            if (PtInRect(&button.bounds, POINT{x, y})) {
                return static_cast<int>(button.id);
            }
        }
        return -1;
    }

    void InvalidateButton(int id) const {
        if (id < 0) {
            return;
        }
        RECT client{};
        GetClientRect(window_, &client);
        for (const ToolButton& button : Buttons(client)) {
            if (static_cast<int>(button.id) == id) {
                InvalidateRect(window_, &button.bounds, FALSE);
                return;
            }
        }
    }

    void UpdateHover(int x, int y) {
        const int next = ButtonAt(x, y);
        if (next != hovered_) {
            const int previous = hovered_;
            hovered_ = next;
            InvalidateButton(previous);
            InvalidateButton(hovered_);
        }
    }

    void HandleKey(UINT key) {
        switch (key) {
        case VK_SPACE:
            ToggleMap();
            break;
        case VK_LEFT:
            PreviousImage();
            break;
        case VK_RIGHT:
            NextImage();
            break;
        case 'F':
            SwitchFloor();
            break;
        case 'M':
            ToggleMask();
            break;
        case 'R':
            StartRapidToggle();
            break;
        case VK_ESCAPE:
            SendMessageW(window_, WM_CLOSE, 0, 0);
            break;
        default:
            break;
        }
    }

    void ActivateButton(ButtonId id) {
        switch (id) {
        case ButtonId::ToggleMap:
            ToggleMap();
            break;
        case ButtonId::RapidToggle:
            StartRapidToggle();
            break;
        case ButtonId::Previous:
            PreviousImage();
            break;
        case ButtonId::Next:
            NextImage();
            break;
        case ButtonId::SwitchFloor:
            SwitchFloor();
            break;
        case ButtonId::ToggleMask:
            ToggleMask();
            break;
        case ButtonId::UploadFloor1:
        case ButtonId::UploadFloor2:
        case ButtonId::UploadFloor3:
        case ButtonId::UploadFloor4:
            UploadFloor(static_cast<int>(id) - static_cast<int>(ButtonId::UploadFloor1));
            break;
        case ButtonId::ClearAllImages:
            ClearAllImages();
            break;
        }
    }

    void ToggleMap() {
        mapOpen_ = !mapOpen_;
        ++toggleCount_;
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
        SendXButton1();
    }

    void StartRapidToggle() {
        if (mapOpen_) {
            return;
        }
        KillTimer(window_, kRapidTimer);
        ToggleMap();
        SetTimer(window_, kRapidTimer, kRapidDelayMs, nullptr);
    }

    void PreviousImage() {
        FloorSet& floor = CurrentFloor();
        if (floor.images.empty()) {
            return;
        }
        imageIndex_ = (imageIndex_ + floor.images.size() - 1) % floor.images.size();
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
    }

    void NextImage() {
        FloorSet& floor = CurrentFloor();
        if (floor.images.empty()) {
            return;
        }
        imageIndex_ = (imageIndex_ + 1) % floor.images.size();
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
    }


    bool SelectFloorIndex(long long floorIndex) {
        if (floorIndex < 0
            || floorIndex >= static_cast<long long>(floors_.size())
            || floors_[static_cast<std::size_t>(floorIndex)].images.empty()) {
            return false;
        }
        floorIndex_ = static_cast<std::size_t>(floorIndex);
        imageIndex_ = 0;
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
        return true;
    }

    bool SelectImageIndex(long long imageIndex) {
        if (floorIndex_ >= floors_.size()
            || imageIndex < 0
            || imageIndex >= static_cast<long long>(floors_[floorIndex_].images.size())) {
            return false;
        }
        imageIndex_ = static_cast<std::size_t>(imageIndex);
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
        return true;
    }

    void SwitchFloor() {
        if (floors_.empty()) {
            return;
        }
        for (std::size_t offset = 1; offset <= floors_.size(); ++offset) {
            const std::size_t candidate = (floorIndex_ + offset) % floors_.size();
            if (!floors_[candidate].images.empty()) {
                floorIndex_ = candidate;
                imageIndex_ = 0;
                message_.clear();
                InvalidateRect(window_, nullptr, FALSE);
                return;
            }
        }
    }

    void ToggleMask() {
        mapOnly_ = !mapOnly_;
        message_.clear();
        InvalidateRect(window_, nullptr, FALSE);
    }

