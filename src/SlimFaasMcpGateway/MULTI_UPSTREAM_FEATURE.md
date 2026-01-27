# Multi-Upstream MCP Support

## Vue d'ensemble

La gateway MCP supporte maintenant la fusion de plusieurs serveurs MCP upstream en un seul service unifié. Chaque upstream peut avoir son propre préfixe de tool pour éviter les collisions de noms.

## Fonctionnalités

### 1. Configuration multi-upstream

Chaque configuration peut maintenant avoir :
- **Mode legacy** : Un seul `UpstreamMcpUrl` (rétrocompatible)
- **Mode multi-upstream** : Une liste de serveurs upstream avec préfixes

### 2. Structure d'un upstream

Chaque upstream contient :
```csharp
public class UpstreamMcpServer
{
    public Guid Id { get; set; }
    public Guid ConfigurationId { get; set; }
    public string ToolPrefix { get; set; }        // Ex: "slack_", "github_"
    public string BaseUrl { get; set; }            // URL du serveur MCP
    public string? DiscoveryJwtTokenProtected { get; set; }
    public int DisplayOrder { get; set; }
}
```

### 3. Découverte de catalogue fusionné

Le service `McpDiscoveryService` :
- Interroge chaque upstream séparément
- Récupère `tools/list`, `resources/list`, `prompts/list` de chacun
- Fusionne les résultats en ajoutant le préfixe à chaque tool/resource/prompt
- Génère un catalogue YAML unifié

Exemple de catalogue fusionné :
```yaml
# MCP catalog discovery - merged from multiple upstreams
# Tool prefixes are added to avoid conflicts

tools:
  # From upstream: slack_
  - name: "slack_send_message"
    description: "Send a message to Slack"
  # From upstream: github_
  - name: "github_create_issue"
    description: "Create a GitHub issue"

resources:
  # From upstream: slack_
  - uri: "slack_channels"
  # From upstream: github_
  - uri: "github_repos"
```

### 4. Routing dynamique des appels

Le `GatewayProxyHandler` :
1. Intercepte les appels MCP (ex: `tools/call`)
2. Parse le body JSON-RPC pour extraire le nom du tool
3. Trouve l'upstream correspondant basé sur le préfixe du tool
4. Route la requête vers le bon serveur upstream

Exemple de flow :
```
Client appelle : tools/call { "name": "slack_send_message" }
  ↓
Gateway extrait : "slack_send_message"
  ↓
Gateway identifie : préfixe "slack_"
  ↓
Gateway route vers : upstream avec ToolPrefix="slack_"
  ↓
Upstream reçoit la requête
```

### 5. Override de catalogue

Le `CatalogOverrideYaml` peut utiliser les noms avec préfixes :

```yaml
tools:
  allow:
    - slack_send_message
    - github_create_issue
  overrides:
    slack_send_message:
      description: "Send a Slack message (custom description)"
```

## API

### Créer/Mettre à jour une configuration

**Mode legacy (single upstream)** :
```json
{
  "name": "my-config",
  "tenantId": null,
  "upstreamMcpUrl": "https://mcp.example.com",
  "discoveryJwtToken": "optional-token",
  "catalogCacheTtlMinutes": 5
}
```

**Mode multi-upstream** :
```json
{
  "name": "my-config",
  "tenantId": null,
  "upstreamServers": [
    {
      "toolPrefix": "slack_",
      "baseUrl": "https://mcp-slack.example.com",
      "discoveryJwtToken": "optional-slack-token"
    },
    {
      "toolPrefix": "github_",
      "baseUrl": "https://mcp-github.example.com",
      "discoveryJwtToken": "optional-github-token"
    }
  ],
  "catalogCacheTtlMinutes": 5
}
```

### Récupérer une configuration

La réponse inclut :
```json
{
  "id": "...",
  "name": "my-config",
  "upstreamMcpUrl": null,
  "upstreamServers": [
    {
      "toolPrefix": "slack_",
      "baseUrl": "https://mcp-slack.example.com",
      "discoveryJwtToken": null,
      "hasDiscoveryJwtToken": true
    },
    {
      "toolPrefix": "github_",
      "baseUrl": "https://mcp-github.example.com",
      "discoveryJwtToken": null,
      "hasDiscoveryJwtToken": false
    }
  ]
}
```

## Interface Utilisateur

### Configuration Editor

L'UI propose maintenant :

1. **Toggle "Use multiple upstream servers"**
   - Si décoché : affiche le champ legacy `Upstream MCP base URL`
   - Si coché : affiche la liste des upstreams

2. **Gestion dynamique des upstreams**
   - Bouton "+ Add upstream server" pour ajouter un upstream
   - Chaque upstream a :
     - Tool prefix (obligatoire)
     - Base URL (obligatoire)
     - Discovery JWT token (optionnel)
     - Bouton "Remove" pour le supprimer

3. **Validation**
   - En mode single : `upstreamMcpUrl` doit être rempli
   - En mode multi : au moins 1 upstream avec prefix et URL valides
   - Pas de préfixes en double

