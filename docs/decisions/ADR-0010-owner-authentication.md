# ADR-0010: Single-owner authentication and native-client sessions

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

The production service is for one owner using multiple desktop and mobile devices. Public registration and multi-user tenancy are out of scope. Native desktop/mobile clients need a standard sign-in flow, and individual devices must be revocable.

## Decision

- The Identity service hosts an OAuth 2.0/OpenID Connect authorization server using OpenIddict and ASP.NET Core Identity for the owner account.
- Native clients use Authorization Code with PKCE in the operating system's system browser. The Identity service provides a small browser-based sign-in page; the product does not add a general web client.
- Do not expose public registration. Provision the initial owner through a protected administrative operation, not a public sign-up endpoint.
- Require a strong password and TOTP multi-factor authentication, with one-time recovery codes and a protected administrative recovery path.
- Issue short-lived access tokens and rotate refresh tokens per device. Allow the owner to revoke any device session.
- Store refresh credentials in the platform secure store (Apple Keychain, Android Keystore-backed storage, and the corresponding secure desktop credential store). Never embed client secrets or server signing keys in an app.
- Life and Learning validate signed access tokens using the issuer's published key metadata; they do not query Identity for every API request.

## Consequences

- The app needs HTTPS and a stable Identity issuer URL, signing-key storage/rotation, login rate limiting, and safe trusted-proxy configuration.
- Desktop and mobile redirect/callback flows must be implemented and verified for their target platforms.
- The EF Core 10 compatibility gate in ADR-0009 includes Identity's persistence packages. Use currently supported stable OpenIddict packages and verify the selected version set against ASP.NET Core 11 before implementation.

## Primary references

- [OpenIddict ASP.NET Core integration and supported runtimes](https://documentation.openiddict.com/integrations/aspnet-core)
- [OpenIddict Entity Framework Core integration](https://documentation.openiddict.com/integrations/entity-framework-core)
- [IETF RFC 8252: OAuth 2.0 for Native Apps](https://www.rfc-editor.org/info/rfc8252/)
