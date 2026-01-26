# SlimFaasMcpGateway API

Gateway API pour MCP (Model Context Protocol) avec frontend React intégré.

## 🚀 Démarrage rapide

```bash
# 1. Installer les dépendances frontend
cd ClientApp && npm install && cd ..

# 2. Builder l'application
dotnet build

# 3. Lancer
dotnet run --urls "http://localhost:5269"

# 4. Ouvrir le navigateur
open http://localhost:5269
```

## 📁 Structure

```
SlimFaasMcpGateway.Api/
├── ClientApp/              # Frontend React/Vite
│   ├── src/                # Code source
│   ├── dist/               # Build output (généré)
│   └── README.md           # Doc frontend
├── wwwroot/                # Fichiers statiques servis (généré)
├── Auth/                   # Authentification JWT/DPoP
├── Data/                   # Entity Framework DbContext
├── Dto/                    # Data Transfer Objects
├── Gateway/                # Proxy MCP
├── Services/               # Services métier
├── Validation/             # Validation et exceptions
├── Program.cs              # Point d'entrée
└── SlimFaasMcpGateway.Api.csproj
```

## 🔨 Build

### Build complet (frontend + backend)
```bash
dotnet build
```

### Build avec script
```bash
./build.sh        # macOS/Linux
.\build.ps1       # Windows
```

### Clean + Build
```bash
dotnet clean
dotnet build
```

## 🧪 Développement

### Mode 1 : Application intégrée
```bash
dotnet run --urls "http://localhost:5269"
# Frontend servi depuis wwwroot/
```

### Mode 2 : Dev séparé avec Hot Reload
```bash
# Terminal 1 : Backend
dotnet run --urls "http://localhost:5269"

# Terminal 2 : Frontend avec HMR
cd ClientApp && npm run dev
# Ouvrir http://localhost:5173
```

## 📦 Déploiement

### Build de production
```bash
dotnet publish -c Release -o ./publish
```

Le dossier `./publish/` contient tout :
- Backend compilé
- Frontend optimisé dans wwwroot/
- Toutes les dépendances

## 🌐 Endpoints

| Route | Description |
|-------|-------------|
| `/` | Frontend SPA |
| `/api/tenants` | Gestion des tenants |
| `/api/configurations` | Gestion des configurations MCP |
| `/api/environments` | Liste des environnements |
| `/gateway/mcp/{tenant}/{env}/{config}` | Proxy MCP |
| `/health` | Health check |
| `/metrics` | Métriques Prometheus |

## 🔍 Vérification

```bash
./verify-spa-setup.sh
```

## 📚 Documentation

- [QUICKSTART.md](QUICKSTART.md) - Guide de démarrage + dépannage
- [ClientApp/README.md](ClientApp/README.md) - Documentation frontend
- [MCP_PROTOCOL.md](../../MCP_PROTOCOL.md) - Protocole MCP
- [MCP_DISCOVERY_RESILIENCE.md](../../MCP_DISCOVERY_RESILIENCE.md) - Découverte MCP

## 🛠️ Technologies

**Backend:**
- .NET 10
- ASP.NET Core Minimal APIs
- Entity Framework Core (SQLite)
- OpenTelemetry
- Prometheus metrics

**Frontend:**
- React 18
- TypeScript
- Vite
- React Router
- Sass

## ⚙️ Configuration

### appsettings.json
```json
{
  "Environments": ["dev", "staging", "prod"],
  "ConnectionStrings": {
    "Sqlite": "Data Source=slimfaas_mcp_gateway.db"
  },
  "Security": {
    "DiscoveryTokenEncryptionKey": "..."
  }
}
```

### Variables d'environnement
```bash
export ASPNETCORE_URLS="http://localhost:5269"
export ConnectionStrings__Sqlite="Data Source=mydb.db"
```

## 🔐 Sécurité

- Authentification JWT avec validation JWKS
- Support DPoP (Demonstrating Proof of Possession)
- Rate limiting configurable par tenant
- Encryption des tokens de découverte

## 📊 Observabilité

- OpenTelemetry (traces, metrics, logs)
- Export vers OTLP ou Console
- Métriques Prometheus sur `/metrics`
- Health checks sur `/health`

## 🧩 Fonctionnalités

- ✅ Gateway MCP multi-tenant
- ✅ Découverte automatique de catalog (tools/resources/prompts)
- ✅ Override de catalog en YAML
- ✅ Authentification configurable par configuration
- ✅ Rate limiting par tenant/environment
- ✅ Audit trail complet
- ✅ Gestion des déploiements par environnement
- ✅ Cache de catalog avec TTL
- ✅ Frontend React intégré

## 🤝 Contribution

Le projet utilise :
- C# 12 avec nullable reference types
- Records pour les DTOs
- Minimal APIs pour les endpoints
- YamlDotNet pour le parsing YAML

## 📝 License

[MIT](../../LICENSE)
