# Deployment and local containers

The Compose file is for **local development only**. It uses HTTP between containers, a Redis instance without authentication or encryption on the private Compose network, and unencrypted development data-protection keys. Do not expose it to the internet or use it as a production template without replacing those controls. File storage uses a local volume and is not shared across hosts.

## Local Compose

Install Docker and set `SITELEDGER_DB_PASSWORD`, `SITELEDGER_DB_ROOT_PASSWORD` and `SITELEDGER_JWT_KEY` in your shell or a local ignored `.env` file. Use a random signing key of at least 32 bytes. Choose a database password without semicolons for this Compose connection string. Do not commit secrets. Then, from the repository root:

```sh
docker compose config --quiet
docker compose up --build -d mysql redis
docker compose run --rm api --migrate
docker compose up --build -d api web
```

Open `http://localhost:5177`. Ports are bound to loopback. The migration command applies pending EF migrations; back up an existing database before applying them. Compose retains database, data-protection keys and file volumes when containers are stopped. The Redis development container has no durable volume; a Redis restart signs users out because server-side sessions are lost. Sessions are encrypted with the MVC data-protection key ring. Multiple MVC instances must share both Redis and that protected key ring; refresh and logout operations use Redis leases to serialize changes to a session. API request timeouts are bounded below the lease duration. Network partitions can force sign-in again; no availability guarantee is made.

Registration requires working email and an externally accessible HTTPS API origin. Configure `SITELEDGER_PUBLIC_API_URL`, `SITELEDGER_SMTP_HOST`, `SITELEDGER_SMTP_PORT`, `SITELEDGER_SMTP_FROM`, and, when required, `SITELEDGER_SMTP_USERNAME` and `SITELEDGER_SMTP_PASSWORD`. SMTP uses TLS. No real email delivery or public callback has been verified for this setup.

## Production requirements

Use a managed deployment with HTTPS, a trusted reverse proxy, restricted database and Redis access, encrypted connections and credentials stored in a secret manager. Set `SiteLedgerApi__BaseUrl` to the API HTTPS origin and `ConnectionStrings__Redis` to a protected Redis endpoint. The web application requires Redis outside development. Configure both the API and MVC applications with an absolute `DataProtection__KeysPath`, and provide `DataProtection__CertificatePath` and `DataProtection__CertificatePassword` for certificate encryption. Use a persistent key store shared between instances of the same application, with separate storage for API and MVC. Protect the certificate, key files and backups. Store uploaded files in an appropriate private persistent/shared store; local container volumes do not provide cross-host sharing.

Set the API connection string, JWT issuer/audience/signing key, account public URL and SMTP configuration via a secret store. Apply and verify migrations as a separate deployment step and arrange backups and rollback. Terminate TLS at a trusted proxy and configure forwarding appropriately for your environment. This repository does not yet provide a verified production proxy, automated deployment, live SMTP verification or a completed security review; do not treat this guide as proof of production readiness.
