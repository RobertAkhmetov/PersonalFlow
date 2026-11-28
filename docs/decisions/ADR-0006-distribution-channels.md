# ADR-0006: Initial distribution channels

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

The app targets Windows and macOS desktop plus Android and iOS mobile. Windows must run for the current user without administrator rights or a traditional installer.

## Decision

- Windows: ship a portable application folder with a user-level update path. Keep data and settings in a stable per-user data location separate from replaceable app binaries.
- macOS: distribute a signed app.
- Android: distribute an APK initially.
- iOS: distribute through TestFlight initially.

## Consequences

- Build, signing, and release packaging differ by platform. Signing credentials and private provisioning profiles stay outside Git.
- The Windows update mechanism, macOS notarization and delivery details, Android signing-key backup, and future store distribution are Open. Direct distribution of a macOS app to many viewers generally needs Developer ID signing and notarization for Gatekeeper; confirm the intended delivery route before preparing releases.
- Updating app files must not overwrite local client databases, queued changes, or settings.

## Primary distribution reference

- [Apple notarization for macOS software distributed outside the App Store](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)

## Recommended desktop update behavior

Fetch a signed release manifest, validate the artifact and stage the complete version in a user-writable directory. Switch versions at a safe restart after flushing drafts and queued mutations. If downloading stops or verification fails, keep the running version available. If first launch fails after a local schema change, binary rollback is safe only when the previous build can open that schema. Preserve a consistent pre-migration SQLite backup and a recovery path for newer edits.

A portable Windows folder may be read-only. In that case offer an update into a writable per-user location and retain the same per-user data directory. The mobile release process should respect the selected APK and TestFlight installation channels and their operating-system prompts.
