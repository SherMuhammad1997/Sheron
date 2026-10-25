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
| F1 | ID and one-time password | The server gives each PC a 9-digit ID. An 8-character one-time password is shown and changes after every session |
| F2 | Connect by ID | Type the partner ID, then the password, and the session opens. A Recent list keeps the last 10 PCs |
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
| Bad network | Usable at 300 ms ± 100 ms jitter and 5 % loss; no freeze longer than 1 s | `Sheron.Sim` |
| Reconnect | ≤ 5 s after the network drops and comes back | Turn the hotspot off and on |
| UDP blocked | Falls back to TCP 443 by itself within 3 s | Firewall rule that blocks UDP |
| Windows versions | Windows 10 (1809 or newer) and Windows 11, 64-bit | Test PCs |

## Acceptance checks (all must pass for version 1)

| # | Check |
| --- | --- |
| A1 | Installed on two PCs, each shows its ID within 5 s of starting |
| A2 | PC 1 connects to PC 2 by ID and one-time password, each on a different internet connection (for example hotspot and office line) |
| A3 | PC 2 shows the request; Deny blocks it; no answer denies it after 30 s |
| A4 | Mouse, keyboard and Ctrl+Alt+Del work; the pointer feels instant |
| A5 | Unattended: PC 2 is locked with nobody there; PC 1 connects with the fixed password and unlocks it |
| A6 | The banner shows for the whole session; Disconnect on PC 2 ends it at once |
| A7 | The same-network targets above are met (delay, CPU, still screen) |
| A8 | With UDP blocked on PC 2, the session still connects (TCP 443) |
| A9 | A wrong password 3 times locks that ID for 30 s |
| A10 | A Wireshark capture of our own link shows no readable screen data; server logs hold no passwords |
| A11 | Field test from Sher's PC to a PC in Hong Kong or China; numbers recorded in `docs/02-TRD.md` |

## Decided

| Date | Decision |
| --- | --- |
| 25 Oct | Repository name Sheron, private |
| 25 Oct | Version 1 is Windows ↔ Windows only |
| 25 Oct | Simple app, few features, speed first |
| 25 Oct | #1 UI style: Control Room (same as Virtual Repeater, drawn in the design canvas) |
| 25 Oct | #2 Server: Alibaba Cloud Hong Kong (best route in the 25 Oct test) |
| 25 Oct | Unattended access (F5) is in version 1 |
| 25 Oct | Version 1 shows the main monitor only; no file transfer or clipboard |

## Decisions still open — ask Sher

| # | Question | Needed before |
| --- | --- | --- |
| 3 | Domain name for the server | Step 13 |
| 4 | Installer tool: Inno Setup proposed | Step 19 |
| 5 | Code-signing certificate: standard or EV | Step 20 |
| 6 | Product name | Release |
| 7 | Price and licence model | Release |
