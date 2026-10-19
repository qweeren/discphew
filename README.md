# discphew

A lightweight, open-source utility for reading and writing CDs and DVDs, built around an intuitive UI.

Most disc software is either bloated (multi-GB suites that install a separate program for every function) or functional but painful to use. discphew aims to be the WinDirStat of optical discs: **one small program** (a single ~300 KB `.exe`, nothing to install) that does the job well and is pleasant to use.

Made for old-heads, retro lovers and collectors who still use discs today, especially people keeping lossless album collections on CD.

## Tools

| | |
|---|---|
| **Disc Visualizer** | The physical layout of the disc: hub, mirror band, lead-in, every track or file drawn as a ring at its real radius on the spiral, lead-out and unrecorded space. Hover any ring for details; data discs are coloured by file type, like WinDirStat. Zoom up to 1000× to pick out single files: scroll to zoom around the cursor, drag to pan, double-click to zoom in, `+`/`-`/`0` from the keyboard, click a row in the list to fly to it (double-click a file to open it), and a scale bar shows real millimetres. |
| **Burner** | Data discs (drag in files and folders; ISO 9660 + Joliet + UDF, multisession-aware), audio CDs (FLAC/WAV, or anything ffmpeg reads; hi-res files are resampled and dithered to CD quality), and ISO images. Speed choice, capacity bar, erase for RW discs. |
| **Ripper** | Audio CDs to FLAC or WAV with no extra software (ALAC, MP3, AAC, Opus and Vorbis if ffmpeg is installed). Two-pass verified reads with cache-defeating re-reads, drive read-offset correction (remembered per drive, or detected with any CD that's in AccurateRip), AccurateRip v1/v2 verification with confidence per track, CRC32 per track, cue sheet and rip log, MusicBrainz metadata, Cover Art Archive artwork and CD-Text. Data discs: exact ISO image or a file copy, optionally converting video (DVD titles → MP4/MOV/MKV) through ffmpeg. |
| **Album Player** | Plays a CD as an album: straight off the disc, gapless, pregaps and hidden tracks included, with the cover and track list. Also plays a folder of FLAC/WAV files the same way. Space = play/pause, ←/→ = seek, Ctrl+←/→ = previous/next track. |

## Requirements

- Windows 10 or 11 (uses .NET Framework 4.8, which ships with Windows, and IMAPI2, Windows' built-in burning engine)
- An optical drive (USB drives work fine)
- Optional: [ffmpeg](https://ffmpeg.org) on your `PATH` (or `ffmpeg.exe` next to `discphew.exe`) for MP3/AAC/Opus/Vorbis/ALAC and video conversion. discphew never bundles it.

No admin rights needed. Settings and remembered album metadata live in `%APPDATA%\discphew`.

## Building

You need the C# compiler that comes with the free [Build Tools for Visual Studio](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022) (the MSBuild workload) or any Visual Studio install. There are no NuGet packages and no other dependencies.

```bat
build.cmd          :: builds bin\discphew.exe
build.cmd -Run     :: builds and launches
build.cmd -Debug   :: debug build with symbols
```

`tools\make-icon.ps1` regenerates `assets\discphew.ico`.

`tests\run-tests.ps1` checks the AccurateRip disc IDs, response parsing and v1/v2 checksums against the published vectors from [whipper](https://github.com/whipper-team/whipper) and [arver](https://github.com/arcctgx/arver) (their sample files are downloaded into `bin\testdata` on first run), and the offset correction against a simulated drive.

## How it's put together

```
src/
  Native/Win32.cs          P/Invoke: CD-ROM IOCTLs, waveOut, DWM/uxtheme
  Disc/CdDrive.cs          TOC, sessions, CD-Text, raw CD-DA reads, 2048-byte data reads
  Disc/FileSystemReader.cs ISO 9660 / Joliet parser (gives every file's sector position)
  Disc/Imapi.cs            Burning, erasing and media detection via IMAPI2 (late-bound COM)
  Disc/Ripper.cs           Verified, offset-corrected audio ripping, cue/log, offset detection, ISO imaging, file copy
  Disc/AccurateRip.cs      AccurateRip disc IDs, database lookup, v1/v2 track checksums
  Disc/Metadata.cs         MusicBrainz disc ID + lookup, Cover Art Archive, local cache
  Audio/FlacEncoder.cs     FLAC encoder (fixed + LPC prediction, partitioned Rice, stereo decorrelation)
  Audio/FlacReader.cs      FLAC decoder with seeking
  Audio/AudioIO.cs         WAV I/O, optional ffmpeg bridge, resampler, conversion to CD audio
  Audio/AlbumSource.cs     Disc and folder sources for gapless album playback
  Audio/WavePlayer.cs      waveOut output
  UI/                      Dark, DPI-aware WinForms UI with hand-drawn controls and icons
```

The visualizer's geometry comes from the fact that discs are written at constant linear velocity: the area of the spiral grows linearly with the sector count, so a sector's radius is `r = √(r₀² + k·sector)`, with the program area starting at 25 mm (CD) or 24 mm (DVD) and ending at about 58 mm.

The FLAC encoder output decodes bit-identically with libsndfile/libFLAC, and its STREAMINFO MD5 matches the source PCM.

## Known limitations

- Drives can't read into the lead-in or lead-out, so with a non-zero read offset the first or last few samples of the disc are filled with silence (AccurateRip doesn't check those samples). The disc's pressing offset isn't matched either: a rip from a different pressing shows as an AccurateRip mismatch.
- Offset detection uses AccurateRip v1 checksums, so a key disc that only has v2 entries in the database won't match. A different pressing of the same album can shift the result, so check it with a second disc if you're unsure.
- Read offsets are stored per drive in `settings.ini`; there's no built-in table of known drive offsets.
- Audio CDs are burned track-at-once (2-second gaps between tracks, no CD-Text). Disc-at-once with CD-Text would need IMAPI's raw CD writer.
- File positions in the visualizer need ISO 9660; UDF-only discs show their file list without positions.
- Only ISO-style (2048-byte sector) images can be burned; BIN/CUE isn't supported yet.
- Blu-ray writing isn't available through IMAPI2.

## Contributing

Issues and pull requests are welcome. Keep it small: no new runtime dependencies, nothing that needs an installer, and match the style of the surrounding code.

## License

MIT, see [LICENSE](LICENSE).
