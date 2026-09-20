#include <windows.h>
#include <windowsx.h>
#include <wincodec.h>
#include <shobjidl.h>
#include <shellapi.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cwctype>
#include <filesystem>
#include <iterator>
#include <limits>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "windowscodecs.lib")

namespace {

constexpr wchar_t kWindowClass[] = L"IDVBOverlayGameWindow";
constexpr wchar_t kWindowTitle[] = L"Identity Vision Bridge - Overlay Game";
constexpr UINT_PTR kRapidTimer = 1;
constexpr UINT kRapidDelayMs = 50;
constexpr UINT kPipeCommandMessage = WM_APP + 17;
constexpr std::size_t kMaxPipeRequestBytes = 64 * 1024;
constexpr std::uint64_t kMaxImagePixels = 64ULL * 1024ULL * 1024ULL;
constexpr std::size_t kMaxFloorBytes = 512ULL * 1024ULL * 1024ULL;
constexpr std::size_t kMaxImagesPerFloor = 64;

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int byteCount = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (byteCount <= 0) return {};
    std::string result(static_cast<std::size_t>(byteCount), '\0');
    WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        result.data(), byteCount, nullptr, nullptr);
    return result;
}

std::string JsonEscape(const std::string& value) {
    std::string result;
    result.reserve(value.size() + 8);
    for (const unsigned char ch : value) {
        switch (ch) {
        case '\\': result += "\\\\"; break;
        case '"': result += "\\\""; break;
        case '\b': result += "\\b"; break;
        case '\f': result += "\\f"; break;
        case '\n': result += "\\n"; break;
        case '\r': result += "\\r"; break;
        case '\t': result += "\\t"; break;
        default:
            if (ch < 0x20) {
                result += "\\u00";
                constexpr char digits[] = "0123456789abcdef";
                result += digits[(ch >> 4) & 0x0F];
                result += digits[ch & 0x0F];
            } else result.push_back(static_cast<char>(ch));
            break;
        }
    }
    return result;
}

std::string JsonString(const std::string& json, const std::string& key) {
    const std::string marker = "\"" + key + "\"";
    const std::size_t keyPosition = json.find(marker);
    if (keyPosition == std::string::npos) return {};
    const std::size_t colon = json.find(':', keyPosition + marker.size());
    const std::size_t firstQuote = colon == std::string::npos ? std::string::npos : json.find('"', colon + 1);
    if (firstQuote == std::string::npos) return {};
    std::string result;
    bool escaped = false;
    for (std::size_t i = firstQuote + 1; i < json.size(); ++i) {
        const char ch = json[i];
        if (escaped) { result.push_back(ch); escaped = false; }
        else if (ch == '\\') escaped = true;
        else if (ch == '"') return result;
        else result.push_back(ch);
    }
    return {};
}

bool JsonBool(const std::string& json, const std::string& key, bool& value) {
    const std::string marker = "\"" + key + "\"";
    const std::size_t keyPosition = json.find(marker);
    if (keyPosition == std::string::npos) return false;
    const std::size_t colon = json.find(':', keyPosition + marker.size());
    if (colon == std::string::npos) return false;
    const std::size_t position = json.find_first_not_of(" \t\r\n", colon + 1);
    if (position == std::string::npos) return false;
    if (json.compare(position, 4, "true") == 0) { value = true; return true; }
    if (json.compare(position, 5, "false") == 0) { value = false; return true; }
    return false;
}

bool JsonInteger(const std::string& json, const std::string& key, long long& value) {
    const std::string marker = "\"" + key + "\"";
    const std::size_t keyPosition = json.find(marker);
    if (keyPosition == std::string::npos) return false;
    const std::size_t colon = json.find(':', keyPosition + marker.size());
    if (colon == std::string::npos) return false;
    const std::size_t position = json.find_first_not_of(" \t\r\n", colon + 1);
    if (position == std::string::npos) return false;
    try {
        std::size_t parsed = 0;
        value = std::stoll(json.substr(position), &parsed, 10);
        return parsed > 0;
    } catch (...) { return false; }
}

struct PipeRequest final {
    std::string request;
    std::string response;
    HANDLE completed = nullptr;
};

template <typename T>
class ComPtr final {
public:
    ComPtr() = default;
    explicit ComPtr(T* value) noexcept : value_(value) {}
    ~ComPtr() { reset(); }

    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;

    ComPtr(ComPtr&& other) noexcept : value_(other.value_) {
        other.value_ = nullptr;
    }

