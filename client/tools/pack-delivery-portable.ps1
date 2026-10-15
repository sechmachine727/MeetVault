# Build the slim portable self-extracting package into delivery\.
#
#   1. clean publish of the current source (self-contained win-x64)
#   2. stage the shipped models WITHOUT the optional second-pass rescoring trees (rescore/ + rnnlm/)
#   3. pack it with 7-Zip LZMA2 and prepend a self-extractor module, then VERIFY by extracting it
#
# Why the models are staged without rescore/rnnlm: with those trees present vosk-api loads them in
# Model() and rescores every final result. Measured 2026-10-15 on large-en: 40.2 s of model load and
# 4.55 GB working set with them, 2.7 s and 0.7 GB without; the deliverable shrinks from ~3.0 GB (a
# deflate zip) to ~0.45 GB. The trees are not deleted — they stay staged under
# client\models\vosk\<model>\{rescore,rnnlm} — so a second-pass build can stage them again whenever
# the accuracy is worth 3.3 GB, 40 s of load and 4.5 GB of RAM to whoever runs it.
#
# Why 7zCon.sfx and not 7z.sfx: this archive is ~0.45 GB / 587 files. The GUI module (7z.sfx) cannot
# be driven non-interactively for it (-y -o still ends in an error/dialog: measured exit 2, then a
# hang), while the console module extracts it correctly and silently with -y -o<dir>. Double-clicking
# the package shows that console prompt: answer y and it extracts into the folder the package sits in.
$ErrorActionPreference = 'Stop'
$client = 'D:\workspace\MeetVault\client'
$delivery = 'D:\workspace\MeetVault\delivery'
$stage = Join-Path $client 'publish\win-x64'
$stamp = Get-Date -Format 'yyyy-MM-dd'
$artifact = Join-Path $delivery "MeetVault-$stamp-win-x64-portable.exe"
$ShippedModels = @('large-en', 'large-ja')
$RescoringDirs = @('rescore', 'rnnlm')
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
$sfxModule = 'C:\Program Files\7-Zip\7zCon.sfx'
foreach ($p in $dotnet, $sevenZip, $sfxModule) { if (-not (Test-Path $p)) { throw "missing tool: $p" } }

Write-Host '== 1. clean publish =='
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
& $dotnet publish (Join-Path $client 'src\MeetVault\MeetVault.csproj') -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host '== 2. stage the shipped models (base trees only) =='
$modelsOut = Join-Path $stage 'models'
New-Item -ItemType Directory -Force (Join-Path $modelsOut 'vosk') | Out-Null
foreach ($m in $ShippedModels) {
    $src = Join-Path $client "models\vosk\$m"
    if (-not (Test-Path $src)) { throw "missing staged model: $src" }
    $dst = Join-Path $modelsOut "vosk\$m"
    New-Item -ItemType Directory -Force $dst | Out-Null
    Get-ChildItem $src -Directory | Where-Object { $RescoringDirs -notcontains $_.Name } |
        ForEach-Object { Copy-Item -Recurse -Force $_.FullName (Join-Path $dst $_.Name) }
    Get-ChildItem $src -File | ForEach-Object { Copy-Item -Force $_.FullName $dst }
}
$langid = Join-Path $client 'models\langid'
if (-not (Test-Path $langid)) { throw "missing staged langid model: $langid" }
Copy-Item -Recurse -Force $langid (Join-Path $modelsOut 'langid')

$stageBytes = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
$modelsBytes = (Get-ChildItem (Join-Path $modelsOut 'vosk') -Recurse -File | Measure-Object Length -Sum).Sum
foreach ($m in $ShippedModels) {
    foreach ($d in $RescoringDirs) {
        if (Test-Path (Join-Path $modelsOut "vosk\$m\$d")) { throw "accuracy tree leaked into the package: $m\$d" }
    }
}
Write-Host ("   staged tree {0:N1} MiB, models {1:N1} MiB" -f ($stageBytes / 1MB), ($modelsBytes / 1MB))

