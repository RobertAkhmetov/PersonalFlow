# Private local data

User-specific seed data can live in `local-private/personal-seed.json`. The directory is ignored by Git. The mobile app imports the file once into the local SQLite store when it is present in a local Debug build.

The MAUI project includes this file as an app-package asset only for Debug builds. Release builds do not package it. Never copy the private seed into tracked source files, documentation, screenshots, or public build artifacts.

The seed format is versioned. If it changes incompatibly, increment `version` and update `PrivateSeedImporter` to handle the new version without overwriting records the user has already edited.
