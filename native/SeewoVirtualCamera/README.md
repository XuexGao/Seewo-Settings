# SeewoVirtualCamera

A native C++17 Windows Media Foundation **virtual camera media source** DLL for
Seewo-Settings. It is a pure in-process COM server built on the Windows SDK only
(`Microsoft::WRL` + Media Foundation). No C++/WinRT, no WIL, no NuGet, no vcpkg.

The DLL **produces** frames. It does **not** create or register the camera —
that is the job of the separate registration tool (see
[Camera creation](#camera-creation-and-windows-version-requirements)).

---

## Build

```
msbuild SeewoVirtualCamera.vcxproj /p:Configuration=Release /p:Platform=x64
```

| Setting | Value |
| --- | --- |
| ConfigurationType | `DynamicLibrary` |
| PlatformToolset | `v143` |
| WindowsTargetPlatformVersion | `10.0.26100.0` |
| Language standard | C++17 (`stdcpp17`) |
| Character set | Unicode |
| Warning level | `Level4` |
| Conformance mode | `true` |
| Configurations | `Debug\|x64`, `Release\|x64`, `Debug\|Win32`, `Release\|Win32` |
| Output | `$(SolutionDir)artifacts\native\$(Platform)\$(Configuration)\SeewoVirtualCamera.dll` |
| Intermediates | `$(ProjectDir)$(Platform)\$(Configuration)\` |

The project builds standalone. When no solution supplies `SolutionDir`, the
project defaults it to its own parent directory, so the output lands in
`native\artifacts\native\<platform>\<configuration>\` either way.

### Link and include

* `AdditionalIncludeDirectories` includes `$(ProjectDir)..\SeewoCommon`
* `..\SeewoCommon\SeewoIpc.cpp` is compiled into this DLL as a `ClCompile` item
  (it is not shipped as a separate library)
* Link: `mfplat.lib;mf.lib;mfuuid.lib;ole32.lib;oleaut32.lib;uuid.lib;advapi32.lib;windowscodecs.lib;ksguid.lib;OneCoreUAP.lib`
* `ModuleDefinitionFile` = `SeewoVirtualCamera.def`

### Deliberately absent

* **No `MFCreateVirtualCamera` reference.** It is exported from
  `mfsensorgroup.lib` (`Mfsensorgroup.dll`), which exists only on Windows 11
  22000+. Referencing it would make this DLL fail to load on Windows 10.
* **No `mfsensorgroup.lib`.** Nothing in this project needs it.
* **No Windows 11-only API.** The newest thing used is
  `MFAllocateSerialWorkQueue` (Windows 8).

---

## CLSID

```
{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}
```

This is the CLSID of the **media source class object** — the coclass returned by
`DllGetClassObject`. It is the value the registration tool passes to
`MFCreateVirtualCamera()` as the `clsid` argument, and the value it writes into
`HKCR\CLSID\{A7E4B2C1-...}\InprocServer32`.

The same GUID appears in `SeewoVirtualCameraActivate.h` (as `__declspec(uuid(...))`)
and in `SeewoVirtualCameraActivate.cpp` (as the `CLSID_SeewoVirtualCamera`
definition). Keep them in sync.

`SeewoVirtualCamera.def` exports exactly two entry points:

```
DllGetClassObject
DllCanUnloadNow
```

---

## Registration

The DLL is an ordinary in-process COM server. The registration tool is
responsible for:

1. Copying `SeewoVirtualCamera.dll` to a stable location (e.g. next to the
   Seewo assistant executable).
2. Writing the COM registration:
   * `HKCR\CLSID\{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}\InprocServer32` = the
     DLL path, with `ThreadingModel` = `Both`
   * `HKCR\CLSID\{A7E4B2C1-...}` default value = a display name, e.g.
     `Seewo Virtual Camera`
3. Creating the camera with `MFCreateVirtualCamera` (see below) and enabling it
   with `MFCreateVirtualCamera` + `IMFVirtualCamera::Start`.

Because the FrameServer runs as a service, the DLL must be readable (and its
directory executable) by `SYSTEM` and by the built-in service accounts. Placing
it under `%ProgramFiles%` is the simplest way to guarantee that.

### Camera creation and Windows version requirements

`MFCreateVirtualCamera` is **Windows 11 build 22000 or later only**. It is the
only supported way to publish a virtual camera to the frame server. The
registration tool must therefore:

* call `MFCreateVirtualCamera` with
  `MFVirtualCameraType_SoftwareCameraSource`, the CLSID above, a friendly name,
  a lifetime of `MFVirtualCameraLifetime_System` (or `_Session`), and an access
  mode of `MFVirtualCameraAccess_CurrentUser` (or `_AllUsers`);
* then call `IMFVirtualCamera::Start()`.

On Windows 10 the registration tool must not attempt this. This DLL still loads
and functions there — it simply will not be reachable through the camera picker,
because Windows 10 has no virtual camera framework.

`MFCreateVirtualCamera` is deliberately **not** called from inside this DLL:
* it is Windows 11-only, and
* a media source must never create itself, or the frame server would recurse.

---

## Architecture

| File | Responsibility |
| --- | --- |
| `SeewoVirtualCamera.def` | Exports `DllGetClassObject` / `DllCanUnloadNow` |
| `SeewoVirtualCameraActivate.h/.cpp` | `IMFActivate` (+ delegated `IMFAttributes`) and `IClassFactory` |
| `SeewoMediaSource.h/.cpp` | `IMFMediaSourceEx` + `IMFAttributes` + `IMFGetService`, presentation descriptor, source event queue |
| `SeewoMediaStream.h/.cpp` | `IMFMediaStream2` + `IMFMediaTypeHandler` + `IMFAsyncCallback`, sample production |
| `SeewoFrameSource.h/.cpp` | Shared-memory consumption, test-pattern synthesis, BGRA scaling and BGRA→NV12 conversion |
| `dllmain.cpp` | `DllMain`, `DllGetClassObject`, `DllCanUnloadNow` |

### Interfaces exposed

* **Activate object:** `IMFActivate`, `IMFAttributes` (delegated to a real
  `MFCreateAttributes` store), `IUnknown`.
* **Media source:** `IMFMediaSourceEx` → `IMFMediaSource` → `IMFMediaEventGenerator`;
  plus `IMFAttributes` (a real store) and `IMFGetService`.
* **Media stream:** `IMFMediaStream2` → `IMFMediaStream` → `IMFMediaEventGenerator`;
  plus `IMFMediaTypeHandler` and `IMFAsyncCallback` (used internally for the work
  item).

Only the most derived interface of each chain is listed in the WRL
`RuntimeClass<...>` declaration; `RuntimeClass::QueryInterface` resolves base
interfaces of the listed ones, so all of the above are reachable.

### Media types

Four types are offered on the single video stream:

| Subtype | Size |
| --- | --- |
| `MFVideoFormat_NV12` | 1920×1080 (default, current) |
| `MFVideoFormat_RGB32` | 1920×1080 |
| `MFVideoFormat_NV12` | 1280×720 |
| `MFVideoFormat_RGB32` | 1280×720 |

All are progressive, all-samples-independent, 30/1 fps, 1:1 pixel aspect ratio.
The single stream is **selected by default**; `MF_PD_DURATION` is set to
~10 years (3 153 600 000 000 000 × 100 ns) because a live source has no natural
end.

### Threading model

* One `SRWLOCK` per object (source, stream, activate object). The frame source
  has its own lock, so the work-queue thread and the pipeline thread never block
  each other for long.
* `RequestSample()` **never blocks**: it appends the token to a queue and posts a
  work item to a private MF work queue (`MFAllocateSerialWorkQueue`, falling back
  to `MFAllocateWorkQueue`). The frame is generated on that worker thread, which
  then queues `MEMediaSample` on the stream's `IMFMediaEventQueue`.
* `GetEvent()` deliberately drops the lock before calling into the event queue,
  because it can block indefinitely.
* `Shutdown()` is idempotent and safe from any thread. It skips
  `MFUnlockWorkQueue` when it detects it is running on the work thread itself
  (which happens when a work item holds the last reference), so it can never
  deadlock waiting for itself.
* `DllCanUnloadNow` consults a private object counter (incremented from the
  activate object and factory constructors, decremented from their destructors)
  plus WRL's module object count. `LockServer(TRUE)` also bumps the private
  counter.

### Error handling

Every `HRESULT` is either checked or deliberately propagated. The two
deliberate cases are:

* `SeewoMediaSource::SetD3DManager` returns `S_OK` — the source hands out CPU
  memory buffers, so there is no DXGI manager to store; failing the call would
  make the frame server reject the source.
* `SeewoMediaSource::GetService` returns `MF_E_UNSUPPORTED_SERVICE` for services
  it does not implement, which is the documented answer, and resolves
  `MF_MEDIASOURCE_SERVICE` / `IID_IMFMediaSource` / `IID_IMFMediaSourceEx` /
  `IID_IMFMediaEventGenerator` / `IID_IMFAttributes` through `QueryInterface`.

There is no `TODO` and no `E_NOTIMPL` anywhere in the project.

---

## Shared-memory protocol

The frame channel is defined in `..\SeewoCommon\SeewoIpc.h` and implemented in
`..\SeewoCommon\SeewoIpc.cpp`. This DLL is the **consumer**; the Seewo assistant
app is the producer.

### Names

| Object | Global name | Local fallback |
| --- | --- | --- |
| Frame section | `SeewoAssistant.VCam.Frame.v1` | `SeewoAssistant.VCam.Frame.v1.local` |
| Data-ready event | `SeewoAssistant.VCam.DataReady.v1` | — |
| Sample-request event | `SeewoAssistant.VCam.SampleRequest.v1` | — |

`CreateOrOpenSharedSection` tries the `Global\` namespace first (so a session-0
frame server can see a frame published by an interactive-session producer) and
falls back to `Local\` when the caller lacks `SeCreateGlobalPrivilege`. The
section carries a DACL that grants Everyone full access.

### Layout

The section is `kVcamSectionBytes` = 64 (`kVcamPayloadOffset`) +
1920 × 1080 × 4 (`kVcamMaxFrameBytes`) bytes. The header is a
`seewo::VcamFrameHeader` at offset 0:

| Field | Meaning |
| --- | --- |
| `magic` | `kVcamMagic` (`0x53435631`, `'SCV1'`) |
| `version` | `kVcamVersion` (1) |
| `width`, `height` | frame dimensions |
| `stride` | bytes per row of the payload |
| `format` | `VcamPixelFormat`; only `Bgra32` is defined |
| `frameIndex` | monotonic; incremented by the producer |
| `sourceState` | `VcamSourceState`; `Live` means "publishing" |
| `timestampMs` | producer's `GetTickCount64()` at publish time |
| `payloadBytes` | bytes written into the payload |

Pixels begin at `kVcamPayloadOffset` (64), as top-down BGRA with `stride` bytes
per row.

### Consumer rules in `SeewoFrameSource`

1. Open the section lazily, at most once every 500 ms while it is absent.
   **A missing section never fails activation** — it is the normal "no producer"
   case and simply means the built-in pattern is shown.
2. Accept a frame only when `magic` and `version` match, `sourceState == Live`,
   `format == Bgra32`, `0 < width <= 1920`, `0 < height <= 1080`,
   `stride >= width * 4`, `stride * height <= kVcamMaxFrameBytes`, and
   `payloadBytes` (when non-zero) covers `stride * height`.
3. Reject a frame whose `timestampMs` is more than **2000 ms** away from the
   current `GetTickCount64()`. A stale frame means the producer died or paused.
4. Guard against torn frames: read `frameIndex`, copy, read it again. If it
   moved, retry once; if it is still moving, show the test pattern for that
   sample instead of a half-written image.
5. After 60 consecutive frames without a live frame (~2 s at 30 fps), close and
   reopen the section, so a producer restart (which creates a new mapping under
   the same name) is picked up.

### Test pattern

Whenever a live frame is unavailable, the source synthesises a **visibly
animated** pattern directly at the negotiated output size:

* top two thirds: seven 75 %-amplitude colour bars (white, yellow, cyan, green,
  magenta, red, blue);
* bottom third: a horizontal greyscale ramp;
* a translucent vertical sweep bar travelling left to right across the whole
  frame;
* a solid white block walking top to bottom.

Animation is driven by `GetTickCount64()` at an effective 30 fps, so liveness is
obvious even if samples are requested irregularly.

### Conversion

* **RGB32 output** — BGRA is copied straight into the sample buffer (via
  `IMF2DBuffer` when available, so a padded stride is honoured). A negative
  `Lock2D` pitch is rejected with `MF_E_INVALIDMEDIATYPE` rather than rendering
  upside down.
* **NV12 output** — BT.601 studio-swing coefficients, identical to the reference
  sample's math: `Y = ((66R + 129G + 25B + 128) >> 8) + 16`,
  `U = ((-38R - 74G + 112B + 128) >> 8) + 128`,
  `V = ((112R - 94G - 18B + 128) >> 8) + 128`. Chroma is 2×2 box-averaged.
* **Scaling** — when the producer's frame size differs from the negotiated
  output size, a **bilinear** resample is used (sample-centre mapping, edge
  clamped, 8-bit alpha forced opaque). Same-size frames take a straight row-copy
  fast path.

Timestamps come from `MFGetSystemTime()` and advance by exactly one frame
interval per sample; both `SetSampleTime` and `SetSampleDuration` are set.

---

## Debugging

### Attach to the frame server

The media source is loaded inside the Media Foundation frame server, so
breakpoints in this DLL fire in those processes, not in your test app:

* `FrameServer` — `%SystemRoot%\System32\FrameServer.dll` hosted by
  `svchost.exe` (`-k CameraServiceGroup`). This is where the source is
  activated and where `RequestSample` runs.
* `FrameServerMonitor` — the diagnostic sibling that reports camera state and
  per-stream errors. Useful when the camera appears but never delivers frames.

Practical steps in Visual Studio / WinDbg:

1. Build `Debug|x64`.
2. Register the camera with the registration tool and restart the frame server:
   `net stop FrameServer && net start FrameServer` (or reboot).
3. Attach to the **`svchost.exe`** that has `FrameServer.dll` loaded, or to
   `FrameServerMonitor.exe` if you are debugging camera enumeration.
4. Set `Debug > Attach to Process > Attach to: Native code` and pick the host
   process. If several `svchost.exe` instances exist, find the right one with
   `tasklist /svc | findstr /i camera` or Process Explorer's DLL view.
5. Load the symbols for `SeewoVirtualCamera.dll` (`Debug > Windows > Modules` →
   right-click → *Load Symbols*).

Because the DLL is loaded by a system service, the PDB should sit next to the
DLL. Copying both to `%ProgramFiles%` (or wherever the registration points) is
usually easier than fighting symbol paths.

### Trace points that matter

* `DllGetClassObject` — confirms COM activation reached this DLL at all. If it
  never fires, the `InprocServer32` path or the CLSID is wrong.
* `SeewoVirtualCameraActivate::ActivateObject` — confirms the frame server asked
  for the source.
* `SeewoMediaSource::Start` / `SeewoMediaStream::Start` — confirms the stream was
  selected and the frame source was configured.
* `SeewoMediaStream::RequestSample` / `SeewoMediaStream::Invoke` — confirms the
  pipeline is pulling frames. If `RequestSample` runs but `Invoke` does not, the
  work queue failed to allocate.
* `SeewoFrameSource::TryTakeLiveFrameLocked` — the live/pattern decision. If the
  camera shows the colour-bar pattern forever, the shared section was never
  found, the header failed validation, or the producer's `timestampMs` is stale.

### Quick producer sanity check

The producer must set `magic = 0x53435631`, `version = 1`,
`sourceState = VcamSourceState::Live (1)`, `format = 0` (BGRA),
`width`/`height`/`stride` matching the payload, and refresh `timestampMs` with
`GetTickCount64()` at least every two seconds. Any deviation makes the consumer
fall back to the animated pattern.

### Instrumentation already present

`SeewoFrameSource` tracks `liveFrameCount()` and `patternFrameCount()`. They are
not wired to a public interface (the media source interfaces have no place for
them), but they are handy from a debugger watch window or from a temporary log
statement.

---

## Known limitations

* **CPU memory only.** The source does not allocate DXGI-backed samples, so it
  always uses the system-memory path. This is intentional: it keeps the DLL
  buildable with the Windows SDK alone and loadable on Windows 10.
* **No pause.** `Pause()` returns `MF_E_INVALID_STATE_TRANSITION` and
  `MFMEDIASOURCE_CAN_PAUSE` is not advertised, which is correct for a live
  source. Per-stream `MF_STREAM_STATE_PAUSED` is supported, but a paused stream
  rejects `RequestSample` with `MF_E_INVALIDREQUEST` rather than producing
  nothing, which is the documented way to signal back-pressure.
* **No D3D manager.** `SetD3DManager` accepts and ignores the manager.
* **One stream.** `NUM_STREAMS` is effectively 1. Adding more would mean
  widening the presentation descriptor loop in `SeewoMediaSource::Initialize` and
  giving each stream its own `SeewoFrameSource`.
* **1920×1080 ceiling on live frames.** The shared section is sized for it.
  Larger negotiated output sizes are still supported (the bilinear scaler
  upscales the producer's frame or the test pattern).
