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
#include <dwmapi.h>
#include <cmath>
#include <string>
#include <vector>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cctype>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "kernel32.lib")
#pragma comment(lib, "ole32.lib")

#define PIPE_NAME        L"\\\\.\\pipe\\MusicPlayerDesktopLyrics"
#define WM_APP_RENDER    (WM_APP + 1)
#define WM_APP_SHOW      (WM_APP + 2)
#define WM_APP_HIDE      (WM_APP + 3)
#define WM_APP_QUIT      (WM_APP + 4)   // real quit: WndProc calls DestroyWindow

// ---------- minimal JSON (flat objects: string / number / bool) ----------
static void AppendUtf8(std::string& out, unsigned int cp);  // defined later, near Utf8ToWide
struct JsonVal {
    enum Type { NUL, STR, NUM, BOOL, OBJ, ARR } type = NUL;
    std::string str;
    double num = 0;
    bool boolean = false;
    std::vector<std::pair<std::string, JsonVal>> members;
    std::vector<JsonVal> items;    // ARR
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
    } else if (c == '[') {
        i++; out.type = JsonVal::ARR;
        SkipWs(s, i);
        if (i < s.size() && s[i] == ']') { i++; return true; }
        while (true) {
            JsonVal val; if (!ParseValue(s, i, val)) return false;
            out.items.push_back(std::move(val));
            SkipWs(s, i);
            if (i >= s.size()) return false;
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; break; }
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
    std::string order = "ORT";     // vertical line order: O=原文 R=罗马音 T=翻译
    bool vertical = false;         // render lines as vertical columns
    int userWidth = 0;             // user-set wrap width (logical px; 0 = auto)
    float font = 24.0f;
    D2D1_COLOR_F color = D2D1::ColorF(1, 1, 1, 1);
    float bg = 0.0f;
    bool bold = false;
    bool alignLeft = false;
    bool visible = false;
    bool clickThrough = false;
    double prog = -1.0;            // word-timed karaoke progress 0..1 (-1 = not word-timed)
    bool paused = false;           // host paused: freeze the sweep, stop extrapolation
    double progPrev = -1.0;        // previous sample (for local interpolation)
    double progRate = 0.0;         // progress per second, from the last two samples
    LARGE_INTEGER progPrevAt{};    // timestamp of the previous sample
    LARGE_INTEGER progAt{};        // timestamp of the latest sample
    // Self-owned playback clock (word-timed lines): the host streams the
    // line's per-word timing once and then only calibrates the clock with
    // low-weight corrections — the sweep is advanced locally from QPC, so
    // sample jitter can no longer kick the sweep position around.
    struct WordTime { double start, dur; int chars; };
    std::vector<WordTime> wtimes;  // relative to line start, seconds; empty = legacy path
    double lineStart = 0.0;        // absolute playback position of the line start, seconds
    double hostSec = -1.0;         // last calibrated host playback position, seconds
    double hostRate = 1.0;         // local clock rate vs wall clock (≈1 while playing)
    LARGE_INTEGER hostSecAt{};     // QPC of the last calibration
    D2D1_COLOR_F accent = D2D1::ColorF(0.19f, 0.76f, 0.49f, 1.f); // karaoke sung color
    D2D1_COLOR_F unsung = D2D1::ColorF(1, 1, 1, 1);               // karaoke unsung color
};
static State g_state;
static CRITICAL_SECTION g_cs;

static HWND g_hwnd = nullptr;
// g_x/g_y = the CENTER of the lyric box (persisted/restored across sessions);
// the box spreads symmetrically around this point as content size changes.
static int g_x = 0, g_y = 0;
static bool g_positioned = false;
static bool g_hover = false;            // mouse over the window (frame visible)
static bool g_hiddenByFullscreen = false;
static bool g_pipeConnected = false;    // host currently attached to the cmd pipe
static FILETIME g_hostGoneAt = { 0, 0 }; // when the host last disconnected
static bool g_resizing = false;         // dragging the right edge to set width
static float g_resizeStartX = 0;
static int g_resizeStartW = 0;
static float g_lastWrapW = 820.0f;      // wrap width actually used last render

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
    // DeleteDC first: the DIB is still selected into the memory DC, so a
    // DeleteObject issued before it would fail and leak the bitmap.
    if (g_hdcMem) { DeleteDC(g_hdcMem); g_hdcMem = nullptr; }
    if (g_hbm) { DeleteObject(g_hbm); g_hbm = nullptr; }
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

// Length in code units of the grapheme-ish cluster at i: one surrogate pair
// plus trailing combining marks / variation selectors / ZWJ sequences.
static size_t ClusterLen(const std::wstring& t, size_t i) {
    size_t len = 1;
    if (i + 1 < t.size() && IS_HIGH_SURROGATE(t[i]) && IS_LOW_SURROGATE(t[i + 1]))
        len = 2;
    while (i + len < t.size()) {
        wchar_t c = t[i + len];
        bool combining = (c >= 0x0300 && c <= 0x036F) || (c >= 0x3099 && c <= 0x309A) ||
                         c == 0xFE0F || c == 0xFE0E || c == 0x200D;
        if (!combining) break;
        len++;
    }
    return len;
}

// ---------- karaoke interpolation ----------
// Local 60 fps extrapolation of the karaoke progress between pipe samples:
// the sweep then runs as smoothly as the in-app panel even though the host
// only streams updates at ~25 Hz over the pipe.
static double EffectiveProg() {
    if (g_state.prog < 0)
        return -1.0;
    // Host paused: show the frozen sample position — extrapolating here is
    // exactly the "sweep keeps running forward after pause" bug.
    if (g_state.paused)
        return g_state.prog;
    LARGE_INTEGER now{}, freq{};
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);

    // Preferred path: the host shipped this line's per-word timing, so the
    // sweep position is computed locally from the self-owned playback clock
    // (calibrated by low-weight host corrections, advanced by QPC between
    // them). The 25 Hz sample jitter never touches the position directly.
    // The whole branch holds g_cs: wtimes is a vector the pipe thread swaps
    // wholesale on line changes, so lock-free reads here would race it.
    EnterCriticalSection(&g_cs);
    if (!g_state.wtimes.empty() && g_state.hostSec >= 0.0) {
        double clock = g_state.hostSec;
        if (!g_state.paused && g_state.hostSecAt.QuadPart > 0 && freq.QuadPart > 0) {
            double dt = (double)(now.QuadPart - g_state.hostSecAt.QuadPart) / (double)freq.QuadPart;
            clock += g_state.hostRate * dt;
        }
        double tIn = clock - g_state.lineStart;
        // Same formula the host panel uses: character-count space, linear
        // inside the current word.
        int total = 0;
        for (const auto& w : g_state.wtimes) total += w.chars;
        double result;
        if (total <= 0) {
            result = g_state.prog;
        } else if (tIn <= 0) {
            result = 0.0;
        } else {
            double done = 0;
            for (const auto& w : g_state.wtimes) {
                if (tIn >= w.start + w.dur) {
                    done += w.chars;                   // word fully sung
                } else if (tIn > w.start) {
                    double frac = w.dur > 0 ? (tIn - w.start) / w.dur : 1.0;
                    if (frac > 1.0) frac = 1.0;
                    done += w.chars * frac;
                    break;                             // inside this word
                } else {
                    break;                             // before this word
                }
            }
            result = (double)done / (double)total;
            if (result > 1) result = 1;
        }
        LeaveCriticalSection(&g_cs);
        return result;
    }
    LeaveCriticalSection(&g_cs);

    // Legacy path (no word timing shipped): extrapolate the last prog sample
    // with the measured rate, capped at half a second of coasting so a
    // stalled host cannot push the sweep past the line.
    double dt = (double)(now.QuadPart - g_state.progAt.QuadPart) / (double)freq.QuadPart;
    double coast = dt > 0 ? g_state.progRate * dt : 0.0;
    if (coast > 0.5) coast = 0.5;
    if (coast < -0.5) coast = -0.5;
    double p = g_state.prog + coast;
    if (p < 0) p = 0;
    if (p > 1) p = 1;
    return p;
}

