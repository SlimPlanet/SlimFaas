#!/bin/bash

# Quick verification script

echo "🔍 Vérification de la configuration SPA..."
echo ""

cd "$(dirname "$0")"

echo "1️⃣ Vérification de la structure..."
if [ -d "ClientApp" ]; then
    echo "   ✅ ClientApp existe"
else
    echo "   ❌ ClientApp n'existe pas"
    exit 1
fi

if [ -f "ClientApp/package.json" ]; then
    echo "   ✅ package.json existe"
else
    echo "   ❌ package.json n'existe pas"
    exit 1
fi

if [ -f "ClientApp/vite.config.ts" ]; then
    echo "   ✅ vite.config.ts existe"
else
    echo "   ❌ vite.config.ts n'existe pas"
    exit 1
fi

echo ""
echo "2️⃣ Vérification de la configuration .csproj..."
if grep -q "SpaRoot" SlimFaasMcpGateway.Api.csproj; then
    echo "   ✅ SpaRoot configuré"
else
    echo "   ❌ SpaRoot non configuré"
    exit 1
fi

if grep -q "BuildFrontend" SlimFaasMcpGateway.Api.csproj; then
    echo "   ✅ Target BuildFrontend configuré"
else
    echo "   ❌ Target BuildFrontend non configuré"
    exit 1
fi

echo ""
echo "3️⃣ Vérification de Program.cs..."
if grep -q "UseStaticFiles" Program.cs; then
    echo "   ✅ UseStaticFiles configuré"
else
    echo "   ❌ UseStaticFiles non configuré"
    exit 1
fi

if grep -q "MapFallbackToFile" Program.cs; then
    echo "   ✅ MapFallbackToFile configuré"
else
    echo "   ❌ MapFallbackToFile non configuré"
    exit 1
fi

echo ""
echo "4️⃣ Test de compilation npm (si node_modules existe)..."
if [ -d "ClientApp/node_modules" ]; then
    echo "   ℹ️  node_modules existe, test de build..."
    cd ClientApp
    if npm run build > /dev/null 2>&1; then
        echo "   ✅ npm run build réussi"
        if [ -d "dist" ]; then
            echo "   ✅ Dossier dist créé"
            FILE_COUNT=$(find dist -type f | wc -l)
            echo "   ℹ️  $FILE_COUNT fichiers générés"
        else
            echo "   ❌ Dossier dist non créé"
        fi
    else
        echo "   ⚠️  npm run build a échoué (vérifier les erreurs)"
    fi
    cd ..
else
    echo "   ⚠️  node_modules n'existe pas (exécuter: cd ClientApp && npm install)"
fi

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "✅ Configuration SPA vérifiée avec succès!"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Pour builder et lancer l'application:"
echo "  ./build.sh"
echo "  dotnet run --urls \"http://localhost:5269\""
echo ""
