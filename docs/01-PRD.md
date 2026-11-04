# Sheron – Product Requirements (PRD)

Oct 25, 2026 · @Sher Muhammad · "Sheron" is the working name

## What it is

A small Windows app that lets one PC see and control another PC from anywhere in the world, using a 9-digit ID and a password. Simple, professional, and as fast as the network allows. It is **not** feature-rich on purpose. This is document 1 of 3.

## Who uses it

| User | Need |
| --- | --- |
| Sher (field engineer) | Fix customer PCs and run software on a site PC without travelling |
| Person at the remote PC | Let someone in safely, see who is connected, stop it at any time |
| Small companies (later, paid) | Reach their own PCs when nobody is there |

## Version 1 features

| # | Feature | Detail |
| --- | --- | --- |
| F1 | ID | The server gives each PC a 4-character ID: 4 digits while any are free (0000–9999), then digits and letters (no look-alikes). Changed 4 Nov: no one-time password |
| F2 | Connect by ID | Type the partner ID and Connect; the person at the remote PC accepts and the session opens. A password is typed only for unattended access (F5). A Recent list keeps the last 10 PCs |
| F3 | Accept or deny | Without a fixed password the remote PC shows a request; it denies by itself after 30 s |
| F4 | View and control | Screen, mouse, keyboard, Ctrl+Alt+Del. The mouse pointer is drawn on the viewer's PC, so it never lags |
| F5 | Unattended access | Fixed password set on the remote PC; works with nobody there, also on the lock screen and UAC prompts |
| F6 | Banner on the remote PC | "Controlled by …" with Disconnect, shown for the whole session; it cannot be hidden |
| F7 | Automatic quality | Changes bitrate, frame rate and picture size to suit the link. Quality menu: Auto, Best speed, Best picture |
| F8 | Live link info | The session toolbar shows delay (ms), fps, Direct link or Relay, and the remote screen size |
| F9 | Works from anywhere | Direct link first; relay through the server if direct fails; TCP 443 if UDP is blocked |
| F10 | End-to-end encryption | The server passes sealed packets only; it cannot see the screen or the keys |
| F11 | Settings | Unattended password, PC name, start with Windows, default quality, server address |

Version 1 shows the **main monitor** only.

## Not in version 1 (ask Sher before adding)

File transfer, clipboard sync, chat, sound, session recording, more than one monitor, accounts or address book, Mac / Linux / Android, connect by IP with no internet, USB / COM port forwarding, viewer in a web browser.

## Targets

| Item | Target | How we check |
| --- | --- | --- |
| Delay on the same network | ≤ 35 ms from capture to display at 1080p 60 fps (hardware encoder) | Timer in the debug overlay |
| Delay over the internet | Link round trip + ≤ 40 ms | Toolbar delay compared with ping to the server |
| Mouse pointer | Instant (drawn on the viewer) | Look |
| CPU on the remote PC, office work | ≤ 5 % with a hardware encoder; ≤ 20 % without | Task Manager, 5 minutes |
| Still screen | ≤ 1 % CPU and ≤ 50 kbps | Task Manager and toolbar |
| Memory | ≤ 150 MB on each PC | Task Manager |
| Bad network | Usable at 300 ms ± 100 ms jitter (round trip, as ping shows it) and 5 % loss; no freeze longer than 1 s | `Sheron.Sim` |
| Reconnect | ≤ 5 s after the network drops and comes back | Turn the hotspot off and on |
| UDP blocked | Falls back to TCP 443 by itself within 3 s | Firewall rule that blocks UDP |
| Windows versions | Windows 10 (1809 or newer) and Windows 11, 64-bit | Test PCs |

## Acceptance checks (all must pass for version 1)

| # | Check |
| --- | --- |
| A1 | Installed on two PCs, each shows its ID within 5 s of starting |
| A2 | PC 1 connects to PC 2 by ID (PC 2 accepts), each on a different internet connection (for example hotspot and office line) |
| A3 | PC 2 shows the request; Deny blocks it; no answer denies it after 30 s |
| A4 | Mouse, keyboard and Ctrl+Alt+Del work; the pointer feels instant |
| A5 | Unattended: PC 2 is locked with nobody there; PC 1 connects with the fixed password and unlocks it |
| A6 | The banner shows for the whole session; Disconnect on PC 2 ends it at once |
| A7 | The same-network targets above are met (delay, CPU, still screen) |
| A8 | With UDP blocked on PC 2, the session still connects (TCP 443) |
| A9 | A wrong fixed password 3 times locks that ID for 30 s; 3 refused requests from one PC stop that PC's requests for 10 minutes |
| A10 | A Wireshark capture of our own link shows no readable screen data; server logs hold no passwords |
| A11 | Field test from Sher's PC to a PC in Hong Kong or China; numbers recorded in `docs/02-TRD.md` |