// ---------- smoothed display progress ----------
// The host streams progress samples at ~25 Hz; feeding each one straight
// into the sweep makes every sample that disagrees with the local
// extrapolation show up as a visible jump: the smoothed rate lags the real
// per-word speed inside a line, and pipe/timer jitter moves the correction
// around. The window thread therefore keeps a display position that eases
// toward the extrapolated target with a ~28 ms time constant — corrections
// become a slight acceleration the eye cannot see, and the overshoot-pop of
// a frame that was late because UpdateLayeredWindow blocked (50-80 ms under
// DWM) lands softly too. Window-thread state; no locking.
static double g_dispProg = -1.0;
static std::wstring g_dispLO, g_dispLR, g_dispLT;   // line signature at reset
static LARGE_INTEGER g_dispAt{};
static double DisplayProgress() {
    double target = EffectiveProg();
    if (target < 0) {                   // not a word-timed line
        g_dispProg = -1.0;
        g_dispLO.clear(); g_dispLR.clear(); g_dispLT.clear();
        return target;
    }
    if (g_state.paused) {               // frozen: display exactly the sample
        g_dispProg = target;
        return target;
    }
    EnterCriticalSection(&g_cs);
    bool lineChanged = g_dispLO != g_state.orig || g_dispLR != g_state.roma
                    || g_dispLT != g_state.trans;
    if (lineChanged) {
        g_dispLO = g_state.orig; g_dispLR = g_state.roma; g_dispLT = g_state.trans;
    }
    LeaveCriticalSection(&g_cs);
    LARGE_INTEGER now{}, freq{};
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    if (lineChanged) {                  // new line: snap, never ease across
        g_dispProg = target;
        g_dispAt = now;
        return target;
    }
    double dt = g_dispAt.QuadPart > 0
        ? (double)(now.QuadPart - g_dispAt.QuadPart) / (double)freq.QuadPart : 0.0;
    g_dispAt = now;
    // Real seek, or the render stalled long enough for easing to be pointless
    // (host resumed after a pause uses the same path): snap.
    if (dt <= 0 || dt > 0.25 || target - g_dispProg > 0.12 || g_dispProg - target > 0.12) {
        g_dispProg = target;
        return target;
    }
    double a = 1.0 - exp(-dt / 0.028);
    // A late frame (UpdateLayeredWindow blocked 50-80 ms under DWM) makes the
    // ease factor jump to ~0.95 — the catch-up would land as a pop. Cap the
    // per-frame correction so a stalled frame recovers over two or three
    // frames as a slight acceleration instead.
    if (a > 0.5) a = 0.5;
    double d = g_dispProg + (target - g_dispProg) * a;
    if (d > target) d = target;                 // never show past the sung position
    if (target - d > 0.06) d = target - 0.06;   // cap the lag after a late frame
    if (d < 0) d = 0;
    if (d > 1) d = 1;
    g_dispProg = d;
    return d;
}

// ---------- per-role text layout cache ----------
// Rebuilding the DirectWrite text format + layout every frame (measure and
// draw run per frame at 60 fps) cost measurable CPU on long wrapped lines
// and showed up as judder. The cache rebuilds only when the text, style or
// layout width actually changes. Only the window thread touches these, so
// no locking is needed.
static const wchar_t* kFontFamily = L"Microsoft YaHei";
struct LayoutCache {
    IDWriteTextLayout* lay = nullptr;
    DWRITE_TEXT_METRICS m = {};
    std::wstring text;
    float size = 0.0f, width = 0.0f;
    bool bold = false, left = false;
    bool Matches(const std::wstring& t, float sz, bool b, float w, bool l) const {
        return lay != nullptr && text == t && size == sz && bold == b && width == w && left == l;
    }
    void Release() {
        if (lay) { lay->Release(); lay = nullptr; }
        text.clear(); m = {};
    }
};
static LayoutCache g_layO, g_layR, g_layT;

static IDWriteTextLayout* GetLayout(LayoutCache& c, const std::wstring& text,
                                    float size, bool bold, float maxW, bool alignLeft)
{
    if (c.Matches(text, size, bold, maxW, alignLeft))
        return c.lay;
    c.Release();
    IDWriteTextFormat* fmt = nullptr;
    g_dwriteFactory->CreateTextFormat(kFontFamily, nullptr,
        bold ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"", &fmt);
    if (!fmt) return nullptr;
    fmt->SetTextAlignment(alignLeft ? DWRITE_TEXT_ALIGNMENT_LEADING : DWRITE_TEXT_ALIGNMENT_CENTER);
    g_dwriteFactory->CreateTextLayout(text.c_str(), (UINT32)text.size(), fmt, maxW, 10000.0f, &c.lay);
    fmt->Release();
    if (!c.lay) return nullptr;
    c.lay->GetMetrics(&c.m);
    c.text = text; c.size = size; c.bold = bold; c.width = maxW; c.left = alignLeft;
    return c.lay;
}

// ---------- vsync render pump ----------
// A WM_TIMER at 16 ms fires at the system timer granularity (~15.6 ms with
// wide jitter whenever any window is busy), so the karaoke sweep visibly
// stuttered. This thread instead posts one render per DWM composition cycle
// (DwmFlush waits for vsync) while a word-timed line is playing, and idles
// otherwise — static text only needs the event-driven renders from commands.
// g_renderPending keeps at most one frame queued: if the window thread
// renders slower than the display refreshes, the pump auto-throttles
// instead of flooding the message queue.
static volatile LONG g_renderPending = 0;
static HANDLE g_renderQuit = nullptr;

static bool KaraokeActive() {
    EnterCriticalSection(&g_cs);
    // Vertical mode draws no karaoke sweep (plain single-color columns), so it
    // must not keep the 60 fps pump running: its picture only changes when a
    // lyric/style command arrives, and those post their own render.
    bool a = g_state.prog >= 0.0 && !g_state.vertical &&
             g_state.visible && !g_state.paused;
    LeaveCriticalSection(&g_cs);
    return a;
}

