@echo off
:: RIFE TRT — No-menu build launcher
:: Unblocks, builds, done. No prompts. No ceremony.

cd /d "%~dp0"

:: Unblock once
powershell -NoProfile -Command "Get-ChildItem -Recurse -Include '*.ps1','*.bat' | Unblock-File" 2>nul

:: Run build directly (assumes preflight already passed)
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\build_rife_trt.ps1" -ProjectRoot "%~dp0" -DllManifest @(
    @{Name='nvinfer_10.dll';Source='tensorrt_bin';Required=$true},
    @{Name='nvinfer_plugin_10.dll';Source='tensorrt_bin';Required=$true},
    @{Name='nvinfer_builder_resource_sm86_10.dll';Source='tensorrt_bin';Required=$true},
    @{Name='nvinfer_lean_10.dll';Source='tensorrt_bin';Required=$true},
    @{Name='cudart64_13.dll';Source='cuda_bin';Required=$true},
    @{Name='cublas64_13.dll';Source='cuda_bin';Required=$true},
    @{Name='cublasLt64_13.dll';Source='cuda_bin';Required=$true},
    @{Name='cufft64_12.dll';Source='cuda_bin';Required=$true},
    @{Name='msvcp140.dll';Source='system';Required=$true},
    @{Name='vcruntime140.dll';Source='system';Required=$true},
    @{Name='vcruntime140_1.dll';Source='system';Required=$true}
)

pause