## Base de données

### Nouvelle table : `UpstreamServers`

```sql
CREATE TABLE UpstreamServers (
    Id TEXT PRIMARY KEY,
    ConfigurationId TEXT NOT NULL,
    ToolPrefix TEXT NOT NULL,
    BaseUrl TEXT NOT NULL,
    DiscoveryJwtTokenProtected TEXT,
    DisplayOrder INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (ConfigurationId) REFERENCES Configurations(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IX_UpstreamServers_ConfigurationId_ToolPrefix
    ON UpstreamServers(ConfigurationId, ToolPrefix);
```

### Migration

Pour migrer les configurations existantes :
```bash
cd /Users/a115vc/Desktop/github/SlimFaas/src/SlimFaasMcpGateway
dotnet ef migrations add AddUpstreamMcpServers
dotnet ef database update
```

Les configurations existantes continueront de fonctionner en mode legacy (single upstream).

## Rétrocompatibilité

✅ **100% rétrocompatible**

- Les configurations existantes avec `UpstreamMcpUrl` continuent de fonctionner
- Le système crée automatiquement un `UpstreamMcpServer` avec préfixe vide pour les configs legacy
- L'API accepte toujours `upstreamMcpUrl` dans les requêtes
- L'UI affiche le mode approprié selon la configuration

## Cas d'usage

### Exemple 1 : Slack + GitHub + Jira

```json
{
  "name": "team-tools",
  "upstreamServers": [
    { "toolPrefix": "slack_", "baseUrl": "https://mcp-slack.company.com" },
    { "toolPrefix": "github_", "baseUrl": "https://mcp-github.company.com" },
    { "toolPrefix": "jira_", "baseUrl": "https://mcp-jira.company.com" }
  ]
}
```

Résultat : Un seul endpoint MCP qui expose tous les tools de Slack, GitHub et Jira avec préfixes.

### Exemple 2 : Environnements multiples

```json
{
  "name": "multi-env",
  "upstreamServers": [
    { "toolPrefix": "prod_", "baseUrl": "https://mcp-prod.company.com" },
    { "toolPrefix": "staging_", "baseUrl": "https://mcp-staging.company.com" }
  ]
}
```

Permet d'exposer les tools de production et staging dans le même catalogue.

### Exemple 3 : Migration progressive

1. Démarrer en mode legacy avec un seul upstream
2. Activer le mode multi-upstream dans l'UI
3. Ajouter progressivement d'autres upstreams
4. Les clients n'ont pas besoin de changer leur configuration

## Sécurité

- Chaque upstream peut avoir son propre JWT token pour la découverte
- Les tokens sont encryptés dans la base de données
- Les tokens ne sont jamais retournés en clair par l'API
- Le routing se fait côté gateway, les clients n'ont pas besoin de connaître les upstreams

## Performance

- **Cache de catalogue** : fonctionne avec les catalogues fusionnés
- **Routing** : O(n) où n = nombre d'upstreams (négligeable pour <100 upstreams)
- **Découverte** : parallélisable (appels HTTP concurrents vers chaque upstream)

## Limitations actuelles

1. **Préfixes obligatoires en mode multi** : Pour éviter les collisions
2. **Pas de fusion intelligente** : Si deux upstreams ont le même tool, le premier l'emporte
3. **Pas de load balancing** : Chaque tool est routé vers un seul upstream
4. **Pas de fallback** : Si un upstream est down, les tools de cet upstream échouent

## Améliorations futures possibles

- [ ] Préfixe optionnel avec détection automatique des collisions
- [ ] Load balancing entre plusieurs upstreams pour le même préfixe
- [ ] Circuit breaker par upstream
- [ ] Métriques par upstream (latence, taux d'erreur)
- [ ] UI pour visualiser le mapping tool → upstream
- [ ] Support de wildcards dans les préfixes (ex: `team-*`)

## Tests

### Test manuel

1. Créer une configuration multi-upstream dans l'UI
2. Ajouter 2+ upstreams avec préfixes différents
3. Appeler `/api/configurations/{id}/load-catalog`
4. Vérifier que le YAML contient les tools de tous les upstreams avec préfixes
5. Appeler un tool via la gateway : `/gateway/mcp/default/dev/my-config`
6. Vérifier que le tool est routé vers le bon upstream

### Tests unitaires à ajouter

- [ ] `ConfigurationService` : validation des préfixes uniques
- [ ] `McpDiscoveryService` : fusion de catalogues
- [ ] `GatewayProxyHandler` : extraction du tool name et routing
- [ ] `GatewayResolver` : GetUpstreamsAsync

## Documentation API

### Endpoints

- `POST /api/configurations` - Créer avec upstreams
- `PUT /api/configurations/{id}` - Mettre à jour les upstreams
- `GET /api/configurations/{id}` - Récupérer avec upstreams
- `POST /api/configurations/{id}/load-catalog` - Catalogue fusionné

### JSON Schema

Voir `Dto/Dto.cs` pour les définitions complètes des DTOs.