static DWORD WINAPI RenderPumpThread(LPVOID) {
    LARGE_INTEGER freq{};
    QueryPerformanceFrequency(&freq);
    while (WaitForSingleObject(g_renderQuit, 25) != WAIT_OBJECT_0) {
        if (!KaraokeActive()) continue;
        while (KaraokeActive() && WaitForSingleObject(g_renderQuit, 0) != WAIT_OBJECT_0) {
            // DwmFlush can return S_OK instantly when the call lands inside a
            // just-completed composition slot — and once the render loop
            // settles into that rhythm it stays phase-locked. Sleeping 1 ms
            // here would pace frames by the system timer tick (1-15.6 ms of
            // jitter, visible as uneven sweep speed), so flush a second time
            // instead: after an instant return the next call genuinely waits
            // for the following composition. Two instant returns in a row
            // mean there is no real composition beat (remote session,
            // headless) — fall back to a fixed beat.
            LARGE_INTEGER t0{}, t1{};
            QueryPerformanceCounter(&t0);
            HRESULT hr = DwmFlush();
            QueryPerformanceCounter(&t1);
            double waited = freq.QuadPart > 0
                ? (double)(t1.QuadPart - t0.QuadPart) / (double)freq.QuadPart : 1.0;
            if (FAILED(hr)) {
                Sleep(15);
            } else if (waited < 0.004) {
                QueryPerformanceCounter(&t0);
                hr = DwmFlush();
                QueryPerformanceCounter(&t1);
                double w2 = freq.QuadPart > 0
                    ? (double)(t1.QuadPart - t0.QuadPart) / (double)freq.QuadPart : 1.0;
                if (FAILED(hr) || w2 < 0.002)
                    Sleep(15);
            }
            if (InterlockedCompareExchange(&g_renderPending, 1, 0) == 0)
                PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
        }
    }
    return 0;
}



// Per-monitor DPI of the overlay window. Cheap enough to call every frame —
// a purely cached scale would go stale when the box crosses monitors.
static float WindowDpiScale() {
    UINT dpi = g_hwnd ? GetDpiForWindow(g_hwnd) : 0;
    if (dpi == 0) dpi = GetDpiForSystem();
    return (float)dpi / 96.0f;
}

