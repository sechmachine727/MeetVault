# Add CUDA support to a build

This guide stages the NVIDIA CUDA runtime and the Microsoft C++ runtime into the repository and builds them into the app. A package built this way can run transcription on an NVIDIA GPU without the CUDA Toolkit or the Visual C++ Redistributable installed on the target machine.

This is a build-time task. Running CUDA inference still needs an NVIDIA driver of 580 or newer on the target machine, because the display driver cannot be bundled.

## Before you start

- A Windows build machine with the .NET 8 SDK. See [Build and test](../README.md#build-and-test).
- The Microsoft Visual C++ 2015-2022 Redistributable (x64) installed on the build machine, so the runtime DLLs are available to copy.
- About 1 GB free for the staged payloads and 1 GB more for the build output.
- Network access to `developer.download.nvidia.com`.
- On the machine that runs CUDA inference: an NVIDIA driver of 580 or newer and a GPU of compute capability 8.6-8.9 or 12.0/12.1. The device picker states the reason when these do not hold; see [Why CUDA is unavailable](#why-cuda-is-unavailable).

NVIDIA does not publish the CUDA user-mode runtime on NuGet, and it is several hundred megabytes, so this repository stages it and gitignores it. The speech models in `models/` work the same way.

## What gets staged

Two folders at the repository root feed the build:

| Folder | Files | Bundled into |
| --- | --- | --- |
| `cuda\win-x64` | `cublas64_13.dll`, `cublasLt64_13.dll`, `cudart64_13.dll` | `runtimes\cuda\win-x64` |
| `vcruntime\win-x64` | `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, `vcomp140.dll` | `runtimes\win-x64`, `runtimes\cuda\win-x64`, `runtimes\vulkan\win-x64` |

Both folders are gitignored. The app project declares them as `Content` items with a `Link`, so a publish or MSIX build copies them into those destinations. Do not copy them into `publish\win-x64` by hand.

Each whisper native loads the C++ runtime from the folder it sits in. .NET loads a native library with the `LOAD_WITH_ALTERED_SEARCH_PATH` search order, which searches the loaded library's own folder before the application folder.

## Stage the CUDA runtime

1. Create the staging folder.

   ```powershell
   mkdir cuda\win-x64
   ```

2. Download the two CUDA 13 redistributable archives for Windows x64. The versions below are the ones this repository was verified with. To pick newer ones, read the current archive names from the redistributable manifest, which lists every component and its Windows x64 archive:

   ```powershell
   Invoke-RestMethod https://developer.download.nvidia.com/compute/cuda/redist/redistrib_13.0.1.json |
       Select-Object -ExpandProperty libcublas | Select-Object -ExpandProperty 'windows-x86_64'
   ```

   ```powershell
   $w = "$env:TEMP\meetvault-cuda"
   mkdir $w
   Invoke-WebRequest 'https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.0.2.14-archive.zip' -OutFile "$w\libcublas.zip"
   Invoke-WebRequest 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.88-archive.zip' -OutFile "$w\cudart.zip"
   ```

3. Extract the archives and copy the three DLLs into `cuda\win-x64`.

   ```powershell
   Expand-Archive "$w\libcublas.zip" "$w\libcublas" -Force
   Expand-Archive "$w\cudart.zip"    "$w\cudart"    -Force
   Copy-Item "$w\libcublas\*\bin\x64\cublas64_13.dll"   cuda\win-x64
   Copy-Item "$w\libcublas\*\bin\x64\cublasLt64_13.dll" cuda\win-x64
   Copy-Item "$w\cudart\*\bin\x64\cudart64_13.dll"      cuda\win-x64
   ```

4. Check the result. Three files, about 506 MB in total.

   ```powershell
   Get-ChildItem cuda\win-x64 | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
   ```

`cublasLt64_13.dll` is the largest file and is required: `cublas64_13.dll` loads through it. The `libcublas` archive also contains `nvblas64_13.dll`, which CUDA inference does not use.

The `13` in each name is the CUDA major version, and it must match the backend that the referenced Whisper.net runtime package ships. The project references `Whisper.net.Runtime.Cuda.Windows`, whose native links `cublas64_13.dll`. If a future Whisper.net moves to another CUDA major, the file names change with it.

## Stage the Microsoft C++ runtime

The whisper natives link the MSVC and OpenMP runtimes. Stage them so a machine without the Visual C++ Redistributable can still load Whisper.

1. Create the staging folder and copy the four DLLs from the build machine.

   ```powershell
   mkdir vcruntime\win-x64
   foreach ($n in 'msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll') {
       Copy-Item "$env:SystemRoot\System32\$n" vcruntime\win-x64
   }
   ```

2. Check the result. Four files, about 900 KB.

   ```powershell
   Get-ChildItem vcruntime\win-x64 | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB)}}
   ```

If a file is missing, install the Microsoft Visual C++ 2015-2022 Redistributable (x64) on the build machine and copy again.

## Stage the speech models

The build does not bundle `models/`. Copy it into the published output after publishing. See [Speech models](../README.md#speech-models) for the four files, their purposes, and their sources.

## Build the payload into the app

Publish the portable build. The staged `cuda\` and `vcruntime\` folders are copied in as part of the build.

```powershell
rmdir /s /q publish\win-x64
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
Copy-Item -Recurse -Force models publish\win-x64\models
```

Replace `dotnet` with the SDK path if it is not on `PATH`, for example `D:\dotnet\dotnet.exe`.

To build the MSIX instead, the same `Content` items apply:

```powershell
dotnet build src\MeetVault\MeetVault.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

## Verify the build

1. Confirm the payload landed. The CUDA folder should hold `ggml-cuda-whisper.dll` and the other whisper DLLs, the three CUDA DLLs, and the four C++ DLLs.

   ```powershell
   Get-ChildItem publish\win-x64\runtimes\cuda\win-x64
   ```

2. Launch `publish\win-x64\MeetVault.exe` and read `%TEMP%\MeetVault\app.log`. The startup report names the driver, the devices, and the CUDA decision:

   ```text
   gpu: cudaDevices=1 cudaRuntime=present vulkanLoader=True vulkanDevices=0 dxgiAdapters=1 apis=[CPU, CUDA]
   gpu: cuda driver reports max CUDA 13.0, device capabilities [sm_8.9] (devices 1)
   ```

   On a machine with no usable NVIDIA GPU the report states the reason instead, and CUDA stays out of the device picker.

## Why CUDA is unavailable

The Settings device picker greys the GPU entry out and shows the reason. The same reason is in `app.log`.

| Reason | Resolution |
| --- | --- |
| `CUDA runtime missing from this build` | Stage `cuda\win-x64` and rebuild, as in [Stage the CUDA runtime](#stage-the-cuda-runtime). |
| `update the NVIDIA driver to 580.0 or newer for CUDA, or use Vulkan` | Install a newer NVIDIA driver on the machine that runs the app. |
| `this build's CUDA has no code for this GPU (sm_8.6-8.9 and sm_120/121 only), use Vulkan` | The GPU is outside the architectures the shipped native carries. Use Vulkan, or a supported GPU. |
| `no usable CUDA or Vulkan driver found` | No NVIDIA driver is installed, or the card is exposed only through a virtual display adapter. |

## Licence

The bundled files are NVIDIA and Microsoft redistributables. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) records them and ships inside the package.
