# Sheron

Simple, fast remote desktop for Windows. Connect to any PC in the world with a 9-digit ID and a password, see its screen and control it.

Status: Implementation Plan step 5 (decoder, renderer, capture-to-display timer) done: Gate 1 passed at 16.2 ms capture to screen on Sher's PC; next is step 6 (Core packets).

Build and test: `dotnet build Sheron.sln` and `dotnet test Sheron.sln` (.NET 8 SDK).

Capture and encoder test window (Plan steps 3–4): `dotnet run --project src/Sheron.Win -- --capture-test` (add `--software-encoder` to skip the hardware encoder)

Loop test window (Plan step 5): `dotnet run --project src/Sheron.Win -- --loop-test` (add `--software-encoder` or `--software-decoder` to skip a hardware route)

| Document | What it covers |
| --- | --- |
| [docs/01-PRD.md](docs/01-PRD.md) | What the app does, targets, acceptance checks |
| [docs/02-TRD.md](docs/02-TRD.md) | How it is built: video path, network, security, licences |
| [docs/03-Implementation-Plan.md](docs/03-Implementation-Plan.md) | The 21 build steps |
| [docs/design/](docs/design/) | Screen designs |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Problems solved |

Private project of Sher Muhammad. All rights reserved.
