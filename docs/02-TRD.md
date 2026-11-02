# Sheron – Technical Requirements (TRD)

Oct 25, 2026 · @Sher Muhammad

## How it fits together

```text
 Viewer PC  (Sheron.exe)                        Remote PC  (Sheron.exe + Sheron Service)
   decode -> draw -> pointer drawn here   <-- video --   capture -> encode
   mouse + keyboard  ---------- input ---------------->  SendInput
          \                                               /
           \-- TLS, TCP 443: find ID, signalling --+-- TLS, TCP 443: stays online --/
                                                   |
                                     Sheron Server (Hong Kong)
                           relay on UDP 443 or TCP 443, used only if the direct link fails
```

This is document 2 of 3. The drawing is also on the design canvas (board 5).

## Projects

| Project | Target | Contents |
| --- | --- | --- |
| `src/Sheron.Core` | net8.0 | Protocol, packets, error correction (FEC), encryption handshake, quality control, jitter buffer. No Windows code |
| `src/Sheron.Win` | net8.0-windows | WinForms app, screen capture, encoder, decoder, renderer, input, Windows service |
| `src/Sheron.Server` | net8.0 (Linux) | ID registry, signalling, address check, relay |
| `src/Sheron.Sim` | net8.0 | Bad-network simulator (delay, jitter, loss, UDP blocked) used by tests and by hand |
| `tests/Sheron.Tests` | net8.0 | xUnit tests for Core, Server and Sim; run on Windows and on Linux (cloud sessions, the server) |
| `tests/Sheron.Win.Tests` | net8.0-windows | xUnit tests for Win (DPAPI, capture, input helpers); run on Windows only |

Language: C# 12, .NET 8. `global.json` accepts any .NET 8 SDK from 8.0.100 up. The Windows projects set `EnableWindowsTargeting`, so the whole solution also builds on Linux; there `dotnet test` runs `Sheron.Tests` only (added 25 Oct: Claude Code cloud sessions run on Linux).

## Video path (remote PC → viewer)

| Stage | How | Time budget |
| --- | --- | --- |
| Capture | DXGI Desktop Duplication of the main monitor (the one at desktop position 0, 0), on that monitor's own graphics adapter (needed on laptops with two GPUs). Each frame is copied to a GPU texture for the encoder; changed areas, pointer shape and position read separately | 1–5 ms |
| Colour | BGRA → NV12 on the GPU with the Direct3D 11 video processor (BT.709, 16–235), into a pool of 3 NV12 textures | < 0.1 ms CPU |
| Encode | H.264, four routes tried in order, falling back when one fails on this PC: 0) on Intel GPUs, Intel oneVPL with frames staying on the GPU (Plan step 4b, see below); then Media Foundation: 1) hardware encoder of the capture adapter's vendor, fed the NV12 GPU texture directly (texture bind flags: render target, shader resource and whatever the encoder asks for in MF_SA_D3D11_BINDFLAGS); 2) the same hardware encoder fed a CPU copy; 3) Microsoft software encoder fed a CPU copy. A hardware encoder is owned by one worker thread that reads its events and makes each call they ask for before reading the next (Intel's encoder fails with E_UNEXPECTED if output is collected on another thread). Scenario "display remoting" (eAVScenarioInfo_DisplayRemoting; Intel Quick Sync fails with E_UNEXPECTED on its first output without it), fastest preset (quality vs speed 0), low-latency mode, constant bitrate 8 Mbps, no B-frames, Main profile, keyframe every 300 frames (later: on request). Encoder settings go through the documented Windows `ICodecAPI` interface, declared in our own code | ≤ 8 ms average over 5 s on Intel built-in graphics (about 6 ms floor); 2–5 ms on NVIDIA / AMD |
| Packets | Packets of ≤ 1,200 bytes; extra FEC packets, more when loss rises; a new keyframe only when the viewer asks after loss | < 1 ms |
| Encrypt | AES-256-GCM | < 1 ms |
| Viewer | Jitter buffer 0–100 ms that adapts to the link (Plan step 10); decode and draw as below (Plan step 5) | 2–5 ms + buffer |
| Still screen | No change → no video packets, only a keep-alive | ≈ 0 |

Capture test window (developer only, Plan steps 3–4): `Sheron.Win.exe --capture-test` shows capture fps, time per frame, changed area, pointer, the encoder, encode time, bitrate and Sheron's own CPU use. Add `--software-encoder` to skip the hardware encoder.

The capture device is made with BGRA and video support and is multithread-protected, because the hardware encoder uses it from its own threads.

While video runs, Sheron asks Windows for a 1 ms timer (`timeBeginPeriod`) and opts out of power throttling (`SetProcessInformation`, ProcessPowerThrottling: execution speed and ignore-timer-resolution off), so it is not slowed down when its window is hidden. With the default 15.6 ms timer, Intel Quick Sync took 16.3 ms per frame (25 Oct).

Measured on Sher's laptop, 25 Oct (Intel Graphics, 1920 × 1200 at 60 Hz): capture 60 fps, 0.15 ms average and 0.22 ms max per frame; Sheron CPU 0.0 % on a still screen and 0.4 % while capturing 60 fps.

Measured 25 Oct, Intel Quick Sync through Media Foundation (GPU input, 60 fps, 8 Mbps): encoder's own time at least 6.5 ms in every setting, 7.7–8.5 ms on average once warm; round trip 8–9 ms; CPU 0.7 %. The fastest preset made no difference. Memory input: 6.0 ms minimum inside the encoder but 13.3 ms round trip (CPU copy).

**Intel route (Plan step 4b):** on Intel GPUs, encode through Intel oneVPL instead: the D3D11 device is handed to oneVPL, the captured frame stays on the GPU, low-power encoding (VDEnc), target usage 7 (fastest), async depth 1, no B-frames, low-delay rate control. Media Foundation stays the route for NVIDIA and AMD, and the fallback when oneVPL is missing or fails.

Measured 26 Oct, Intel oneVPL 2.15 runtime (driver 32.0.101.7076, Core 7 240H, 1920 × 1200, 60 frames after warm-up): copy into the encoder's surface 6.71 ms average (5.85 minimum); importing our texture 6.56 ms (5.92); Media Foundation in the same run 6.46 ms (5.55). All routes share the same floor of about 6 ms, the chip's own encode time; the copy route is the default (import is still experimental in oneVPL). A second run the same day: 7.55, 7.28 and 7.37 ms with the same minimums, so averages swing by about 1 ms between runs. The 5-second timing test then passed (≤ 8 ms). Live in the capture test window, with the browser drawing a 60 fps animation on the same GPU: 8.36 ms inside the encoder, 8.77 ms round trip, 60 fps, 0 frames held back, Sheron CPU 0.6 %.

