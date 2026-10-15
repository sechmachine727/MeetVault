# Builds the single canonical portable deliverable: clean publish, stage ONLY the shipped speech
# models, trim anything else, zip, and print the size + SHA256 for docs/BUILD-LEDGER.md.
#
# Keep $ShippedModels in step with MeetVault.Core/EngineCatalog.cs, the TrimUnshippedModels target
# in src/MeetVault/MeetVault.csproj, and docs/ASR-ENGINES.md.
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$RepoRoot = "",
    # The installer pipeline stages the same tree and does not need the archive: skip the zip (and
    # its minutes of lzma) with -SkipZip.
    [switch]$SkipZip
)
$ErrorActionPreference = "Stop"

# Resolve the client repo root by looking for the app project, not by trusting a path shape:
# PowerShell 5.1 leaves $PSScriptRoot at a drive root when -File is given a relative path
# (observed: it resolved to "D:\", so every path below landed in D:\src\...). Try the script's
# own parent first, then the current directory.
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

$ShippedModels = @("large-en", "large-ja")
$PublishDir = Join-Path $RepoRoot "publish\win-x64"
$PublishRoot = Join-Path $RepoRoot "publish"
$Stamp = Get-Date -Format "yyyy-MM-dd"
$Zip = Join-Path $PublishRoot "MeetVault-$Stamp-win-x64-portable.zip"

Write-Host "== clean publish =="
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
& dotnet publish (Join-Path $RepoRoot "src\MeetVault\MeetVault.csproj") -c $Config -r win-x64 --self-contained true -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "== stage the shipped models only =="
$ModelsOut = Join-Path $PublishDir "models"
New-Item -ItemType Directory -Force -Path (Join-Path $ModelsOut "vosk") | Out-Null
foreach ($m in $ShippedModels) {
    $src = Join-Path $RepoRoot "models\vosk\$m"
    if (-not (Test-Path $src)) { throw "missing staged model: $src (stage it per docs/ASR-ENGINES.md)" }
    Copy-Item -Recurse -Force $src (Join-Path $ModelsOut "vosk\$m")
}
$langid = Join-Path $RepoRoot "models\langid"
if (-not (Test-Path $langid)) { throw "missing staged langid model: $langid" }
Copy-Item -Recurse -Force $langid (Join-Path $ModelsOut "langid")

Write-Host "== trim anything that is not shipped =="
Get-ChildItem (Join-Path $ModelsOut "vosk") -Directory |
    Where-Object { $ShippedModels -notcontains $_.Name } |
    ForEach-Object { Write-Host ("  dropping " + $_.Name); Remove-Item -Recurse -Force $_.FullName }

$modelsMb = [math]::Round((Get-ChildItem (Join-Path $ModelsOut "vosk") -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$stageMb = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)

if ($SkipZip) {
    Write-Host "== zip skipped (-SkipZip) =="
    Write-Host ""
    Write-Host ("STAGE    " + $PublishDir + "  (" + $stageMb + " MB)")
    Write-Host ("MODELS   " + $modelsMb + " MB: " + ($ShippedModels -join ", "))
    return
}

Write-Host "== zip =="
# One canonical package: drop a previous stamp so publish/ never holds two.
Get-ChildItem $PublishRoot -Filter "MeetVault-*-portable.zip" | Remove-Item -Force
# Use the SYSTEM bsdtar by absolute path: a bare `tar.exe` picks up MSYS/GNU tar from a git-bash
# PATH, and GNU tar reads "D:\..." as a remote host ("Cannot connect to D: resolve failed").
# bsdtar also handles a multi-GB tree far better than Compress-Archive.
$tar = Join-Path $env:SystemRoot "System32\tar.exe"
if (Test-Path $tar) {
    & $tar -a -c -f $Zip -C $PublishDir .
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
} else {
    Write-Host "  (no bsdtar; falling back to Compress-Archive - slower)"
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $Zip -CompressionLevel Optimal -Force
}

$size = (Get-Item $Zip).Length
$mb = [math]::Round($size / 1MB, 1)
$sha = (Get-FileHash $Zip -Algorithm SHA256).Hash
Write-Host ""
Write-Host ("ZIP      " + $Zip)
Write-Host ("SIZE     " + $mb + " MB (" + $size + " bytes)")
Write-Host ("SHA256   " + $sha)
Write-Host ("MODELS   " + $modelsMb + " MB: " + ($ShippedModels -join ", "))