    ComPtr& operator=(ComPtr&& other) noexcept {
        if (this != &other) {
            reset();
            value_ = other.value_;
            other.value_ = nullptr;
        }
        return *this;
    }

    T* get() const noexcept { return value_; }
    T* operator->() const noexcept { return value_; }
    explicit operator bool() const noexcept { return value_ != nullptr; }

    T** put() noexcept {
        reset();
        return &value_;
    }

    void reset(T* value = nullptr) noexcept {
        if (value_ != nullptr) {
            value_->Release();
        }
        value_ = value;
    }

private:
    T* value_ = nullptr;
};

class ScopedCom final {
public:
    ScopedCom() : result_(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)) {}
    ~ScopedCom() {
        if (SUCCEEDED(result_)) {
            CoUninitialize();
        }
    }

    ScopedCom(const ScopedCom&) = delete;
    ScopedCom& operator=(const ScopedCom&) = delete;

    HRESULT result() const noexcept { return result_; }

private:
    HRESULT result_;
};

std::wstring ModuleDirectory() {
    std::vector<wchar_t> buffer(512);
    for (;;) {
        const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0) {
            return {};
        }
        if (length < buffer.size() - 1) {
            std::filesystem::path module(std::wstring(buffer.data(), length));
            return module.parent_path().wstring();
        }
        buffer.resize(buffer.size() * 2);
        if (buffer.size() > 32768) {
            return {};
        }
    }
}

std::wstring Lowercase(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

int ImageNumber(const std::filesystem::path& path) {
    const std::wstring stem = path.stem().wstring();
    const std::size_t open = stem.find(L'(');
    const std::size_t close = stem.find(L')', open == std::wstring::npos ? 0 : open + 1);
    if (open == std::wstring::npos || close == std::wstring::npos || close <= open + 1) {
        return std::numeric_limits<int>::max();
    }

    int value = 0;
    for (std::size_t i = open + 1; i < close; ++i) {
        if (!std::iswdigit(stem[i])) {
            return std::numeric_limits<int>::max();
        }
        const int digit = stem[i] - L'0';
        if (value > (std::numeric_limits<int>::max() - digit) / 10) {
            return std::numeric_limits<int>::max();
        }
        value = value * 10 + digit;
    }
    return value;
}

std::vector<std::filesystem::path> DiscoverFloorImages(
    const std::filesystem::path& directory,
    const std::wstring& prefix) {
    std::vector<std::filesystem::path> result;
    std::error_code error;
    if (!std::filesystem::is_directory(directory, error) || error) {
        return result;
    }

    const std::wstring wantedPrefix = Lowercase(prefix) + L" (";
    std::filesystem::directory_iterator iterator(directory, error);
    const std::filesystem::directory_iterator end;
    while (!error && iterator != end) {
        const std::filesystem::directory_entry& entry = *iterator;
        std::error_code entryError;
        if (entry.is_regular_file(entryError) && !entryError) {
            const std::wstring extension = Lowercase(entry.path().extension().wstring());
            const std::wstring stem = Lowercase(entry.path().stem().wstring());
            if ((extension == L".png" || extension == L".jpg" || extension == L".jpeg" ||
                 extension == L".bmp" || extension == L".gif" || extension == L".tif" ||
                 extension == L".tiff") &&
                stem.size() > wantedPrefix.size() &&
                stem.compare(0, wantedPrefix.size(), wantedPrefix) == 0 &&
                stem.back() == L')') {
                result.push_back(entry.path());
            }
        }
        iterator.increment(error);
    }

    std::sort(result.begin(), result.end(), [](const auto& left, const auto& right) {
        const int leftNumber = ImageNumber(left);
        const int rightNumber = ImageNumber(right);
        if (leftNumber != rightNumber) {
            return leftNumber < rightNumber;
        }
        return Lowercase(left.filename().wstring()) < Lowercase(right.filename().wstring());
    });
    return result;
}

struct Bitmap final {
    std::wstring path;
    UINT width = 0;
    UINT height = 0;
    std::vector<std::uint8_t> pixels;

    bool valid() const noexcept {
        return width != 0 && height != 0 && !pixels.empty();
    }

    static bool Load(IWICImagingFactory* factory,
                     const std::filesystem::path& file,
                     Bitmap& output) noexcept {
        try {
            if (factory == nullptr) {
                return false;
            }

            ComPtr<IWICBitmapDecoder> decoder;
            HRESULT hr = factory->CreateDecoderFromFilename(
                file.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, decoder.put());
            if (FAILED(hr)) {
                return false;
            }

            ComPtr<IWICBitmapFrameDecode> frame;
            if (FAILED(decoder->GetFrame(0, frame.put()))) {
                return false;
            }

            UINT width = 0;
            UINT height = 0;
            if (FAILED(frame->GetSize(&width, &height)) || width == 0 || height == 0) {
                return false;
            }
            const std::uint64_t pixelCount = static_cast<std::uint64_t>(width) * height;
            if (width > static_cast<UINT>(std::numeric_limits<LONG>::max()) ||
                height > static_cast<UINT>(std::numeric_limits<LONG>::max()) ||
                pixelCount > kMaxImagePixels ||
                pixelCount > (std::numeric_limits<std::size_t>::max() / 4)) {
                return false;
            }

            ComPtr<IWICFormatConverter> converter;
            if (FAILED(factory->CreateFormatConverter(converter.put()))) {
                return false;
            }
            if (FAILED(converter->Initialize(
                    frame.get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone,
                    nullptr, 0.0, WICBitmapPaletteTypeCustom))) {
                return false;
            }

            if (static_cast<std::uint64_t>(width) >
                static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max() / 4)) {
                return false;
            }
            const std::size_t stride = static_cast<std::size_t>(width) * 4;
            if (static_cast<std::size_t>(height) > std::numeric_limits<std::size_t>::max() / stride) {
                return false;
            }
            const std::size_t byteCount = stride * static_cast<std::size_t>(height);
            if (stride > std::numeric_limits<UINT>::max() || byteCount > std::numeric_limits<UINT>::max()) {
                return false;
            }

            Bitmap candidate;
            candidate.path = file.wstring();
            candidate.width = width;
            candidate.height = height;
            candidate.pixels.resize(byteCount);
            if (FAILED(converter->CopyPixels(
                    nullptr, static_cast<UINT>(stride), static_cast<UINT>(byteCount), candidate.pixels.data()))) {
                return false;
            }
            output = std::move(candidate);
            return true;
        } catch (...) {
            return false;
        }
    }
};

