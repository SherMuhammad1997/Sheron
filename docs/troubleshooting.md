# Troubleshooting log

Read this before debugging. Add one entry for every problem fixed, newest at the top, in the same commit as the fix.

## Entry format

```text
### YYYY-MM-DD — short title  (Plan step N)
Symptom:     what was seen (error text, screen, numbers)
Root cause:  why it happened
Exact fix:   commands, settings or code change, step by step
Files:       paths changed (repo, Windows setting, server setting)
```

## Entries

### 2026-10-26 — Loop test: capture to screen 48.7 ms, draw 16.3 ms  (Plan step 5)
Symptom:     First run of `--loop-test` on Sher's PC (Intel Graphics, 60 Hz, window composed by Windows): capture → screen 48.7 ms average (max 49.5), red. Parts: encode 9.1, decode 7.5, draw 16.3 ms; 60 fps captured and shown.
Root cause:  Before drawing each frame the viewer waited on the swap chain's frame-latency "ready" object (maximum frame latency 1). With one frame allowed, it is signalled only when Windows takes the previous frame at its next composition, so every frame waited most of a screen refresh (the 16.3 ms "draw") and frames arriving meanwhile queued behind it (the 7.5 ms "decode"). Two buffers also meant the next frame's buffer could still be on screen.
Exact fix:   No waiting before drawing: draw and `Present(0)` as soon as a frame is decoded; Windows shows the newest frame at its next refresh. Three buffers (one on screen, one waiting, one to draw into); the swap chain no longer has the waitable flag; the device's maximum frame latency is 1 so drawing never queues on the GPU.
Files:       `src/Sheron.Win/View/VideoRenderer.cs`, `src/Sheron.Win/View/LoopViewer.cs`, `tests/Sheron.Win.Tests/View/ViewerTests.cs`, `docs/02-TRD.md`

### 2026-10-25 — Hardware encoder took about 16 ms per frame  (Plan step 4)
Symptom:     Intel Quick Sync (held back 0, low latency on) showed 16.3 ms average per frame in the capture test window, almost exactly one 15.6 ms Windows timer tick plus the encode.
Root cause:  The encoder's own short waits run on the Windows timer, which ticks every 15.6 ms by default. Windows 11 also ignores timer requests from processes whose windows are hidden, unless they opt out.
Exact fix:   `TimerResolution` asks for a 1 ms timer (`timeBeginPeriod(1)`) while the capture loop runs and opts the process out of power throttling (`SetProcessInformation`, ProcessPowerThrottling: execution speed and ignore-timer-resolution off). Encode time fell to about 8 ms; the rest is the chip's own encode time (about 6 ms floor, see TRD). The test window's Timer line shows whether Windows granted 1 ms.
Files:       `src/Sheron.Win/TimerResolution.cs`, `src/Sheron.Win/Capture/CaptureLoop.cs`, `src/Sheron.Win/CaptureTestForm.cs`, `docs/02-TRD.md`

### 2026-10-25 — Intel Quick Sync encoder: ProcessOutput fails with E_UNEXPECTED (0x8000FFFF)  (Plan step 4)
Symptom:     The Intel® Quick Sync Video H.264 Encoder MFT accepted every setting and the first frame (events "need input", "have output"), then ProcessOutput returned E_UNEXPECTED. Same with GPU and memory input, with and without low latency, with our own output buffer, through Vortice's ProcessOutput, and with one thread doing every call. The Microsoft software encoder worked.
Root cause:  This Intel encoder only produces output when the ICodecAPI scenario (AVScenarioInfo) is set; with display remoting it works. Found by the Windows test `Hardware_encoder_settings_check`, which tries the settings one by one.
Exact fix:   `EncoderOptions.Scenario` defaults to display remoting (eAVScenarioInfo_DisplayRemoting = 1), set through ICodecAPI before the media types. Also kept from the hunt: one worker thread owns a hardware encoder (each call follows its event), encoder routes fall back GPU input → memory input → software, and hardware encoders must match the capture adapter's vendor.
Files:       `src/Sheron.Win/Encode/EncoderOptions.cs`, `src/Sheron.Win/Encode/CodecApi.cs`, `src/Sheron.Win/Encode/H264Encoder.cs`, `src/Sheron.Win/Encode/EncoderFactory.cs`, `src/Sheron.Win/Capture/CaptureLoop.cs`, `tests/Sheron.Win.Tests/Encode/EncoderTests.cs`, `docs/02-TRD.md`

### 2026-10-25 — Windows test: DuplicateOutput fails with E_INVALIDARG (0x80070057)  (Plan step 4)
Symptom:     `The_main_monitor_gives_a_first_frame_with_the_whole_desktop` failed in `DesktopDuplicator.Open` with E_INVALIDARG, although it passed in step 3.
Root cause:  xUnit runs test classes in parallel. EncoderTests opened the screen at the same time, and Windows lets one program duplicate a screen only once at a time; the second DuplicateOutput fails with E_INVALIDARG.
Exact fix:   Every test class that opens the screen carries `[Collection(DesktopDuplication.Collection)]`, so they run one after another. In the app only the capture loop opens the screen. Do not run the capture test window and the tests at the same time.
Files:       `tests/Sheron.Win.Tests/DesktopDuplication.cs`, `tests/Sheron.Win.Tests/Capture/DesktopDuplicatorTests.cs`, `tests/Sheron.Win.Tests/Encode/EncoderTests.cs`

### 2026-10-25 — Linux build: "Microsoft.NET.Sdk.WindowsDesktop.targets was not found"  (Plan step 2)
Symptom:     In the Claude Code cloud session, `dotnet sln add` and `dotnet build` said the imported project `…/Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.targets` was not found for Sheron.Win and Sheron.Win.Tests.
Root cause:  Ubuntu's own `dotnet-sdk-8.0` package (8.0.1xx, built from source by Canonical) leaves out the Windows Desktop SDK, so `EnableWindowsTargeting` cannot work with it. The usual Microsoft download (dot.net → builds.dotnet.microsoft.com) is blocked by the session's network proxy.
Exact fix:   Use Microsoft's own build from packages.microsoft.com, which the proxy allows:
             1. Download the Ubuntu 22.04 feed's index: https://packages.microsoft.com/ubuntu/22.04/prod/dists/jammy/main/binary-amd64/Packages
             2. From it, download the newest .deb of: dotnet-sdk-8.0 (8.0.427), dotnet-runtime-8.0, aspnetcore-runtime-8.0, dotnet-hostfxr-8.0, dotnet-host (8.0.x), dotnet-targeting-pack-8.0, aspnetcore-targeting-pack-8.0, dotnet-apphost-pack-8.0, netstandard-targeting-pack-2.1; check each SHA256 against the index.
             3. Unpack each without installing: `dpkg-deb -x <file>.deb <folder>`.
             4. `export DOTNET_ROOT=<folder>/usr/share/dotnet PATH=$DOTNET_ROOT:$PATH`; `dotnet --version` prints 8.0.427.
             `global.json` accepts any 8.0 SDK from 8.0.100 up, so this SDK and Sher's Windows SDK both work. On Linux `dotnet test` skips Sheron.Win.Tests (its IsTestProject is true only on Windows).
Files:       `global.json`, `src/Sheron.Win/Sheron.Win.csproj`, `tests/Sheron.Win.Tests/Sheron.Win.Tests.csproj`, `docs/02-TRD.md`, `CLAUDE.md`