static void Render() {
    if (!g_hwnd) return;
    g_dpiScale = WindowDpiScale();   // track the monitor the box is on now
    State st;
    EnterCriticalSection(&g_cs); st = g_state; LeaveCriticalSection(&g_cs);

    struct Line { std::wstring text; float size; float w = 0, h = 0; char role = 'O'; };
    std::vector<Line> lines;
    auto addLine = [&](const std::wstring& text, float size, char role) {
        if (!text.empty()) lines.push_back({ text, size, 0, 0, role });
    };
    for (char role : st.order) {
        if (role == 'O')      addLine(st.orig,  st.font, 'O');
        else if (role == 'R') addLine(st.roma,  std::max(10.0f, st.font * 0.55f), 'R');
        else if (role == 'T') addLine(st.trans, std::max(11.0f, st.font * 0.65f), 'T');
    }

    const float gap = 6.0f;
    const float pad = 16.0f;
    const float wrapW = st.userWidth > 0 ? (float)st.userWidth : 820.0f;
    g_lastWrapW = wrapW;
    const wchar_t* fontFamily = L"Microsoft YaHei";

    float totalH = 0, maxW = 0;
    // Vertical mode: each line becomes one or more vertical character
    // columns (chars stacked top-to-bottom), columns laid left-to-right.
    struct VColumn { std::vector<std::wstring> glyphs; float size; };
    std::vector<VColumn> vcols;
    if (st.vertical) {
        int sh = GetSystemMetrics(SM_CYSCREEN);
        const float maxColH = (sh / g_dpiScale) - 140.0f;
        for (char role : st.order) {
            std::wstring text =
                role == 'O' ? st.orig  :
                role == 'R' ? st.roma  :
                role == 'T' ? st.trans : L"";
            if (text.empty()) continue;
            float size = role == 'O' ? st.font :
                         role == 'R' ? std::max(10.0f, st.font * 0.55f) :
                                       std::max(11.0f, st.font * 0.65f);
            int perCol = (int)std::max(1.0f, maxColH / (size * 1.16f));
            // Walk grapheme-ish clusters so surrogate pairs (emoji) and
            // combining marks are never split across columns.
            size_t pos = 0;
            while (pos < text.size()) {
                size_t take = 0, glyphs = 0;
                while (pos + take < text.size() && glyphs < (size_t)perCol) {
                    size_t cl = ClusterLen(text, pos + take);
                    take += cl; glyphs++;
                }
                VColumn col; col.size = size;
                size_t off = 0;
                while (off < take) {
                    size_t cl = ClusterLen(text, pos + off);
                    col.glyphs.push_back(text.substr(pos + off, cl));
                    off += cl;
                }
                vcols.push_back(std::move(col));
                pos += take;
            }
        }
        for (auto& c : vcols) {
            maxW += st.font * 1.35f + gap;
            totalH = std::max(totalH, (float)c.glyphs.size() * c.size * 1.16f);
        }
        if (maxW > 0) maxW -= gap; // no trailing gap
    } else
    for (auto& ln : lines) {
        LayoutCache& c = ln.role == 'O' ? g_layO : (ln.role == 'R' ? g_layR : g_layT);
        IDWriteTextLayout* lay = GetLayout(c, ln.text, ln.size, st.bold, wrapW, st.alignLeft);
        float w = 10, hgt = ln.size * 1.3f;
        if (lay) { w = c.m.widthIncludingTrailingWhitespace; hgt = c.m.height; }
        ln.w = w; ln.h = hgt;
        totalH += hgt + gap;
        if (w > maxW) maxW = w;
    }
    if (totalH > 0) totalH -= gap;
    if (!st.vertical && maxW > wrapW) maxW = wrapW;
    const float logicalW = maxW + pad * 2.0f;
    const float logicalH = totalH + pad * 2.0f;
    int pixW = (int)(logicalW * g_dpiScale + 0.5f);
    int pixH = (int)(logicalH * g_dpiScale + 0.5f);
    if (pixW < 1) pixW = 1;
    if (pixH < 1) pixH = 1;

    if (st.visible && !g_positioned) {
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        g_x = sw / 2;
        g_y = sh - pixH / 2 - 60;
        g_positioned = true;
    }

    // g_x/g_y are the CENTER of the box: the window always spreads
    // symmetrically around this point as its content size changes, and the
    // exact same point is persisted/restored across sessions.
    SetWindowPos(g_hwnd, HWND_TOPMOST, g_x - pixW / 2, g_y - pixH / 2, pixW, pixH,
        SWP_NOACTIVATE | ((st.visible && !g_hiddenByFullscreen) ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));

    EnsureBitmap(pixW, pixH);
    if (!g_dcRT || !g_hdcMem) return;

    RECT rc = { 0, 0, pixW, pixH };
    g_dcRT->BindDC(g_hdcMem, &rc);
    g_dcRT->BeginDraw();
    g_dcRT->SetTransform(D2D1::Matrix3x2F::Scale(g_dpiScale, g_dpiScale));
    g_dcRT->Clear(D2D1::ColorF(0, 0, 0, 0));   // transparent

    // Background panel (alpha = bg opacity; invisible at 0). While hovered,
    // force a minimum alpha: layered-window hit-testing is per-pixel, and at
    // bg = 0 only the glyph strokes were hoverable — the hover state flickered
    // on/off between glyphs and the resize zone was unreachable.
    float bgA = st.bg;
    if (g_hover && !st.clickThrough && bgA < 0.15f) bgA = 0.15f;
    if (bgA > 0.001f) {
        ID2D1SolidColorBrush* bgBrush = nullptr;
        g_dcRT->CreateSolidColorBrush(D2D1::ColorF(0.05f, 0.05f, 0.07f, bgA), &bgBrush);
        if (bgBrush) {
            D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(D2D1::RectF(0, 0, logicalW, logicalH), 16, 16);
            g_dcRT->FillRoundedRectangle(rr, bgBrush);
            bgBrush->Release();
        }
    }

    // Text (per-pixel alpha, always opaque)
    // Legibility helpers: lyrics float over arbitrary desktop content, so the
    // glyphs get a soft drop shadow plus a dark outline around every stroke.
    // Both are cheap re-draws of the SAME cached layout (only the brush and
    // offset change); the outline width scales with the font so it reads as
    // a hairline on small companion rows and a bold rim on the main line.
    const float outlineW = std::max(1.4f, st.font * 0.045f);
    const D2D1_POINT_2F outlineOffs[8] = {
        { outlineW, 0 }, { -outlineW, 0 }, { 0, outlineW }, { 0, -outlineW },
        { outlineW * 0.7071f, outlineW * 0.7071f },
        { -outlineW * 0.7071f, outlineW * 0.7071f },
        { outlineW * 0.7071f, -outlineW * 0.7071f },
        { -outlineW * 0.7071f, -outlineW * 0.7071f },
    };
    ID2D1SolidColorBrush* shadowBrush = nullptr;
    ID2D1SolidColorBrush* outlineBrush = nullptr;
    g_dcRT->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 0.34f), &shadowBrush);
    g_dcRT->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 0.58f), &outlineBrush);
    auto drawShadowed = [&](IDWriteTextLayout* l, ID2D1SolidColorBrush* body, float x, float y) {
        if (shadowBrush)
            g_dcRT->DrawTextLayout(D2D1::Point2F(x, y + outlineW * 1.3f), l, shadowBrush);
        if (outlineBrush) {
            for (auto& o : outlineOffs)
                g_dcRT->DrawTextLayout(D2D1::Point2F(x + o.x, y + o.y), l, outlineBrush);
        }
        g_dcRT->DrawTextLayout(D2D1::Point2F(x, y), l, body);
    };

    if (st.vertical) {
        const float colW = st.font * 1.35f;
        ID2D1SolidColorBrush* tb = nullptr;
        g_dcRT->CreateSolidColorBrush(st.color, &tb);
        // One TextFormat per distinct size (at most three roles) instead of
        // one object per drawn character.
        IDWriteTextFormat* fmtO = nullptr, *fmtR = nullptr, *fmtT = nullptr;
        auto mkFmt = [&](float size) -> IDWriteTextFormat* {
            IDWriteTextFormat* f = nullptr;
            g_dwriteFactory->CreateTextFormat(fontFamily, nullptr,
                st.bold ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"", &f);
            if (f) f->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
            return f;
        };
        auto fmtFor = [&](float size) -> IDWriteTextFormat* {
            if (size == st.font) { if (!fmtO) fmtO = mkFmt(size); return fmtO; }
            if (size == std::max(10.0f, st.font * 0.55f)) { if (!fmtR) fmtR = mkFmt(size); return fmtR; }
            if (!fmtT) fmtT = mkFmt(size);
            return fmtT;
        };
        float x = pad;
        for (auto& c : vcols) {
            const float charH = c.size * 1.16f;
            IDWriteTextFormat* fmt = fmtFor(c.size);
            if (tb && fmt) {
                for (UINT32 i = 0; i < c.glyphs.size(); i++) {
                    IDWriteTextLayout* lay = nullptr;
                    g_dwriteFactory->CreateTextLayout(
                        c.glyphs[i].c_str(), (UINT32)c.glyphs[i].size(), fmt, colW, charH * 1.3f, &lay);
                    if (lay) drawShadowed(lay, tb, x, pad + i * charH);
                    if (lay) lay->Release();
                }
            }
            x += colW + gap;
        }
        if (fmtO) fmtO->Release();
        if (fmtR) fmtR->Release();
        if (fmtT) fmtT->Release();
        if (tb) tb->Release();
    } else {
    float y = pad;
    for (auto& ln : lines) {
        LayoutCache& c = ln.role == 'O' ? g_layO : (ln.role == 'R' ? g_layR : g_layT);
        // Draw with the SAME cached layout the measure pass built (wrapW
        // wide). Requesting a second layout at maxW here missed the cache on
        // every frame and rebuilt format + layout at 60 fps. When the box is
        // narrower than the wrap width (maxW < wrapW) shift the layout left
        // by (wrapW - maxW) / 2 so CENTER-aligned glyphs still land at
        // pad + (maxW - textW) / 2 — exactly where a maxW-wide layout would
        // have put them, and where the sweep clip starts.
        IDWriteTextLayout* lay = GetLayout(c, ln.text, ln.size, st.bold, wrapW, st.alignLeft);
        float drawX = pad;
        if (!st.alignLeft) drawX += (maxW - wrapW) * 0.5f;
        ID2D1SolidColorBrush* tb = nullptr;
        // Karaoke original line: the base layer is the unsung color.
        g_dcRT->CreateSolidColorBrush(
            (st.prog >= 0.0 && ln.role == 'O') ? st.unsung : st.color, &tb);

        // Karaoke sweep: when a word-timed line is playing (prog >= 0), draw
        // the whole line in the unsung color, then re-draw it clipped to the
        // sung prefix in the accent color. Applies to orig/roma/trans alike —
        // companion lines ride the same progress as their main line.
        bool karaoke = st.prog >= 0.0 && ln.role == 'O';
        float doneW = 0.0f;
        float textX = drawX;
        if (karaoke && lay) {
            // Centred glyphs start at (layoutWidth - textWidth) / 2, not at
            // the box edge — the sweep clip must start there too, or short
            // centred lines lit up late and then raced through.
            if (!st.alignLeft)
                textX += (wrapW - c.m.widthIncludingTrailingWhitespace) * 0.5f;
            UINT32 maxClusters = (UINT32)ln.text.size() + 1;
            std::vector<DWRITE_CLUSTER_METRICS> clusters(maxClusters);
            UINT32 actual = 0;
            if (SUCCEEDED(lay->GetClusterMetrics(clusters.data(), maxClusters, &actual)) && actual > 0) {
                // Sub-pixel sweep: progress maps onto the cluster sequence as
                // a float — whole clusters plus the fractional slice of the
                // current one. The previous char-rounded clip width made the
                // sweep jump a whole character at a time, the main source of
                // the "not flowing" feel reported on desktop lyrics. The
                // position comes from the display smoother, not straight from
                // the extrapolated target, so 25 Hz sample corrections and
                // late-frame recoveries ease in instead of popping.
                double exact = DisplayProgress() * (double)actual;
                UINT32 full = (UINT32)exact;
                if (full > actual) full = actual;
                double frac = exact - (double)full;
                for (UINT32 i = 0; i < full; i++) doneW += clusters[i].width;
                if (full < actual) doneW += (float)(frac * clusters[full].width);
            }
        }

        if (tb) {
            if (lay) drawShadowed(lay, tb, drawX, y);

            if (karaoke && lay && doneW > 0.5f) {
                ID2D1SolidColorBrush* hb = nullptr;
                g_dcRT->CreateSolidColorBrush(st.accent, &hb);
                if (hb) {
                    D2D1_RECT_F lineRect = D2D1::RectF(textX, y, textX + doneW, y + ln.h);
                    g_dcRT->PushAxisAlignedClip(lineRect, D2D1_ANTIALIAS_MODE_ALIASED);
                    drawShadowed(lay, hb, drawX, y);
                    g_dcRT->PopAxisAlignedClip();
                    hb->Release();
                }
            }
            tb->Release();          // was leaked once per line per frame
        }
        y += ln.h + gap;
    }
    }

    if (shadowBrush) shadowBrush->Release();
    if (outlineBrush) outlineBrush->Release();

    // Hover frame: thin border + corner resize grips, shown only while the
    // mouse is over the window and never in click-through mode.
    bool showFrame = g_hover && !st.clickThrough;
    if (showFrame) {
        ID2D1SolidColorBrush* fb = nullptr;
        g_dcRT->CreateSolidColorBrush(D2D1::ColorF(1.0f, 1.0f, 1.0f, 0.75f), &fb);
        if (fb) {
            float m = 1.0f;
            g_dcRT->DrawRoundedRectangle(
                D2D1::RoundedRect(D2D1::RectF(m, m, logicalW - m, logicalH - m), 14, 14), fb, 1.6f);
            float gs = 9.0f;
            D2D1_ROUNDED_RECT grip = D2D1::RoundedRect(D2D1::RectF(0, 0, gs, gs), 2.5f, 2.5f);
            float cs[2] = { 4.0f, logicalW - gs - 4.0f };
            float rs[2] = { 4.0f, logicalH - gs - 4.0f };
            for (float gx : cs) for (float gy : rs) {
                grip.rect.left = gx; grip.rect.top = gy;
                grip.rect.right = gx + gs; grip.rect.bottom = gy + gs;
                g_dcRT->FillRoundedRectangle(grip, fb);
            }
            fb->Release();
        }
    }

    HRESULT hr = g_dcRT->EndDraw();
    if (hr == (HRESULT)D2DERR_RECREATE_TARGET) {
        // Device lost (driver reset etc.): drop the target so the next render
        // recreates it instead of spinning on a dead one forever.
        g_dcRT->Release(); g_dcRT = nullptr;
        // Same GDI rule as EnsureBitmap: drop the DC (deselecting the DIB)
        // before deleting the bitmap, or DeleteObject silently fails.
        if (g_hdcMem) { DeleteDC(g_hdcMem); g_hdcMem = nullptr; }
        if (g_hbm) { DeleteObject(g_hbm); g_hbm = nullptr; }
        g_bmW = g_bmH = 0;
        return;
    }
    if (SUCCEEDED(hr)) {
        POINT ptd = { g_x - pixW / 2, g_y - pixH / 2 };
        SIZE sz = { pixW, pixH };
        POINT pts = { 0, 0 };
        BLENDFUNCTION bf = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
        UpdateLayeredWindow(g_hwnd, nullptr, &ptd, &sz, g_hdcMem, &pts, 0, &bf, ULW_ALPHA);
    }
}

