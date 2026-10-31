# Decisions

- The requested plans are capacity limits, not a payment integration. Payments are a future extension; a manual billing provider is required before plan-change workflows can be considered complete.
- Storage limits use binary units: MB = 1,048,576 bytes and GB = 1,073,741,824 bytes.
- Tenant-owned entities use a composite tenant and entity key to put the tenant discriminator in EF-generated updates and deletes. Query filters and save-time tenant checks add defense in depth. Raw SQL, identity records, privileged access, API authorization and blob paths still require their own controls and tests before deployment.
