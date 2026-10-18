# Changelog

All notable changes are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-18

First public release. Marked as a pre-release: it has so far been tested on one
machine only.

### Added

- Sorting of photos and videos into event folders named `YYYYMMDD_<topic>`, in
  two modes: EXIF-only (time, GPS and an offline place database) and Combined
  (the same chain plus a local vision model).
- Local image understanding through [Ollama](https://ollama.com). Two models are
  offered: Gemma 4 12B and Gemma 4 26B as Unsloth's quantisation-aware build,
  which fits a 16 GB graphics card whole.
- Scene similarity from image vectors instead of asking the model whether two
  shots belong together.
- Face recognition with a name only ever used as a fact above a strict
  threshold, plus a knowledge base of facts and image notes.
- Home zones, vacation zones and place zones, and everyday places recognised
  from the data itself.
- Plan preview before anything is touched, a run journal to undo a run, and a
  rule that nothing is ever overwritten.
- Own video playback through ffmpeg, so playback does not depend on the Windows
  Media Player feature.
- Interface and folder names switchable between German and English.
- Portable, self-contained Windows build with a self-update that verifies the
  SHA-256 of the download before unpacking.
- The environment variable `MEDIAORGANIZER_DATA` redirects the data folder, so
  the application can be tried against a throwaway folder.

### Known limits

- The build is not code-signed yet, so Windows SmartScreen will warn on first
  start.
- Release builds carry no debug symbols (they embed the build machine's paths),
  so stack traces in `error.log` have no line numbers.
- Analysing a large collection with the model takes hours; see the performance
  section of the README.
