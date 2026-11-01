# SiteLedger

[![Version: development](https://img.shields.io/badge/version-development-blue)](README.md)
[![Build and test](https://github.com/LethaboMohapi/SiteLedger/actions/workflows/ci.yml/badge.svg?branch=master&event=push)](https://github.com/LethaboMohapi/SiteLedger/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MySQL 8.0](https://img.shields.io/badge/MySQL-8.0-00758F?logo=mysql&logoColor=white)](https://www.mysql.com/)
![Code by Lethabo Mohapi](https://img.shields.io/badge/Code_by-Lethabo_Mohapi-blue)

SiteLedger is an **unfinished** C# / ASP.NET Core and MySQL foundation for managing construction projects. It is not yet a complete application and must not be exposed or deployed for production use. The MVC interface provides onboarding and access to the current project workflows; production deployment and a complete security review are still outstanding.

## Current functionality

- API account registration, email confirmation, password reset and authenticator setup for a new tenant administrator.
- ASP.NET Core API with JWT login, rotating refresh-token families and optional authenticator-app codes or single-use recovery codes.
- Tenant-scoped persistence, project listing and creation with a free-plan active-project limit.
- Project and task creation and version-checked updates for managers and tenant administrators. Tenant administrators can list tenant users and add, remove, or restore project memberships; membership changes create audit records.
- Project report and expense listing, draft creation, version-checked editing by the author while draft or rejected, submission and review. Project membership is required except for tenant administrators; viewers see only approved reports and expenses. A creator cannot review their own submission.
- Project, report and expense CSV exports for Pro and Enterprise tenants. Tenant administrators can export all tenant projects; managers export only projects they belong to. The exports escape spreadsheet formula prefixes.
- A dashboard showing visible projects, active projects, open tasks and approved expenses.
- An ASP.NET Core MVC interface for registration, confirmation resend, password recovery, sign-in, authenticator setup, projects, tasks, reports, expenses, memberships and CSV downloads.
- API invitations, tenant user and role administration, task assignment and version-checked task and eligible draft-record deletion. Eligible projects can be soft-deleted under the same write lock used by child creation; projects containing reports, expenses or files are retained until those records can be removed safely.
- Private local file uploads and downloads, with project access checks, quotas and viewer-sharing controls.
- Domain rules for plan limits, permissions and approval transitions; MySQL schema migrations; and automated tests.
- Site-report PDF downloads, encrypted durable email delivery with retry backoff, workflow audit events, an administrator audit page and MVC assignment/deletion controls.
- Report and expense submission/review email notifications to eligible tenant users.
- Tenant-scoped auditing of authenticated API reads, CSV/PDF exports and private downloads, including denied outcomes. An access attempt is persisted before the read executes; failed audit storage blocks the read with 503.
- Validated MVC sign-in, registration and recovery forms with inline errors, password confirmation, safe-field preservation and secret clearing on failure.

### Project CSV export

`GET /api/v1/exports/projects.csv` returns a UTF-8 CSV attachment for a Pro or Enterprise tenant. The caller must be a tenant administrator or project manager. Managers receive only their assigned projects. The file contains project identifiers, names, clients, site addresses, budgets, dates and statuses. `GET /api/v1/exports/projects/{projectId}/reports.csv` and `.../expenses.csv` export records for an accessible project under the same plan and role restrictions.

### Account API

Set `Account__PublicBaseUrl` to the externally accessible HTTPS API origin and configure `Email__SmtpHost`, `Email__SmtpPort` (default 587), `Email__From`, and, if needed, `Email__Username` and `Email__Password` using environment variables or a secret store. SMTP uses TLS. Migrate the database before starting the API. Do not commit credentials. Data-protection keys must be persisted and shared across instances or account tokens may stop working after a restart.

`POST /api/v1/account/register` creates a free tenant and an unconfirmed administrator; the confirmation link is emailed. `POST /api/v1/account/confirmation/resend` resends it. `POST /api/v1/account/password/forgot` emails a reset token; submit it with the email address and new password to `POST /api/v1/account/password/reset`. These endpoints return generic responses for existing or missing accounts. Email is queued in MySQL with Data Protection-encrypted contents and retry backoff. The queue requires the EmailOutbox migration and stable shared protection keys. Queue tests use a substitute email transport; real SMTP delivery remains unverified. The SMTP transport and real email delivery have not been end-to-end tested.

### Authenticator API

Existing confirmed users can request an authenticator key with `POST /api/v1/auth/authenticator/setup` (email and password), then verify a code at `POST /api/v1/auth/authenticator/enable` (email, password and code). Once enabled, include `authenticatorCode` with the password at `POST /api/v1/auth/login`. `POST /api/v1/auth/authenticator/disable` requires email, password and either a current authenticator code or an unused recovery code. These endpoints must be used over HTTPS. Enabling or disabling invalidates existing access tokens. Generate one-time recovery codes in the MVC security page or through `POST /api/v1/auth/authenticator/recovery-codes`; save them securely because they are shown only once. API clients can refresh at `POST /api/v1/auth/refresh` and revoke the token family at `POST /api/v1/auth/logout`. An issued access token remains valid until it expires or its security stamp changes.

### MVC interface

Run the API and web projects separately. For local development the MVC project defaults to `http://localhost:5051/` for the API; set `SiteLedgerApi__BaseUrl` to the API HTTPS origin in production. The web app uses an HTTP-only protected authentication cookie and encrypted server-side session data. It rotates refresh tokens before access-token expiry and attempts API family revocation at logout. Session access is coordinated across instances using Redis; a temporary development cache also supports a single process. Losing the session cache signs users out. Sessions have a maximum 14-day lifetime. If the API is unreachable during logout, the user is signed out locally and told that remote revocation could not be confirmed. Redis-backed shared token caching and certificate-protected persistent data-protection keys are configurable; production still needs a secure shared deployment and verification. The API performs the final authorization checks; the interface does not yet hide every action a user cannot perform.

```sh
dotnet run --project src/SiteLedger.Api
dotnet run --project src/SiteLedger.Web
```

Configure the API database, signing key and email settings first. Use HTTPS outside local development.

## Still needed

- Shared production file storage, crash reconciliation and cleanup, and malware/full-format inspection. The current private storage uses a local filesystem and signature checks.
- Authentication-lifecycle audit coverage, audit retention and tamper protection, plus remaining notifications. Authenticated API reads/exports/downloads now record attempts and outcomes; workflow email production delivery remains unverified.
- Further browser coverage and validation improvements for project, task, report, expense, invitation and security forms beyond the current account-form validation and role-aware controls.
- Production deployment configuration and verification of real SMTP delivery, persistent protected keys and secure shared token storage. See [deployment notes](docs/DEPLOYMENT.md) for the local Compose setup.
- Broader tenant-isolation, end-to-end and security testing. Existing endpoints are not a substitute for a complete security review.

### Private files

Set `Storage__Path` to an absolute private directory outside the web root. Uploads accept PDF, PNG and JPEG signatures, subject to plan quotas. Files are served as attachments. The current implementation uses local disk, does not scan for malware and does not reconcile files after a crash; it is not a shared production blob store.

### Read and export auditing

Authenticated controller GET/HEAD requests generate paired `read.started`/`read.completed`, `export.started`/`export.completed`, or `file.download.started`/`file.download.completed` events. The pair shares an API-request identifier. Entries record the server route template, method and completion status; they exclude URL/query values, request/response bodies and credentials. Denied project reads and exports are recorded within the caller's tenant. Reading the audit log is also audited. Anonymous requests and failed authentication are outside this feature's coverage.

The started event must be committed before the endpoint runs. If that write fails, the response is 503 and the read does not execute. Completion writes have a bounded timeout; failures are logged at critical severity and the earlier started event remains. A process crash can leave an unmatched started event. Streaming completion describes server processing, not proof that the client saved every byte. Retention, external tamper-resistant storage and authentication-lifecycle auditing remain outstanding.

### Account-form validation

Registration and password reset require matching password confirmation. Account forms validate required fields, lengths, email addresses and authenticator-code format on the server; local validation scripts provide corresponding client feedback. Invalid forms render inline errors and retain safe fields while clearing passwords, reset tokens and recovery codes. API password-policy messages are displayed without rendering raw response bodies. Confirmation resend and password reset use separate validated form models. Other management forms still need broader validation and browser coverage.

## Build and test

Requirements: .NET SDK 8.0.419 (see `global.json`), NuGet access, and a running Docker engine for the MySQL integration tests.

```sh
dotnet restore SiteLedger.sln
dotnet build SiteLedger.sln --no-restore -warnaserror
dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj --no-build --no-restore
```

The test suite creates temporary MySQL containers. API startup also requires a MySQL connection string and JWT issuer, audience and signing key; production deployment configuration has not yet been completed. See [verification](docs/VERIFICATION.md) and [progress](docs/PROGRESS.md) for current evidence and outstanding work.

## License

[MIT](LICENSE)

Coded by Lethabo Mohapi

### Verification scope

See [verification](docs/VERIFICATION.md) for current build, migration, MySQL, Redis, PDF, email-queue and MVC test evidence. These checks do not establish production SMTP delivery, malware scanning, complete audit protection or an independent security review. Apply the EmailOutbox migration before using the queue.
