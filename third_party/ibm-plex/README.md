# IBM Plex fonts

The app's fonts (TRD › Windows UI › Fonts; approved in TRD › Third-party components). Licence: SIL Open Font License 1.1, in `OFL.txt` (copyright IBM Corp., Reserved Font Name "Plex"). The files are unchanged: no glyphs added or removed, not renamed, not converted.

| File | Version | SHA-256 |
| --- | --- | --- |
| IBMPlexSans-Regular.ttf | 3.005 | 975dcda37d80f038dcd143c22e33ca2d97a0cc5a929aace1c749153b0fe1afa5 |
| IBMPlexSans-Medium.ttf | 3.005 | 331c8639d7598b2cde62a911a71db195e30cb655cd6bdf2e324a7e984955f907 |
| IBMPlexSans-SemiBold.ttf | 3.005 | a20caf8286023a6a7a85e40b1d2a4ae9fc3e3b1f9eda8f4c542dd4986af67bb1 |
| IBMPlexSans-Bold.ttf | 3.005 | 9e6c74a889a700d707613d24548fe4ffa6bc59559a0689d2cf9e133bdcdafb2f |
| IBMPlexMono-Regular.ttf | 2.005 | 7c6fbddca4b700be918f5f6183d9bd4464fa427fe435f0b480d77fe2bb8c5a43 |
| IBMPlexMono-Medium.ttf | 2.005 | 98fbd727aae340b236955879dabed4d991aac9e8e90b3b2a67ce4a59221cc97c |
| IBMPlexMono-SemiBold.ttf | 2.005 | f04d7c488ddf7d1fa99f2574efc3406ea4cbe17bb1af3a1ab960f84d0c96a172 |

Taken on 5 Nov 2026 from IBM's repository, `https://raw.githubusercontent.com/IBM/plex/master/packages/plex-sans/fonts/complete/ttf/` and `.../plex-mono/fonts/complete/ttf/`; `OFL.txt` is that repository's `LICENSE.txt`.

How the app uses them: `src/Sheron.Win/Ui/Fonts.cs` builds the files into the exe and loads them for the app's own process only (`AddFontMemResourceEx` for GDI text and a `PrivateFontCollection` for the font objects); nothing is installed on the PC. Windows names the weights "IBM Plex Sans", "IBM Plex Sans Medm", "IBM Plex Sans SmBld" (and the same for Mono); Bold is a style of "IBM Plex Sans".