## Decided

| Date | Decision |
| --- | --- |
| 25 Oct | Repository name Sheron, private |
| 25 Oct | Version 1 is Windows ↔ Windows only |
| 25 Oct | Simple app, few features, speed first |
| 25 Oct | #1 UI style: Control Room (same as Virtual Repeater, drawn in the design canvas) |
| 25 Oct | #2 Server: Alibaba Cloud Hong Kong (best route in the 25 Oct test); changed to Oracle Cloud on 1 Nov, below |
| 25 Oct | Unattended access (F5) is in version 1 |
| 25 Oct | Version 1 shows the main monitor only; no file transfer or clipboard |
| 25 Oct | Encoder target stays ≤ 5 ms per frame. Intel GPUs get their own route through Intel oneVPL (Plan step 4b), because Intel's Media Foundation encoder cannot go below about 6.5 ms at 1920 × 1200 |
| 26 Oct | Intel oneVPL measured the same floor (about 6 ms at 1920 × 1200 on the Core 7 240H: the chip's own encode time), so the encoder target is ≤ 7 ms average on Intel built-in graphics and stays ≤ 5 ms on NVIDIA and AMD. oneVPL is the default route on Intel (option A) |
| 26 Oct | The average swings by about 1 ms between runs (6.7 and 7.6 ms; floor 6 ms), so the Intel target is ≤ 8 ms average, measured over 5 s (300 frames) (option A) |
| 1 Nov | Order after step 9 (Sher: "start preparing for cloud deployment"): server steps 11 and 12, then passwords (step 16) so the server never goes online without them, then deployment (13) and connect by ID (14); step 10 after that. Every screen layout shows the password (Sher) |
| 1 Nov | Home screen: layout 3 "Address bar" from the 10 layouts (`docs/design/LayoutsAndLogos.html`): partner ID, password and Connect in one line at the top; this PC's ID and one-time password below on the left, Recent on the right (Sher). Built in Plan step 15 |
| 1 Nov | Logo: idea 10 "Wordmark", the name with its o drawn as a small screen (Sher). Drawn for the working name; redrawn if the product name changes (open decision 6) |
| 1 Nov | Server: Oracle Cloud (Always Free) with Oracle Linux 9, the free CentOS-family system (CentOS Linux itself gets no more security updates). Replaces Alibaba Cloud Hong Kong from 25 Oct. Region chosen at sign-up; Singapore recommended (202 ms average in the 25 Oct test, the best measured) (Sher) |
| 2 Nov | Server while the app matures: Oracle Cloud Always Free only, kept simple and free; a paid server once Sheron is mature and tested. No domain for now: the app reaches the server by its IP address (open decision 3 moves to release) (Sher) |
| 5 Nov | "Bad network" target: 300 ms ± 100 ms is the round trip (150 ms ± 50 ms each way), as ping shows it, like the other delay targets. At 300 ms each way (600 ms round trip) recovering from a lost frame takes about one round trip, 0.8–1.3 s, so that case is measured and recorded but not held to the 1 s limit (Sher) |
| 4 Nov | IDs of 4 characters: digits first (0000–9999), then digits and letters once the digit IDs are used up. Connect with the ID alone: the person at the remote PC accepts; the one-time password is dropped; the fixed password stays for unattended access. Because 4-digit IDs are easy to guess: after 3 refused requests (Deny or no answer) a viewer's requests are refused for 10 minutes, and the server allows 20 ID lookups a minute per internet address (Sher; Claude advised 6 digits for fewer guesses, Sher chose 4) |
| 1 Nov | Testing on a second PC uses one self-contained `Sheron.exe`: copy it over and double-click, no install, no .NET, no GitHub account on that PC. Until the home screen (Plan step 15) it opens a small start window: "Share this screen" or "View a PC" by IP address |

## Decisions still open — ask Sher

| # | Question | Needed before |
| --- | --- | --- |
| 3 | Domain name for the server (none while testing, 2 Nov) | Release |
| 4 | Installer tool: Inno Setup proposed | Step 19 |
| 5 | Code-signing certificate: standard or EV | Step 20 |
| 6 | Product name | Release |
| 7 | Price and licence model | Release |
