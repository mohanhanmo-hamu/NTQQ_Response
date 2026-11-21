$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Version = (Get-Content -Raw (Join-Path $Root "VERSION")).Trim()
if (-not $Version) { throw "VERSION file is empty." }

$parts = $Version.Split('.')
$vMajor = if ($parts.Length -ge 1) { $parts[0] } else { "1" }
$vMinor = if ($parts.Length -ge 2) { $parts[1] } else { "0" }
$vPatch = if ($parts.Length -ge 3) { $parts[2] } else { "0" }
$vComma = "$vMajor,$vMinor,$vPatch,0"

$asmContent = @"
using System.Reflection;

[assembly: AssemblyTitle("NTQQ_Response")]
[assembly: AssemblyDescription("QQ Win11 系统级快捷回复助手 (NTQQ Toast Quick Reply)")]
[assembly: AssemblyCompany("NTQQ_Response")]
[assembly: AssemblyProduct("NTQQ_Response")]
[assembly: AssemblyCopyright("Copyright © 2026 NTQQ_Response")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@

[System.IO.File]::WriteAllText((Join-Path $Root "AssemblyInfo.cs"), $asmContent + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

$rcContent = @"
#include <winver.h>

VS_VERSION_INFO VERSIONINFO
 FILEVERSION $vComma
 PRODUCTVERSION $vComma
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_DLL
 FILESUBTYPE 0x0L
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904b0"
        BEGIN
            VALUE "CompanyName", "NTQQ_Response Open Source"
            VALUE "FileDescription", "NTQQ_Response Bootstrap Proxy Loader"
            VALUE "FileVersion", "$Version.0"
            VALUE "InternalName", "version.dll"
            VALUE "LegalCopyright", "Copyright (C) 2026 NTQQ_Response (MIT License)"
            VALUE "OriginalFilename", "version.dll"
            VALUE "ProductName", "NTQQ_Response"
            VALUE "ProductVersion", "$Version"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x0409, 1200
    END
END
"@

[System.IO.File]::WriteAllText((Join-Path $Root "src\loader\version.rc"), $rcContent + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

$manifestPath = Join-Path $Root "src\app.manifest"
if (Test-Path $manifestPath) {
    $m = [System.IO.File]::ReadAllText($manifestPath)
    $m = [System.Text.RegularExpressions.Regex]::Replace($m, 'assemblyIdentity version="[0-9\.]+"', "assemblyIdentity version=`"$Version.0`"")
    [System.IO.File]::WriteAllText($manifestPath, $m, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host "Synced AssemblyInfo.cs, src/loader/version.rc, and src/app.manifest -> $Version"
