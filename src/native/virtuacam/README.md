# src/native/virtuacam — the VirtuaCam native artifacts (an OS-DLL tier, outside self-build)

These are the STABLE, prebuilt VirtuaCam binaries, bundled beside ss.exe so the managed gateway
(`ss camera`, src/runspace/windows/Camera.cs) can host them by P/Invoke. They are treated like an OS
DLL: the in-proc Roslyn self-build (`ss build self`) never recompiles them — it is a managed-only road.
Their C++ source is studied, not imported (home-roll the managed seam; mount the native behind a contract).

Canonical source: `S:\virtuacam-project\VirtuaCam` (build.ps1 → CMake/Ninja). Copied 2026-06-20.

| artifact                          | role                                                                 |
|-----------------------------------|----------------------------------------------------------------------|
| DirectPortBroker.dll              | the broker/multiplexer — discovers DirectPort producers, composites  |
| DirectPortClient.dll              | the Media Foundation virtual-camera COM source (the webcam the OS sees; regsvr32, admin) |
| DirectPortConsumer.dll            | generic consumer/filter producer                                     |
| DirectPortDisplay.dll             | virtual-display (IddCx) producer                                     |
| DirectPortMFCamera.dll            | physical-webcam passthrough producer                                 |
| DirectPortMFGraphicsCapture.dll   | window/screen capture producer                                       |
| VirtuaCamProcess.exe              | the per-source producer host (loads the capture/display/consumer producer DLLs) |

The broker's C API (the seam `ss camera` drives) is fenced in VirtuaCamNative.cs. The DirectPort GPU
primitive these build on is the separate `src/native/directport` binding (the producer side `ss surface`
already publishes). Producers stay SEPARATE PROCESSES mounted over DirectPort (the reference pattern);
the broker + MF client are the consumer side, hosted in-proc by the managed gateway.
