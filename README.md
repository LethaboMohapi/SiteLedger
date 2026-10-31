# SiteLedger — incomplete foundation

This is an unfinished scaffold, not a runnable SiteLedger application and not suitable for production. The API and MVC projects still contain template code. The tenant claim resolver and domain model are not wired into an authentication pipeline or data store.

Implemented so far: partial domain types, plan limits and checks, approval transitions, a permission matrix, pure access rules, a tenant-scoped EF context and an initial MySQL migration. The API includes password-based JWT login for confirmed, active users and limited project listing and creation. MySQL-backed tests cover project isolation for database reads and guessed-ID updates/deletes, and selected HTTP authorization and plan-limit behavior. Registration, refresh tokens, 2FA and the rest of the API and UI are incomplete. Identity records and privileged administrative access do not yet have the required isolation paths.

Still required: MySQL Identity and EF Core mapping and migrations; tenant isolation; authorization and project membership checks; user and project management; all report and expense workflows; uploads and private blob access; refresh tokens and secure server-side MVC token storage; plan limit enforcement; PDF reports and API keys; Docker configuration; integration tests; and a complete UI. Do not expose or deploy this scaffold.

Run the tests with `dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj` using the .NET 8 SDK, access to NuGet and a running Docker engine. See docs/VERIFICATION.md for the precise scope.
