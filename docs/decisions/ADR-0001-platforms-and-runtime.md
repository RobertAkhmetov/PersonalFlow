# ADR-0001: Platforms and runtime

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

Personal Flow needs desktop and mobile clients plus a centrally hosted backend. The initial desktop requirement is Windows/macOS; Linux is useful for server hosting.

## Decision

- Target .NET 11 and use C# 15 language features when they improve clarity, correctness, or performance.
- Use Avalonia for the Windows and macOS desktop client.
- Use .NET MAUI with Blazor Hybrid for the iOS and Android client.
- Run the API on a Linux VPS.
- Linux desktop and a separate Blazor web client are outside the current platform scope.
- Windows desktop distribution must not require administrator rights or a traditional installer.

## Consequences

- Desktop and mobile have different UI frameworks. Share domain/application rules and API contracts where useful; keep platform UI components native to their chosen framework.
- .NET 11 is an STS release supported through November 14, 2028; plan regular major-version upgrades and stay current on patches. .NET MAUI 11 has a separate support lifecycle through May 9, 2028, so plan a MAUI upgrade before the .NET 11 runtime reaches end of support.
- Avalonia 12 supports desktop on .NET 8 and later and recommends .NET 10; verify the chosen Avalonia packages and all required targets against .NET 11 when project creation begins. This validation does not change the selected target framework.
- Use stable C# 15 features by default; avoid preview-only memory-safety features in production code until explicitly evaluated.
- Initial distribution targets are recorded in ADR-0006; update mechanisms and signing details remain separate implementation decisions.

## Primary compatibility references

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [.NET MAUI support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/maui)
- [C# 15 features and language version](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new)
- [Avalonia 12 .NET support changes](https://v11.docs.avaloniaui.net/docs/avalonia12-breaking-changes/)
- [.NET MAUI Blazor platform requirements](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms)
- [.NET 11 MAUI behavior and supported Android API](https://learn.microsoft.com/en-us/dotnet/maui/whats-new/dotnet-11?view=net-maui-11.0)
