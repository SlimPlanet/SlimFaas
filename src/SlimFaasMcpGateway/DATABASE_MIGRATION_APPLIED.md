# ✅ Problème résolu - Migration de base de données

## Problème rencontré

```json
{
  "error": "Internal server error",
  "detail": "Cannot create a DbSet for 'UpstreamMcpServer' because this type is not included in the model for the context."
}
```

## Cause

La table `UpstreamServers` n'existait pas dans la base de données SQLite. Bien que le modèle EF Core était correctement configuré dans `GatewayDbContext.cs`, la migration n'avait pas encore été appliquée à la base de données.

## Solution appliquée

### 1. Création de la migration ✅

```bash
cd /Users/a115vc/Desktop/github/SlimFaas/src/SlimFaasMcpGateway
dotnet ef migrations add AddUpstreamMcpServers
```

**Résultat** : Migration créée avec succès

### 2. Application de la migration ✅

```bash
dotnet ef database update
```

**Résultat** : Table `UpstreamServers` créée avec succès

## Structure de la table créée

```sql
CREATE TABLE "UpstreamServers" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_UpstreamServers" PRIMARY KEY,
    "ConfigurationId" TEXT NOT NULL,
    "ToolPrefix" TEXT NOT NULL,
    "BaseUrl" TEXT NOT NULL,
    "DiscoveryJwtTokenProtected" TEXT NULL,
    "DisplayOrder" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_UpstreamServers_Configurations_ConfigurationId"
        FOREIGN KEY ("ConfigurationId")
        REFERENCES "Configurations" ("Id")
        ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_UpstreamServers_ConfigurationId_ToolPrefix"
    ON "UpstreamServers" ("ConfigurationId", "ToolPrefix");
```

## Caractéristiques de la table

- **Primary Key** : `Id` (GUID en TEXT)
- **Foreign Key** : `ConfigurationId` → `Configurations.Id` (CASCADE DELETE)
- **Index unique** : `(ConfigurationId, ToolPrefix)` - Empêche les préfixes dupliqués par configuration
- **Champs** :
  - `ToolPrefix` : Préfixe des tools (ex: "slack_", "github_")
  - `BaseUrl` : URL du serveur MCP upstream
  - `DiscoveryJwtTokenProtected` : Token JWT encrypté (optionnel)
  - `DisplayOrder` : Ordre d'affichage
  - `CreatedAtUtc` / `UpdatedAtUtc` : Timestamps

## Vérification

Pour vérifier que la table existe :

```bash
sqlite3 slimfaas_mcp_gateway.db ".schema UpstreamServers"
```

Ou directement dans l'application, créer une configuration multi-upstream et vérifier qu'elle se sauvegarde correctement.

## État actuel

✅ **Base de données à jour**
✅ **Table UpstreamServers créée**
✅ **Foreign keys configurées**
✅ **Index uniques appliqués**

L'application devrait maintenant fonctionner correctement sans l'erreur "Cannot create a DbSet for 'UpstreamMcpServer'".

---

**Date** : 27 janvier 2026
**Migration** : `20260127113502_AddUpstreamMcpServers`
**Status** : ✅ **RÉSOLU**
