# ADR-0009: PostgreSQL, device SQLite, and EF Core provider versions

- **Status:** Storage accepted; EF Core 10 and Npgsql EF Core 10 selected, runtime compatibility remains to be verified
- **Date:** 2026-11-25

## Context

The owner approved PostgreSQL on the VPS and SQLite on each device. The API target remains .NET 11, and the user prefers EF Core 10 with the matching Npgsql provider.

## Decision

- Use one PostgreSQL server instance on the VPS. Give Identity, Life, and Learning separate logical databases and database credentials, with service-owned schemas and migrations.
- Use a local SQLite database on every desktop/mobile device for cached data and the offline outbox.
- The current implementation uses EF Core 10, Npgsql EF Core 10, and the EF Core 10 SQLite provider while targeting .NET 11.
- Keep the application target on .NET 11. Do not silently change the framework or provider version. Validate PostgreSQL runtime operations, the ASP.NET Core Identity store, migration/recovery behavior, and the complete MAUI build against the chosen package combination before production release.

## Consequences

- The portable solution compiled on .NET SDK 11.0.100 with these EF Core 10 packages.
- Compilation does not prove that Microsoft supports EF Core 10 on .NET 11 or that PostgreSQL, Identity, migrations, and MAUI runtime paths work end to end. These remain release gates.
- Initial SQL schema files were exported from the EF Core PostgreSQL models; production schema evolution currently uses the checksummed SQL runner described in [ADR-0011](ADR-0011-versioned-migrations-and-operations.md).
- Keep each service's migrations isolated. Do not share a DbContext or persistence entities across service boundaries.

## Primary references

- [EF Core release and support policy](https://learn.microsoft.com/en-us/ef/core/what-is-new/)
- [EF Core 10 release notes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- [Npgsql EF Core 10 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html)
