# Sheron

Simple, fast remote desktop for Windows. Connect to any PC in the world with a 9-digit ID and a password, see its screen and control it.

Status: Implementation Plan step 8 (connect by IP on the same network, view only) written; being checked on Sher's two PCs.

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
