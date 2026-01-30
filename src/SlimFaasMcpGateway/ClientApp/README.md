# SlimFaasMcpGateway - Frontend Application

Frontend React/Vite pour la gateway MCP.

## Développement

### Installation des dépendances
```bash
npm install
```

### Lancer en mode développement (avec hot-reload)
```bash
npm run dev
```

L'application sera disponible sur `http://localhost:5173` avec proxy automatique vers l'API backend sur `http://localhost:5269`.

### Build de production
```bash
npm run build
```

Les fichiers compilés seront générés dans le dossier `dist/`.

### Preview du build de production
```bash
npm run preview
```

## Structure

```
ClientApp/
├── src/
│   ├── main.tsx          # Point d'entrée
│   ├── lib/
│   │   ├── api.ts        # Client API
│   │   └── types.ts      # Types TypeScript
│   ├── pages/
│   │   ├── App.tsx                      # Page principale
│   │   ├── ConfigurationsPage.tsx      # Liste des configurations
│   │   ├── ConfigurationEditorPage.tsx # Éditeur de configuration
│   │   ├── DeploymentPage.tsx          # Gestion des déploiements
│   │   └── TenantsPage.tsx             # Gestion des tenants
│   └── styles/
│       ├── index.scss     # Styles globaux
│       ├── components.scss # Styles des composants
│       └── tokens.scss    # Variables de design
├── index.html
├── package.json
├── tsconfig.json
└── vite.config.ts
```

## Configuration

### Proxy API (vite.config.ts)

En mode développement, les requêtes vers `/api`, `/gateway`, `/health` et `/metrics` sont automatiquement proxifiées vers le backend sur `http://localhost:5269`.

### Variables d'environnement

Créez un fichier `.env.local` pour les variables locales :

```env
# URL de l'API (utilisé uniquement en production)
VITE_API_URL=http://localhost:5269
```

## Build avec .NET

Le frontend est automatiquement compilé lors du build .NET :

```bash
# Depuis le répertoire parent (SlimFaasMcpGateway.Api)
dotnet build
```

Les fichiers compilés sont copiés dans `../wwwroot/` et servis par l'API .NET.

## Technologies

- **React 18** - Framework UI
- **TypeScript** - Typage statique
- **Vite** - Build tool et dev server
- **React Router** - Routing côté client
- **Sass** - Préprocesseur CSS
