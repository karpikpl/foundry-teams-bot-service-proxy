# Changelog

## 0.9.0

### Breaking changes

- Removed background Foundry agent-catalog refresh. The container app identity no longer needs `Azure AI User` / `Foundry User` RBAC on the Foundry project.
- Agent catalogs are fetched on demand with the signed-in user's OBO token and cached per `(userObjectId, projectEndpoint)`.
- If OBO is unavailable, catalog lookups return no agents instead of falling back to the container managed identity.

### Configuration

- Added `Foundry__CatalogCacheSeconds` to configure the per-user catalog cache TTL. Default: `300` seconds.
