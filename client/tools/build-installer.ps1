# Builds the per-user MeetVault installer.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1
#
# It stages the payload through the ONE source of truth (tools\pack-portable.ps1, no zip), then
# compiles installer\MeetVault.iss with Inno Setup and prints the setup's size + SHA256.
# Neither building nor installing needs administrator rights: the installer is per-user
# (PrivilegesRequired=lowest -> %LOCALAPPDATA%\Programs\MeetVault, no UAC prompt).
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$Iscc = "",
    [switch]$SkipStage
)
$ErrorActionPreference = "Stop"

# Resolve the client repo root by evidence (PS 5.1 leaves $PSScriptRoot at a drive root when
# -File gets a relative path).
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    foreach ($candidate in @((Join-Path $PSScriptRoot ".."), (Get-Location).Path)) {
        if (Test-Path (Join-Path $candidate "src\MeetVault\MeetVault.csproj")) {
            $RepoRoot = (Resolve-Path $candidate).Path
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        throw "client repo root not found: run this from the client folder (cd client) or pass -RepoRoot <path>"
    }
}
Write-Host ("== repo root: " + $RepoRoot)

if (-not $SkipStage) {
    Write-Host "== staging the payload (pack-portable.ps1 -SkipZip) =="
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\pack-portable.ps1") -SkipZip
    if ($LASTEXITCODE -ne 0) { throw "staging failed" }
}

$stage = Join-Path $RepoRoot "publish\win-x64"
if (-not (Test-Path (Join-Path $stage "MeetVault.exe"))) { throw "staged payload missing: $stage\MeetVault.exe" }
foreach ($m in @("large-en", "large-ja")) {
    if (-not (Test-Path (Join-Path $stage "models\vosk\$m"))) { throw "staged payload is missing the shipped model $m" }
}
Write-Host "== payload ok (exe + both shipped models) =="

if ([string]::IsNullOrWhiteSpace($Iscc)) {
    foreach ($c in @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { $Iscc = $c; break }
    }
}
if ([string]::IsNullOrWhiteSpace($Iscc) -or -not (Test-Path $Iscc)) {
    throw "ISCC.exe not found. Install Inno Setup 6 per-user (innosetup-6.x.exe /VERYSILENT /CURRENTUSER) or pass -Iscc <path>."
}
Write-Host ("== ISCC: " + $Iscc)

Write-Host "== compiling the installer =="
& $Iscc "/Qp" "/DStageDir=$stage" (Join-Path $RepoRoot "installer\MeetVault.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Get-ChildItem (Join-Path $RepoRoot "publish") -Filter "MeetVaultSetup-*.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "no MeetVaultSetup-*.exe was produced" }

$size = $setup.Length
Write-Host ""
Write-Host ("SETUP    " + $setup.FullName)
Write-Host ("SIZE     " + [math]::Round($size / 1MB, 1) + " MB (" + $size + " bytes)")
Write-Host ("SHA256   " + (Get-FileHash $setup.FullName -Algorithm SHA256).Hash)
Write-Host ("INSTALL  double-click it, or: " + $setup.Name + " /VERYSILENT /CURRENTUSER (no admin)")