## Viewer: decode and draw (Plan step 5)

| Stage | How |
| --- | --- |
| Device | The viewer has its own Direct3D 11 device (BGRA and video support, multithread-protected), as it will on a second PC |
| Decode | Microsoft H.264 decoder (Media Foundation, found with `MFTEnumEx`, H.264 → NV12), low-latency mode (MF_LOW_LATENCY), so each frame comes out as soon as it goes in. Two routes, falling back: 1) hardware (DXVA): the decoder gets the viewer device through a DXGI device manager and gives back GPU textures; 2) software: no device manager, NV12 in memory, uploaded through a staging texture. A change of picture size in the stream (MF_E_TRANSFORM_STREAM_CHANGE) picks the new output type and goes on. Each decoded frame is copied on the GPU into the viewer's own NV12 texture, so the decoder gets its surface back at once |
| Draw | Flip-model swap chain (flip discard, 3 buffers, BGRA) in one child window; the device keeps Windows' default frame latency (3), because Windows counts a frame as queued until it is on screen and a latency of 1 made each present wait a whole refresh. The Direct3D 11 video processor turns NV12 (BT.709, 16–235) into RGB and scales it to fit, keeping the shape, with the Control Room background around it. Each frame is drawn and presented as soon as it is decoded, never waiting for the screen: `Present(0)`, and Windows shows the newest frame at its next screen refresh (waiting for the swap chain's "ready" signal first cost a whole refresh, 26 Oct, see troubleshooting) |
| Pointer | Desktop Duplication leaves the pointer out of the picture. The viewer draws the pointer itself on top of the video, from the shape and position sent apart from the video, so it moves without waiting for encode and decode. The position is the top-left corner of the pointer image, as Desktop Duplication reports it. The shape becomes two small textures: colour with alpha, and the pixels that invert the screen (monochrome and masked-colour pointers, such as the text I-beam). Two tiny shaders draw them, compiled at start with the Windows shader compiler (`D3DCompile`, d3dcompiler_47.dll, part of Windows) |
| Timer | Capture to display: from the moment Desktop Duplication hands over the frame to the screen refresh that shows it. The refresh time comes from DXGI frame statistics (`SyncQPCTime` for the matching present count). Both use the Windows performance counter (`Stopwatch`), so they compare directly. Shown with its parts: capture to encoded, decode, draw, the present call itself, wait for the screen. The window also shows how Windows put the frame on screen (composed by the desktop window manager, overlay plane or direct), which changes the wait by up to one refresh |

Loop test window (developer only, Plan step 5): `Sheron.Win.exe --loop-test` runs capture → encode → decode → draw on one PC and shows the main monitor in a window with the timer: the average and longest over the last 5 s, and its parts. Gate 1 reads the 5 s average, as step 4b did. Add `--software-encoder` or `--software-decoder` to skip a hardware route. The window also shows itself inside the picture (a mirror); a 60 fps video elsewhere on the screen gives a steady 60 frames a second to time.

Measured 26 Oct on Sher's laptop (Intel Graphics, 1920 × 1200 at 60 Hz, oneVPL encoder, Microsoft decoder on the GPU, window composed by Windows, picture shown at 936 × 585): capture → screen 16.2 ms average over 5 s, 32.7 ms longest (a frame that just misses a screen refresh waits one more). Parts: encode 7.4, decode 0.4, draw 0.3, present 0.2, wait for the screen 7.8 ms. 60 fps captured and shown, 0 untimed; Sheron CPU 1.2 % for capture, encode, decode and drawing together. Gate 1 (≤ 25 ms) passed. Before the frame-latency fix (troubleshooting, 26 Oct) the same loop took 49 ms.

## Packets (Plan step 6)

Sheron's own packet format, written for this project. Every packet on the wire is at most 1,200 bytes; 32 of them are kept for encryption (Plan step 7), so a plain packet is at most **1,168 bytes**. Numbers are little-endian. The first byte says the kind.

| Kind | Byte 0 | Use |
| --- | --- | --- |
| Video | 1 | One piece (shard) of one encoded frame, data or repair; lost ones are not sent again |
| Reliable | 2 | Input and control messages, delivered once and in order |
| Ack | 3 | Says which reliable messages arrived |

**Video packet**: 16-byte header, then the shard.

| Bytes | Field |
| --- | --- |
| 0 | Kind = 1 |
| 1 | Flags: bit 0 keyframe, bit 1 repair shard |
| 2–5 | Frame number (counts up from 0, wraps) |
| 6–9 | Frame length in bytes |
| 10 | Block index |
| 11 | Block count |
| 12 | Shard index in the block: data shards first, then repair shards |
| 13 | Data shards in this block (k) |
| 14 | Repair shards in this block (m) |
| 15 | 0 (kept) |
| 16– | Shard bytes; every shard of a frame has the same size (the last data shard is padded with zeros) |

A frame is cut into the fewest shards of at most 1,152 bytes, all the same size; the shards are dealt into the fewest blocks of at most 32 data shards, as evenly as possible (the first blocks take one more when they do not divide evenly). Each block gets its own repair shards.

| Item | Rule |
| --- | --- |
| Repair (FEC) | Reed–Solomon over GF(2⁸) (polynomial x⁸ + x⁴ + x³ + x² + 1), systematic, with a Cauchy matrix for the repair rows: any k of a block's k + m shards rebuild it. Written from the mathematics in our own code |
| How many repair shards | The fewest m for which a block of k + m shards, each lost with the link's loss rate, loses more than m with a chance of at most 0.1 %. The loss rate comes from the viewer's reports (Plan step 10); until then, and never below, 1 % is assumed. At 5 % loss a 15-shard frame gets 5 repair shards |
| Receiver | Collects shards per frame; a block is rebuilt as soon as any k of its shards are in; a frame is handed on when all its blocks are. A frame newer than the last one handed on is handed on at once; older ones are dropped. Frames skipped this way are counted as lost, so the viewer can ask for a keyframe (Plan step 10). At most 64 unfinished frames are kept |
| Bad packets | Wrong sizes or numbers that do not fit the frame are ignored, never an error |