struct FloorSet final {
    std::wstring label;
    std::vector<Bitmap> images;
};

enum class ButtonId : int {
    ToggleMap = 1,
    RapidToggle,
    Previous,
    Next,
    SwitchFloor,
    ToggleMask,
    UploadFloor1,
    UploadFloor2,
    UploadFloor3,
    UploadFloor4,
    ClearAllImages
};

struct ToolButton final {
    ButtonId id;
    RECT bounds{};
    std::wstring text;
};

class OverlayGame final {
#include "overlay_game_session.inc"
#include "overlay_game_rendering.inc"
#include "overlay_game_assets.inc"
};

std::filesystem::path FindAssetDirectory() {
    std::error_code error;
    const std::filesystem::path moduleAssets = std::filesystem::path(ModuleDirectory()) / L"Assets";
    if (std::filesystem::is_directory(moduleAssets, error) && !error) {
        return moduleAssets;
    }

    error.clear();
    const std::filesystem::path currentAssets = std::filesystem::current_path(error) / L"Assets";
    if (!error && std::filesystem::is_directory(currentAssets, error) && !error) {
        return currentAssets;
    }
    return moduleAssets;
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    ScopedCom com;
    if (FAILED(com.result())) {
        MessageBoxW(nullptr, L"COM initialization failed.", kWindowTitle, MB_ICONERROR | MB_OK);
        return 1;
    }

    try {
        std::wstring pipeName;
        int argumentCount = 0;
        LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
        if (arguments != nullptr) {
            for (int index = 1; index + 1 < argumentCount; ++index) {
                if (Lowercase(arguments[index]) == L"--pipe-name") {
                    pipeName = arguments[index + 1];
                    break;
                }
            }
            LocalFree(arguments);
        }
        OverlayGame game(FindAssetDirectory(), std::move(pipeName));
        return game.Run();
    } catch (const std::exception& error) {
        MessageBoxA(nullptr, error.what(), "Identity Vision Bridge - Overlay Game", MB_ICONERROR | MB_OK);
        return 1;
    } catch (...) {
        MessageBoxW(nullptr, L"The overlay game could not start.", kWindowTitle, MB_ICONERROR | MB_OK);
        return 1;
    }
}
