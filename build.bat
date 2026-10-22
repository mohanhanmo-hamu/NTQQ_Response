@echo off
chcp 65001 >nul
echo [1/3] Compiling version.dll...
if exist "C:\mingw64\bin\gcc.exe" (
    "C:\mingw64\bin\gcc.exe" -shared -O2 -o version.dll version.c version.def -Wl,--enable-stdcall-fixup -s
) else (
    gcc -shared -O2 -o version.dll version.c version.def -Wl,--enable-stdcall-fixup -s
)

echo [2/3] Compiling QQToastTray.exe...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:QQToastTray.exe ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Drawing.dll" ^
    /r:"System.Web.Extensions.dll" ^
    /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" ^
    /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.InteropServices.WindowsRuntime.dll" ^
    /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll" ^
    /r:"C:\Windows\System32\WinMetadata\Windows.UI.winmd" ^
    /r:"C:\Windows\System32\WinMetadata\Windows.Data.winmd" ^
    /r:"C:\Windows\System32\WinMetadata\Windows.Foundation.winmd" ^
    QQToastTray.cs

echo [3/3] Building standalone installer QQToastSetup.exe...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:QQToastSetup.exe ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Drawing.dll" ^
    /resource:QQToastTray.exe,QQToastTray.exe ^
    /resource:version.dll,version.dll ^
    /resource:qq-bridge.js,qq-bridge.js ^
    QQToastSetup.cs

echo Done! Built QQToastSetup.exe successfully.
