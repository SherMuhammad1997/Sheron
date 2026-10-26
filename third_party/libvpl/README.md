# Intel oneVPL dispatcher (libvpl)

| Item | Value |
| --- | --- |
| What | `libvpl.dll`: the dispatcher that finds Intel's oneVPL GPU runtime (`libmfx64-gen.dll`, installed with the Intel graphics driver) and passes our calls to it |
| Used by | Intel encoder route, `src/Sheron.Win/Encode/Vpl/` (Plan step 4b) |
| Source | https://github.com/intel/libvpl, tag v2.16.0, commit 778a66d6c6537f08eabb91955dbbf1bce3812894 |
| Licence | MIT (`LICENSE`); approved in TRD › Third-party components |
| Built with | `build-libvpl.sh`: MinGW-w64 GCC 13 (posix threads) on Ubuntu 24.04, dispatcher only, MinGW runtime linked in |
| Needs on Windows | Only system DLLs: ADVAPI32, KERNEL32, msvcrt, ole32 |
| SHA-256 | `602f13cb1b58ef5f68c2ba4218d2b264c8968f12ac6ae9fc7b1724d10136ebc0` |

## Struct layouts

`VplApi.cs` declares the oneVPL structures with fixed byte offsets. They come from `vpl-layout.c`, run against the v2.16.0 headers (`vpl-layout.txt`), and were checked with `_Static_assert` under the Windows x64 compiler (140 checks). They also match the sizes asserted in Intel's GPU runtime (`mfxInfoMFX` 136, `mfxVideoParam` 208, `mfxFrameInfo` 68, `mfxBitstream` 72, `mfxFrameSurfaceInterface` 112). `build-libvpl.sh` re-checks them.
