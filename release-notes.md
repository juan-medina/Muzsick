## Release Notes

### New Features

- **Replay last announcement button** — a circular-arrow button to the right of the Play/Pause button replays the DJ voiceover for the current song. Disabled (dimmed) until the first announcement has played.

### Bug Fixes

- Fixed voiceover announcement not playing after refactor — `NotifyCanExecuteChanged` was being called from a background thread, causing an Avalonia cross-thread exception that silently aborted the announcement.

### Improvements

- Any new announcement (track change or replay) now atomically cancels the previous one so only one voiceover plays at a time.
- Added tooltips to all main-window buttons: Open playlist, Play / Pause, Replay last announcement, Settings, About.
- Disabled state of the Replay announcement button is visually dimmed (35% opacity) so it is clearly distinguishable from the enabled state.

### Known Issues

### Notes
