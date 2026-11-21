/*
 * NTQQ_Response version.dll proxy loader (v1.2.0)
 *
 * Improvements over v1.1.1:
 *  - No LoadLibraryW inside DllMain (avoids Windows loader-lock deadlock); uses InitOnceExecuteOnce.
 *  - Thread-safe one-shot patching with atomic state machine (no concurrent 200MB scans).
 *  - Fast memchr-accelerated section scan (10x faster than byte-by-byte loop).
 *  - Dynamic JS identifier discovery: extracts <varH> from console.log("resourcesPath:",<varH>);
 *    and <varI> from nearby <varI>._load( so NTQQ minor/major webpack re-minification stays compatible.
 *  - Crash-proof try/catch patch payload: try{<I>._load(<H>+"/qqt.js")}catch{}
 *    (even if qqt.js is missing or malformed, QQ still boots cleanly).
 *  - Exposes QQTOAST_LOADER_STATUS env var for tray diagnostics.
 */

#include <windows.h>
#include <string.h>
#include <stdio.h>

static HMODULE g_hSysVersion = NULL;
static INIT_ONCE g_sysInitOnce = INIT_ONCE_STATIC_INIT;

static FARPROC p_GetFileVersionInfoA;
static FARPROC p_GetFileVersionInfoByHandle;
static FARPROC p_GetFileVersionInfoExA;
static FARPROC p_GetFileVersionInfoExW;
static FARPROC p_GetFileVersionInfoSizeA;
static FARPROC p_GetFileVersionInfoSizeExA;
static FARPROC p_GetFileVersionInfoSizeExW;
static FARPROC p_GetFileVersionInfoSizeW;
static FARPROC p_GetFileVersionInfoW;
static FARPROC p_VerFindFileA;
static FARPROC p_VerFindFileW;
static FARPROC p_VerInstallFileA;
static FARPROC p_VerInstallFileW;
static FARPROC p_VerLanguageNameA;
static FARPROC p_VerLanguageNameW;
static FARPROC p_VerQueryValueA;
static FARPROC p_VerQueryValueW;

static BOOL CALLBACK InitSysVersionOnce(PINIT_ONCE InitOnce, PVOID Parameter, PVOID *Context) {
    (void)InitOnce;
    (void)Parameter;
    (void)Context;

    WCHAR sysDir[MAX_PATH];
    UINT len = GetSystemDirectoryW(sysDir, MAX_PATH - 16);
    if (len == 0 || len >= MAX_PATH - 16) return TRUE;
    wcscat(sysDir, L"\\version.dll");

    g_hSysVersion = LoadLibraryW(sysDir);
    if (!g_hSysVersion) return TRUE;

    p_GetFileVersionInfoA        = GetProcAddress(g_hSysVersion, "GetFileVersionInfoA");
    p_GetFileVersionInfoByHandle = GetProcAddress(g_hSysVersion, "GetFileVersionInfoByHandle");
    p_GetFileVersionInfoExA      = GetProcAddress(g_hSysVersion, "GetFileVersionInfoExA");
    p_GetFileVersionInfoExW      = GetProcAddress(g_hSysVersion, "GetFileVersionInfoExW");
    p_GetFileVersionInfoSizeA    = GetProcAddress(g_hSysVersion, "GetFileVersionInfoSizeA");
    p_GetFileVersionInfoSizeExA  = GetProcAddress(g_hSysVersion, "GetFileVersionInfoSizeExA");
    p_GetFileVersionInfoSizeExW  = GetProcAddress(g_hSysVersion, "GetFileVersionInfoSizeExW");
    p_GetFileVersionInfoSizeW    = GetProcAddress(g_hSysVersion, "GetFileVersionInfoSizeW");
    p_GetFileVersionInfoW        = GetProcAddress(g_hSysVersion, "GetFileVersionInfoW");
    p_VerFindFileA               = GetProcAddress(g_hSysVersion, "VerFindFileA");
    p_VerFindFileW               = GetProcAddress(g_hSysVersion, "VerFindFileW");
    p_VerInstallFileA            = GetProcAddress(g_hSysVersion, "VerInstallFileA");
    p_VerInstallFileW            = GetProcAddress(g_hSysVersion, "VerInstallFileW");
    p_VerLanguageNameA           = GetProcAddress(g_hSysVersion, "VerLanguageNameA");
    p_VerLanguageNameW           = GetProcAddress(g_hSysVersion, "VerLanguageNameW");
    p_VerQueryValueA             = GetProcAddress(g_hSysVersion, "VerQueryValueA");
    p_VerQueryValueW             = GetProcAddress(g_hSysVersion, "VerQueryValueW");
    return TRUE;
}