// ---------- position reports over a dedicated OUTBOUND pipe ----------
// Never write on the main (inbound) pipe handle: its pipe thread sits in a
// blocking ReadFile, and a second simultaneous operation on the same
// byte-mode handle deadlocks both threads. The report pipe is written only
// by the window thread and read only by the host — one direction each.
static const wchar_t* RPT_PIPE_NAME = L"\\\\.\\pipe\\MusicPlayerDesktopLyricsRpt";
static CRITICAL_SECTION g_pipeCs;
static HANDLE g_rptPipe = nullptr;     // current report-pipe instance

static void PipeWrite(const std::string& s) {
    EnterCriticalSection(&g_pipeCs);
    HANDLE h = g_rptPipe;
    LeaveCriticalSection(&g_pipeCs);
    if (!h || h == INVALID_HANDLE_VALUE) return;

    DWORD wr = 0;
    if (!WriteFile(h, s.c_str(), (DWORD)s.size(), &wr, nullptr) || wr == 0) {
        // Host went away: clear so the report thread re-listens and recycles.
        EnterCriticalSection(&g_pipeCs);
        if (g_rptPipe == h) g_rptPipe = nullptr;
        LeaveCriticalSection(&g_pipeCs);
    }
}

// Report the current overlay position back to the host so it can persist it.
static void ReportPosition() {
    char b[96];
    snprintf(b, sizeof b, "{\"t\":\"pos\",\"x\":%ld,\"y\":%ld}\n", (long)g_x, (long)g_y);
    PipeWrite(b);
}

// Report the user-set wrap width so the host can persist it.
static void ReportSize() {
    char b[96];
    EnterCriticalSection(&g_cs);
    const int w = g_state.userWidth;
    LeaveCriticalSection(&g_cs);
    snprintf(b, sizeof b, "{\"t\":\"size\",\"w\":%d}\n", w);
    PipeWrite(b);
}

// ---------- fullscreen auto-hide ----------
// True when the foreground window covers its whole monitor (a game or any
// borderless fullscreen app). The desktop lyrics hide while that is the case.
static bool IsForegroundFullscreen() {
    HWND f = GetForegroundWindow();
    if (!f || f == g_hwnd) return false;
    if (f == GetShellWindow()) return false;
    // The desktop is a SHELLDLL_DefView hosted in a Progman/WorkerW window.
    if (FindWindowExW(f, nullptr, L"SHELLDLL_DefView", nullptr)) return false;
    LONG ex = GetWindowLongW(f, GWL_EXSTYLE);
    if (ex & WS_EX_TOOLWINDOW) return false;
    // A MAXIMIZED normal window also covers its monitor (rect + invisible
    // borders) but keeps WS_CAPTION; true fullscreen (games, video) drops it.
    // Without this check the lyrics hid whenever any maximized app was front.
    LONG style = GetWindowLongW(f, GWL_STYLE);
    if (style & WS_CAPTION) return false;
    HMONITOR mon = MonitorFromWindow(f, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(mon, &mi)) return false;
    RECT r;
    if (!GetWindowRect(f, &r)) return false;
    return r.left <= mi.rcMonitor.left + 2 && r.right >= mi.rcMonitor.right - 2 &&
           r.top <= mi.rcMonitor.top + 2 && r.bottom >= mi.rcMonitor.bottom - 2;
}

static void UpdateFullscreenHide() {
    bool fs = IsForegroundFullscreen();
    bool visible;
    EnterCriticalSection(&g_cs);
    visible = g_state.visible;
    LeaveCriticalSection(&g_cs);
    if (fs && visible && !g_hiddenByFullscreen) {
        g_hiddenByFullscreen = true;
        ShowWindow(g_hwnd, SW_HIDE);
    } else if (!fs && g_hiddenByFullscreen) {
        g_hiddenByFullscreen = false;
        bool visible;
        EnterCriticalSection(&g_cs);
        visible = g_state.visible;
        LeaveCriticalSection(&g_cs);
        if (visible) ShowWindow(g_hwnd, SW_SHOW); // host may have hidden it meanwhile
    }
}

static DWORD g_hostPid = 0;   // spawning process id, from --host-pid

// Only the host (our parent process) may talk to / read from the pipes.
static bool ClientIsHost(HANDLE hPipe) {
    if (g_hostPid == 0) return true; // legacy launch without the argument
    ULONG pid = 0;
    if (!GetNamedPipeClientProcessId(hPipe, &pid)) return false;
    return pid == g_hostPid;
}

