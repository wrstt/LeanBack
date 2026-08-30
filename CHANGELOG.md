# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-29

First public release.

### Added
- Scan a project and detect reinstallable directories, with the reason for each shown.
- Kept-versus-skipped ratio as the primary readout, updating live as rows are toggled.
- Simple and Advanced modes; Advanced exposes per-directory skip choices, custom patterns,
  archive format, and backup history.
- Custom skip patterns per project (`coverage/`, `*.log`), persisted between runs.
- Folder-copy and single-`.zip` output formats.
- xxHash64 content verification of every copied file.
- Keep-1 retention: the previous backup is removed only after the new one verifies.
- Restore any previous backup to a chosen folder.
- Regeneration commands recorded and shown on completion.
- Headless CLI (`--cli scan|backup|verify|restore`) sharing the engine with the UI.
- Portable settings stored beside the executable when writable, else in `%APPDATA%`.
- Single-file, self-contained, unpackaged distribution — no installer, no runtime download.
