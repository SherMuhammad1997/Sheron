# Sheron – Implementation Plan

Oct 25, 2026 · @Sher Muhammad

## How the build runs

The build is 21 steps in 5 phases. A fast picture on one PC comes first: if the speed targets fail there, nothing else matters. This is document 3 of 3; no dates are set.

```text
Phase 0  Repository and solution             steps 1-2
   v
Phase 1  Fast picture on one PC              steps 3-5
   v
GATE 1: capture -> encode -> decode -> display <= 25 ms on one PC
   v
Phase 2  Two PCs on the same network         steps 6-10
   v
GATE 2: same-network targets met (PRD A7)
   v
Phase 3  Anywhere in the world               steps 11-14
   v
GATE 3: two PCs on different internet links connect by ID
   v
Phase 4  Product                             steps 15-21
   v
GATE 4: all 11 PRD acceptance checks pass
```

| Rule | For every step |
| --- | --- |
| One step per session | Each step is one Claude Code session |
| Read first | Read the three documents in `docs/` and `docs/troubleshooting.md` before writing code |
| Done means tested | A step is done only when its "Done when" check passes; Sher confirms checks on real PCs |
| Commit | One Git commit per step, with the step number in the message |
| Log every problem | Each fixed problem goes into `docs/troubleshooting.md`: symptom, cause, exact fix, files |
| Change the docs first | A change of plan updates the documents before the code |

## Phase 0 — Repository and solution

| # | Step | Done when |
| --- | --- | --- |
| 1 | Create the private GitHub repository, the three documents, the design canvas copies in `docs/design/`, `CLAUDE.md` and an empty troubleshooting log | First commit pushed |
| 2 | Create the .NET solution: `Sheron.Core`, `Sheron.Win`, `Sheron.Server`, `Sheron.Sim`, `tests/Sheron.Tests` | `dotnet build` and `dotnet test` pass |

## Phase 1 — Fast picture on one PC (Gate 1)

| # | Step | Done when |
| --- | --- | --- |
| 3 | Screen capture with DXGI Desktop Duplication: changed areas, pointer shape and position; a debug window shows capture fps and time per frame | 60 fps capture; ≤ 1 % CPU on a still screen |
| 4 | H.264 encoder through Media Foundation: hardware first, software fallback, low-latency settings; the debug window shows the encoder name and time per frame | 1080p frame encodes in ≤ 5 ms on a hardware encoder |
| 5 | Decoder and Direct3D 11 renderer in a loop on the same PC, pointer drawn locally; capture-to-display timer | Loop shows the screen; timer ≤ 25 ms |

## Phase 2 — Two PCs on the same network (Gate 2)

| # | Step | Done when |
| --- | --- | --- |
| 6 | Core packets: framing (≤ 1,200 bytes), FEC, reliable ordered input channel; bad-network simulator in `Sheron.Sim` | Unit tests pass, including 5 % loss through the simulator |
| 7 | Core encryption: ECDH handshake signed by device keys, AES-256-GCM, key pinning, replay window | Tests pass; a changed packet is rejected; a changed key raises the warning |
| 8 | Connect by IP on the same network, view only (developer mode) | PC 2's screen shows on PC 1 at 60 fps |
| 9 | Remote control: mouse, keyboard by scan code, wheel; pointer from the remote shape | Sher controls PC 2 from PC 1 |
| 10 | Automatic quality, jitter buffer, toolbar delay and fps; test through the simulator at 300 ms ± 100 ms and 5 % loss | Stays usable; no freeze longer than 1 s; PRD A7 numbers recorded |

## Phase 3 — Anywhere in the world (Gate 3)

| # | Step | Done when |
| --- | --- | --- |
| 11 | Server: ID registry and signalling over TLS on TCP 443 (runs on Linux) | Two local clients register, get IDs and find each other |
| 12 | Server: address check, UDP hole punching, UDP relay, TCP 443 relay fallback | Simulator tests: blocked direct link uses the relay; blocked UDP uses TCP 443 |
| 13 | Sher rents the server (PRD open decisions 2 and 3); deploy as a systemd service | `systemctl status sheron-server` shows active; a client registers from Sher's laptop |
| 14 | Connect by ID over the internet | Sher's laptop on the hotspot connects to a second PC on another network; toolbar shows Direct or Relay |

## Phase 4 — Product (Gate 4)

| # | Step | Done when |
| --- | --- | --- |
| 15 | Screens from the design: home, session toolbar, incoming request, banner, settings (custom-painted) | Screens match the design canvas; Sher approves |
| 16 | One-time and fixed passwords (PBKDF2), DPAPI secrets, 3 tries then 30 s lock | Unit tests pass; PRD A3 and A9 pass |
| 17 | Unattended service: agent in the active session, lock screen, UAC, Ctrl+Alt+Del | PRD A5 passes |
| 18 | Reconnect after a drop; Serilog logs; nothing secret in logs | Hotspot off and on → session back in ≤ 5 s; log check passes |
| 19 | Installer (PRD open decision 4): app, service, policy for Ctrl+Alt+Del, fonts | Clean install and uninstall on a fresh Windows 10 and 11 PC |
| 20 | Code signing (PRD open decision 5) | Signed installer runs with no SmartScreen block |
| 21 | Field tests: Sher ↔ a PC in Hong Kong or China; Sher ↔ a PC on a fixed line | All 11 PRD acceptance checks pass; numbers added to the TRD |
