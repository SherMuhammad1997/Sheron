# Screen designs

Five boards for version 1, in the Control Room style (proposed; PRD open decision 1). View them rendered on the design canvas: https://claude.ai/artifact/L74V7HCeYQdpD9D7UiXVyn

| Board | Shows | File |
| --- | --- | --- |
| 1 · Home | Your ID, one-time password, unattended status, Partner ID + Connect, Recent list | `Main.dc.html` |
| 2 · Session window | Thin toolbar (delay, fps, Direct/Relay, size, Quality, Ctrl+Alt+Del, full screen, Disconnect) over the remote screen | `Session.dc.html` |
| 3 · On the remote PC | Incoming request (Accept / Deny, denies by itself in 30 s) and the banner shown during a session | `Incoming.dc.html` |
| 4 · Settings | Unattended password, PC name, start with Windows, default quality, server | `Settings.dc.html` |
| 5 · How it connects | Server in Hong Kong, direct link first, relay only if direct fails, encrypted end to end | `HowItConnects.dc.html` |

## Style

| Token | Value |
| --- | --- |
| Background | #0E141B |
| Panel | #151E28 |
| Line | #243142 |
| Text | #E6EDF3 |
| Muted text | #9FB0C3 |
| Accent | Amber #F2A93B |
| Good | Green #3FB871 |
| Stop | Red #E5534B |
| Fonts | IBM Plex Sans / IBM Plex Mono |

## Reading the files

- They are design-board sources, not app code, and do not open on their own in a browser. Use them for exact colours, sizes, spacing and layout.
- `{{accent}}` is the accent colour; its default is in the `data-props` JSON at the bottom of each file.
- In the Windows app, draw these layouts in custom-painted panels (few native controls — see `CLAUDE.md`). Fonts must be bundled with the app; the Google Fonts links are only for the mock-ups.
