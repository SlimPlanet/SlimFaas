# 🚀 Guide de démarrage rapide - Multi-Upstream MCP

## Introduction rapide

Cette fonctionnalité vous permet de **fusionner plusieurs serveurs MCP** (Slack, GitHub, Jira, etc.) en un **seul endpoint unifié** avec routing automatique.

---

## ⚡ Démarrage en 3 étapes

### 1. Lancer la gateway

```bash
cd /Users/a115vc/Desktop/github/SlimFaas/src/SlimFaasMcpGateway
dotnet run
```

Ouvrez http://localhost:5000

### 2. Créer une configuration multi-upstream

Dans l'UI:
1. Cliquer sur **"New configuration"**
2. Remplir le nom: `my-multi-mcp`
3. ☑ Cocher **"Use multiple upstream servers"**
4. Cliquer sur **"+ Add upstream server"**

Pour chaque upstream:
- **Tool prefix**: `slack_` (ou `github_`, `jira_`, etc.)
- **Base URL**: `https://your-mcp-server.com`
- **JWT token**: (optionnel) pour l'authentification discovery

5. Cliquer sur **"Save"**

### 3. Tester

```bash
# Charger le catalogue fusionné
curl -X POST http://localhost:5000/api/configurations/{id}/load-catalog

# Appeler un tool (sera routé automatiquement)
curl -X POST http://localhost:5000/gateway/mcp/default/dev/my-multi-mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": "1",
    "method": "tools/call",
    "params": {
      "name": "slack_send_message",
      "arguments": { "channel": "#general", "text": "Hello!" }
    }
  }'
```

---

## 📖 Exemple complet

### Configuration via API

```bash
curl -X POST http://localhost:5000/api/configurations \
  -H "Content-Type: application/json" \
  -H "X-Audit-Author: admin@example.com" \
  -d '{
    "name": "team-tools",
    "tenantId": null,
    "upstreamServers": [
      {
        "toolPrefix": "slack_",
        "baseUrl": "https://mcp-slack.yourcompany.com",
        "discoveryJwtToken": "eyJhbGc..."
      },
      {
        "toolPrefix": "github_",
        "baseUrl": "https://mcp-github.yourcompany.com",
        "discoveryJwtToken": null
      },
      {
        "toolPrefix": "jira_",
        "baseUrl": "https://mcp-jira.yourcompany.com",
        "discoveryJwtToken": null
      }
    ],
    "catalogCacheTtlMinutes": 5,
    "description": "Unified tools for Slack, GitHub, and Jira"
  }'
```

### Catalogue résultant

```yaml
tools:
  # From upstream: slack_
  - name: "slack_send_message"
    description: "Send a message to a Slack channel"
  - name: "slack_create_channel"
    description: "Create a new Slack channel"

  # From upstream: github_
  - name: "github_create_issue"
    description: "Create a GitHub issue"
  - name: "github_create_pr"
    description: "Create a GitHub pull request"

  # From upstream: jira_
  - name: "jira_create_ticket"
    description: "Create a Jira ticket"
  - name: "jira_update_ticket"
    description: "Update a Jira ticket"
```

### Utilisation dans votre client MCP

Votre client MCP voit un **seul serveur** avec tous les tools:

```python
from mcp import Client

client = Client("http://localhost:5000/gateway/mcp/default/dev/team-tools")

# Liste tous les tools (fusionnés)
tools = client.list_tools()
# ['slack_send_message', 'github_create_issue', 'jira_create_ticket', ...]

# Appel automatiquement routé vers l'upstream Slack
result = client.call_tool("slack_send_message", {
    "channel": "#general",
    "text": "PR merged!"
})

# Appel automatiquement routé vers l'upstream GitHub
result = client.call_tool("github_create_issue", {
    "repo": "myorg/myrepo",
    "title": "Bug report",
    "body": "Description..."
})
```

---

## 🎨 Personnalisation du catalogue

Vous pouvez override le catalogue fusionné:

```yaml
# Dans "Catalog override (YAML)"
tools:
  allow:
    - slack_send_message
    - github_create_issue
    # Pas jira_* - filtrés !

  overrides:
    slack_send_message:
      description: "Post a message (custom description)"
```

---

## 🔒 Sécurité par upstream

Chaque upstream peut avoir:
- Son propre **JWT token** pour discovery
- Son propre **base URL**
- Son propre **préfixe** de tools

Les tokens sont:
- ✅ Encryptés dans la DB
- ✅ Jamais retournés en clair
- ✅ Uniquement utilisés par la gateway

---

## 🆚 Mode Legacy vs Multi-Upstream

### Mode Legacy (single URL)
```json
{
  "upstreamMcpUrl": "https://mcp.example.com"
}
```
✅ Toujours supporté
✅ Pas de préfixe sur les tools

### Mode Multi-Upstream (nouveau)
```json
{
  "upstreamServers": [
    { "toolPrefix": "slack_", "baseUrl": "https://mcp-slack.com" },
    { "toolPrefix": "github_", "baseUrl": "https://mcp-github.com" }
  ]
}
```
✅ Plusieurs upstreams
✅ Préfixes automatiques
✅ Routing dynamique

**Note**: Vous pouvez migrer du legacy au multi-upstream à tout moment via l'UI.

---

## ❓ FAQ

### Q: Dois-je migrer mes configurations existantes ?
**R**: Non, elles continuent de fonctionner en mode legacy.

### Q: Les préfixes sont-ils obligatoires ?
**R**: Oui, en mode multi-upstream, pour éviter les conflits.

### Q: Puis-je avoir deux upstreams avec le même préfixe ?
**R**: Non, les préfixes doivent être uniques.

### Q: Comment router vers un upstream spécifique ?
**R**: Automatique ! Le tool name détermine l'upstream (`slack_*` → upstream Slack).

### Q: Puis-je utiliser un préfixe vide ?
**R**: Oui, mais uniquement en mode legacy (un seul upstream).

### Q: Les tokens sont-ils sécurisés ?
**R**: Oui, encryptés avec AES-GCM dans la DB.

---

## 🛠️ Troubleshooting

### Erreur: "Duplicate tool prefix"
➡️ Chaque upstream doit avoir un préfixe unique.

### Erreur: "Either UpstreamMcpUrl or UpstreamServers must be provided"
➡️ Vous devez fournir soit `upstreamMcpUrl` (legacy) soit `upstreamServers` (multi).

### Les tools ne sont pas préfixés
➡️ Vérifiez que vous êtes en mode multi-upstream, pas en legacy.

### Routing ne fonctionne pas
➡️ Vérifiez que le tool name commence par le préfixe configuré.

---

## 📚 Documentation complète

- 📄 **MULTI_UPSTREAM_FEATURE.md** - Documentation technique détaillée
- 📄 **IMPLEMENTATION_SUMMARY.md** - Résumé de l'implémentation

---

## 🎉 C'est tout !

Vous êtes maintenant prêt à utiliser la fusion multi-upstream MCP.

**Besoin d'aide ?** Consultez les fichiers de documentation ou les commentaires dans le code.
