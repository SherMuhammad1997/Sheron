# Sheron — rules for Claude Code

A simple, fast remote-desktop app for Windows: connect to any PC in the world by a 9-digit ID and password, see its screen and control it. Version 1 is Windows ↔ Windows only. Owner: Sher Muhammad.

## Read before every session

| Order | File | Why |
| --- | --- | --- |
| 1 | `docs/troubleshooting.md` | Problems already solved — check here before debugging anything |
| 2 | `docs/03-Implementation-Plan.md` | The 21 steps; find the step Sher asked for |
| 3 | `docs/02-TRD.md` | Architecture, stack, ports, security, licences |
| 4 | `docs/01-PRD.md` | Scope, targets, acceptance checks |
| — | `docs/design/` | Screen designs |

## How we work

1. **One step per session.** Do only the step Sher names from the Implementation Plan. Say the step number first. Stop when its "Done when" check passes and wait for Sher's OK.
2. **One action at a time for Sher.** When Sher must do something by hand (install, server, second PC, firewall), give ONE action, then wait for his result or screenshot.
3. **Short answers.** Plain words, tables where possible.
4. **Done means tested.** Run `dotnet build` and `dotnet test` before saying a step is done. Checks on real PCs are confirmed by Sher.
5. **One Git commit per step.** Message starts with the step number, e.g. `Step 2: create .NET solution`.
6. **Log every fix.** Every problem fixed goes into `docs/troubleshooting.md` (symptom, root cause, exact fix, files) in the same commit.
7. **Docs before code.** If a decision changes, update the document in `docs/` first, tell Sher, then change the code.
8. **No extras.** Build only what the documents say. Ask before adding features, screens or settings.
9. **Ask before any new dependency.** Only the libraries in TRD › Third-party components are approved.

## Hard rules

| Rule | Detail |
| --- | --- |
| No GPL / AGPL / LGPL in the product | Dependencies must allow closed-source sale (MIT, BSD, Apache, MS-PL; SIL OFL for bundled fonts). No RustDesk, FFmpeg, x264, TightVNC or UltraVNC code |
| No copying other products | Never copy the code, protocol, name, logo or look of AnyDesk, UltraViewer, TeamViewer, RustDesk or any other remote-desktop product |
| No hidden access | Never build a silent, hidden or stealth mode. Without a fixed password the remote user must accept; the banner is always shown during a session |
| Secrets | DPAPI for the device private key and the fixed-password hash. Never log passwords, keys or screen content |
| Passwords | PBKDF2-SHA256, 600,000 rounds. One-time passwords: 8 characters, new after every session. 3 wrong tries → 30 s lock |
| Encryption | End to end: the server never has session keys or screen data |

## Technical rules

| Area | Rule |
| --- | --- |
| Stack | C# 12 / .NET 8. `Sheron.Core` (net8.0, no Windows code), `Sheron.Win` (net8.0-windows), `Sheron.Server` (net8.0, Linux), `Sheron.Sim`, `tests/Sheron.Tests` (xUnit, net8.0, runs on Linux too), `tests/Sheron.Win.Tests` (xUnit, net8.0-windows, Windows only) |
| Linux builds | Claude Code cloud sessions run on Linux: `dotnet build` builds everything (`EnableWindowsTargeting`), `dotnet test` runs `Sheron.Tests` only. Win tests are confirmed on Sher's PC |
| Speed first | Every change to the video or input path reports its time per frame; no change may push a PRD target over its limit |
| Windows UI | WinForms, custom-painted. Keep native control count LOW — Sher's PC runs window-hooking software that costs ~40 ms per control. Real controls only for text boxes and dropdowns |
| Network | Direct UDP first, relay second, TCP 443 last. Packets ≤ 1,200 bytes |
| Ports | Every port is configurable; defaults are in TRD › Default ports |
| Testing without two PCs | Logic goes in Core with unit tests; use `Sheron.Sim` for delay, jitter, loss and blocked UDP |
| Logs | Serilog, daily files in `%ProgramData%\Sheron\logs\`, kept 14 days |

## Folder layout

| Folder | Contents |
| --- | --- |
| `docs/` | Project documents and the troubleshooting log |
| `docs/design/` | Design canvas copies (`.dc.html`) |
| `src/Sheron.Core` | All logic, no Windows code |
| `src/Sheron.Win` | Windows app, capture, codec, input, service |
| `src/Sheron.Server` | ID, signalling and relay server |
| `src/Sheron.Sim` | Bad-network simulator |
| `tests/` | xUnit tests |

## Decisions still open — ask Sher, do not guess

See PRD › Decisions still open (UI style, server, domain, installer, code signing, product name, price).
