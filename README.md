# SiteLedger

[![Version: development](https://img.shields.io/badge/version-development-blue)](README.md)
[![Build and test](https://github.com/LethaboMohapi/SiteLedger/actions/workflows/ci.yml/badge.svg?branch=master&event=push)](https://github.com/LethaboMohapi/SiteLedger/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MySQL 8.0](https://img.shields.io/badge/MySQL-8.0-00758F?logo=mysql&logoColor=white)](https://www.mysql.com/)

SiteLedger is an **unfinished** C# / ASP.NET Core and MySQL foundation for managing construction projects. It is not yet a complete application and must not be exposed or deployed for production use. The MVC project is still a template and there is no supported account registration or onboarding flow.

## Current functionality

- ASP.NET Core API with password-based JWT login for active, confirmed tenant users, role checks and per-request account validation.
- Tenant-scoped persistence, project listing and creation with a free-plan active-project limit.
- Project and task creation and version-checked updates for managers and tenant administrators. Tenant administrators can list tenant users and add, remove, or restore project memberships; membership changes create audit records.
- Project report and expense listing, draft creation, version-checked editing by the author while draft or rejected, submission and review. Project membership is required except for tenant administrators; viewers see only approved reports and expenses. A creator cannot review their own submission.
- Domain rules for plan limits, permissions and approval transitions; a MySQL schema migration; and automated tests.

## Still needed

Account registration, confirmation, password recovery, two-factor authentication and refresh tokens; complete user administration; complete project, task, report and expense editing workflows; uploads and private storage; audit and notifications; dashboards and exports; a functional MVC interface; deployment configuration; and broader tenant-isolation and end-to-end tests. The existing endpoints are not a substitute for a complete security review.

## Build and test

Requirements: .NET SDK 8.0.419 (see `global.json`), NuGet access, and a running Docker engine for the MySQL integration tests.

```sh
dotnet restore SiteLedger.sln
dotnet build SiteLedger.sln --no-restore -warnaserror
dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj --no-build --no-restore
```

The test suite creates temporary MySQL containers. API startup also requires a MySQL connection string and JWT issuer, audience and signing key; registration, provisioning and configuration for a usable deployment have not yet been completed. See [verification](docs/VERIFICATION.md) and [progress](docs/PROGRESS.md) for current evidence and outstanding work.

## License

[MIT](LICENSE)
