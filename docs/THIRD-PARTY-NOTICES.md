# Third-party notices

MeetVault redistributes the following third-party components. Nothing here changes the licence
terms of those components.

## NVIDIA CUDA runtime (redistributable)

The package ships the CUDA user-mode runtime next to the whisper.cpp CUDA backend, so CUDA
inference works without the CUDA Toolkit installed on the target machine:

| File | Component |
| --- | --- |
| `cublas64_13.dll` | NVIDIA CUDA Toolkit, libcublas 13.0.2 |
| `cublasLt64_13.dll` | NVIDIA CUDA Toolkit, libcublas 13.0.2 |
| `cudart64_13.dll` | NVIDIA CUDA Toolkit, cuda_cudart 13.0.88 |

`cublas64_13.dll` loads through `cublasLt64_13.dll`, so both are required. `cudart64_13.dll` is
shipped for completeness; nothing in the native chain imported it at the time of writing.

These are redistributable components under the NVIDIA CUDA Toolkit End User Licence Agreement,
Attachment A (Redistributable Software), which permits distributing them as part of an
application. They are staged, not committed, under `cuda/win-x64` (see `AGENTS.md`) and copied
into the build as `Content` items in `src/MeetVault/MeetVault.csproj`.

Not bundled, because it is part of the display driver rather than the toolkit and carries no
redistribution grant: `nvcuda.dll` and `nvcudart_hybrid64.dll`. They are installed by the
NVIDIA driver, which is why CUDA requires a suitable driver on the target machine.

## Others

## Microsoft Visual C++ runtime

The whisper.cpp natives link the MSVC and OpenMP runtimes, which are shipped app-locally beside
each native set. They are redistributable under the Microsoft Visual Studio licence terms:

| File | Component |
| --- | --- |
| `msvcp140.dll` | Microsoft Visual C++ 2015-2022 Redistributable |
| `vcruntime140.dll` | Microsoft Visual C++ 2015-2022 Redistributable |
| `vcruntime140_1.dll` | Microsoft Visual C++ 2015-2022 Redistributable |
| `vcomp140.dll` | Microsoft Visual C++ 2015-2022 OpenMP runtime |

Staged under `vcruntime/win-x64` (see `AGENTS.md`). Without them whisper fails to load on a
machine that has no VC++ redistributable installed, for CPU and GPU alike.

The application and its packaged native libraries each keep their own upstream licences
(Whisper.net and whisper.cpp, sherpa-onnx, ONNX Runtime, NAudio, the Windows App SDK, and the
.NET runtime). Those are unchanged by this repository.
