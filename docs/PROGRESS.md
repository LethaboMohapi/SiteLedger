# SiteLedger implementation checklist

Items are checked only when implemented and verified as specified. This file records outstanding work; the current application is not ready to deploy.

## Solution and development environment
- [ ] Required project layout, dependencies, versions, configuration and solution
- [ ] Development compose with MySQL, Azurite, MailHog, API and web; health dependencies and demo accounts
- [ ] Build with warnings as errors and relevant tests

## Domain and migrations
- [ ] Complete domain entities, audit fields, soft deletion, concurrency and indexes
- [ ] Permission matrix, plan rules and billing provider
- [ ] Identity MySQL persistence, global tenant filters, save stamping and audited privileged access
- [ ] Real EF migrations and migration execution strategy
- [ ] Domain and persistence tests

## API authentication and tenancy
- [ ] Registration, confirmation, login, lockout, optional 2FA, password reset/change
- [ ] Rotating hashed refresh tokens, reuse detection and logout
- [ ] Policies, project membership/ownership checks and tenant isolation tests
- [ ] Validation, error handling, security controls, health, observability and OpenAPI

## API features
- [ ] Invitations, user administration, plans and manual billing requests
- [ ] Projects, members, tasks, progress, comments and search/sort/filter/page
- [ ] Reports and expenses, approvals, notifications, budgets and audit
- [ ] Role dashboards, CSV, PDF, SuperAdmin console and scoped Enterprise API keys
- [ ] Integration tests for workflows, roles and plan limits

## Files and blob storage
- [ ] Private tenant/project blobs, streaming upload validation, hashing and quotas
- [ ] Authorized preview/download/delete, transactional accounting and reconciliation
- [ ] Deferred hard deletion and cross-tenant guessed-ID/path tests

## Web application
- [ ] Secure cookie login and shared data-protection keys, typed resilient API client and refresh
- [ ] Accessible responsive theme, locally bundled assets, tables, forms and uploads
- [ ] All requested pages and role-aware actions; server-side enforcement

## Background jobs and email
- [ ] Queued SMTP email with retries and all required notifications
- [ ] Storage reconciliation, blob cleanup and plan-based audit retention

## Tests
- [ ] Unit tests for plans, permissions, approvals, files, tokens and budgets
- [ ] WebApplicationFactory/Testcontainers integration suite with MySQL and Azurite
- [ ] At least 70% Core and Infrastructure service line coverage reported in CI

## Production deployment and CI/CD
- [ ] Multi-stage non-root Dockerfiles, production compose and TLS proxy
- [ ] Azure Bicep and least-privilege identity, secret and storage configuration
- [ ] CI build, format, tests, coverage, vulnerability and image checks
- [ ] Deployment, migration, smoke test and rollback workflow
- [ ] Compose and Bicep validation; document unverified live Azure operations

## Documentation and final audit
- [ ] README, architecture, deployment, runbook, security, decisions and verification
- [ ] Complete documented environment variables; no committed secrets
- [ ] Execute full Definition of Done audit and record actual evidence