**Reliable messages** (input, and later control messages): each message gets the next sequence number; a message longer than one packet is sent as several fragments, each with its own number.

| Reliable packet bytes | Field |
| --- | --- |
| 0 | Kind = 2 |
| 1 | Number of entries (1–255) |
| then per entry: 0–3 | Sequence number |
| 4 | Flags: bit 0 last fragment of its message |
| 5–6 | Length |
| 7– | Bytes |

| Ack packet bytes | Field |
| --- | --- |
| 0 | Kind = 3 |
| 1–4 | Next sequence number expected: everything before it has arrived |
| 5–12 | 64 bits: bit i set means sequence number (next + 1 + i) has arrived too |

| Item | Rule |
| --- | --- |
| Sending | New messages go out at the next send (input goes ahead of video, Plan step 9). At most 1,024 numbers are in flight |
| Ack | The receiver answers every reliable packet with an ack at once |
| Resend | A fragment is sent again when an ack shows later numbers arrived but not it and its own ack is overdue (a round trip and a quarter since it went, so a packet that is only late, overtaken by a later one, is not resent), or when no ack covers it within the resend time: smoothed round trip + 4 × its variation, at least 30 ms, at most 2 s, 250 ms before the first measurement, doubling with each resend of the same fragment. Round trips are measured only on fragments sent once |
| Receiving | Fragments are put back in order; duplicates are dropped; a message is handed on when its last fragment is in |

