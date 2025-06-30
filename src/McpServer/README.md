# McpServer

Serveur MCP minimal conforme à la spécification MCP, écrit en **.NET 9.0** / ASP.NET Core.

## Prérequis

* [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (Preview)
* `curl` ou tout client HTTP pour tester

## Démarrage

```bash
dotnet run --project McpServer.csproj
```

Le serveur écoute alors sur **http://localhost:5000** (ou `https://localhost:5001` si Kestrel HTTPS est activé).

## Exemple d'appels

### initialise

```bash
curl -X POST http://localhost:5000/mcp \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
```

### list tools

```bash
curl -X POST http://localhost:5000/mcp \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
```

### call add

```bash
curl -X POST http://localhost:5000/mcp \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add","arguments":{"a":5,"b":7}}}'
```

## Ajouter des tools

1. Définissez un schéma JSON des arguments dans **ToolRegistry.cs**.
2. Ajoutez l'instance `Tool` dans la liste `_tools`.
3. Étendez le `switch` `tools/call` dans **Program.cs** pour gérer le nouvel outil. 

**Bonne exploration !**