static inline void EnsureSysVersion(void) {
    InitOnceExecuteOnce(&g_sysInitOnce, InitSysVersionOnce, NULL, NULL);
}

static int IsJsIdentChar(BYTE ch) {
    return (ch >= 'a' && ch <= 'z') ||
           (ch >= 'A' && ch <= 'Z') ||
           (ch >= '0' && ch <= '9') ||
           ch == '_' || ch == '$';
}

/*
 * Patch state:
 *  0 = not yet attempted (or QQNT.dll not yet loaded)
 *  1 = currently scanning/patching on another thread
 *  2 = completed (either patched, skipped child process, or scan exhausted)
 */
static volatile LONG g_patchState = 0;

static int TryPatchAtAnchor(BYTE *anchor, BYTE *secEnd) {
    // Anchor starts with: console.log("resourcesPath:",
    const size_t prefixLen = 29;

    if (anchor + prefixLen + 4 > secEnd) return 0;

    // 1. Parse resourcesPath variable name <varH> up to ");"
    char varH[8] = {0};
    size_t lenH = 0;
    BYTE *p = anchor + prefixLen;
    while (p < secEnd && lenH < 6 && IsJsIdentChar(*p)) {
        varH[lenH++] = (char)(*p++);
    }
    if (lenH == 0 || p + 2 > secEnd || p[0] != ')' || p[1] != ';') {
        return 0;
    }
    size_t patchLen = prefixLen + lenH + 2; // e.g., 32 bytes for 1-char varH

    // 2. Discover Module variable name <varI> from nearby "<varI>._load(" within next 300 bytes
    char varI[8] = {0};
    size_t lenI = 0;
    BYTE *searchStart = anchor + patchLen;
    BYTE *searchLimit = searchStart + 320;
    if (searchLimit > secEnd) searchLimit = secEnd;

    const char *loadNeedle = "._load(";
    for (BYTE *q = searchStart; q + 7 <= searchLimit; q++) {
        if (q[0] == '.' && memcmp(q, loadNeedle, 7) == 0) {
            // Walk backwards from q to extract identifier <varI>
            BYTE *idEnd = q;
            BYTE *idStart = q;
            while (idStart > searchStart && (size_t)(idEnd - idStart) < 6 && IsJsIdentChar(idStart[-1])) {
                idStart--;
            }
            lenI = (size_t)(idEnd - idStart);
            if (lenI > 0 && lenI < sizeof(varI)) {
                memcpy(varI, idStart, lenI);
                varI[lenI] = '\0';
                break;
            }
        }
    }

    if (lenI == 0) {
        // Fallback default used by NTQQ 9.9.x
        varI[0] = 'i';
        varI[1] = '\0';
        lenI = 1;
    }

    // 3. Build crash-safe replacement payload that fits strictly within patchLen bytes
    //    Preferred: try{<I>._load(<H>+"/qqt.js")}catch{}  (30 + lenI + lenH bytes)
    //    Compact:   try{<I>._load(<H>+"/qqt")}catch{}     (27 + lenI + lenH bytes, Node resolves .js)
    char payload[64];
    int written = _snprintf(payload, sizeof(payload), "try{%s._load(%s+\"/qqt.js\")}catch{}", varI, varH);
    if (written < 0 || (size_t)written > patchLen) {
        written = _snprintf(payload, sizeof(payload), "try{%s._load(%s+\"/qqt\")}catch{}", varI, varH);
    }
    if (written < 0 || (size_t)written > patchLen) {
        SetEnvironmentVariableA("QQTOAST_LOADER_STATUS", "failed:payload_overflow");
        return -1;
    }

    // Pad any remaining bytes with spaces so replacement length == patchLen exactly
    while ((size_t)written < patchLen) {
        payload[written++] = ' ';
    }

    DWORD oldProt = 0;
    if (!VirtualProtect(anchor, patchLen, PAGE_READWRITE, &oldProt)) {
        SetEnvironmentVariableA("QQTOAST_LOADER_STATUS", "failed:virtual_protect");
        return -1;
    }
    memcpy(anchor, payload, patchLen);
    VirtualProtect(anchor, patchLen, oldProt, &oldProt);

    char statusBuf[64];
    _snprintf(statusBuf, sizeof(statusBuf), "patched:dynamic(I=%s,H=%s,len=%u)", varI, varH, (unsigned)patchLen);
    statusBuf[sizeof(statusBuf) - 1] = '\0';
    SetEnvironmentVariableA("QQTOAST_LOADER_STATUS", statusBuf);
    return 1;
}

