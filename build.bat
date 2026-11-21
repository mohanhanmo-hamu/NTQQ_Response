@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

if not exist VERSION (
    echo [ERROR] Missing VERSION file.
    exit /b 1
)
set /p VERSION=<VERSION
set "VERSION=%VERSION: =%"

echo ============================================
echo   NTQQ_Response v%VERSION% modular build
echo ============================================

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4.x csc.exe not found.
    exit /b 1
)

if not exist dist mkdir dist

echo [0/4] Syncing AssemblyInfo.cs from VERSION (%VERSION%) ...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\sync-version.ps1"
if errorlevel 1 goto :error

echo [1/4] Compiling src\loader\version.c -^> dist\version.dll ...
set "GCC="
if exist "C:\mingw64\bin\gcc.exe" set "GCC=C:\mingw64\bin\gcc.exe"
if not defined GCC (
    for /f "delims=" %%i in ('where gcc 2^>nul') do (
        if not defined GCC set "GCC=%%i"
    )
)
set "WINDRES="
if exist "C:\mingw64\bin\windres.exe" set "WINDRES=C:\mingw64\bin\windres.exe"
if not defined WINDRES (
    for /f "delims=" %%i in ('where windres 2^>nul') do (
        if not defined WINDRES set "WINDRES=%%i"
    )
)
if defined GCC (
    if defined WINDRES (
        "%WINDRES%" src\loader\version.rc -O coff -o dist\version_res.o
        if errorlevel 1 goto :error
        "%GCC%" -shared -O2 -Wall -Wextra -Wno-cast-function-type -o dist\version.dll src\loader\version.c dist\version_res.o src\loader\version.def -Wl,--enable-stdcall-fixup -s
        del /q dist\version_res.o 2>nul
    ) else (
        "%GCC%" -shared -O2 -Wall -Wextra -Wno-cast-function-type -o dist\version.dll src\loader\version.c src\loader\version.def -Wl,--enable-stdcall-fixup -s
    )
    if errorlevel 1 goto :error
) else (
    if exist dist\version.dll (
        echo    [skip] gcc not found, reusing existing dist\version.dll
    ) else if exist version.dll (
        copy /y version.dll dist\version.dll >nul
        echo    [skip] gcc not found, reusing root version.dll
    ) else (
        echo [ERROR] gcc not found and no prebuilt version.dll available.
        exit /b 1
    )
)

echo [2/4] Copying src\bridge\qq-bridge.js -^> dist\qq-bridge.js ...
copy /y src\bridge\qq-bridge.js dist\qq-bridge.js >nul
if errorlevel 1 goto :error

echo [3/4] Compiling src\tray\*.cs -^> dist\QQToastTray.exe ...
"%CSC%" /nologo /optimize+ /target:winexe /win32manifest:src\app.manifest /out:dist\QQToastTray.exe ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Drawing.dll" ^
    /r:"System.Web.Extensions.dll" ^
    /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" ^
    /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.InteropServices.WindowsRuntime.dll" ^
    /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll" ^
    /r:"%WINDIR%\System32\WinMetadata\Windows.UI.winmd" ^
    /r:"%WINDIR%\System32\WinMetadata\Windows.Data.winmd" ^
    /r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" ^
    AssemblyInfo.cs ^
    src\tray\Models.cs ^
    src\tray\Logger.cs ^
    src\tray\NativeMethods.cs ^
    src\tray\ConfigStore.cs ^
    src\tray\BridgeDeployer.cs ^
    src\tray\LocalHttpServer.cs ^
    src\tray\ToastManager.cs ^
    src\tray\FluentControls.cs ^
    src\tray\TrayFlyoutForm.cs ^
    src\tray\TrayAppContext.cs ^
    src\tray\Program.cs
if errorlevel 1 goto :error

echo [4/4] Building standalone installer dist\QQToastSetup.exe ...
"%CSC%" /nologo /optimize+ /target:winexe /win32manifest:src\app.manifest /out:dist\QQToastSetup.exe ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Drawing.dll" ^
    /r:"System.Web.Extensions.dll" ^
    /resource:dist\QQToastTray.exe,QQToastTray.exe ^
    /resource:dist\version.dll,version.dll ^
    /resource:dist\qq-bridge.js,qq-bridge.js ^
    AssemblyInfo.cs ^
    src\setup\SetupForm.cs
if errorlevel 1 goto :error

echo.
echo Done! Built NTQQ_Response v%VERSION% into dist\:
echo    dist\QQToastTray.exe
echo    dist\QQToastSetup.exe
echo    dist\version.dll
echo    dist\qq-bridge.js
exit /b 0

:error
echo.
echo [ERROR] Build failed.
exit /b 1