static DWORD WINAPI ReportPipeThread(LPVOID) {
    while (true) {
        HANDLE h = CreateNamedPipeW(RPT_PIPE_NAME,
            PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) { Sleep(500); continue; }

        if (ConnectNamedPipe(h, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            if (!ClientIsHost(h)) {
                DisconnectNamedPipe(h);
                CloseHandle(h);
                continue;
            }
            EnterCriticalSection(&g_pipeCs);
            g_rptPipe = h;
            LeaveCriticalSection(&g_pipeCs);

            // Park until a failed write clears g_rptPipe (host disconnected),
            // then recycle the handle and listen for the next host session.
            while (true) {
                Sleep(250);
                EnterCriticalSection(&g_pipeCs);
                HANDLE cur = g_rptPipe;
                LeaveCriticalSection(&g_pipeCs);
                if (cur != h) break;
            }
        }

        DisconnectNamedPipe(h);
        CloseHandle(h);
    }
    return 0;
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
        auto* sg = FindMember(root, "prog");
        double newProg = (sg && sg->type == JsonVal::NUM) ? sg->num : -1.0;
        // NaN slips through every comparison below (all false) and would
        // reach (UINT32)NaN in the render sweep — undefined behavior. Garbage
        // and negative samples mean "not word-timed" (-1, the protocol value).
        if (!std::isfinite(newProg) || newProg < 0.0) newProg = -1.0;
        // Self-owned clock fields: "s" = the host playback position this
        // sample was taken at (seconds), "ls" = the line start position,
        // "wt" = flat per-word timing [start, dur, chars, ...] relative to
        // the line start. All optional — an older host (or a non-word-timed
        // line) simply omits them and the legacy sample path keeps working.
        auto* ss = FindMember(root, "s");
        double posSec = (ss && ss->type == JsonVal::NUM) ? ss->num : -1.0;
        if (!std::isfinite(posSec) || posSec < 0.0) posSec = -1.0;
        auto* ls = FindMember(root, "ls");
        double lineStartSec = (ls && ls->type == JsonVal::NUM) ? ls->num : 0.0;
        if (!std::isfinite(lineStartSec) || lineStartSec < 0.0) lineStartSec = 0.0;
        auto* wt = FindMember(root, "wt");
        std::vector<State::WordTime> newTimes;
        if (wt && wt->type == JsonVal::ARR && wt->items.size() >= 3) {
            for (size_t k = 0; k + 2 < wt->items.size(); k += 3) {
                const JsonVal& s0 = wt->items[k];
                const JsonVal& d0 = wt->items[k + 1];
                const JsonVal& c0 = wt->items[k + 2];
                if (s0.type != JsonVal::NUM || d0.type != JsonVal::NUM || c0.type != JsonVal::NUM)
                    continue;
                if (!std::isfinite(s0.num) || !std::isfinite(d0.num)) continue;
                int chars = (int)c0.num;
                if (chars <= 0) continue;
                newTimes.push_back({ s0.num, d0.num > 0 ? d0.num : 0.0, chars });
            }
        }
        // Keep the last two samples so the vsync pump can interpolate the
        // sweep locally (25 Hz pipe updates judder; 60 Hz local extrapolation
        // is as smooth as the in-app panel).
        LARGE_INTEGER now{}, freq{};
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&freq);

        auto newOrig = Utf8ToWide(a && a->type == JsonVal::STR ? a->str : "");
        auto newRoma = Utf8ToWide(r && r->type == JsonVal::STR ? r->str : "");
        auto newTrans = Utf8ToWide(tr && tr->type == JsonVal::STR ? tr->str : "");

        EnterCriticalSection(&g_cs);
        // Calibrate the self-owned playback clock. The correction is split:
        // a small fraction of the error snaps into the position (invisible
        // at ≤ a few ms), the rest trims the clock rate — so host position
        // read jitter can no longer kick the sweep around, it only bends the
        // local clock slightly toward the true one.
        if (posSec >= 0.0) {
            double predicted = g_state.hostSec;
            if (g_state.hostSec < 0.0) {
                predicted = posSec;
            } else if (g_state.hostSecAt.QuadPart > 0 && freq.QuadPart > 0) {
                double dtc = (double)(now.QuadPart - g_state.hostSecAt.QuadPart) / (double)freq.QuadPart;
                if (!g_state.paused) predicted += g_state.hostRate * dtc;
            }
            double err = posSec - predicted;
            if (!g_state.paused && (fabs(err) > 0.35 || g_state.hostSec < 0.0)) {
                // Seek (or first sample): adopt the host position outright.
                g_state.hostSec = posSec;
                g_state.hostRate = 1.0;
            } else if (g_state.paused) {
                g_state.hostSec = posSec;   // frozen: just track the sample
            } else {
                g_state.hostSec = predicted + err * 0.12;
                g_state.hostRate += err * 0.06;
                if (g_state.hostRate < 0.95) g_state.hostRate = 0.95;
                if (g_state.hostRate > 1.05) g_state.hostRate = 1.05;
            }
            g_state.hostSecAt = now;
        }
        // Word timing ships with the line: replace it wholesale (also on line
        // changes, where text equality below would otherwise keep stale
        // timing). Empty wt for a line = no word timing = legacy path.
        bool lineChanged = newOrig != g_state.orig || newRoma != g_state.roma || newTrans != g_state.trans;
        if (!newTimes.empty())
            g_state.wtimes = newTimes;
        else if (lineChanged)
            g_state.wtimes.clear();
        g_state.lineStart = lineStartSec;
        // Jitter guard: within the same line the progress must never move
        // backward. The host's position reads are quantized and occasionally
        // read a stale value; accepting a slightly-backward sample made the
        // sweep visibly jerk back, then race forward again, over and over.
        // Line changes (different text) and real seeks (well past the
        // threshold) are still accepted.
        bool sameLine = newOrig == g_state.orig && newRoma == g_state.roma && newTrans == g_state.trans;
        if (sameLine && newProg >= 0 && g_state.prog >= 0) {
            double dtNow = (double)(now.QuadPart - g_state.progAt.QuadPart) / (double)freq.QuadPart;
            double expected = g_state.prog + g_state.progRate * dtNow;
            if (newProg < expected - 0.05) {
                // Backward seek inside the line — fall through and accept.
            } else if (newProg < expected - 0.004) {
                // Small backward jitter: ignore the sample entirely and keep
                // extrapolating from the last good one.
                LeaveCriticalSection(&g_cs);
                return;
            }
        }

        g_state.orig = newOrig;
        g_state.roma = newRoma;
        g_state.trans = newTrans;
        if (newProg >= 0 && g_state.prog >= 0 && g_state.progPrev >= 0)
        {
            double dt = (double)(g_state.progAt.QuadPart - g_state.progPrevAt.QuadPart) / (double)freq.QuadPart;
            double dp = g_state.prog - g_state.progPrev;
            if (dt > 0.005 && dp > -0.5 && dp < 0.5) {
                // Weight the fresh sample above the running average: the real
                // per-word speed changes fast inside a line, and a sluggish
                // estimate makes every incoming sample correct the sweep with
                // a visible jump. The display smoother absorbs the leftover
                // noise, so the estimate can afford to be twitchy here.
                double inst = dp / dt;
                g_state.progRate = (g_state.progRate == 0.0) ? inst
                                        : g_state.progRate * 0.4 + inst * 0.6;
            }
            // Seek detection: when the sample is far from what the current
            // rate predicts the user dragged — drop the rate so the sweep
            // jumps to the target instead of gliding over the pre-seek spot.
            double dtNow = (double)(now.QuadPart - g_state.progAt.QuadPart) / (double)freq.QuadPart;
            double predicted = g_state.prog + g_state.progRate * dtNow;
            if (fabs(newProg - predicted) > 0.12)
                g_state.progRate = 0.0;
        }
        if (newProg >= 0 && g_state.prog < 0)
        {
            // new line: reset interpolation history
            g_state.progPrev = -1;
            g_state.progRate = 0;
        }
        else
        {
            g_state.progPrev = g_state.prog;
            g_state.progPrevAt = g_state.progAt;
        }
        g_state.prog = newProg;
        g_state.progAt = now;
        LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
        // While a word-timed line plays the vsync pump thread keeps posting
        // render frames; static text only needs the PostMessage above.
    } else if (type == "pause") {
        // Host paused/resumed: freeze (or resume) the sweep. On resume the
        // rate restarts from zero and is rebuilt from the incoming samples —
        // reusing the stale pre-pause rate would glide past the new position.
        auto* on = FindMember(root, "on");
        bool v = on && on->type == JsonVal::BOOL ? on->boolean : (on && on->type == JsonVal::NUM ? on->num != 0 : false);
        LARGE_INTEGER nowp{};
        QueryPerformanceCounter(&nowp);
        EnterCriticalSection(&g_cs);
        g_state.paused = v;
        if (!v) {
            g_state.progRate = 0.0;
            // Re-anchor the self-owned clock at the resume instant: the pause
            // gap must not be advanced over by hostRate * (minutes of dt).
            g_state.hostSecAt = nowp;
        }
        LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "style") {
        auto* f = FindMember(root, "font");
        auto* c = FindMember(root, "color");
        auto* b = FindMember(root, "bg");
        auto* bo = FindMember(root, "bold");
        auto* al = FindMember(root, "align");
        auto* or_ = FindMember(root, "order");
        auto* ve = FindMember(root, "vertical");
        auto* uw = FindMember(root, "width");
        EnterCriticalSection(&g_cs);
        if (f && f->type == JsonVal::NUM)  g_state.font = (float)f->num;
        if (c && c->type == JsonVal::STR)  g_state.color = ParseColor(c->str);
        auto* ac = FindMember(root, "accent");
        if (ac && ac->type == JsonVal::STR) g_state.accent = ParseColor(ac->str);
        auto* un = FindMember(root, "unsung");
        if (un && un->type == JsonVal::STR) g_state.unsung = ParseColor(un->str);
        if (b && b->type == JsonVal::NUM)  g_state.bg = (float)b->num;
        // bold arrives as a number (1/0) from the C# side — same compat as
        // vertical/click below; BOOL-only parsing silently dropped every
        // "bold on" toggle.
        if (bo) g_state.bold = (bo->type == JsonVal::BOOL) ? bo->boolean : (bo->type == JsonVal::NUM && bo->num != 0);
        if (al && al->type == JsonVal::STR) g_state.alignLeft = (al->str == "Left");
        if (ve) g_state.vertical = (ve->type == JsonVal::BOOL ? ve->boolean : (ve->type == JsonVal::NUM && ve->num != 0));
        if (uw && uw->type == JsonVal::NUM) g_state.userWidth = (int)uw->num;
        if (or_ && or_->type == JsonVal::STR && or_->str.size() == 3
            && or_->str.find('O') != std::string::npos
            && or_->str.find('R') != std::string::npos
            && or_->str.find('T') != std::string::npos)
            g_state.order = or_->str;
        LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "click") {
        auto* on = FindMember(root, "on");
        bool v = on && on->type == JsonVal::BOOL ? on->boolean : (on && on->type == JsonVal::NUM ? on->num != 0 : false);
        EnterCriticalSection(&g_cs); g_state.clickThrough = v; LeaveCriticalSection(&g_cs);
        if (v) g_hover = false;  // click-through: never show the frame
        if (g_hwnd) {
            LONG ex = GetWindowLong(g_hwnd, GWL_EXSTYLE);
            if (v) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(g_hwnd, GWL_EXSTYLE, ex);
            SetWindowPos(g_hwnd, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
        }
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "pos") {
        // Host restores a persisted position (also suppresses the default
        // bottom-center placement that would otherwise run on first render).
        auto* px = FindMember(root, "x");
        auto* py = FindMember(root, "y");
        if (px && px->type == JsonVal::NUM && py && py->type == JsonVal::NUM) {
            EnterCriticalSection(&g_cs);
            g_x = (int)px->num;   // center of the lyric box
            g_y = (int)py->num;
            g_positioned = true;
            LeaveCriticalSection(&g_cs);
            if (g_hwnd)
                SetWindowPos(g_hwnd, HWND_TOPMOST, g_x - g_bmW / 2, g_y - g_bmH / 2, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
        }
    } else if (type == "show") {
        EnterCriticalSection(&g_cs); g_state.visible = true; LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "hide") {
        EnterCriticalSection(&g_cs); g_state.visible = false; LeaveCriticalSection(&g_cs);
        PostMessage(g_hwnd, WM_APP_RENDER, 0, 0);
    } else if (type == "quit") {
        // ApplyCommand runs on the pipe thread; route the exit to the main
        // (window) thread. A posted WM_DESTROY would only run the handler —
        // the window would never actually be destroyed — so post WM_APP_QUIT
        // instead: WndProc calls DestroyWindow there, running the normal
        // WM_DESTROY / WM_NCDESTROY chain and ending the GetMessage loop.
        if (g_hwnd) PostMessage(g_hwnd, WM_APP_QUIT, 0, 0);
    }
}

// ---------- named pipe server (one connection at a time, re-listens) ----------
static DWORD WINAPI PipeThread(LPVOID) {
    while (true) {
        HANDLE hPipe = CreateNamedPipeW(PIPE_NAME,
            PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, nullptr);
        if (hPipe == INVALID_HANDLE_VALUE) { Sleep(500); continue; } // name squatted — retry, don't brick

        if (ConnectNamedPipe(hPipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            if (!ClientIsHost(hPipe)) {
                DisconnectNamedPipe(hPipe);
                CloseHandle(hPipe);
                continue;
            }
            g_pipeConnected = true;
            char buf[4096]; DWORD rd; std::string acc;
            while (ReadFile(hPipe, buf, sizeof(buf) - 1, &rd, nullptr) && rd > 0) {
                if (acc.size() > 1u << 20) acc.clear(); // runaway stream without newlines
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
        g_pipeConnected = false;
        GetSystemTimeAsFileTime(&g_hostGoneAt); // orphan watchdog starts ticking
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }
    return 0;
}

// ---------- window proc ----------
static bool g_dragging = false;
static POINT g_dragPrev = { 0, 0 };

// Right 14 logical px of the window = width-resize zone.
static bool InResizeZone(HWND h, LPARAM l) {
    RECT r;
    GetWindowRect(h, &r);
    float logicalW = (r.right - r.left) / g_dpiScale;
    float x = (int)(short)LOWORD(l) / g_dpiScale;
    return x >= logicalW - 14.0f;
}

// Keep the persisted box center inside the virtual desktop (the union of all
// monitors): a DPI change or monitor hotplug can otherwise park the window
// entirely off-screen with no way to drag it back.
static void ClampToScreens() {
    const int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    if (vw <= 0 || vh <= 0) return;
    g_x = std::max(vx, std::min(g_x, vx + vw));
    g_y = std::max(vy, std::min(g_y, vy + vh));
}

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
        case WM_APP_RENDER:
            InterlockedExchange(&g_renderPending, 0);
            Render();
            return 0;
        case WM_APP_QUIT:
            // Real teardown instead of the old posted WM_DESTROY:
            // DestroyWindow runs WM_DESTROY / WM_NCDESTROY synchronously here
            // on the window thread, and its PostQuitMessage ends the loop.
            DestroyWindow(h);
            return 0;
        case WM_DPICHANGED: {
            // Per-monitor-v2: the box moved to a monitor with another DPI.
            // Refresh the cached scale (the drag/resize math uses it between
            // frames; Render re-derives it per frame anyway), adopt the
            // system-suggested rect, clamp the persisted center back into the
            // visible desktop and re-render at the new scale.
            g_dpiScale = HIWORD(w) / 96.0f;
            if (g_dpiScale <= 0.0f) g_dpiScale = WindowDpiScale();
            const RECT* sug = (const RECT*)l;
            SetWindowPos(h, nullptr, sug->left, sug->top,
                         sug->right - sug->left, sug->bottom - sug->top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
            ClampToScreens();
            PostMessage(h, WM_APP_RENDER, 0, 0);
            return 0;
        }
        case WM_DISPLAYCHANGE:
            // Monitor hotplug / resolution change: the persisted center may
            // now sit off-screen — clamp it and let the render reposition.
            ClampToScreens();
            PostMessage(h, WM_APP_RENDER, 0, 0);
            return 0;
        case WM_TIMER:
            if (w == 1) {
                UpdateFullscreenHide();
                // Host-death suicide: if the command pipe has no client for
                // 10 s, the host died without a quit command (crash /
                // taskkill) — exit regardless of visibility. Watching only
                // while visible left a hidden zombie squatting on the single
                // pipe instance, so the next host launch could not reconnect.
                if (!g_pipeConnected) {
                    FILETIME now;
                    GetSystemTimeAsFileTime(&now);
                    ULONGLONG now64 = ((ULONGLONG)now.dwHighDateTime << 32) | now.dwLowDateTime;
                    ULONGLONG gone64 = ((ULONGLONG)g_hostGoneAt.dwHighDateTime << 32) | g_hostGoneAt.dwLowDateTime;
                    if (gone64 != 0 && (now64 - gone64) > 10000000ULL * 10)
                        PostMessage(h, WM_APP_QUIT, 0, 0);
                } else {
                    GetSystemTimeAsFileTime(&g_hostGoneAt);
                }
            }
            return 0;
        case WM_MOUSEMOVE: {
            if (!g_hover) {
                g_hover = true;
                TRACKMOUSEEVENT tme = { sizeof(tme), TME_LEAVE, h, 0 };
                TrackMouseEvent(&tme);
                PostMessage(h, WM_APP_RENDER, 0, 0); // show the frame
            }
            if (g_resizing) {
                int dx = (int)(short)LOWORD(l) - (int)g_resizeStartX;
                int nw = g_resizeStartW + (int)(dx / g_dpiScale);
                EnterCriticalSection(&g_cs);
                g_state.userWidth = nw < 160 ? 160 : (nw > 3000 ? 3000 : nw);
                LeaveCriticalSection(&g_cs);
                PostMessage(h, WM_APP_RENDER, 0, 0);
            } else if (g_dragging) {
                int dx = (int)(short)LOWORD(l) - g_dragPrev.x;
                int dy = (int)(short)HIWORD(l) - g_dragPrev.y;
                g_x += dx; g_y += dy;   // the center moves with the drag
                SetWindowPos(h, HWND_TOPMOST, g_x - g_bmW / 2, g_y - g_bmH / 2, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            }
            return 0;
        }
        case WM_MOUSELEAVE:
            g_hover = false;
            PostMessage(h, WM_APP_RENDER, 0, 0); // hide the frame
            return 0;
        case WM_SETCURSOR: {
            if (LOWORD(l) == HTCLIENT) {
                DWORD lp = GetMessagePos();
                POINT pt = { (short)LOWORD(lp), (short)HIWORD(lp) };
                ScreenToClient(h, &pt);
                SetCursor(LoadCursor(nullptr, InResizeZone(h, MAKELPARAM(pt.x, pt.y)) ? IDC_SIZEWE : IDC_ARROW));
                return TRUE;
            }
            return DefWindowProc(h, m, w, l);
        }
        case WM_LBUTTONDOWN: {
            bool clickThroughNow;
            EnterCriticalSection(&g_cs);
            clickThroughNow = g_state.clickThrough;
            LeaveCriticalSection(&g_cs);
            if (!clickThroughNow) {
                if (InResizeZone(h, l)) {
                    g_resizing = true;
                    g_resizeStartX = (int)(short)LOWORD(l);
                    EnterCriticalSection(&g_cs);
                    g_resizeStartW = g_state.userWidth > 0 ? g_state.userWidth : (int)g_lastWrapW;
                    LeaveCriticalSection(&g_cs);
                } else {
                    g_dragging = true;
                    g_dragPrev.x = (int)(short)LOWORD(l);
                    g_dragPrev.y = (int)(short)HIWORD(l);
                }
                SetCapture(h);
            }
            return 0;
        }
        case WM_LBUTTONUP:
            if (g_resizing) {
                g_resizing = false;
                ReleaseCapture();
                ReportSize();       // persist the new width via the host
            } else if (g_dragging) {
                g_dragging = false;
                ReleaseCapture();
                ReportPosition();   // persist the new position via the host
            }
            return 0;
        case WM_DESTROY:
            KillTimer(h, 1);
            if (g_renderQuit) SetEvent(g_renderQuit);
            ReportPosition();       // last chance: where the user left it
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
    InitializeCriticalSection(&g_pipeCs);

    // Per-monitor-v2 DPI awareness: without it the process runs DPI-unaware,
    // GetDpiForSystem below always reports 96 and DWM stretches the rendered
    // bitmap — visibly blurry lyrics on 125%/150% displays. Dynamically bound
    // so the MinGW build does not need a newer SDK header.
    if (HMODULE u32 = GetModuleHandleW(L"user32.dll")) {
        using SetDpiCtxFn = BOOL(WINAPI*)(void*);
        if (auto setDpiCtx = (SetDpiCtxFn)(void*)GetProcAddress(u32, "SetProcessDpiAwarenessContext")) {
            // (void*)-4 == DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            setDpiCtx((void*)(intptr_t)-4);
        }
    }

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
    g_dpiScale = WindowDpiScale();   // real per-monitor DPI now that the window exists

    CreateThread(nullptr, 0, PipeThread, nullptr, 0, nullptr);
    CreateThread(nullptr, 0, ReportPipeThread, nullptr, 0, nullptr);
    // The host passes its own PID: pipe clients with any other process id
    // are rejected, so only MusicPlayer can inject lyrics or read reports.
    {
        LPWSTR cl = GetCommandLineW();
        if (cl) {
            const wchar_t* tag = wcsstr(cl, L"--host-pid");
            if (tag) g_hostPid = (DWORD)_wtoi(tag + 10);
        }
    }
    SetTimer(g_hwnd, 1, 1000, nullptr);   // fullscreen auto-hide watchdog

    g_renderQuit = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    CreateThread(nullptr, 0, RenderPumpThread, nullptr, 0, nullptr);

    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0) > 0) {   // > 0: -1 is an error, not "keep looping"
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    if (g_dcRT) g_dcRT->Release();
    // Same GDI rule as EnsureBitmap: delete the DC (deselecting the DIB)
    // before the bitmap itself.
    if (g_hdcMem) DeleteDC(g_hdcMem);
    if (g_hbm) DeleteObject(g_hbm);
    g_layO.Release();   // cached per-role IDWriteTextLayouts
    g_layR.Release();
    g_layT.Release();
    if (g_dwriteFactory) g_dwriteFactory->Release();
    if (g_d2dFactory) g_d2dFactory->Release();
    // NOTE: g_cs / g_pipeCs are intentionally NOT deleted — the pipe threads
    // may still hold them at this point (nothing joins them before the
    // process exits), and deleting a busy CS is undefined behavior.
    // The process is exiting; the OS reclaims everything.
    CoUninitialize();
    return 0;
}