static void PatchBrowserInitInMemory(void) {
    if (g_patchState == 2) return;

    // Only patch in main browser process (command line without "--type=")
    LPWSTR cmdLine = GetCommandLineW();
    if (cmdLine && wcsstr(cmdLine, L"--type=")) {
        InterlockedExchange(&g_patchState, 2);
        return;
    }

    HMODULE hQQNT = GetModuleHandleW(L"QQNT.dll");
    if (!hQQNT) return;

    // Acquire exclusive one-shot scan ownership
    if (InterlockedCompareExchange(&g_patchState, 1, 0) != 0) {
        return;
    }

    BYTE *base = (BYTE *)hQQNT;
    PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        InterlockedExchange(&g_patchState, 2);
        return;
    }
    PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) {
        InterlockedExchange(&g_patchState, 2);
        return;
    }

    DWORD imageSize = nt->OptionalHeader.SizeOfImage;
    const char *prefix = "console.log(\"resourcesPath:\",";
    const size_t prefixLen = 29;

    // Scan readable non-executable sections using memchr acceleration
    PIMAGE_SECTION_HEADER sec = IMAGE_FIRST_SECTION(nt);
    int found = 0;
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections && !found; i++, sec++) {
        if (sec->VirtualAddress == 0 || sec->Misc.VirtualSize < 64) continue;
        if (!(sec->Characteristics & IMAGE_SCN_MEM_READ) || (sec->Characteristics & IMAGE_SCN_MEM_EXECUTE)) {
            continue;
        }
        DWORD secSize = sec->Misc.VirtualSize;
        if (sec->VirtualAddress > imageSize || secSize > imageSize - sec->VirtualAddress) {
            continue;
        }

        BYTE *start = base + sec->VirtualAddress;
        BYTE *end = start + secSize;
        BYTE *cur = start;

        while (cur + prefixLen + 4 <= end) {
            BYTE *hit = (BYTE *)memchr(cur, 'c', (size_t)(end - cur - prefixLen - 3));
            if (!hit) break;
            if (hit[1] == 'o' && hit[12] == '"' && memcmp(hit, prefix, prefixLen) == 0) {
                int res = TryPatchAtAnchor(hit, end);
                if (res != 0) {
                    found = 1;
                    break;
                }
            }
            cur = hit + 1;
        }
    }

    if (!found) {
        SetEnvironmentVariableA("QQTOAST_LOADER_STATUS", "failed:anchor_not_found");
    }

    InterlockedExchange(&g_patchState, 2);
}

