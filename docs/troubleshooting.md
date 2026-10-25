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
