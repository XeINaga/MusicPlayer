// MusicPlayer installer custom action.
// Normalizes the chosen install directory so it always ends with "MusicPlayer".
// If the user picks a folder that does not already end with the software name
// (case-insensitive, ignoring a trailing backslash), we append "\MusicPlayer".
//
// Implemented in plain C (only kernel32 + msi) to avoid any C/C++ runtime
// dependency, so msiexec can load it on any target machine without extra DLLs.

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <msi.h>

// MinGW's msi.h does not prototype these, so declare them ourselves and link
// against -lmsi. Signatures match the Windows MSI API.
extern "C" UINT __stdcall MsiGetPropertyW(MSIHANDLE hInstall, LPCWSTR szName,
                                          LPWSTR szValueBuf, LPDWORD pcchValueBuf);
extern "C" UINT __stdcall MsiSetPropertyW(MSIHANDLE hInstall, LPCWSTR szName,
                                          LPCWSTR szValue);

static int wendswith_ci(const wchar_t* str, const wchar_t* suffix) {
    int sl = lstrlenW(str);
    int pl = lstrlenW(suffix);
    if (pl > sl) return 0;
    int off = sl - pl;
    for (int i = 0; i < pl; i++) {
        wchar_t a = str[off + i];
        wchar_t b = suffix[i];
        if (a >= L'A' && a <= L'Z') a = (wchar_t)(a + 32);
        if (b >= L'A' && b <= L'Z') b = (wchar_t)(b + 32);
        if (a != b) return 0;
    }
    return 1;
}

extern "C" UINT __stdcall NormalizeInstallDir(MSIHANDLE hInstall) {
    wchar_t buf[4096];
    DWORD len = 4096;
    if (MsiGetPropertyW(hInstall, L"INSTALLFOLDER", buf, &len) != ERROR_SUCCESS) {
        return ERROR_SUCCESS; // never fail the install over this
    }

    // If this is still a formatted directory reference (e.g.
    // "[ProgramFiles64Folder]MusicPlayer"), the default is already correct;
    // leave it untouched to avoid corrupting the path.
    for (DWORD i = 0; i < len && buf[i] != L'\0'; i++) {
        if (buf[i] == L'[') return ERROR_SUCCESS;
    }

    // Trim a single trailing backslash.
    while (len > 0 && buf[len - 1] == L'\\') {
        buf[len - 1] = L'\0';
        len--;
    }
    if (len == 0) return ERROR_SUCCESS;

    const wchar_t* name = L"MusicPlayer";
    if (!wendswith_ci(buf, name)) {
        // Room check before appending: a property of up to 4095 chars fits the
        // MsiGetPropertyW buffer, but lstrcatW("\\MusicPlayer") on top of it
        // would write past the end of buf (stack overwrite inside the
        // elevated custom action). Too long → leave the property untouched,
        // same as every other bail-out here.
        if (len + 12 > 4096)
            return ERROR_SUCCESS;
        lstrcatW(buf, L"\\MusicPlayer");
        MsiSetPropertyW(hInstall, L"INSTALLFOLDER", buf);
    }
    return ERROR_SUCCESS;
}
