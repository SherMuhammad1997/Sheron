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
| Viewer | Jitter buffer 0–100 ms that adapts to the link; hardware decode (Media Foundation / DXVA); Direct3D 11 flip-model swap chain; pointer drawn on the viewer | 2–5 ms + buffer |
| Still screen | No change → no video packets, only a keep-alive | ≈ 0 |

Capture test window (developer only, Plan steps 3–4): `Sheron.Win.exe --capture-test` shows capture fps, time per frame, changed area, pointer, the encoder, encode time, bitrate and Sheron's own CPU use. Add `--software-encoder` to skip the hardware encoder.

The capture device is made with BGRA and video support and is multithread-protected, because the hardware encoder uses it from its own threads.

While video runs, Sheron asks Windows for a 1 ms timer (`timeBeginPeriod`) and opts out of power throttling (`SetProcessInformation`, ProcessPowerThrottling: execution speed and ignore-timer-resolution off), so it is not slowed down when its window is hidden. With the default 15.6 ms timer, Intel Quick Sync took 16.3 ms per frame (25 Oct).

Measured on Sher's laptop, 25 Oct (Intel Graphics, 1920 × 1200 at 60 Hz): capture 60 fps, 0.15 ms average and 0.22 ms max per frame; Sheron CPU 0.0 % on a still screen and 0.4 % while capturing 60 fps.

Measured 25 Oct, Intel Quick Sync through Media Foundation (GPU input, 60 fps, 8 Mbps): encoder's own time at least 6.5 ms in every setting, 7.7–8.5 ms on average once warm; round trip 8–9 ms; CPU 0.7 %. The fastest preset made no difference. Memory input: 6.0 ms minimum inside the encoder but 13.3 ms round trip (CPU copy).

**Intel route (Plan step 4b):** on Intel GPUs, encode through Intel oneVPL instead: the D3D11 device is handed to oneVPL, the captured frame stays on the GPU, low-power encoding (VDEnc), target usage 7 (fastest), async depth 1, no B-frames, low-delay rate control. Media Foundation stays the route for NVIDIA and AMD, and the fallback when oneVPL is missing or fails.

Measured 26 Oct, Intel oneVPL 2.15 runtime (driver 32.0.101.7076, Core 7 240H, 1920 × 1200, 60 frames after warm-up): copy into the encoder's surface 6.71 ms average (5.85 minimum); importing our texture 6.56 ms (5.92); Media Foundation in the same run 6.46 ms (5.55). All routes share the same floor of about 6 ms, the chip's own encode time; the copy route is the default (import is still experimental in oneVPL). A second run the same day: 7.55, 7.28 and 7.37 ms with the same minimums, so averages swing by about 1 ms between runs. The 5-second timing test then passed (≤ 8 ms). Live in the capture test window, with the browser drawing a 60 fps animation on the same GPU: 8.36 ms inside the encoder, 8.77 ms round trip, 60 fps, 0 frames held back, Sheron CPU 0.6 %.

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
| Keys | Sent as scan codes, so keyboard layouts work |
| Mouse | Absolute position scaled to 0–65535, buttons and wheel |
| Channel | Ordered and reliable, sent ahead of video |
| Remote side | `SendInput` |
| Ctrl+Alt+Del | `SendSAS` from the service; the installer sets the Windows policy that allows a software Secure Attention Sequence |
| Pointer | Remote PC sends the shape when it changes, then positions; the viewer draws it |

## Network

| Item | Detail |
| --- | --- |
| Server | One server to start: Alibaba Cloud Hong Kong (PRD decision #2). More regions later |
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

## Security

| Item | Rule |
| --- | --- |
| Device key | Each install makes an ECDSA P-256 key pair; the private key is protected with DPAPI (machine scope, so the service can use it) |
| ID | The server gives a 9-digit ID tied to the device's public key |
| Key pinning | The viewer remembers the remote PC's key fingerprint on first connect and warns if it ever changes |
| Session keys | New ECDH P-256 key per session, signed by both device keys; HKDF-SHA256 → AES-256-GCM, one key per direction; counter nonces; replay window |
| End to end | The server only sees sealed packets and never has the session keys |
| Server link | TLS 1.3; the server's certificate key is pinned in the app |
| One-time password | 8 characters from a 32-character set with no look-alikes (no 0/O, 1/I/L); new after every session |
| Fixed password | PBKDF2-SHA256, 600,000 rounds, 16-byte salt; only the hash is stored |
| Password check | Only inside the encrypted channel |
| Wrong password | 3 tries → 30 s lock on that ID; the server also limits tries |
| Consent | Without a fixed password the person at the remote PC must Accept. The banner is always shown. There is no hidden or silent mode |
| Logs | Never log passwords, keys or screen content |

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
| Server | `/opt/sheron-server`, systemd unit `sheron-server`, logs to journald |

## Server

| Item | Detail |
| --- | --- |
| Machine | Ubuntu 24.04 LTS, 2 vCPU, 2 GB RAM to start |
| Stores | ID ↔ public key and last-seen time, in one small file. No passwords, no screen data |
| Bandwidth | A relayed session uses about 0.3–2 Mbps; 100 Mbps carries about 50–100 relayed sessions at once. Direct links cost the server nothing |
| Limits | Connect attempts limited per IP address and per ID |

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
