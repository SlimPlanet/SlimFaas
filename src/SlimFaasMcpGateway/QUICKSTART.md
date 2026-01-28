# Guide de démarrage rapide - SPA Configuration

## 🚀 Démarrage en 3 étapes

### 1. Installer les dépendances frontend
```bash
cd ClientApp
npm install
cd ..
```

### 2. Builder l'application
```bash
# Option A : Script automatique (recommandé)
./build.sh

# Option B : Build .NET (compile automatiquement le frontend)
dotnet build

# Option C : Build manuel
cd ClientApp && npm run build && cd ..
rm -rf wwwroot
mkdir -p wwwroot
cp -r ClientApp/dist/* wwwroot/
dotnet build --no-restore
```

### 3. Lancer l'application
```bash
dotnet run --urls "http://localhost:5269"
```

Ouvrir dans le navigateur : **http://localhost:5269**

---

## 🔧 Dépannage

### Problème : "npm: command not found"
**Solution** : Installer Node.js
```bash
# macOS (avec Homebrew)
brew install node

# Vérifier l'installation
node --version
npm --version
```

### Problème : "wwwroot n'est pas créé après build"
**Causes possibles** :
1. `npm install` n'a pas été exécuté
2. Erreur lors du build npm
3. wwwroot existe déjà (skip automatique)

**Solutions** :
```bash
# 1. Vérifier que npm fonctionne
cd ClientApp
npm install
npm run build
ls -la dist/  # Doit contenir index.html

# 2. Forcer la recompilation
cd ..
rm -rf wwwroot
dotnet build

# 3. Utiliser le script de vérification
./verify-spa-setup.sh
```

### Problème : "404 Not Found" sur les routes frontend
**Cause** : `MapFallbackToFile` n'est pas configuré

**Solution** : Vérifier Program.cs contient :
```csharp
app.MapFallbackToFile("index.html");
```

### Problème : "Cannot find module 'react'" lors du npm build
**Solution** :
```bash
cd ClientApp
rm -rf node_modules package-lock.json
npm install
npm run build
```

### Problème : Erreurs CORS en développement
**Solution** : Utiliser le proxy Vite
```bash
# Au lieu de lancer directement l'API et le frontend séparément
# Utiliser npm run dev qui proxy automatiquement vers l'API

# Terminal 1 : API
dotnet run --urls "http://localhost:5269"

# Terminal 2 : Frontend avec proxy
cd ClientApp
npm run dev  # Écoute sur 5173, proxy vers 5269
```

### Problème : "The build failed" sans message d'erreur
**Solution** : Build avec verbose
```bash
dotnet build --verbosity detailed
```

### Problème : Changements frontend non pris en compte
**Cause** : Build incrémentiel skip le frontend si wwwroot existe

**Solution** :
```bash
# Option 1 : Clean puis build
dotnet clean
dotnet build

# Option 2 : Supprimer wwwroot manuellement
rm -rf wwwroot
dotnet build

# Option 3 : Builder le frontend manuellement
cd ClientApp && npm run build && cd ..
```

### Problème : Port 5269 déjà utilisé
**Solution** :
```bash
# Trouver le processus
lsof -i :5269

# Tuer le processus
kill -9 <PID>

# OU utiliser un autre port
dotnet run --urls "http://localhost:5270"
```

---

## 📋 Checklist de vérification

Utiliser le script automatique :
```bash
./verify-spa-setup.sh
```

Ou vérifier manuellement :

- [ ] `ClientApp/` existe
- [ ] `ClientApp/package.json` existe
- [ ] `ClientApp/node_modules/` existe (après npm install)
- [ ] `SlimFaasMcpGateway.Api.csproj` contient `<SpaRoot>`
- [ ] `SlimFaasMcpGateway.Api.csproj` contient `<Target Name="BuildFrontend">`
- [ ] `Program.cs` contient `app.UseStaticFiles()`
- [ ] `Program.cs` contient `app.MapFallbackToFile("index.html")`
- [ ] `ClientApp/dist/` existe après `npm run build`
- [ ] `wwwroot/` existe après `dotnet build`
- [ ] `wwwroot/index.html` existe

---

## 🎯 Modes de développement

### Mode 1 : Application intégrée (plus simple)
```bash
dotnet run --urls "http://localhost:5269"
# Frontend servi depuis wwwroot/
# Ouvrir : http://localhost:5269
```

**Avantages** :
- Un seul processus
- Pas de CORS
- Environnement identique à la production

**Inconvénient** :
- Pas de hot-reload frontend (rebuild nécessaire)

### Mode 2 : Développement séparé avec HMR (plus rapide)
```bash
# Terminal 1 : Backend
dotnet run --urls "http://localhost:5269"

# Terminal 2 : Frontend
cd ClientApp
npm run dev
# Ouvrir : http://localhost:5173
```

**Avantages** :
- Hot Module Replacement (HMR)
- Changements frontend instantanés
- Debugging facilité

**Inconvénient** :
- Deux processus à gérer
- Proxy nécessaire (déjà configuré dans vite.config.ts)

---

## 📦 Build pour production

```bash
# Clean
dotnet clean

# Publish
dotnet publish -c Release -o ./publish

# Le dossier ./publish contient tout :
# - Backend compilé
# - wwwroot/ avec frontend optimisé
# - Dépendances
```

Pour déployer, copier le contenu de `./publish/` sur le serveur.

---

## 🔍 Vérification rapide

```bash
# 1. Vérifier la configuration
./verify-spa-setup.sh

# 2. Build de test
./build.sh

# 3. Lancer
dotnet run --urls "http://localhost:5269"

# 4. Tester dans le navigateur
# Ouvrir : http://localhost:5269
# Devrait afficher le frontend React
```

---

## 📞 Support

Si vous rencontrez des problèmes :

1. **Vérifier les logs** :
   ```bash
   dotnet build --verbosity detailed
   ```

2. **Tester le frontend seul** :
   ```bash
   cd ClientApp
   npm run build
   # Vérifier que dist/ est créé
   ```

3. **Vérifier la configuration** :
   ```bash
   ./verify-spa-setup.sh
   ```

4. **Clean complet** :
   ```bash
   dotnet clean
   cd ClientApp
   rm -rf node_modules dist
   npm install
   cd ..
   dotnet build
   ```
