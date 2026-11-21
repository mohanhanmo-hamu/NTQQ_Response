param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Version = (Get-Content -Raw (Join-Path $Root "VERSION")).Trim()
if (-not $Version) { throw "VERSION file is empty." }

if (-not $SkipBuild) {
    & cmd.exe /c build.bat
    if ($LASTEXITCODE -ne 0) { throw "build.bat failed with exit code $LASTEXITCODE" }
}

$DistDir = Join-Path $Root "dist"
$ReleaseDir = Join-Path $Root "release"
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$SetupDest = Join-Path $ReleaseDir "NTQQ_Response_Setup_v$Version.exe"
$PortableZip = Join-Path $ReleaseDir "NTQQ_Response_Portable_v$Version.zip"
$ShaFile = Join-Path $ReleaseDir "SHA256SUMS_v$Version.txt"

Copy-Item -Force (Join-Path $DistDir "QQToastSetup.exe") $SetupDest

$StageDir = Join-Path $DistDir "portable_stage"
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

Copy-Item (Join-Path $DistDir "QQToastTray.exe") $StageDir
Copy-Item (Join-Path $DistDir "version.dll") $StageDir
Copy-Item (Join-Path $DistDir "qq-bridge.js") $StageDir
Copy-Item (Join-Path $DistDir "QQToastSetup.exe") (Join-Path $StageDir "NTQQ_Response_Setup_v$Version.exe")
Copy-Item (Join-Path $Root "README.md") $StageDir
Copy-Item (Join-Path $Root "LICENSE") $StageDir

if (Test-Path $PortableZip) { Remove-Item -Force $PortableZip }
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $PortableZip -CompressionLevel Optimal
Remove-Item -Recurse -Force $StageDir

$Targets = @(
    $SetupDest,
    $PortableZip,
    (Join-Path $DistDir "QQToastTray.exe"),
    (Join-Path $DistDir "version.dll"),
    (Join-Path $DistDir "qq-bridge.js")
)

$Lines = @()
foreach ($file in $Targets) {
    $hash = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $file
    $Lines += "$hash  $name"
}
[System.IO.File]::WriteAllText($ShaFile, ($Lines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Packaged NTQQ_Response v$Version successfully:" -ForegroundColor Green
Write-Host "  - $SetupDest"
Write-Host "  - $PortableZip"
Write-Host "  - $ShaFile"
