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

### 2026-11-04 — Bad-network test: freezes over 1 s at 300 ms ± 100 ms and 5 % loss  (Plan step 10)
Symptom:     The step 10 simulator test (600 ms round trip, 5 % loss each way) showed pauses of 0.9–1.5 s: several frames counted lost right after the start, then single lost frames now and then; quality also stepped down for no reason.
Root cause:  Five things, found one after the other with the test's own figures. (1) The first Link report came before any frame, measured "jitter 0" and set the jitter buffer's hold time to 0, so every out-of-order frame at the start counted as lost. (2) Twice the jitter was too short a hold: packets of frames that had been given up kept arriving afterwards (a temporary count showed 29 late packets for 3 given-up frames). (3) Repair data was planned from a loss count that sometimes came out low (2 s of packets: 5 % ± 1 %). (4) "Queueing delay" was the average delay above the lowest, which ±100 ms of jitter alone pushes to 100 ms, so the link looked full. (5) The packet-loss figure was averaged per 250 ms report, and packets overtaking each other made short reports look worse (9 % for 5 %).
Exact fix:   (1) The hold stays at 250 ms until 10 frames have measured the jitter. (2) Hold = 3 × jitter, at most 250 ms. (3) Repair is planned for the loss plus twice its uncertainty, 5 % until the first report, and for fewer lost blocks the longer the round trip (a lost frame costs a round trip). (4) Queueing delay = the lowest delay of the last second above the lowest of the last 10 s. (5) Loss = packets received ÷ expected summed over the last 8 reports. Also: a keyframe is asked again only after 1.5 round trips. Result: 10 runs in a row with 0 lost frames, longest gap ≤ 100 ms, about 59.7 fps.
Files:       `src/Sheron.Core/Session/Link.cs`, `src/Sheron.Core/Net/FrameAssembler.cs`, `src/Sheron.Core/Net/LinkTiming.cs`, `src/Sheron.Core/Net/FecPolicy.cs`, `src/Sheron.Core/Net/VideoPacketizer.cs`, `src/Sheron.Win/ViewerTestForm.cs`, `tests/Sheron.Tests/Session/BadNetworkSessionTests.cs`, `docs/02-TRD.md`

### 2026-11-04 — Reliable channel never measured the round trip on a slow link  (Plan step 10)
Symptom:     In the 600 ms round-trip simulator test the remote PC showed no round trip at all, and every control message was sent two or three times.
Root cause:  Before a first measurement a fragment is resent after 250 ms; only fragments sent once give a round trip. On a link slower than 250 ms every fragment was resent before its answer came, the wait went back to 250 ms for the next one, and so no fragment was ever "sent once".
Exact fix:   Karn's rule: after a resend for want of an answer, new fragments also wait the doubled time, until one is answered cleanly; then the measured round trip takes over. Test `On_a_link_slower_than_the_first_guess_…`.
Files:       `src/Sheron.Core/Net/ReliableSender.cs`, `tests/Sheron.Tests/Net/ReliableChannelTests.cs`, `docs/02-TRD.md`

