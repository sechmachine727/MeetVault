# Builds the single canonical portable deliverable: clean publish, stage ONLY the shipped speech
# models, trim anything else, zip, and print the size + SHA256 for docs/BUILD-LEDGER.md.
#
# Keep $ShippedModels in step with MeetVault.Core/EngineCatalog.cs, the TrimUnshippedModels target
# in src/MeetVault/MeetVault.csproj, and docs/ASR-ENGINES.md.
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)
$ErrorActionPreference = "Stop"
$ShippedModels = @("large-ja", "large-vi")
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

Write-Host "== zip =="
# One canonical package: drop a previous stamp so publish/ never holds two.
Get-ChildItem $PublishRoot -Filter "MeetVault-*-portable.zip" | Remove-Item -Force
# tar (bsdtar, Windows 10+) handles a multi-GB tree far better than Compress-Archive.
& tar.exe -a -c -f $Zip -C $PublishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

$size = (Get-Item $Zip).Length
$mb = [math]::Round($size / 1MB, 1)
$sha = (Get-FileHash $Zip -Algorithm SHA256).Hash
$modelsMb = [math]::Round((Get-ChildItem (Join-Path $ModelsOut "vosk") -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host ("ZIP      " + $Zip)
Write-Host ("SIZE     " + $mb + " MB (" + $size + " bytes)")
Write-Host ("SHA256   " + $sha)
Write-Host ("MODELS   " + $modelsMb + " MB: " + ($ShippedModels -join ", "))
