# Sheron

Simple, fast remote desktop for Windows. Connect to any PC in the world with a 9-digit ID and a password, see its screen and control it.

Status: Plan step 16b (4-character IDs, connect by ID with Accept, no one-time password; PRD decision 4 Nov) built in the cloud; the server needs updating and Sher's laptops a new build. Step 10 (automatic quality, jitter buffer, delay and fps) built; the simulator check passes (300 ms ± 100 ms, 5 % loss, no freeze over 1 s); PRD A7 numbers on Sher's laptops are next. Step 14 (connect by ID) works on one network (4 Nov); its check across two networks is still to do. Step 13 done on 2 Nov: the server runs on Oracle Cloud (Always Free, Singapore, 168.138.174.180) as a systemd service, and Sher's laptop registered with it. Step 16 (passwords) done: PRD A3 and A9 passed on Sher's two laptops. Step 12 (address check, hole punching, relay, TCP 443) done through the network simulator. Then 10.

Server: `dotnet run --project src/Sheron.Server -- --port 443 --data <folder>` (TCP and UDP on that port); it prints the pin the app checks. On Oracle Linux 9: the files in `deploy/` (TRD › Server › Deployment).

One-file test build (PRD decision 1 Nov): `dotnet publish src/Sheron.Win -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o <folder>`, then rename `Sheron.Win.exe` to `Sheron.exe`. Copy it to any 64-bit Windows 10/11 PC and double-click: "Share this screen" on the PC to be viewed, "View a PC" with its IP address on the other.

Build and test: `dotnet build Sheron.sln` and `dotnet test Sheron.sln` (.NET 8 SDK).

Capture and encoder test window (Plan steps 3–4): `dotnet run --project src/Sheron.Win -- --capture-test` (add `--software-encoder` to skip the hardware encoder)

Loop test window (Plan step 5): `dotnet run --project src/Sheron.Win -- --loop-test` (add `--software-encoder` or `--software-decoder` to skip a hardware route)

Connect by IP on the same network (Plan step 8, developer mode): on the remote PC `dotnet run --project src/Sheron.Win -- --host` (UDP 47920; `--port N` to change), on the viewer `dotnet run --project src/Sheron.Win -- --view <remote IP>`. The remote PC needs no GitHub account or source: on the developer PC run `dotnet publish src/Sheron.Win -c Release -r win-x64 --self-contained false -o <folder>`, copy the folder (about 4 MB) to the remote PC, which needs the .NET 8 Desktop Runtime, and run `Sheron.Win.exe --host` there

| Document | What it covers |
| --- | --- |
| [docs/01-PRD.md](docs/01-PRD.md) | What the app does, targets, acceptance checks |
| [docs/02-TRD.md](docs/02-TRD.md) | How it is built: video path, network, security, licences |
| [docs/03-Implementation-Plan.md](docs/03-Implementation-Plan.md) | The 21 build steps |
| [docs/design/](docs/design/) | Screen designs |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Problems solved |

Private project of Sher Muhammad. All rights reserved.
