# SiteLedger

[![Version: development](https://img.shields.io/badge/version-development-blue)](README.md)
[![Build and test](https://github.com/LethaboMohapi/SiteLedger/actions/workflows/ci.yml/badge.svg?branch=master&event=push)](https://github.com/LethaboMohapi/SiteLedger/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MySQL 8.0](https://img.shields.io/badge/MySQL-8.0-00758F?logo=mysql&logoColor=white)](https://www.mysql.com/)

SiteLedger is an **unfinished** C# / ASP.NET Core and MySQL foundation for managing construction projects. It is not yet a complete application and must not be exposed or deployed for production use. The MVC project is still a template; account management is currently available through the API only.

## Current functionality

- API account registration, email confirmation, password reset and authenticator setup for a new tenant administrator.
- ASP.NET Core API with JWT login for active, confirmed tenant users, role checks, per-request account validation, and optional authenticator-app codes.
- Tenant-scoped persistence, project listing and creation with a free-plan active-project limit.
- Project and task creation and version-checked updates for managers and tenant administrators. Tenant administrators can list tenant users and add, remove, or restore project memberships; membership changes create audit records.
- Project report and expense listing, draft creation, version-checked editing by the author while draft or rejected, submission and review. Project membership is required except for tenant administrators; viewers see only approved reports and expenses. A creator cannot review their own submission.
- Project CSV export for Pro and Enterprise tenants. Tenant administrators can export all tenant projects; managers export only projects they belong to. The export escapes spreadsheet formula prefixes.
- Domain rules for plan limits, permissions and approval transitions; a MySQL schema migration; and automated tests.

### Project CSV export

`GET /api/v1/exports/projects.csv` returns a UTF-8 CSV attachment for a Pro or Enterprise tenant. The caller must be a tenant administrator or project manager. Managers receive only their assigned projects. The file contains project identifiers, names, clients, site addresses, budgets, dates and statuses.

### Account API

Set `Account__PublicBaseUrl` to the externally accessible HTTPS API origin and configure `Email__SmtpHost`, `Email__SmtpPort` (default 587), `Email__From`, and, if needed, `Email__Username` and `Email__Password` using environment variables or a secret store. SMTP uses TLS. Migrate the database before starting the API. Do not commit credentials. Data-protection keys must be persisted and shared across instances or account tokens may stop working after a restart.

`POST /api/v1/account/register` creates a free tenant and an unconfirmed administrator; the confirmation link is emailed. `POST /api/v1/account/confirmation/resend` resends it. `POST /api/v1/account/password/forgot` emails a reset token; submit it with the email address and new password to `POST /api/v1/account/password/reset`. These endpoints return generic responses for existing or missing accounts. Delivery failures are logged and the user can request another email; there is no durable email queue yet. The SMTP transport and real email delivery have not been end-to-end tested.

### Authenticator API

Existing confirmed users can request an authenticator key with `POST /api/v1/auth/authenticator/setup` (email and password), then verify a code at `POST /api/v1/auth/authenticator/enable` (email, password and code). Once enabled, include `authenticatorCode` with the password at `POST /api/v1/auth/login`. `POST /api/v1/auth/authenticator/disable` also requires email, password and a current code. These endpoints must be used over HTTPS. Enabling or disabling invalidates existing access tokens. Recovery codes and account recovery are not implemented; losing the authenticator can lock a user out.

## Still needed

- Refresh tokens and authenticator recovery codes.
- Complete user administration and project, task, report and expense editing workflows.
- Private file uploads and storage.
- Comprehensive audit logging and notifications, including durable email retries.
- Dashboards, report and expense exports, and PDF exports. Project CSV export is available through the API as described above.
- A functional MVC interface, including onboarding and access to the API features.
- Deployment configuration and verification of real SMTP delivery and persistent data-protection keys.
- Broader tenant-isolation, end-to-end and security testing. Existing endpoints are not a substitute for a complete security review.

## Build and test

Requirements: .NET SDK 8.0.419 (see `global.json`), NuGet access, and a running Docker engine for the MySQL integration tests.

```sh
dotnet restore SiteLedger.sln
dotnet build SiteLedger.sln --no-restore -warnaserror
dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj --no-build --no-restore
```

The test suite creates temporary MySQL containers. API startup also requires a MySQL connection string and JWT issuer, audience and signing key; the MVC onboarding flow and deployment configuration have not yet been completed. See [verification](docs/VERIFICATION.md) and [progress](docs/PROGRESS.md) for current evidence and outstanding work.

## License

[MIT](LICENSE)
