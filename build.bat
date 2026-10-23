@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "VERSION=1.1.1"
echo ============================================
echo   NTQQ_Response v%VERSION% build
echo ============================================

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] 未找到 .NET Framework 4.x csc.exe
    exit /b 1
)

echo [1/3] Compiling version.dll ...
set "GCC="
if exist "C:\mingw64\bin\gcc.exe" set "GCC=C:\mingw64\bin\gcc.exe"
if not defined GCC (
    for /f "delims=" %%i in ('where gcc 2^>nul') do (
        if not defined GCC set "GCC=%%i"
    )
)
if defined GCC (
    "%GCC%" -shared -O2 -o version.dll version.c version.def -Wl,--enable-stdcall-fixup -s
    if errorlevel 1 goto :error
) else (
    if exist version.dll (
        echo    [skip] 未找到 gcc，复用已有的 version.dll
    ) else (
        echo [ERROR] 未找到 gcc 且 version.dll 不存在
        exit /b 1
    )
)

echo [2/3] Compiling QQToastTray.exe ...
"%CSC%" /nologo /target:winexe /out:QQToastTray.exe ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Drawing.dll" ^
    /r:"System.Web.Extensions.dll" ^
    /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" ^
    /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.InteropServices.WindowsRuntime.dll" ^
    /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll" ^
    /r:"%WINDIR%\System32\WinMetadata\Windows.UI.winmd" ^
    /r:"%WINDIR%\System32\WinMetadata\Windows.Data.winmd" ^
    /r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" ^
    AssemblyInfo.cs QQToastTray.cs
if errorlevel 1 goto :error

echo [3/3] Building standalone installer QQToastSetup.exe ...
"%CSC%" /nologo /target:winexe /out:QQToastSetup.exe ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Drawing.dll" ^
    /resource:QQToastTray.exe,QQToastTray.exe ^
    /resource:version.dll,version.dll ^
    /resource:qq-bridge.js,qq-bridge.js ^
    AssemblyInfo.cs QQToastSetup.cs
if errorlevel 1 goto :error

echo.
echo Done! Built NTQQ_Response v%VERSION%:
echo    QQToastTray.exe   - 托盘助手
echo    QQToastSetup.exe  - 一键安装/卸载程序
echo    version.dll       - QQ 内核桥接引导模块
exit /b 0

:error
echo.
echo [ERROR] Build failed.
exit /b 1
