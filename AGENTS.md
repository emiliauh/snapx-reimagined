# Repository agent guidance

## Cross-platform feature parity

- Treat Windows, macOS, Linux/X11, and Linux/Wayland as equal supported targets.
- When adding or changing a user-facing feature, preserve equivalent behavior, settings, persistence, error handling, and regression coverage on every supported platform.
- Use platform-native implementations behind shared contracts when operating-system APIs differ. Do not silently disable or remove an existing platform path to simplify another one.
- If an operating system cannot provide true parity, gate the unsupported path explicitly, keep the other platforms working, explain the limitation in the UI and documentation, and add coverage for both the supported behavior and the guarded fallback.
- Validate platform-sensitive changes against packaged artifacts where practical; ordinary framework-dependent unit tests are not sufficient evidence for native capture, permissions, hotkeys, recording, packaging, or shutdown behavior.

## Release versioning

- Advance the application version for every shipped update. Keep the source version, executable-reported version, macOS bundle metadata, artifact filenames, Git tag, and GitHub release tag aligned; never publish updated binaries under an existing version.
