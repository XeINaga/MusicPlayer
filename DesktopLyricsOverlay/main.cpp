// DesktopLyricsOverlay/main.cpp
//
// Transparent desktop-lyrics overlay built as a standalone Win32 GUI process.
//
// Why a separate process / C++:
//   WinUI 3 (Windows App SDK) content windows are composited through an opaque
//   swap chain, so the desktop never shows through no matter what the XAML
//   background alpha is set to, and AppWindow.TransparencyKind is missing from
//   every WinAppSDK build available to this project. A classic Win32 *layered*
//   window (WS_EX_LAYERED + UpdateLayeredWindow) gives true per-pixel alpha:
//   the desktop shows through wherever the 32-bit BGRA bitmap we supply has an
//   alpha of 0, while the Direct2D/DirectWrite-rendered lyric text stays fully
//   opaque and crisp. This is the same technique desktop widgets (Rainmeter,
//   etc.) use, and it has zero runtime dependencies.
//
// IPC:
//   The host (MusicPlayer, a WinUI 3 app) launches this exe and talks to it over
//   a named pipe (\\.\pipe\MusicPlayerDesktopLyrics) using newline-delimited
//   UTF-8 JSON. Each line is one command, e.g.
//     {"t":"lyric","orig":"...","roma":"...","trans":"..."}
//     {"t":"style","font":24,"color":"#FFFFFFFF","bg":0,"bold":0,"align":"Center"}
//     {"t":"click","on":1}
//     {"t":"show"}  {"t":"hide"}  {"t":"quit"}

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <string>
#include <vector>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cctype>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "kernel32.lib")
#pragma comment(lib, "ole32.lib")

#define PIPE_NAME        L"\\\\.\\pipe\\MusicPlayerDesktopLyrics"
#define WM_APP_RENDER    (WM_APP + 1)
#define WM_APP_SHOW      (WM_APP + 2)
#define WM_APP_HIDE      (WM_APP + 3)

// ---------- minimal JSON (flat objects: string / number / bool) ----------
static void AppendUtf8(std::string& out, unsigned int cp);  // defined later, near Utf8ToWide
struct JsonVal {
    enum Type { NUL, STR, NUM, BOOL, OBJ } type = NUL;
    std::string str;
    double num = 0;
    bool boolean = false;
    std::vector<std::pair<std::string, JsonVal>> members;
};

static void SkipWs(const std::string& s, size_t& i) {
    while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
}
static bool ParseString(const std::string& s, size_t& i, std::string& out) {
    if (i >= s.size() || s[i] != '"') return false;
    i++; out.clear();
    while (i < s.size()) {
        char c = s[i++];
        if (c == '"') return true;
        if (c == '\\' && i < s.size()) {
            char e = s[i++];
            switch (e) {
                case 'n': out += '\n'; break;
                case 't': out += '\t'; break;
                case 'r': out += '\r'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case '/': out += '/';  break;
                case '\\': out += '\\'; break;
                case '"': out += '"';  break;
                case 'u': {
                    if (i + 4 <= s.size()) {
                        unsigned int cp = (unsigned int)strtoul(s.substr(i, 4).c_str(), nullptr, 16);
                        i += 4;
                        // Surrogate pair (e.g. emoji): \uD83D\uDE00
                        if (cp >= 0xD800 && cp <= 0xDBFF && i + 6 <= s.size()
                            && s[i] == '\\' && s[i + 1] == 'u') {
                            unsigned int lo = (unsigned int)strtoul(s.substr(i + 2, 4).c_str(), nullptr, 16);
                            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                i += 6;
                            }
                        }
                        AppendUtf8(out, cp);
                    }
                    break;
                }
                default: out += e; break;
            }
        } else out += c;
    }
    return false;
}
static bool ParseValue(const std::string& s, size_t& i, JsonVal& out);
static bool ParseValue(const std::string& s, size_t& i, JsonVal& out) {
    SkipWs(s, i);
    if (i >= s.size()) return false;
    char c = s[i];
    if (c == '{') {
        i++; out.type = JsonVal::OBJ;
        SkipWs(s, i);
        if (i < s.size() && s[i] == '}') { i++; return true; }
        while (true) {
            SkipWs(s, i);
            std::string key; if (!ParseString(s, i, key)) return false;
            SkipWs(s, i); if (i >= s.size() || s[i] != ':') return false; i++;
            JsonVal val; if (!ParseValue(s, i, val)) return false;
            out.members.push_back({ key, val });
            SkipWs(s, i);
            if (i >= s.size()) return false;
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; break; }
            return false;
        }
        return true;
    } else if (c == '"') {
        std::string str; if (!ParseString(s, i, str)) return false;
        out.type = JsonVal::STR; out.str = str; return true;
    } else if (c == 't' || c == 'f') {
        if (s.compare(i, 4, "true") == 0)  { out.type = JsonVal::BOOL; out.boolean = true;  i += 4; return true; }
        if (s.compare(i, 5, "false") == 0) { out.type = JsonVal::BOOL; out.boolean = false; i += 5; return true; }
        return false;
    } else if (c == '-' || (c >= '0' && c <= '9')) {
        size_t start = i;
        while (i < s.size() && (isdigit((unsigned char)s[i]) || s[i] == '.' || s[i] == '-' || s[i] == 'e' || s[i] == 'E' || s[i] == '+')) i++;
        out.type = JsonVal::NUM; out.num = atof(s.substr(start, i - start).c_str()); return true;
    }
    return false;
}
static const JsonVal* FindMember(const JsonVal& obj, const std::string& key) {
    if (obj.type != JsonVal::OBJ) return nullptr;
    for (const auto& m : obj.members) if (m.first == key) return &m.second;
    return nullptr;
}

