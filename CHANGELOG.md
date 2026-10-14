# Changelog

All notable changes to this project are documented here. Format: [Keep a Changelog](https://keepachangelog.com/), versioning: [SemVer](https://semver.org/).

## [1.0.0] – 2026-10-14

First public release. 🎉

### Added
- Window library: all activatable top-level windows with icon, title and process, live search, F5 refresh.
- Drag-and-drop rotation editor: reorder, per-window duration (3–3600 s), enable/disable toggle, live cycle time.
- Start / Pause / Resume / Stop / Next – in the window and in the system tray.
- Profiles as versioned JSON with autosave, atomic writes and corrupt-file backups.
- Smart re-matching after app or PC restarts (exact title → unique candidate → optional title filter; never guesses).
- Target monitor: windows are moved to the chosen display, maximized state preserved.
- Failure strategies: retry-then-skip or skip, activation verification, focus-lock fallback.
- Auto-pause on session lock, autostart at logon (`--tray`), single instance.
- Daily log files + in-app event log.
- Self-contained single-file package, per-user installer script, sample profile.