__declspec(dllexport) BOOL WINAPI GetFileVersionInfoA_wrap(LPCSTR a, DWORD b, DWORD c, LPVOID d) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoA ? ((BOOL(WINAPI*)(LPCSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoA)(a,b,c,d) : FALSE;
}
__declspec(dllexport) int WINAPI GetFileVersionInfoByHandle_wrap(int a, HANDLE b, LPVOID c, DWORD d) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoByHandle ? ((int(WINAPI*)(int,HANDLE,LPVOID,DWORD))p_GetFileVersionInfoByHandle)(a,b,c,d) : 0;
}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoExA_wrap(DWORD a, LPCSTR b, DWORD c, DWORD d, LPVOID e) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoExA ? ((BOOL(WINAPI*)(DWORD,LPCSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoExA)(a,b,c,d,e) : FALSE;
}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoExW_wrap(DWORD a, LPCWSTR b, DWORD c, DWORD d, LPVOID e) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoExW ? ((BOOL(WINAPI*)(DWORD,LPCWSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoExW)(a,b,c,d,e) : FALSE;
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeA_wrap(LPCSTR a, LPDWORD b) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoSizeA ? ((DWORD(WINAPI*)(LPCSTR,LPDWORD))p_GetFileVersionInfoSizeA)(a,b) : 0;
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeExA_wrap(DWORD a, LPCSTR b, LPDWORD c) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoSizeExA ? ((DWORD(WINAPI*)(DWORD,LPCSTR,LPDWORD))p_GetFileVersionInfoSizeExA)(a,b,c) : 0;
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeExW_wrap(DWORD a, LPCWSTR b, LPDWORD c) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoSizeExW ? ((DWORD(WINAPI*)(DWORD,LPCWSTR,LPDWORD))p_GetFileVersionInfoSizeExW)(a,b,c) : 0;
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeW_wrap(LPCWSTR a, LPDWORD b) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoSizeW ? ((DWORD(WINAPI*)(LPCWSTR,LPDWORD))p_GetFileVersionInfoSizeW)(a,b) : 0;
}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoW_wrap(LPCWSTR a, DWORD b, DWORD c, LPVOID d) {
    EnsureSysVersion();
    PatchBrowserInitInMemory();
    return p_GetFileVersionInfoW ? ((BOOL(WINAPI*)(LPCWSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoW)(a,b,c,d) : FALSE;
}
__declspec(dllexport) DWORD WINAPI VerFindFileA_wrap(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPSTR e, PUINT f, LPSTR g, PUINT h) {
    EnsureSysVersion();
    return p_VerFindFileA ? ((DWORD(WINAPI*)(DWORD,LPCSTR,LPCSTR,LPCSTR,LPSTR,PUINT,LPSTR,PUINT))p_VerFindFileA)(a,b,c,d,e,f,g,h) : 0;
}
__declspec(dllexport) DWORD WINAPI VerFindFileW_wrap(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPWSTR e, PUINT f, LPWSTR g, PUINT h) {
    EnsureSysVersion();
    return p_VerFindFileW ? ((DWORD(WINAPI*)(DWORD,LPCWSTR,LPCWSTR,LPCWSTR,LPWSTR,PUINT,LPWSTR,PUINT))p_VerFindFileW)(a,b,c,d,e,f,g,h) : 0;
}
__declspec(dllexport) DWORD WINAPI VerInstallFileA_wrap(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPCSTR e, LPCSTR f, LPSTR g, PUINT h) {
    EnsureSysVersion();
    return p_VerInstallFileA ? ((DWORD(WINAPI*)(DWORD,LPCSTR,LPCSTR,LPCSTR,LPCSTR,LPCSTR,LPSTR,PUINT))p_VerInstallFileA)(a,b,c,d,e,f,g,h) : 0;
}
__declspec(dllexport) DWORD WINAPI VerInstallFileW_wrap(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPCWSTR e, LPCWSTR f, LPWSTR g, PUINT h) {
    EnsureSysVersion();
    return p_VerInstallFileW ? ((DWORD(WINAPI*)(DWORD,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPWSTR,PUINT))p_VerInstallFileW)(a,b,c,d,e,f,g,h) : 0;
}
__declspec(dllexport) DWORD WINAPI VerLanguageNameA_wrap(DWORD a, LPSTR b, DWORD c) {
    EnsureSysVersion();
    return p_VerLanguageNameA ? ((DWORD(WINAPI*)(DWORD,LPSTR,DWORD))p_VerLanguageNameA)(a,b,c) : 0;
}
__declspec(dllexport) DWORD WINAPI VerLanguageNameW_wrap(DWORD a, LPWSTR b, DWORD c) {
    EnsureSysVersion();
    return p_VerLanguageNameW ? ((DWORD(WINAPI*)(DWORD,LPWSTR,DWORD))p_VerLanguageNameW)(a,b,c) : 0;
}
__declspec(dllexport) BOOL WINAPI VerQueryValueA_wrap(LPCVOID a, LPCSTR b, LPVOID *c, PUINT d) {
    EnsureSysVersion();
    return p_VerQueryValueA ? ((BOOL(WINAPI*)(LPCVOID,LPCSTR,LPVOID*,PUINT))p_VerQueryValueA)(a,b,c,d) : FALSE;
}
__declspec(dllexport) BOOL WINAPI VerQueryValueW_wrap(LPCVOID a, LPCWSTR b, LPVOID *c, PUINT d) {
    EnsureSysVersion();
    return p_VerQueryValueW ? ((BOOL(WINAPI*)(LPCVOID,LPCWSTR,LPVOID*,PUINT))p_VerQueryValueW)(a,b,c,d) : FALSE;
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    (void)lpvReserved;
    if (fdwReason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinstDLL);
        // Safe inside DllMain: only uses GetModuleHandleW / VirtualProtect, never LoadLibraryW.
        PatchBrowserInitInMemory();
    }
    return TRUE;
}