Write-Host '== 3. self-extracting LZMA2 package =='
$tmp = Join-Path $env:TEMP ("mv-pack-" + [guid]::NewGuid().ToString('n') + ".7z")
try {
    & $sevenZip a -t7z -m0=lzma2 -mx=9 -mmt=on -bso0 -bsp0 $tmp (Join-Path $stage '*')
    if ($LASTEXITCODE -ne 0) { throw "7z failed ($LASTEXITCODE)" }
    if (Test-Path $artifact) { Remove-Item -Force $artifact }
    cmd /c copy /b "`"$sfxModule`"" + "`"$tmp`"" "`"$artifact`"" | Out-Null
    if (-not (Test-Path $artifact)) { throw 'sfx assembly failed' }
    # The archive and the module are two independent files until the copy above joins them: extract
    # the result and check the tree, so a bad join can never ship.
    #
    # Run the probe from a copy in %TEMP%, not from $artifact itself: the SFX module wants write
    # access to the folder it is launched from, and a folder in this workspace that has no explicit
    # user grant fails it with "Cannot create folder : Access is denied" (measured 2026-10-15 —
    # delivery\ and a fresh workspace folder both fail, %TEMP% and the repaired client\ both work).
    # Stdout/stderr are redirected because a console SFX with no console and no stdout handle blocks.
    $probeExe = Join-Path $env:TEMP ("mv-probe-" + [guid]::NewGuid().ToString('n') + ".exe")
    $probe = Join-Path $env:TEMP ("mv-verify-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force $probe | Out-Null
    Copy-Item $artifact $probeExe -Force
    try {
        $p = Start-Process -FilePath $probeExe -ArgumentList '-y', "-o$probe" -PassThru -Wait -WindowStyle Hidden `
            -RedirectStandardOutput "$probe.o.log" -RedirectStandardError "$probe.e.log"
        if ($p.ExitCode -ne 0) {
            $why = (Get-Content "$probe.e.log" -First 1 -ErrorAction SilentlyContinue)
            throw "the package did not extract (exit $($p.ExitCode)) $why"
        }
        foreach ($must in 'MeetVault.exe', 'models\vosk\large-en\am\final.mdl', 'models\vosk\large-ja\am\final.mdl', 'models\langid\voxlingua107.onnx') {
            if (-not (Test-Path (Join-Path $probe $must))) { throw "extracted package is missing $must" }
        }
        if (Test-Path (Join-Path $probe 'models\vosk\large-en\rescore')) { throw 'extracted package carries rescore/ (should not)' }
        $probeFiles = (Get-ChildItem $probe -Recurse -File | Measure-Object).Count
        $stageFiles = (Get-ChildItem $stage -Recurse -File | Measure-Object).Count
        if ($probeFiles -ne $stageFiles) { throw "extracted $probeFiles files, staged $stageFiles" }
    } finally {
        Remove-Item -Recurse -Force $probe -ErrorAction SilentlyContinue
        Remove-Item -Force $probeExe, "$probe.o.log", "$probe.e.log" -ErrorAction SilentlyContinue
    }
} finally {
    Remove-Item -Force $tmp -ErrorAction SilentlyContinue
}

$size = (Get-Item $artifact).Length
$sha = (Get-FileHash $artifact -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("ARTIFACT " + $artifact)
Write-Host ("SIZE     {0:N1} MiB ({1} bytes)" -f ($size / 1MB), $size)
Write-Host ("SHA256   " + $sha)
Write-Host ("VERIFIED extracts clean: {0} files, exe + both models + langid present, no rescore/" -f $probeFiles)
Write-Host ("STAGE    " + $stage + "  ({0:N1} MiB)" -f ($stageBytes / 1MB))
Write-Host ("MODELS   {0:N1} MiB: {1} (no rescore/rnnlm)" -f ($modelsBytes / 1MB), ($ShippedModels -join ', '))
