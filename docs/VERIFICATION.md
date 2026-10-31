# Verification

This file records checks performed against the revised specification. No full application or deployment check has yet been completed. Run commands from the SiteLedger directory with .NET 8 installed.

- `dotnet restore SiteLedger.sln`: succeeded after network access was enabled.
- `dotnet build SiteLedger.sln -warnaserror -m:1`: succeeded with zero warnings and errors.
- `dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj --no-build --no-restore`: 18 tests passed. The MySQL-backed tests cover database project reads and guessed-ID updates and deletes across tenants, plus HTTP password login, account deactivation, role changes, Member denial of project creation, Manager project creation and the Free plan's two-active-project limit. Docker access is required for these tests. Other tests exercise domain values, approval transitions and isolated access/plan rules.
- `dotnet-ef database update` using the generated initial migration: succeeded against a fresh temporary MySQL 8 container after rebuilding the migration assembly. This does not verify application startup or all future schema changes.
- `docker info --format '{{.ServerVersion}}'`: Docker server 29.9.2 responded when run with permission to access the Docker engine.

## Not verified

- `docker compose up --build` and `docker compose config`: not run; compose files and application functionality do not exist yet.
- Full end-to-end UI/API, Azurite, email, security, integration and coverage checks: not run; these components and tests are not implemented yet. The MySQL project isolation test does not cover identity records, other entity types, file access or cross-tenant HTTP endpoints. Login does not yet implement refresh tokens or two-factor authentication; users with 2FA enabled are denied until the two-factor flow is implemented.
- `az bicep build`, deployment workflow and Azure deployment: not run; Azure infrastructure and workflows do not exist yet.

Once these components are implemented, run `docker compose config`, `docker compose up --build`, the full `dotnet test` suite with coverage, and `az bicep build --file deploy/azure/main.bicep`. Follow the deployment guide and run its Azure smoke and rollback procedures against a real subscription when available.

## Additional verification (2026-10-31)

- `dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj --no-restore`: 18 tests passed with Docker-backed MySQL. The expanded login integration test exercises task creation and stale-version updates, report creation, submission and review by another tenant administrator, self-review denial, expense creation and submission, invalid expenses, and inaccessible project IDs.
- These checks do not verify full UI flows, identity onboarding, cross-tenant access for all new record types, production deployment, or the complete product.

## Additional verification: membership and edits

- `dotnet test src/SiteLedger.Tests/SiteLedger.Tests.csproj --no-restore`: 18 tests passed with MySQL containers. The expanded HTTP test checks membership add, removal and restoration, denial of a cross-tenant membership, guessed project access from another tenant, and version conflicts on project, report and expense edits.
- Account onboarding, production deployment, browser-based workflows, and complete security and cross-tenant coverage remain unverified.