### 2026-11-02 — By ID: viewer showed "Path: Direct" but stayed on "Connecting"  (Plan step 14)
Symptom:     Sher's laptop viewed ID 622 531 326 (a Windows Server on an office network with two gateways, used through Remote Desktop). The viewer showed "Path: Direct · 119.30.112.11:2048" (the server's public address) but stayed on "Connecting to ID …" with no remote key; the sharing window stayed on "Waiting for a viewer".
Root cause:  The viewer took the first address that sent it anything during punching. A Punch from the remote PC only proves packets reach the viewer, not the way back; on a path that works one way (a firewall or router at the far end dropping the viewer's packets), the viewer started the handshake on a dead path and never fell back to the relay. Reproduced in the simulator with a one-way block. The exact network cause at Sher's office is not confirmed yet.
Exact fix:   The viewer takes a direct path only on a Punch ack (its own Punch got through and the answer came back); a Punch from the remote PC is answered and its address punched too. If the session handshake on a direct path still has not finished within 3 s, the viewer drops it and asks for the relay, and the remote PC joins that relay even after it took a direct path. The simulator now drops a blocked packet after it has left its own router, as a far-end firewall does, and can block one way only.
Files:       `src/Sheron.Core/Rendezvous/Connector.cs`, `src/Sheron.Core/Rendezvous/HostRendezvous.cs`, `src/Sheron.Win/ViewerTestForm.cs`, `src/Sheron.Sim/SimNetwork.cs`, `tests/Sheron.Tests/Rendezvous/ConnectByIdTests.cs`, `docs/02-TRD.md`

### 2026-11-02 — Oracle Cloud: "Out of capacity", "Too many requests", "Request not completed within 20000 ms"  (Plan step 13)
Symptom:     Creating the server machine in Singapore failed with "Out of capacity for shape VM.Standard.A1.Flex in availability domain AD-1" (and the same for VM.Standard.E2.1.Micro); clicking Create again soon gave "Too many requests for the user"; one try gave "Request not completed within 20000 ms", and oracle.com would not load for a while.
Root cause:  Oracle's free machines in a region are shared by everyone; when none is spare, Create fails until someone frees one. Many clicks in a short time are throttled. The 20-second message only means the console stopped waiting: the request may still go through.
Exact fix:   After a timeout, first look at Compute → Instances (region Singapore) before creating again, so there are never two machines. Then try Create once every 30 minutes or so; try the small AMD shape (VM.Standard.E2.1.Micro) when Ampere (A1.Flex) is full. On 2 Nov the E2.1.Micro try went through. Paid shapes (E4.Flex, E5.Flex) on the trial credit work at once but stop when the 30-day trial ends.
Files:       none (Oracle console)

### 2026-11-02 — A file sent through the chat never reached Sher's PC  (Plan step 13)
Symptom:     The server package sent as a file card in the chat was not in Downloads or anywhere under the user folder; scp said "No such file or directory".
Root cause:  The file was not saved from the chat on Sher's PC (the card may not download there). Large files that do arrive can also be cut short (see the damaged exe entry below).
Exact fix:   Build on Sher's own laptop from the repository instead of sending files: `git pull`, then `dotnet publish src\Sheron.Server -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o "$env:USERPROFILE\sheron-publish\server-linux-x64"`, then copy `Sheron.Server`, `deploy\install.sh` and `deploy\sheron-server.service` to the server with `scp -C` and run `sudo bash sheron-server/install.sh` there. `.gitattributes` keeps `*.sh` and `*.service` in Linux line endings on Windows; the copy command also strips any Windows line endings on the server.
Files:       `.gitattributes`, `deploy/install.sh`, `deploy/sheron-server.service`

### 2026-11-02 — Viewer stuck on "Connecting…" to 192.168.56.1  (Plan step 16)
Symptom:     Viewing Sher's PC from the second laptop stayed on "Connecting to 192.168.56.1:47920 …", and Sher's PC never showed a request.
Root cause:  192.168.56.1 is VirtualBox's private network on Sher's PC, which the other laptop cannot reach. The sharing window listed every address in Windows' adapter order (VirtualBox and other virtual adapters first) and the row was cut off with "…", so the Wi-Fi address (172.20.10.x on the iPhone hotspot) was hidden.
Exact fix:   For now, maximise the sharing window (the row widens) or run `ipconfig` and use the Wi-Fi adapter's IPv4 address. In the code, the sharing window lists addresses on networks with a default gateway first (the Wi-Fi or cable another PC is on); virtual networks have none and come after. Connect by ID (Plan step 14) removes the need to type addresses.
Files:       `src/Sheron.Win/HostTestForm.cs`

### 2026-11-02 — New Sheron.exe on the second laptop: double-click, nothing appears  (Plan step 16)
Symptom:     Double-clicking the new build on PC 2 showed nothing at all; on Sher's PC the same build opened. The Windows event log (.NET Runtime) said: "Failure processing application bundle; possible file corruption. Arithmetic overflow while reading bundle."
Root cause:  The copy on PC 2 was cut short: 44,201 KB instead of 70,801 KB. A one-file exe that is incomplete cannot read the bundle inside it, and closes before any window, with no message.
Exact fix:   Copy the exe again with a USB stick, not through a chat app or a browser download, and check its size in Explorer: it must match the size on the PC it came from (this build: 70,801 KB, 72,499,478 bytes). For an exact check in PowerShell: `(Get-Item <file>).Length; (Get-FileHash <file>).Hash`, compared with the numbers given with the build. To see why any Sheron start fails silently: `Get-WinEvent -LogName Application -MaxEvents 100 | Where-Object { $_.Message -match 'Sheron' } | Select-Object -First 3 | Format-List TimeCreated, ProviderName, Message`
Files:       none (the copy on PC 2)

### 2026-11-01 — Step 11 test failed now and then: "the silent PC was not dropped"  (Plan step 16)
Symptom:     After the step 16 tests were added, `A_silent_PC_is_dropped_and_a_pinging_one_stays` failed in about half of the full test runs (`dotnet test`), never when run alone.
Root cause:  A race in the test. It connected the silent PC, then a second PC, and only then added its Disconnected handler. The server drops a silent PC after the test's 400 ms idle time; the new password tests (PBKDF2, 600,000 rounds) keep the processor busy in parallel, so connecting the second PC sometimes took longer than that and the event had already fired. The server was right.
Exact fix:   The test checks the client's state (`IsConnected`, added in step 12) instead of waiting for the event, and also checks that the pinging PC is still connected. Lesson: in tests, check state rather than catching events that may come before the handler is added.
Files:       `tests/Sheron.Tests/Rendezvous/RendezvousTests.cs`

### 2026-11-01 — Server would drop a PC's whole connection when the other PC left the TCP relay  (Plan step 12)
Symptom:     Found in review, then shown by the test `When_the_other_PC_leaves_the_TCP_relay_this_PC_stays_online`: once the remote PC disconnected, the viewer's last session packets (Relay data, type 15) made the server close the viewer's TLS connection, so the viewer went offline too.
Root cause:  The server's message loop accepted Relay data only "when the PC is on a relay"; with the relay already ended, the message fell to the default case, which treats it as a broken protocol and closes the connection.
Exact fix:   Relay data with no relay is now dropped quietly. Also from the same review: packets for a PC on the TCP relay wait in one bounded queue per PC (1,024; extra ones dropped, as on a full network) with a single writer task, instead of one task per packet; idle relays are cleared every 10 s even when no UDP comes in; a Relay join TCP with an unknown role is ignored.
Files:       `src/Sheron.Server/RendezvousServer.cs`, `tests/Sheron.Tests/Rendezvous/ConnectionTests.cs`, `docs/02-TRD.md`

### 2026-11-01 — Connection test hung: the remote PC waited for a Hello that never came  (Plan step 12)
Symptom:     Every connection test with UDP on the remote PC failed after 15 s with "No Hello came from the viewer".
Root cause:  The test waited for both paths before starting either session. The remote PC's connector only knows its path when the viewer's Hello arrives (direct or through the relay), and the viewer's link sends that Hello, so each side waited for the other.
Exact fix:   Start the viewer's link as soon as `ConnectAsync` returns, then await the remote PC's `AcceptAsync` and start its link on the path it returns (its transport hands the Hello on, so the viewer need not resend it). The app must do the same (Plan step 14).
Files:       `tests/Sheron.Tests/Rendezvous/ConnectionTests.cs`, `src/Sheron.Core/Rendezvous/Connector.cs`

### 2026-11-01 — Smeared, broken picture after lost frames on the hotspot  (Plan step 9)
Symptom:     Viewing the second laptop over the iPhone hotspot: smears and broken text in the picture, "Lost frames 9". On a still screen the damage stayed, because nothing new was sent.
Root cause:  Wi-Fi through the phone loses packets in bursts larger than the repair shards; each later frame builds on the lost one, so the damage lasts until the next keyframe, which came only every 300 frames, and never on a still screen.
Exact fix:   Keyframe on request (TRD › Connect by IP › After a lost frame): the viewer spots a gap in the frame numbers, decodes nothing until a keyframe and keeps the last clean picture, and sends a keyframe request (control message 10), again every 300 ms until one comes. The remote PC asks its encoder for an IDR (oneVPL `mfxEncodeCtrl.FrameType`, layout checked against the v2.16.0 headers in `vpl-layout.txt`; Media Foundation `CODECAPI_AVEncVideoForceKeyFrame`) and encodes the last image again if its screen is still. Also: the video window is filled with the background colour before the first frame (it was white).
Files:       `src/Sheron.Core/Session/ControlMessage.cs`, `src/Sheron.Win/Capture/CaptureLoop.cs`, `src/Sheron.Win/Encode/IVideoEncoder.cs`, `src/Sheron.Win/Encode/H264Encoder.cs`, `src/Sheron.Win/Encode/CodecApi.cs`, `src/Sheron.Win/Encode/Vpl/VplApi.cs`, `src/Sheron.Win/Encode/Vpl/VplEncoder.cs`, `src/Sheron.Win/View/VideoViewer.cs`, `src/Sheron.Win/View/VideoRenderer.cs`, `src/Sheron.Win/HostTestForm.cs`, `src/Sheron.Win/ViewerTestForm.cs`, `third_party/libvpl/vpl-layout.c`, `third_party/libvpl/vpl-layout.txt`, `docs/02-TRD.md`

### 2026-11-01 — Clicks could land one pixel off  (Plan step 9)
Symptom:     `Each_remote_pixel_comes_back_from_its_absolute_value` failed: pixel 7 of 1920 came back as 8 when the absolute value (0–65535) was turned back into pixels by rounding to the nearest.
Root cause:  The first mapping aimed at the middle of each remote pixel. That lands right if Windows rounds down, but exactly on the half-way point for many pixels, so rounding to the nearest goes one pixel too far. SendInput documents only the 0–65535 range, not how it rounds.
Exact fix:   `ScreenMapping.Absolute` gives the smallest absolute value at or past the pixel's left (top) edge: (65536 × pixel + size − 1) / size. Turned back it is under 0.06 of a pixel past the edge for screens up to 3840 pixels, so both kinds of rounding land on the pixel. The test checks every pixel of 1366, 1920, 2560 and 3840 both ways.
Files:       `src/Sheron.Core/Session/Input.cs`, `tests/Sheron.Tests/Session/InputTests.cs`

### 2026-10-26 — Timing test failed while other tests ran  (Plan step 7)
Symptom:     `Sealing_and_opening_a_frame_takes_well_under_a_millisecond` failed with 1.391 ms per frame; the same work measured alone took 0.13 ms (2.3 µs to seal, 4.1 µs to open a packet).
Root cause:  xUnit runs test classes side by side; the timing test shared the processor with the 5 % loss simulations.
Exact fix:   Every test that times something joins the `Timing` collection (`DisableParallelization = true`), so it runs alone after the others. Now 0.074 ms per frame in the Debug test run.
Files:       `tests/Sheron.Tests/Timing.cs`, `tests/Sheron.Tests/Security/SealingSpeedTests.cs`, `tests/Sheron.Tests/Net/BadNetworkTests.cs`

### 2026-10-26 — Keyframe repair shards took 8.5 ms  (Plan step 6)
Symptom:     `Packets_for_a_frame_take_under_a_millisecond`: a 200 kB keyframe took 8.5 ms to packetize and rebuild at 5 % loss (Debug build), over the TRD's under-1-ms packet budget; a 16.7 kB frame took 0.48 ms.
Root cause:  GF(2⁸) multiply-add looked up one byte at a time in a 256-byte table, and every shard was copied twice (into its own array, then into its packet).
Exact fix:   `Galois.MultiplyAdd` does 32 bytes per step with AVX2 (16 with SSSE3): c × x = c × (low 4 bits) XOR c × (high 4 bits), each a 16-entry table looked up with one byte shuffle; other processors keep the byte table. Shards are made right inside their packets (`ReedSolomon.Encode` takes `Memory<byte>`). Release build now: keyframe 0.71 ms to send, 0.74 ms to rebuild; 16.7 kB frame 0.09 ms each. A test checks the fast path against plain multiplying at every length from 0 to 100.
Files:       `src/Sheron.Core/Net/Galois.cs`, `src/Sheron.Core/Net/ReedSolomon.cs`, `src/Sheron.Core/Net/VideoPacketizer.cs`, `tests/Sheron.Tests/Net/ReedSolomonTests.cs`

### 2026-10-26 — Reliable channel resent almost every fragment  (Plan step 6)
Symptom:     `Input_messages_all_arrive_in_order_at_5_percent_loss`: 4,669 resends and 4,118 duplicates for 5,000 messages, at 5 % loss with ± 10 ms jitter.
Root cause:  A fragment was resent a quarter of a round trip after it went whenever an ack showed a later number had arrived. With jitter, later packets often overtake earlier ones, so fragments that were only late were taken as lost.
Exact fix:   A gap the ack shows counts as a loss only once the fragment's own ack is overdue: a round trip and a quarter after it went (the ordinary resend time still applies too). Now 932 resends; still every message once and in order.
Files:       `src/Sheron.Core/Net/ReliableSender.cs`, `tests/Sheron.Tests/Net/ReliableChannelTests.cs`, `docs/02-TRD.md`

### 2026-10-26 — Loop test: capture to screen about 49 ms, draw about 16 ms  (Plan step 5)
Symptom:     `--loop-test` on Sher's PC (Intel Graphics, 60 Hz, window composed by Windows): capture → screen 48.7 ms average (max 49.5), red. Parts: encode 9.1, decode 7.5, draw 16.3 ms; 60 fps captured and shown. After the first fix (no wait on the swap chain's "ready" object, 3 buffers, device frame latency 1): the same, 49.1 ms with draw 16.2 and decode 9.0 ms.
Root cause:  A frame latency of 1, first on the swap chain (its "ready" object, waited on before each frame), then on the device (IDXGIDevice1 maximum frame latency). Windows counts a frame as queued until it is on screen, so with 1 allowed each frame waited until the frame before it reached the screen: a whole refresh in "draw", and frames arriving meanwhile queued behind it (the 7.5–9 ms "decode").
Exact fix:   No frame-latency limit of 1 anywhere: no waitable swap chain, Windows' default device latency (3). Each frame is drawn and handed over with `Present(0)` as soon as it is decoded; Windows shows the newest frame at its next refresh. Three buffers (one on screen, one waiting, one to draw into). The loop window now shows the present call's own time apart from drawing.
             Result on Sher's PC: capture → screen 16.2 ms average (max 32.7); decode 0.4, draw 0.3, present 0.2, screen wait 7.8 ms.
Files:       `src/Sheron.Win/View/VideoRenderer.cs`, `src/Sheron.Win/View/LoopViewer.cs`, `src/Sheron.Win/LoopTestForm.cs`, `src/Sheron.Core/Video/LatencyStats.cs`, `tests/Sheron.Tests/Video/*`, `tests/Sheron.Win.Tests/View/ViewerTests.cs`, `docs/02-TRD.md`

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
