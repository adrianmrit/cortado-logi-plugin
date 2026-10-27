# Changelog

## 1.2.0

### Added

- Added an optional status subicon that distinguishes Mac-only from Mac + display
  mode while on or off. The subicon is enabled by default and can be hidden in
  Settings.
- Added a saved clock-format preference for displaying end times and active
  session times in either 12-hour AM/PM or 24-hour format.

### Changed

- Consolidated keep-awake mode selection into a toggle.

## 1.1.0

Changes since [v1.0.0](https://github.com/adrianmrit/cortado-logi-plugin/compare/v1.0.0...v1.1.0).

### Added

- Added a **Reset** button to the duration and end-time selectors. Reset clears
  the duration to zero or sets the end-time draft to the current local time.

### Changed

- New installations default to keeping both the Mac and its display awake.
  Existing saved preferences are preserved.
- Selector captions identify the value as **Duration** or **End time** instead
  of **Shared draft**.

### Fixed

- Reset the Cortado menu when opening it after a profile switch, preventing
  stale selector layouts when the host skips folder lifecycle callbacks.
- Log folder-opening handler failures without blocking navigation.
- Handle caffeinate exiting during argument inspection without reporting a
  spurious error. Inspection failures still surface if the same process remains
  present.

### Testing

- Added regression coverage for selector resets, profile-switch navigation,
  default preferences, and the caffeinate process-exit race.
- Restricted CI core tests to macOS, matching the plugin's supported platform.