**Bad-network simulator** (`Sheron.Sim`): a one-way link on a virtual clock, so tests run in milliseconds. Settings: delay, jitter (each packet's delay varies evenly by up to ± the jitter, so packets can arrive out of order), loss rate, and UDP blocked (everything dropped). It refuses packets over 1,200 bytes. Seeded, so a test gives the same result every run.

Plan step 6 check: frames and input messages sent through the simulator at 5 % loss (each way) arrive whole: at least 99 % of video frames rebuilt byte for byte, and every input message delivered once and in order.

Measured 26 Oct in the cloud session (tests `BadNetworkTests`):

| Test | Result |
| --- | --- |
| Video, 1 minute at 60 fps (keyframe of 150–200 kB every 300 frames, 5–35 kB between), 5 % loss, 20 ms ± 5 ms | 3,599 of 3,600 frames rebuilt byte for byte (99.97 %), 0 wrong; 14,834 data shards rebuilt from repair shards; 131 % of the video's bytes on the wire at this loss rate |
| Input, 5,000 messages, one every millisecond (every 50th 3 kB), 5 % loss each way, 30 ms ± 10 ms each way | All 5,000 once and in order; 932 resends; measured round trip 58 ms; delay median 94 ms, 99 % 178 ms, longest 207 ms. At one message a millisecond nearly every message waits behind a lost one, about a round trip and a half |
| Time per frame (Release build, both sides at 5 % loss) | 16.7 kB frame: 0.09 ms to make the packets, 0.09 ms to rebuild. 200 kB keyframe: 0.71 ms and 0.74 ms. Repair maths 32 bytes per step with AVX2 (16 with SSSE3) |

## Automatic quality

| Rule | Detail |
| --- | --- |
| Measure | Every 250 ms: round trip, loss, received rate |
| Step down | Delay rising or loss > 2 % → lower bitrate; then fps (60 → 30 → 15); then picture size (100 → 75 → 50 %) |
| Step up | Link stable for 2 s → one step back up |
| CPU guard | Sheron above 10 % CPU on the remote PC for 5 s → lower fps, then size |
| Quality menu | Auto (default) · Best speed (low delay first) · Best picture (sharp text first, fps may drop) |

## Input path

| Item | Detail |
| --- | --- |
| Keys | Sent as scan codes, so keyboard layouts work: the key's place on the keyboard travels, and the remote PC's own layout gives the letter. Key repeat travels as repeated key-downs |
| Mouse | Absolute position scaled to 0–65535 across the remote screen, buttons (left, right, middle, back, forward) and wheel (vertical and horizontal, 120 per notch). A button or wheel message carries the position too, so a click lands where it was made |
| Channel | Ordered and reliable (control messages over the reliable channel), sent ahead of video |
| Remote side | `SendInput`: mouse with absolute coordinates on the main monitor (the one Sheron captures), keys with KEYEVENTF_SCANCODE (and KEYEVENTF_EXTENDEDKEY for extended keys) |
| Stuck keys | The remote PC remembers which keys and buttons the viewer holds down. It lets go of all of them when the viewer sends "release all" (the viewer's window loses the keyboard or closes) and when the session ends or goes quiet, so nothing stays pressed |
| Viewer keys | The video window takes the keyboard when clicked. Windows keeps Alt+Tab, the Windows key and Ctrl+Alt+Del for the viewer's own PC; they are not sent (Ctrl+Alt+Del: Plan step 17) |
| Ctrl+Alt+Del | `SendSAS` from the service; the installer sets the Windows policy that allows a software Secure Attention Sequence |
| Pointer | Remote PC sends the shape when it changes, then positions. While the viewer's mouse is over the remote picture, the viewer's own Windows pointer takes the remote shape (built with `CreateIconIndirect`: colour with alpha, monochrome and masked-colour shapes), so it moves with no delay, and the drawn pointer is hidden. Elsewhere the drawn pointer shows where the remote pointer is |
| Limits until Plan step 17 | `SendInput` from a normal program cannot reach programs running as administrator, the UAC prompt or the lock screen (Windows blocks it). The service in Plan step 17 runs at system level for those |

Plan step 9 check, 1 Nov: Sher controlled his second laptop from his own over the iPhone hotspot with the one-file `Sheron.exe` (mouse, clicks, typing), and with keyframe on request the picture stayed clean after lost frames.

## Connect by IP (Plan step 8, developer mode)

Before the server exists (Plan steps 11–14), two PCs on the same network connect by IP address. This is a developer mode: no ID; the password and request work as in Plan step 16.

| Item | Rule |
| --- | --- |
| Remote PC | `Sheron.Win.exe --host [--port 47920]` opens a window that says the screen can be viewed and controlled, shows this PC's IP addresses, key fingerprint and one-time password, who is viewing, and when the viewer last used the mouse or keyboard. A viewer with the one-time password is shown there as a request with Accept and Deny (Plan step 16). Sharing and control last only while that window is open; there is no hidden mode |
| Viewer | `Sheron.Win.exe --view <IP>[:port]` opens the start window with that address filled in, to type the password (never on the command line, where other programs can read it); the viewer window shows the remote screen and its figures |
| Start window | Started with no switches, Sheron opens a small start window until the home screen exists (Plan step 15, PRD decision 1 Nov): "Share this screen" opens the remote PC's window; "View a PC" with an IP address and password typed in opens the viewer. Both choices drawn on the form; the IP and password boxes are the only real controls |
| One file | `dotnet publish src/Sheron.Win -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true` makes one `Sheron.exe` that holds .NET, the libraries and libvpl.dll and unpacks them to the user's temp folder when it starts. It is not signed yet (Plan step 20), so Windows SmartScreen asks first ("More info", "Run anyway") |
| Device keys | Kept in the secrets file from Plan step 16, so a PC has the same key every run; both windows show both fingerprints so they can be compared by eye |
| Socket | One UDP socket per side; receive and send buffers of 4 MB, so a keyframe's packets fit while they are read. A "connection reset" from Windows (an ICMP reply to an earlier packet) is ignored |
| Handshake | The viewer sends Hello every 500 ms until a Welcome comes, then Finish every 500 ms until the first sealed packet comes. The host answers a repeated Hello with the same Welcome. One viewer at a time: a new finished handshake replaces the old session |
| Capture | The remote PC starts capturing and encoding when a viewer's handshake finishes, so the first frame the viewer gets is a keyframe; it stops when the viewer goes |
| Keep-alive | Each side sends a reliable Ping every second, which also measures the round trip both ways. No packet for 5 s: the session is over |
| Pointer | Sent as reliable messages: the shape when it changes, the position when it moves (the position is ordered too for now; Plan step 9 decides) |

**Control messages** go over the reliable channel. Byte 0 is the type.

| Type | Byte 0 | Then |
| --- | --- | --- |
| Video format | 1 | Width, height (4 bytes each): sent before the first frame and whenever the size changes |
| Pointer shape | 2 | Kind (1 byte: 1 monochrome, 2 colour, 4 masked colour), width, height, pitch, hot spot X, hot spot Y (2 bytes each), the pixels |
| Pointer position | 3 | Visible (1 byte), X, Y (4 bytes each, may be negative) |
| Ping | 4 | Nothing |
| Mouse move (Plan step 9) | 5 | X, Y (2 bytes each, 0–65535 across the remote screen) |
| Mouse button | 6 | Button (1 left, 2 right, 3 middle, 4 back, 5 forward), down (1) or up (0), X, Y |
| Mouse wheel | 7 | Delta (2 bytes, signed, 120 per notch), horizontal (1) or vertical (0), X, Y |
| Key | 8 | Scan code (2 bytes), extended (1) or not (0), down (1) or up (0) |
| Release all | 9 | Nothing: the remote PC lets go of every key and button the viewer holds |
| Keyframe request | 10 | Nothing: the remote PC encodes its next frame as a keyframe (IDR), at once even if its screen is still |

**After a lost frame** (added 1 Nov, Plan step 9 test: on the iPhone hotspot, lost frames left smeared, broken pictures, which on a still screen stayed for minutes): the viewer sees a gap in the frame numbers, stops decoding, keeps showing the last clean picture, and sends a keyframe request; again every 300 ms until a keyframe arrives. The remote PC asks its encoder for an IDR (oneVPL: `mfxEncodeCtrl.FrameType` = I + reference + IDR; Media Foundation: `CODECAPI_AVEncVideoForceKeyFrame`) and, if its screen is still, encodes the last captured image again so the keyframe goes out within about 100 ms. This is the "new keyframe only when the viewer asks after loss" of the video path, brought forward from Plan step 10.

The viewer holds frames that come before the video format (at most 60) and decodes them once it is known.

Plan step 8 check: PC 2's screen shows on PC 1 at 60 fps.

Measured 1 Nov on Sher's two laptops, both on an iPhone hotspot (each packet crosses the air twice, laptop → phone → laptop), with `Sheron.exe` (one-file build), PC 2 sharing 1920 × 1080 with testufo running: 61 frames received per second, 9.6 Mbit/s, round trip 10.9 ms, arrival → on screen 12.4 ms average (29.4 longest), viewer CPU 1.5 %, hardware decoder. Keys matched on both screens. 43 frames a second were timed on screen; the rest arrived in bunches and were replaced by the next frame before the screen refreshed. 14 frames were lost over about a minute (8 data shards rebuilt): losses came in bursts larger than the repair shards, or a frame finished after a newer one and was dropped. Each lost frame can leave smears until the next keyframe (up to 5 s); Plan step 10 (jitter buffer, keyframe on request) is where this is fixed.

## Network

| Item | Detail |
| --- | --- |
| Server | One server to start: Oracle Cloud, Always Free, Oracle Linux 9; region chosen at sign-up, Singapore recommended (PRD decision 1 Nov, replacing Alibaba Cloud Hong Kong). More regions later |
| Stay online | Each PC keeps a TLS 1.3 connection to the server on TCP 443; heartbeat every 25 s |
| Connect | Viewer asks the server for the ID; the server tells both sides each other's addresses (LAN and public) |
| Direct link | Both sides send UDP probes to each other at the same time for up to 1.5 s (hole punching). A LAN address wins when both are on the same network |
| Relay | If direct fails: UDP 443 through the server. If UDP is blocked: TCP 443 (TLS) through the server |
| Reconnect | Link lost → try direct and relay at the same time; target ≤ 5 s |
| Packet size | ≤ 1,200 bytes, so packets fit through VPNs and mobile links |
| Later | Relays in Europe and the US; the server picks the closest relay by measured delay |

## Default ports (all configurable)

| Port | Where | Use |
| --- | --- | --- |
| TCP 443 | Server | Signalling (TLS) and the TCP relay fallback |
| UDP 443 | Server | Address check and UDP relay |
| UDP, chosen by Windows | Both PCs | Direct link |
| UDP 47920 | Remote PC | Developer connect by IP on the same network (Plan step 8); `--port` changes it |

## Security

| Item | Rule |
| --- | --- |
| Device key | Each install makes an ECDSA P-256 key pair; the private key is protected with DPAPI (machine scope, so the service can use it) |
| ID | The server gives a 9-digit ID tied to the device's public key |
| Key pinning | The viewer remembers the remote PC's key fingerprint on first connect and warns if it ever changes |
| Session keys | New ECDH P-256 key per session, signed by both device keys; HKDF-SHA256 → AES-256-GCM, one key per direction; counter nonces; replay window |
| End to end | The server only sees sealed packets and never has the session keys |
| Server link | TLS 1.3, or 1.2 where Windows 10 has no 1.3; the server's certificate key is pinned in the app either way (Rendezvous protocol, below) |
| One-time password | 8 characters from the 31 with no look-alikes: digits 2–9 and capital letters except I, L and O (no 0/O, 1/I/L; "32" before 1 Nov was a miscount). Shown as two groups of four; typed with or without the space, in either case. New after every session that was let in |
| Fixed password | 8 to 64 characters. PBKDF2-SHA256, 600,000 rounds, 16-byte salt; only the hash is stored |
| Password check | Only inside the encrypted channel |
| Wrong password | 3 wrong passwords in a row, from any viewer, lock the remote PC's ID for 30 s: it refuses every password, the right one too. A right password resets the count. The server also limits tries (20 Find a minute) |
| Consent | Without a fixed password the person at the remote PC must Accept. The banner is always shown. There is no hidden or silent mode |
| Logs | Never log passwords, keys or screen content |

### Passwords and consent (Plan step 16)

After the session handshake the remote PC sends nothing and takes no input until the viewer is let in. The password goes only inside the sealed session, which is tied to the remote PC's device key (with connect by ID, the key the server gave for that ID).

| Step | What happens |
| --- | --- |
| 1 | The viewer sends Access request with the password typed |
| 2 | Locked: answer Locked with the seconds left. The password is not checked and does not count |
| 3 | It is the one-time password: the person at the remote PC is asked (incoming request: viewer's address and key, Accept, Deny). Answer Asking with 30 s. Accept: Let in. Deny: Denied. No answer within 30 s: No answer |
| 4 | It is the fixed password: Let in at once, with nobody asked (unattended access) |
| 5 | Neither: Wrong, with the tries left; the third in a row also starts the 30 s lock. The viewer may try again in the same session |
| After | Let in: the remote PC starts capture and takes input. When that session ends, a new one-time password is made |

The one-time password is checked first (it is cheap); the fixed password costs one PBKDF2 (about 0.3 s), done off the link's thread. Messages sent before the viewer is let in, other than Access request, are dropped.

| Control message | Byte 0 | Then |
| --- | --- | --- |
| Access request (viewer → remote PC) | 11 | Length (1 byte), the password in UTF-8 (1–255 bytes) |
| Access answer (remote PC → viewer) | 12 | Result (1 byte: 1 let in, 2 asking the person, 3 wrong password, 4 locked, 5 denied, 6 no answer), tries left (1 byte), seconds (2 bytes: time left to answer, or lock time left) |

**Secrets file** `%ProgramData%\Sheron\secrets.bin`: DPAPI, machine scope (so the service of Plan step 17 can read it), with "Sheron 1 secrets" as extra entropy. Inside: version (1 byte, 1); the device private key (2-byte length, PKCS#8); the fixed password (1 byte: 0 none, 1 set; then rounds (4 bytes), salt (16), hash (32)). Machine-scope DPAPI lets any program on the PC open it, so the file itself is only for SYSTEM, Administrators and the Windows user who made it (nothing inherited from the folder). It is written to a new file, then moved over the old one. A file that cannot be opened (for example copied from another PC) is kept as `secrets.bin.unreadable` and new secrets are made: a new device key, so a new ID.

Plan step 16 check: unit tests for the password rules, the lock and the secrets format; session tests with two links on this machine: one-time password asks the person (Accept lets in, Deny and no answer refuse), the fixed password lets in at once, three wrong passwords lock even the right one for 30 s (PRD A3, A9). Windows tests for the DPAPI file. Then Sher checks A3 and A9 on his two laptops by IP.

Checked 2 Nov on Sher's two laptops over the iPhone hotspot, connected by IP: Accept let the viewer in, Deny and no answer for 30 s refused it (A3), and three wrong passwords locked out the right one for 30 s (A9). Each laptop kept its device key between runs (same fingerprint every time), so the secrets file works.

### Session handshake and sealed packets (Plan step 7)

Built only from standard pieces in .NET 8 (`ECDsa`, `ECDiffieHellman`, `HKDF`, `AesGcm`, SHA-256), put together for Sheron. The viewer starts; the remote PC (host) answers. Public keys are sent as the raw 64-byte point (X then Y). Signatures are ECDSA P-256 with SHA-256, 64 bytes (r then s).

| Message | Byte 0 | Then |
| --- | --- | --- |
| Hello (viewer → host) | 0x20 | Version 1 (1 byte); viewer's device public key (64); viewer's new session public key (64); 32 random bytes |
| Welcome (host → viewer) | 0x21 | Version 1; host's device public key (64); host's new session public key (64); 32 random bytes; host's signature (64) |
| Finish (viewer → host) | 0x22 | Version 1; viewer's signature (64) |

| Item | Rule |
| --- | --- |
| Host signs | "Sheron 1 host" followed by SHA-256 of (Hello, then Welcome up to the signature) |
| Viewer signs | "Sheron 1 viewer" followed by SHA-256 of (Hello, then the whole Welcome) |
| Session keys | ECDH of the two session keys; HKDF-SHA256 with the SHA-256 of (Hello, whole Welcome) as salt and "Sheron 1 session keys" as info gives 64 bytes: the first 32 are the viewer → host AES-256-GCM key, the last 32 the host → viewer key. Session private keys live only in memory and are thrown away after the handshake |
| Checks | Each side refuses a message with the wrong version or length, a public key that is not a point on P-256, or a signature that does not check. The host only accepts sealed packets after a good Finish |
| Fingerprint | SHA-256 of a device public key; shown as 8 groups of 4 hex digits (its first 16 bytes) |
| Key pinning | The viewer keeps each remote ID's fingerprint (`%AppData%\Sheron\known-keys.json`, Plan step 15). First connect: remembered. Same: go on. Different: the session stops with a warning, and the new key is only remembered if the person says so |

**Sealed packet** (every packet after the handshake): 25 of the 32 bytes kept for encryption.

| Bytes | Field |
| --- | --- |
| 0 | Kind = 0x10 |
| 1–8 | Packet counter of this direction: 0, 1, 2, … |
| 9– | The plain packet (video, reliable or ack), encrypted with AES-256-GCM |
| last 16 | GCM tag |

The GCM nonce is 4 zero bytes then the 8-byte counter; bytes 0–8 are authenticated too. Each direction has its own key, so a nonce is never used twice with one key. The receiver drops a packet whose tag does not check (any changed byte), and keeps a replay window: the highest counter seen and which of the 2,048 before it have come; a counter seen before, or older than the window, is dropped (checked before decrypting, marked only after the tag checks). 2,048 packets are about 2 s of video at 8 Mbit/s, far more than any jitter Sheron is built for.

Measured 26 Oct in the cloud session (Release build): sealing a 1,168-byte packet 2.3 µs, opening it 4.1 µs, so about 0.13 ms for a typical frame's 20 packets (encrypt budget: under 1 ms). A whole handshake took 11.6 ms the first time, start-up of the crypto included. Plan step 7 tests (`HandshakeTests`): a change to any byte of a sealed packet, or of the Welcome, is refused; a replayed packet is refused; a host key that differs from the one remembered for that ID gives `Changed`.

## Unattended access (Windows service)

| Item | Detail |
| --- | --- |
| Service | "Sheron Service" runs as LocalSystem and starts with Windows |
| Agent | The service starts the Sheron agent in the active console session (`WTSGetActiveConsoleSessionId`, `CreateProcessAsUser`), so it can see the lock screen and UAC prompts |
| Desktop switch | The agent follows desktop changes (`OpenInputDesktop`, `SetThreadDesktop`), so capture and input keep working across the lock screen and UAC |
| User switch | When the active session changes, the service restarts the agent in the new session |

## Windows UI

| Item | Rule |
| --- | --- |
| Toolkit | WinForms, custom-painted. Keep native controls few: Sher's PC runs window-hooking software that costs about 40 ms per control. Real controls only for text boxes and dropdowns |
| Video | The session window draws video with Direct3D 11 in one child window |
| DPI | Per-monitor DPI aware (PerMonitorV2), so capture, pointer and input all use real screen pixels |
| Style | Control Room (PRD decision #1): background #0E141B, panels #151E28, lines #243142, text #E6EDF3, muted #9FB0C3, accent amber #F2A93B, green #3FB871, red #E5534B. IBM Plex Sans / Mono, bundled |
| Screens | Home, session window, incoming request, banner, settings — see `docs/design/` |

## Third-party components (only these are approved)

| Component | Licence | Use |
| --- | --- | --- |
| Vortice.Windows (Direct3D11, DXGI, MediaFoundation), 3.8.3 | MIT | Capture, encode, decode and render interop. Brings Vortice.DirectX, Vortice.Mathematics, SharpGen.Runtime, SharpGen.Runtime.COM and System.Text.Json (all MIT) |
| Intel oneVPL dispatcher (libvpl) | MIT | Intel hardware encoder route (Plan step 4b, approved 25 Oct). The encoder itself (the GPU runtime) comes with the Intel graphics driver; nothing of Intel's is copied into our code |
| Serilog, Serilog.Sinks.File | Apache-2.0 | Logs |
| xUnit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | Tests |
| IBM Plex Sans / Mono fonts | SIL OFL 1.1 | Bundled fonts |

Everything else uses .NET 8 built-ins (`SslStream`, `AesGcm`, `ECDiffieHellman`, `ECDsa`, `HKDF`, `Rfc2898DeriveBytes`) and Windows APIs.

**Not allowed:** GPL, AGPL or LGPL code (for example RustDesk, FFmpeg, x264, TightVNC, UltraVNC), and copying any other remote-desktop product's code, protocol, name or look.

## Files

| Item | Where |
| --- | --- |
| Logs | `%ProgramData%\Sheron\logs\`, one file a day, kept 14 days (Serilog) |
| Settings | `%ProgramData%\Sheron\settings.json` (no secrets) |
| Secrets | `%ProgramData%\Sheron\secrets.bin` (DPAPI): device private key, fixed-password hash |
| Recent list | `%AppData%\Sheron\recent.json` (per Windows user) |
| Known keys | `%AppData%\Sheron\known-keys.json`: remote ID → device key fingerprint (key pinning; public data, no secrets) |
| Server | `/opt/sheron-server`, systemd unit `sheron-server`, logs to journald; data folder `/opt/sheron-server/data` (`--data`) with `server-certificate.pfx` (the TLS key, readable by the server's user only) and `registry.txt`. Install files in the repository's `deploy/` folder |

## Server

| Item | Detail |
| --- | --- |
| Machine | Oracle Cloud Always Free virtual machine with Oracle Linux 9 (PRD decision 1 Nov): the Arm (Ampere A1) shape, or the small AMD one to start. The server is a self-contained .NET 8 program for Linux (linux-arm64 or linux-x64), so the machine needs no .NET install; `firewalld` opens TCP 443 and UDP 443; it runs as a systemd service with only the right to use port 443 (Plan step 13) |
| Stores | ID ↔ public key and last-seen time, in one small file. No passwords, no screen data |
| Bandwidth | A relayed session uses about 0.3–2 Mbps; 100 Mbps carries about 50–100 relayed sessions at once. Direct links cost the server nothing |
| Limits | Connect attempts limited per IP address and per ID |

### Deployment (Plan step 13)

| Item | Detail |
| --- | --- |
| Machine | Instance `Sheron` in Oracle Cloud, Singapore (ap-singapore-1, AD-1), public IP **168.138.174.180**: VM.Standard.E2.1.Micro (AMD x86_64, 1 OCPU burstable, 1 GB, 0.5 Gbps), Always Free; Oracle Linux 9.8; log-in user `opc` with an SSH key kept only on Sher's laptop. VM.Standard.A1.Flex (Arm, more room, also Always Free) was out of capacity in Singapore on 1–2 Nov; the server can move there later. No domain (PRD decision 2 Nov): the app uses the IP address |
| Program | `dotnet publish src/Sheron.Server -c Release -r linux-x64` (AMD) or `linux-arm64` (Arm) `--self-contained -p:PublishSingleFile=true`: one file, `Sheron.Server`, with .NET inside. Invariant globalization, so it needs no ICU on the machine |
| Install | `deploy/install.sh` with `deploy/sheron-server.service`, copied to the machine with the program and run with `sudo bash install.sh`. It makes the `sheron` system user (no login), puts the program in `/opt/sheron-server` and the data in `/opt/sheron-server/data` (only `sheron` can read it), sets SELinux labels, opens TCP and UDP 443 in `firewalld`, and enables and starts the service. Run again to update: the data folder is kept |
| Service | Runs as `sheron` with only `CAP_NET_BIND_SERVICE` (port 443); the rest of the machine read-only to it (`ProtectSystem=strict`, own `/tmp`); restarts 2 s after any stop it did not ask for; log in journald (`journalctl -u sheron-server`), where the pin is too |
| Oracle network | VCN `Sheron`, public subnet 10.0.0.0/24. Its default security list lets in TCP 443 and UDP 443 from anywhere (0.0.0.0/0, stateful), besides SSH (TCP 22) |
| Server pin | `WSKEIND259+ky3mRLnRCl9IkaKc9YxIaVzsAhDs3KpQ=` (SHA-256 of the certificate's public key, from the server's log; public, not a secret). A new machine keeps it only if the data folder is copied over |
| Check | `Sheron.Win.exe --server <IP>[:port] --pin <pin>` opens a developer window that connects to the server, checks its pin, registers this PC and runs the UDP address check, and shows this PC's ID and public UDP address (Plan step 13 "Done when": a client registers from Sher's laptop) |

Plan step 13 check, 2 Nov: `systemctl status sheron-server` showed active (running), 59.5 MB of memory; from Sher's laptop on the iPhone hotspot the check window showed Registered with ID 713 152 388, the pin matched, and the UDP address check worked.

### Rendezvous protocol (Plan step 11)

Sheron's own protocol between each PC and the server. The server only introduces PCs: it never sees session keys or the screen.

| Item | Rule |
| --- | --- |
| Link | TCP, port 443 by default, inside TLS (1.3 where the PC has it; Windows 10 may only offer TLS 1.2). The app checks the server's certificate by its pinned key: SHA-256 of the certificate's public key (SubjectPublicKeyInfo), not by a certificate authority, so a self-signed certificate on a bare IP address is enough |
| Server certificate | Made by the server on its first start (ECDSA P-256, self-signed, 10 years) and kept in its data folder; the server prints its pin so the app can be built with it |
| Messages | 2-byte length (little-endian), then 1 byte type, then the body; at most 4,096 bytes. A malformed message closes the connection |
| Registering | The server sends a challenge (32 random bytes). The PC answers Register: its device public key (64 bytes), its signature over "Sheron 1 register" + the challenge, and its addresses. The server checks the signature, then gives the ID tied to that key: the same key always gets the same ID |
| IDs | 9 digits, 100 000 000 to 999 999 999, chosen at random and never reused. Shown in groups of three |
| Registry | One text file, one line per device: ID, public key (hex), last seen (Unix seconds). Rewritten whole on a new device (written to a new file, then moved over the old one) and every 5 minutes |
| Addresses | Each PC sends its LAN addresses with the UDP port of its link socket; the server adds the public UDP address its address check saw (Plan step 12; none while UDP is blocked) |
| Finding a PC | Find (a 9-digit ID): if that PC is online, the server makes a 16-byte connect ID and sends Found to the asker and Incoming to the target, each with the other's ID, device public key and addresses. The viewer then checks the handshake's host key against the key the server gave for that ID. Otherwise NotFound: unknown ID, or offline |
| Staying online | The PC sends Ping every 25 s; the server answers Pong and drops a connection silent for 60 s. One connection per device: a new one replaces the old |
| Limits | At most 20 Find a minute per connection and 30 new connections a minute per IP address; more are refused |

| Type | Byte | Direction | Body |
| --- | --- | --- | --- |
| Challenge | 1 | server → PC | Version 1 (1 byte), 32 random bytes |
| Registered | 2 | server → PC | ID (4 bytes), UDP token (16, Plan step 12) |
| Found | 3 | server → viewer | Connect ID (16), ID (4), device public key (64), addresses |
| Incoming | 4 | server → target | Connect ID (16), viewer's ID (4), viewer's device public key (64), addresses |
| NotFound | 5 | server → viewer | ID (4), reason (1 byte: 1 unknown, 2 offline, 3 too many tries) |
| Pong | 6 | server → PC | Nothing |
| Register | 10 | PC → server | Device public key (64), signature (64), addresses |
| Find | 11 | PC → server | ID (4) |
| Ping | 12 | PC → server | Nothing |

Addresses: a count (1 byte), then per address its kind (1 byte: 1 LAN, 2 public as the server sees it), family (1 byte: 4 or 6), the address (4 or 16 bytes) and the UDP port (2 bytes).

Plan step 11 check: two clients on one machine register with a local server, get different IDs, keep them when they come back, and find each other.

### Direct link, relay and TCP fallback (Plan step 12)

Each PC has one UDP socket for everything: address check, punching, relay and the session itself, so its router keeps one mapping. Video and input stay sealed end to end on every path; the relay only forwards bytes.

| Stage | How |
| --- | --- |
| Address check | Right after registering, the PC sends Address check (UDP, with the 16-byte token from Registered) to the server's UDP 443, three times 300 ms apart until it answers. The server answers Address seen: the public IP and port it saw. From then on Found and Incoming carry that public address. No answer: this PC's UDP is blocked, and it will use the TCP relay |
| Punching | On Found (viewer) and Incoming (remote PC), both send Punch (connect ID, role) to each of the other's addresses, LAN and public, every 100 ms for up to 1.5 s, and answer each Punch with Punch ack. Sending out opens each router for the other's packets. A Punch from the remote PC proves only that packets reach the viewer, so the viewer answers it with Punch ack, adds its address to the ones it punches, and goes on; it takes the first address that sends a **Punch ack** (its own Punch got through, and the answer came back: both ways work) and starts the session handshake there. The remote PC takes whichever address the Hello comes from (changed 2 Nov after a test where the viewer chose a path that worked one way only) |
| Direct handshake check | If the session handshake on a direct path has not finished within 3 s, the viewer drops that path and asks for the relay (below). The remote PC joins a relay it is asked for even after it took a direct path for that viewer |
| UDP relay | No answer within 1.5 s: the viewer sends Relay request (TLS) with the connect ID; the server passes it to the remote PC. Each side sends Relay join (UDP: connect ID, role) to the server's UDP 443; when both have joined, the server answers Relay ready and forwards every other packet from one side's address to the other's |
| TCP 443 relay | A PC whose address check got no answer joins over its TLS connection instead (Relay join TCP), and its packets travel inside Relay data messages. The server forwards between the two sides whatever each used: UDP to UDP, TCP to UDP, or TCP to TCP. Packets for a PC on TCP wait in a queue of 1,024 at the server (4,096 at the PC); when it is full new ones are dropped, as on a full network |
| Rules | Only the two PCs a connect ID was made for can join its relay (the ID is 16 random bytes known only to them and the server; a TCP join must also come from one of their two registered connections). A relay ends when either side's TLS connection closes, or after 60 s with no packets; Relay data after that is dropped quietly |

| UDP message | Byte 0 | Body |
| --- | --- | --- |
| Address check | 0x30 | Token (16) |
| Address seen | 0x31 | Family (1 byte: 4 or 6), IP (4 or 16), port (2) |
| Relay join | 0x32 | Connect ID (16), role (1 byte: 1 viewer, 2 remote PC) |
| Relay ready | 0x33 | Connect ID (16) |
| Punch | 0x34 | Connect ID (16), role (1) |
| Punch ack | 0x35 | Connect ID (16) |

| TLS message | Byte | Direction | Body |
| --- | --- | --- | --- |
| Registered | 2 | server → PC | ID (4), UDP token (16) |
| Relay request | 7 | server → remote PC | Connect ID (16) |
| Relay ready | 8 | server → PC on TCP | Connect ID (16) |
| Relay data | 9 | server → PC on TCP | One packet |
| Relay request | 13 | viewer → server | Connect ID (16) |
| Relay join TCP | 14 | PC → server | Connect ID (16), role (1) |
| Relay data | 15 | PC on TCP → server | One packet |

**Network simulator** for these tests (`Sheron.Sim`): in-memory UDP between simulated hosts in real time, each behind a router of its own site. A router maps an inside address to one public port for all destinations (or a new port for each destination: a "symmetric" router; punching fails when both PCs are behind one) and lets a packet in only from IP addresses the inside host has sent to. Hosts at one site reach each other by LAN address only: no "hairpin" through their own router's public address, which many routers cannot do. A site's UDP can be blocked, and so can traffic between two sites, both ways or one way only; a blocked packet still leaves through its own router (which then lets answers in) and is dropped on the way, as a far-end firewall would.

Plan step 12 check: through the simulator, two PCs at different sites connect directly; with the direct path blocked they connect through the UDP relay; with one side's UDP blocked they connect through the TCP 443 relay; in each case a session runs (video frames and control messages arrive).

### Connect by ID (Plan step 14)

| Item | Rule |
| --- | --- |
| Server | The app is built with the server's address and pin (TRD › Deployment: 168.138.174.180, TCP and UDP 443). The settings screen (Plan step 15) can change the address |
| Remote PC | The sharing window opens its UDP socket (47920), registers with the server (this PC's ID shows), runs the address check, and keeps the server's view of its UDP address fresh with an address check every 25 s. A lost server link is tried again every 10 s. Connect by IP keeps working on the same socket |
| One socket, two readers | On the remote PC one thread reads the socket and hands each packet on by its first byte: rendezvous messages (0x30–0x35) to the connector, everything else to the session. While the connector is finding a path it also gets a copy of each Hello, to tell direct from relay. The session (Plan step 8 link) runs all the time, so a Hello on any path finds it. Only one viewer's path is worked out at a time |
| TCP relay on the remote PC | When its UDP is blocked, the TCP relay is joined to the same session: packets from the relay come in beside the UDP ones, and packets to the relay's address go out over TLS. The relay's address is the server's IP with port 0, so it never mixes with the server's UDP address |
| Viewer | "View a PC" with a 9-digit ID: the viewer window opens its own UDP socket, registers, runs the address check, finds the ID, works out the path (direct, relay, TCP 443), then starts the session on it. Unknown ID, offline, or too many tries are shown as such |
| Remote PC's key | The server gives the remote PC's device key with Found. The viewer checks the session handshake's key against it before sending the password; a different key stops the session with a warning, and the password is never sent |
| Path shown | Both windows show the path: Direct (with the address), Relay through the server, or TCP 443 relay. This is what PRD A2 and the step 14 check look at; the toolbar of Plan step 15 shows it the same way |
| One registration per PC | A PC registers once; a second window registering the same PC replaces the first. So the sharing window and a viewer window are not open together on one PC |

Plan step 14 check: Sher's laptop on the iPhone hotspot connects by ID to a second PC on another network; both windows show Direct or Relay.

## Risks

| # | Risk | What we do |
| --- | --- | --- |
| R1 | Hardware encoders differ between NVIDIA, Intel and AMD; some ignore low-latency settings | Test all three; software encoder as fallback |
| R2 | Antivirus and SmartScreen block unsigned remote-control apps | Code-signing certificate (PRD open decision 5) |
| R3 | China cross-border links slow down UDP | TCP 443 fallback; relay in Hong Kong |
| R4 | Mobile data jitter (66–100 ms in the 25 Oct test) | Adaptive jitter buffer and quality |
| R5 | Misuse by scammers | Consent prompt, banner that cannot be hidden, no silent mode |
| R6 | Relay bandwidth cost | Direct link first; relay only when needed |
| R7 | Ctrl+Alt+Del needs the service and a Windows policy | Installer sets the policy; tested in step 17 |
| R8 | Oracle may reclaim an Always Free machine that stays idle for 7 days (low CPU and network), and the rendezvous server is mostly idle | Watch it while testing; before release move to a paid server (PRD decision 2 Nov) or upgrade the Oracle account to Pay As You Go, where Always Free machines are not reclaimed |

## Measured network — 25 Oct 2026

Sher's laptop on an iPhone hotspot (mobile data); TCP connect time to port 443, 20 tries each.

| Place | Min | Average | Max | Jitter | Failed |
| --- | --- | --- | --- | --- | --- |
| Singapore (Alibaba) | 99 ms | 202 ms | 581 ms | 72 ms | 0 % |
| Hong Kong (Alibaba) | 142 ms | 227 ms | 555 ms | 98 ms | 0 % |
| Hong Kong (AWS) | 218 ms | 295 ms | 625 ms | 66 ms | 0 % |
| Shenzhen (Alibaba) | 312 ms | 382 ms | 652 ms | 90 ms | 0 % |
| Guangzhou (Tencent) | 407 ms | 505 ms | 724 ms | 100 ms | 0 % |

Laptop to phone over Wi-Fi: 1–3 ms, 0 % loss, so the jitter comes from the mobile network. A repeat on a fixed line is still to do.