// ---------- helpers ----------
static std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w; w.resize(n);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}
// Encode a single Unicode code point into UTF-8 bytes appended to `out`.
static void AppendUtf8(std::string& out, unsigned int cp) {
    if (cp < 0x80) {
        out += (char)cp;
    } else if (cp < 0x800) {
        out += (char)(0xC0 | (cp >> 6));
        out += (char)(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        out += (char)(0xE0 | (cp >> 12));
        out += (char)(0x80 | ((cp >> 6) & 0x3F));
        out += (char)(0x80 | (cp & 0x3F));
    } else {
        out += (char)(0xF0 | (cp >> 18));
        out += (char)(0x80 | ((cp >> 12) & 0x3F));
        out += (char)(0x80 | ((cp >> 6) & 0x3F));
        out += (char)(0x80 | (cp & 0x3F));
    }
}
static float HexByte(const std::string& h, int p) {
    return (float)strtol(h.substr(p, 2).c_str(), nullptr, 16) / 255.0f;
}
static D2D1_COLOR_F ParseColor(const std::string& hex) {
    std::string h = hex;
    if (!h.empty() && h[0] == '#') h = h.substr(1);
    if (h.size() == 8) return D2D1::ColorF(HexByte(h, 2), HexByte(h, 4), HexByte(h, 6), HexByte(h, 0));
    if (h.size() == 6) return D2D1::ColorF(HexByte(h, 0), HexByte(h, 2), HexByte(h, 4), 1.0f);
    return D2D1::ColorF(1, 1, 1, 1);
}

// ---------- globals ----------
struct State {
    std::wstring orig, roma, trans;
    float font = 24.0f;
    D2D1_COLOR_F color = D2D1::ColorF(1, 1, 1, 1);
    float bg = 0.0f;
    bool bold = false;
    bool alignLeft = false;
    bool visible = false;
    bool clickThrough = false;
};
static State g_state;
static CRITICAL_SECTION g_cs;

static HWND g_hwnd = nullptr;
static int g_x = 0, g_y = 0;
static bool g_positioned = false;

static ID2D1Factory* g_d2dFactory = nullptr;
static IDWriteFactory* g_dwriteFactory = nullptr;
static ID2D1DCRenderTarget* g_dcRT = nullptr;
static HBITMAP g_hbm = nullptr;
static HDC g_hdcMem = nullptr;
static int g_bmW = 0, g_bmH = 0;
static float g_dpiScale = 1.0f;

// ---------- rendering ----------
static void EnsureBitmap(int w, int h) {
    if (g_hbm && w == g_bmW && h == g_bmH) return;
    if (g_hbm) { DeleteObject(g_hbm); g_hbm = nullptr; }
    if (g_hdcMem) { DeleteDC(g_hdcMem); g_hdcMem = nullptr; }
    if (g_dcRT) { g_dcRT->Release(); g_dcRT = nullptr; }

    BITMAPINFOHEADER bi = { 0 };
    bi.biSize = sizeof(bi);
    bi.biWidth = w;
    bi.biHeight = -h;            // top-down DIB
    bi.biPlanes = 1;
    bi.biBitCount = 32;
    bi.biCompression = BI_RGB;
    void* bits = nullptr;
    HDC hdc = GetDC(nullptr);
    g_hbm = CreateDIBSection(hdc, (BITMAPINFO*)&bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    ReleaseDC(nullptr, hdc);
    if (!g_hbm) return;
    g_hdcMem = CreateCompatibleDC(nullptr);
    SelectObject(g_hdcMem, g_hbm);
    g_bmW = w; g_bmH = h;

    D2D1_PIXEL_FORMAT pf = { DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED };
    D2D1_RENDER_TARGET_PROPERTIES rp = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT, pf);
    g_d2dFactory->CreateDCRenderTarget(&rp, &g_dcRT);
}

static void Render() {
    if (!g_hwnd) return;
    State st;
    EnterCriticalSection(&g_cs); st = g_state; LeaveCriticalSection(&g_cs);

    struct Line { std::wstring text; float size; float w = 0, h = 0; };
    std::vector<Line> lines;
    if (!st.orig.empty())  lines.push_back({ st.orig,  st.font });
    if (!st.roma.empty())  lines.push_back({ st.roma,  std::max(10.0f, st.font * 0.55f) });
    if (!st.trans.empty()) lines.push_back({ st.trans, std::max(11.0f, st.font * 0.65f) });

    const float gap = 6.0f;
    const float pad = 16.0f;
    const float wrapW = 820.0f;
    const wchar_t* fontFamily = L"Microsoft YaHei";

    // Phase 1: measure (layout width = wrapW so long lines wrap, short lines keep natural width)
    float totalH = 0, maxW = 0;
    for (auto& ln : lines) {
        IDWriteTextFormat* fmt = nullptr;
        g_dwriteFactory->CreateTextFormat(fontFamily, nullptr,
            st.bold ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, ln.size, L"", &fmt);
        IDWriteTextLayout* lay = nullptr;
        if (fmt) g_dwriteFactory->CreateTextLayout(ln.text.c_str(), (UINT32)ln.text.size(), fmt, wrapW, 10000.0f, &lay);
        DWRITE_TEXT_METRICS m = { 0 };
        float w = 10, hgt = ln.size * 1.3f;
        if (lay) { lay->GetMetrics(&m); w = m.widthIncludingTrailingWhitespace; hgt = m.height; }
        ln.w = w; ln.h = hgt;
        totalH += hgt + gap;
        if (w > maxW) maxW = w;
        if (lay) lay->Release();
        if (fmt) fmt->Release();
    }
    if (totalH > 0) totalH -= gap;
    if (maxW > wrapW) maxW = wrapW;
    const float logicalW = maxW + pad * 2.0f;
    const float logicalH = totalH + pad * 2.0f;
    int pixW = (int)(logicalW * g_dpiScale + 0.5f);
    int pixH = (int)(logicalH * g_dpiScale + 0.5f);
    if (pixW < 1) pixW = 1;
    if (pixH < 1) pixH = 1;

    if (st.visible && !g_positioned) {
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        g_x = (sw - pixW) / 2;
        g_y = sh - pixH - 60;
        g_positioned = true;
    }

    SetWindowPos(g_hwnd, HWND_TOPMOST, g_x, g_y, pixW, pixH,
        SWP_NOACTIVATE | (st.visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));

    EnsureBitmap(pixW, pixH);
    if (!g_dcRT || !g_hdcMem) return;

    RECT rc = { 0, 0, pixW, pixH };
    g_dcRT->BindDC(g_hdcMem, &rc);
    g_dcRT->BeginDraw();
    g_dcRT->SetTransform(D2D1::Matrix3x2F::Scale(g_dpiScale, g_dpiScale));
    g_dcRT->Clear(D2D1::ColorF(0, 0, 0, 0));   // transparent

    // Background panel (alpha = bg opacity; invisible at 0)
    if (st.bg > 0.001f) {
        ID2D1SolidColorBrush* bgBrush = nullptr;
        g_dcRT->CreateSolidColorBrush(D2D1::ColorF(0.05f, 0.05f, 0.07f, st.bg), &bgBrush);
        if (bgBrush) {
            D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(D2D1::RectF(0, 0, logicalW, logicalH), 16, 16);
            g_dcRT->FillRoundedRectangle(rr, bgBrush);
            bgBrush->Release();
        }
    }

    // Text (per-pixel alpha, always opaque)
    float y = pad;
    for (auto& ln : lines) {
        IDWriteTextFormat* fmt = nullptr;
        g_dwriteFactory->CreateTextFormat(fontFamily, nullptr,
            st.bold ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, ln.size, L"", &fmt);
        if (fmt) fmt->SetTextAlignment(st.alignLeft ? DWRITE_TEXT_ALIGNMENT_LEADING : DWRITE_TEXT_ALIGNMENT_CENTER);
        IDWriteTextLayout* lay = nullptr;
        if (fmt) g_dwriteFactory->CreateTextLayout(ln.text.c_str(), (UINT32)ln.text.size(), fmt, maxW, 10000.0f, &lay);
        ID2D1SolidColorBrush* tb = nullptr;
        g_dcRT->CreateSolidColorBrush(st.color, &tb);
        if (tb) {
            if (lay) g_dcRT->DrawTextLayout(D2D1::Point2F(pad, y), lay, tb);
            tb->Release();
        }
        y += ln.h + gap;
        if (lay) lay->Release();
        if (fmt) fmt->Release();
    }

    HRESULT hr = g_dcRT->EndDraw();
    if (SUCCEEDED(hr)) {
        POINT ptd = { g_x, g_y };
        SIZE sz = { pixW, pixH };
        POINT pts = { 0, 0 };
        BLENDFUNCTION bf = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
        UpdateLayeredWindow(g_hwnd, nullptr, &ptd, &sz, g_hdcMem, &pts, 0, &bf, ULW_ALPHA);
    }
}

// ---------- command dispatch ----------
static void ApplyCommand(const std::string& line) {
    JsonVal root; size_t i = 0;
    if (!ParseValue(line, i, root)) return;
    const JsonVal* t = FindMember(root, "t");
    if (!t || t->type != JsonVal::STR) return;
    std::string type = t->str;

    if (type == "lyric") {
        auto* a = FindMember(root, "orig");
        auto* r = FindMember(root, "roma");
        auto* tr = FindMember(root, "trans");
        EnterCriticalSection(&g_cs);
        g_state.orig  = Utf8ToWide(a && a->type == JsonVal::STR ? a->str : "");
        g_state.roma  = Utf8ToWide(r && r->type == JsonVal::STR ? r->str : "");
        g_state.trans = Utf8ToWide(tr && tr->type == JsonVal::STR ? tr->str : "");
        LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "style") {
        auto* f = FindMember(root, "font");
        auto* c = FindMember(root, "color");
        auto* b = FindMember(root, "bg");
        auto* bo = FindMember(root, "bold");
        auto* al = FindMember(root, "align");
        EnterCriticalSection(&g_cs);
        if (f && f->type == JsonVal::NUM)  g_state.font = (float)f->num;
        if (c && c->type == JsonVal::STR)  g_state.color = ParseColor(c->str);
        if (b && b->type == JsonVal::NUM)  g_state.bg = (float)b->num;
        if (bo && bo->type == JsonVal::BOOL) g_state.bold = bo->boolean;
        if (al && al->type == JsonVal::STR) g_state.alignLeft = (al->str == "Left");
        LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "click") {
        auto* on = FindMember(root, "on");
        bool v = on && on->type == JsonVal::BOOL ? on->boolean : (on && on->type == JsonVal::NUM ? on->num != 0 : false);
        EnterCriticalSection(&g_cs); g_state.clickThrough = v; LeaveCriticalSection(&g_cs);
        if (g_hwnd) {
            LONG ex = GetWindowLong(g_hwnd, GWL_EXSTYLE);
            if (v) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(g_hwnd, GWL_EXSTYLE, ex);
            SetWindowPos(g_hwnd, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
        }
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "show") {
        EnterCriticalSection(&g_cs); g_state.visible = true; LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "hide") {
        EnterCriticalSection(&g_cs); g_state.visible = false; LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "quit") {
        // ApplyCommand runs on the pipe thread; route the exit to the main
        // (window) thread so its GetMessage loop actually terminates.
        if (g_hwnd) PostMessage(g_hwnd, WM_DESTROY, 0, 0);
    }
}

// ---------- named pipe server (one connection at a time, re-listens) ----------
static DWORD WINAPI PipeThread(LPVOID) {
    while (true) {
        HANDLE hPipe = CreateNamedPipeW(PIPE_NAME,
            PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, nullptr);
        if (hPipe == INVALID_HANDLE_VALUE) break;

        if (ConnectNamedPipe(hPipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            char buf[4096]; DWORD rd; std::string acc;
            while (ReadFile(hPipe, buf, sizeof(buf) - 1, &rd, nullptr) && rd > 0) {
                acc.append(buf, rd);
                size_t pos;
                while ((pos = acc.find('\n')) != std::string::npos) {
                    std::string msg = acc.substr(0, pos);
                    acc.erase(0, pos + 1);
                    if (!msg.empty() && msg.back() == '\r') msg.pop_back();
                    ApplyCommand(msg);
                }
            }
        }
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }
    return 0;
}

// ---------- window proc ----------
static bool g_dragging = false;
static POINT g_dragPrev = { 0, 0 };

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
        case WM_APP_RENDER:
            Render();
            return 0;
        case WM_LBUTTONDOWN:
            if (!g_state.clickThrough) {
                g_dragging = true;
                g_dragPrev.x = (int)(short)LOWORD(l);
                g_dragPrev.y = (int)(short)HIWORD(l);
                SetCapture(h);
            }
            return 0;
        case WM_MOUSEMOVE:
            if (g_dragging) {
                int dx = (int)(short)LOWORD(l) - g_dragPrev.x;
                int dy = (int)(short)HIWORD(l) - g_dragPrev.y;
                g_x += dx; g_y += dy;
                SetWindowPos(h, HWND_TOPMOST, g_x, g_y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            }
            return 0;
        case WM_LBUTTONUP:
            if (g_dragging) { g_dragging = false; ReleaseCapture(); }
            return 0;
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        default:
            return DefWindowProc(h, m, w, l);
    }
}

// ---------- entry ----------
int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    InitializeCriticalSection(&g_cs);

    g_dpiScale = GetDpiForSystem() / 96.0f;

    D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory), (void**)&g_d2dFactory);
    DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)&g_dwriteFactory);

    WNDCLASSW wc = { 0 };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"MusicPlayerDesktopLyrics";
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    RegisterClassW(&wc);

    g_hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        wc.lpszClassName, L"DesktopLyrics",
        WS_POPUP, 0, 0, 10, 10, nullptr, nullptr, hInstance, nullptr);
    if (!g_hwnd) { CoUninitialize(); return 1; }

    ShowWindow(g_hwnd, SW_HIDE);

    CreateThread(nullptr, 0, PipeThread, nullptr, 0, nullptr);

    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    if (g_dcRT) g_dcRT->Release();
    if (g_hbm) DeleteObject(g_hbm);
    if (g_hdcMem) DeleteDC(g_hdcMem);
    if (g_dwriteFactory) g_dwriteFactory->Release();
    if (g_d2dFactory) g_d2dFactory->Release();
    DeleteCriticalSection(&g_cs);
    CoUninitialize();
    return 0;
}
