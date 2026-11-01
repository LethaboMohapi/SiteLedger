# Security boundaries and verification limits

This is an engineering record of implemented controls and unresolved work, not a certification or independent security assessment.

## Implemented boundaries

- The API checks JWT issuer, audience, signature, lifetime, current account activity, security stamp, tenant subscription and current roles. Tenant IDs from routes are not treated as proof of access.
- Tenant-owned EF entities use composite tenant/entity keys, query filters, write stamping and version checks. Project-child creation and project deletion serialize on the tenant row and recheck project access inside that transaction.
- Refresh secrets and one-time recovery codes are hashed. Refresh-token replay revokes the token family. An existing access token expires normally after refresh-family logout; password or security-stamp changes invalidate it earlier.
- MVC access and refresh tokens are held in encrypted server-side session data. Protected cookies carry a random session key. Redis coordinates refresh/logout across instances; HTTPS, trusted Redis access and shared protected keys are required in production. API failures during logout are reported rather than described as successful remote revocation.
- Email queue contents are encrypted with Data Protection. Messages have leases, retry backoff and expiry; sensitive payloads are removed after delivery or expiry. Delivery is at least once, so a crash after SMTP acceptance can duplicate a message.
- Private file endpoints enforce project access, sharing and quotas. Download responses are attachments. Current storage uses local disk and file signatures only.
- Authenticated controller reads, exports and downloads persist tenant-scoped access attempts before execution, followed by status events. Audit metadata contains route templates rather than query values or bodies. Initial audit-storage failures block the read; completion failures emit critical logs. Crashes can leave unmatched attempts. These events do not prove delivery to the remote client.
- MVC account forms validate server-side, require password confirmation for registration/reset, retain safe fields and clear secrets when showing errors. Client validation improves feedback but never replaces server/API checks.

## Unresolved requirements

- A production shared file service, crash reconciliation, orphan cleanup and malware/full-format inspection are not implemented or verified.
- Audit events cover selected writes and authenticated controller reads/exports/downloads. Authentication lifecycle, anonymous access, retention and external tamper-resistant anchoring remain incomplete. Application authorization prevents ordinary users from editing audit records, but a privileged database operator can alter them. Completion-write failures need operational monitoring.
- Real SMTP, production certificate/key rotation, secure external Redis and a trusted TLS proxy have not been verified against a named environment.
- Integration tests cover selected authorization, tenant-isolation, refresh, workflow and MVC paths. They are not a penetration test or proof that all access paths are safe.

Use separate migration and runtime identities, encrypted connections, restricted secret access, monitored backups and deployment-specific logging. Do not infer production readiness from a successful build or test count.
