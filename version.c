#include <windows.h>
#include <string.h>

static HMODULE hSysVersion = NULL;

static void LoadSysVersion(void) {
    if (!hSysVersion) {
        WCHAR sysDir[MAX_PATH];
        GetSystemDirectoryW(sysDir, MAX_PATH);
        wcscat(sysDir, L"\\version.dll");
        hSysVersion = LoadLibraryW(sysDir);
    }
}

#define FORWARD_FUNC(name) \
    static FARPROC p_##name = NULL; \
    __declspec(dllexport) void __attribute__((naked)) name(void) { \
        __asm__ volatile ( \
            "sub $40, %%rsp\n\t" \
            "mov %0, %%rax\n\t" \
            "add $40, %%rsp\n\t" \
            "jmp *%%rax\n\t" \
            : : "m"(p_##name) \
        ); \
    }

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

static void InitForwards(void) {
    LoadSysVersion();
    if (!hSysVersion) return;
    p_GetFileVersionInfoA = GetProcAddress(hSysVersion, "GetFileVersionInfoA");
    p_GetFileVersionInfoByHandle = GetProcAddress(hSysVersion, "GetFileVersionInfoByHandle");
    p_GetFileVersionInfoExA = GetProcAddress(hSysVersion, "GetFileVersionInfoExA");
    p_GetFileVersionInfoExW = GetProcAddress(hSysVersion, "GetFileVersionInfoExW");
    p_GetFileVersionInfoSizeA = GetProcAddress(hSysVersion, "GetFileVersionInfoSizeA");
    p_GetFileVersionInfoSizeExA = GetProcAddress(hSysVersion, "GetFileVersionInfoSizeExA");
    p_GetFileVersionInfoSizeExW = GetProcAddress(hSysVersion, "GetFileVersionInfoSizeExW");
    p_GetFileVersionInfoSizeW = GetProcAddress(hSysVersion, "GetFileVersionInfoSizeW");
    p_GetFileVersionInfoW = GetProcAddress(hSysVersion, "GetFileVersionInfoW");
    p_VerFindFileA = GetProcAddress(hSysVersion, "VerFindFileA");
    p_VerFindFileW = GetProcAddress(hSysVersion, "VerFindFileW");
    p_VerInstallFileA = GetProcAddress(hSysVersion, "VerInstallFileA");
    p_VerInstallFileW = GetProcAddress(hSysVersion, "VerInstallFileW");
    p_VerLanguageNameA = GetProcAddress(hSysVersion, "VerLanguageNameA");
    p_VerLanguageNameW = GetProcAddress(hSysVersion, "VerLanguageNameW");
    p_VerQueryValueA = GetProcAddress(hSysVersion, "VerQueryValueA");
    p_VerQueryValueW = GetProcAddress(hSysVersion, "VerQueryValueW");
}

static void PatchBrowserInitInMemory(void);

__declspec(dllexport) BOOL WINAPI GetFileVersionInfoA_wrap(LPCSTR a, DWORD b, DWORD c, LPVOID d) {
    PatchBrowserInitInMemory();
    return ((BOOL(WINAPI*)(LPCSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoA)(a,b,c,d);
}
__declspec(dllexport) int WINAPI GetFileVersionInfoByHandle_wrap(int a, HANDLE b, LPVOID c, DWORD d) {
    PatchBrowserInitInMemory();
    return ((int(WINAPI*)(int,HANDLE,LPVOID,DWORD))p_GetFileVersionInfoByHandle)(a,b,c,d);
}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoExA_wrap(DWORD a, LPCSTR b, DWORD c, DWORD d, LPVOID e) {
    PatchBrowserInitInMemory();
    return ((BOOL(WINAPI*)(DWORD,LPCSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoExA)(a,b,c,d,e);
}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoExW_wrap(DWORD a, LPCWSTR b, DWORD c, DWORD d, LPVOID e) {
    PatchBrowserInitInMemory();
    return ((BOOL(WINAPI*)(DWORD,LPCWSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoExW)(a,b,c,d,e);
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeA_wrap(LPCSTR a, LPDWORD b) {
    PatchBrowserInitInMemory();
    return ((DWORD(WINAPI*)(LPCSTR,LPDWORD))p_GetFileVersionInfoSizeA)(a,b);
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeExA_wrap(DWORD a, LPCSTR b, LPDWORD c) {
    PatchBrowserInitInMemory();
    return ((DWORD(WINAPI*)(DWORD,LPCSTR,LPDWORD))p_GetFileVersionInfoSizeExA)(a,b,c);
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeExW_wrap(DWORD a, LPCWSTR b, LPDWORD c) {
    PatchBrowserInitInMemory();
    return ((DWORD(WINAPI*)(DWORD,LPCWSTR,LPDWORD))p_GetFileVersionInfoSizeExW)(a,b,c);
}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeW_wrap(LPCWSTR a, LPDWORD b) {
    PatchBrowserInitInMemory();
    return ((DWORD(WINAPI*)(LPCWSTR,LPDWORD))p_GetFileVersionInfoSizeW)(a,b);
}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoW_wrap(LPCWSTR a, DWORD b, DWORD c, LPVOID d) {
    PatchBrowserInitInMemory();
    return ((BOOL(WINAPI*)(LPCWSTR,DWORD,DWORD,LPVOID))p_GetFileVersionInfoW)(a,b,c,d);
}
__declspec(dllexport) DWORD WINAPI VerFindFileA_wrap(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPSTR e, PUINT f, LPSTR g, PUINT h) {
    return ((DWORD(WINAPI*)(DWORD,LPCSTR,LPCSTR,LPCSTR,LPSTR,PUINT,LPSTR,PUINT))p_VerFindFileA)(a,b,c,d,e,f,g,h);
}
__declspec(dllexport) DWORD WINAPI VerFindFileW_wrap(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPWSTR e, PUINT f, LPWSTR g, PUINT h) {
    return ((DWORD(WINAPI*)(DWORD,LPCWSTR,LPCWSTR,LPCWSTR,LPWSTR,PUINT,LPWSTR,PUINT))p_VerFindFileW)(a,b,c,d,e,f,g,h);
}
__declspec(dllexport) DWORD WINAPI VerInstallFileA_wrap(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPCSTR e, LPCSTR f, LPSTR g, PUINT h) {
    return ((DWORD(WINAPI*)(DWORD,LPCSTR,LPCSTR,LPCSTR,LPCSTR,LPCSTR,LPSTR,PUINT))p_VerInstallFileA)(a,b,c,d,e,f,g,h);
}
__declspec(dllexport) DWORD WINAPI VerInstallFileW_wrap(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPCWSTR e, LPCWSTR f, LPWSTR g, PUINT h) {
    return ((DWORD(WINAPI*)(DWORD,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPWSTR,PUINT))p_VerInstallFileW)(a,b,c,d,e,f,g,h);
}
__declspec(dllexport) DWORD WINAPI VerLanguageNameA_wrap(DWORD a, LPSTR b, DWORD c) {
    return ((DWORD(WINAPI*)(DWORD,LPSTR,DWORD))p_VerLanguageNameA)(a,b,c);
}
__declspec(dllexport) DWORD WINAPI VerLanguageNameW_wrap(DWORD a, LPWSTR b, DWORD c) {
    return ((DWORD(WINAPI*)(DWORD,LPWSTR,DWORD))p_VerLanguageNameW)(a,b,c);
}
__declspec(dllexport) BOOL WINAPI VerQueryValueA_wrap(LPCVOID a, LPCSTR b, LPVOID *c, PUINT d) {
    return ((BOOL(WINAPI*)(LPCVOID,LPCSTR,LPVOID*,PUINT))p_VerQueryValueA)(a,b,c,d);
}
__declspec(dllexport) BOOL WINAPI VerQueryValueW_wrap(LPCVOID a, LPCWSTR b, LPVOID *c, PUINT d) {
    return ((BOOL(WINAPI*)(LPCVOID,LPCWSTR,LPVOID*,PUINT))p_VerQueryValueW)(a,b,c,d);
}

static volatile LONG g_patched = 0;

static void PatchBrowserInitInMemory(void) {
    if (g_patched) return;

    // Only patch in main browser process (command line without "--type=")
    LPWSTR cmdLine = GetCommandLineW();
    if (cmdLine && wcsstr(cmdLine, L"--type=")) {
        g_patched = 1;
        return;
    }

    HMODULE hQQNT = GetModuleHandleW(L"QQNT.dll");
    if (!hQQNT) return;

    const char target[33]  = "console.log(\"resourcesPath:\",h);";
    const char replace[33] = "i._load(s.join(h,\"qqtoast.js\"));";

    BYTE *base = (BYTE *)hQQNT;
    PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;

    DWORD imageSize = nt->OptionalHeader.SizeOfImage;

    // 1. Fast check at known RVA 0xb1cf6f0
    if (0xb1cf6f0 + 32 < imageSize) {
        BYTE *cand = base + 0xb1cf6f0;
        if (memcmp(cand, target, 32) == 0) {
            DWORD oldProt;
            if (VirtualProtect(cand, 32, PAGE_READWRITE, &oldProt)) {
                memcpy(cand, replace, 32);
                VirtualProtect(cand, 32, oldProt, &oldProt);
                g_patched = 1;
            }
            return;
        } else if (memcmp(cand, replace, 32) == 0) {
            g_patched = 1;
            return;
        }
    }

    // 2. Fallback scan across sections for cross-version compatibility
    PIMAGE_SECTION_HEADER sec = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, sec++) {
        if (sec->VirtualAddress == 0 || sec->Misc.VirtualSize < 32) continue;
        if ((sec->Characteristics & IMAGE_SCN_MEM_READ) && !(sec->Characteristics & IMAGE_SCN_MEM_EXECUTE)) {
            BYTE *start = base + sec->VirtualAddress;
            DWORD size = sec->Misc.VirtualSize;
            if (sec->VirtualAddress + size > imageSize) continue;
            for (DWORD off = 0; off + 32 <= size; off++) {
                if (start[off] == 'c' && start[off+1] == 'o' && memcmp(start + off, target, 32) == 0) {
                    DWORD oldProt;
                    if (VirtualProtect(start + off, 32, PAGE_READWRITE, &oldProt)) {
                        memcpy(start + off, replace, 32);
                        VirtualProtect(start + off, 32, oldProt, &oldProt);
                        g_patched = 1;
                    }
                    return;
                }
            }
        }
    }
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinstDLL);
        InitForwards();
        PatchBrowserInitInMemory();
    }
    return TRUE;
}
