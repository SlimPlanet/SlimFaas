#!/bin/bash

# Build script for SlimFaasMcpGateway (macOS/Linux)

set -e

echo "=================================="
echo "Building SlimFaasMcpGateway"
echo "=================================="
echo ""

# Navigate to project root
cd "$(dirname "$0")"

echo "📦 Installing frontend dependencies..."
cd ClientApp
npm install

echo ""
echo "🏗️  Building frontend..."
npm run build

echo ""
echo "📋 Copying frontend to wwwroot..."
cd ..
rm -rf wwwroot
mkdir -p wwwroot
cp -r ClientApp/dist/* wwwroot/

echo ""
echo "🔨 Building .NET backend..."
dotnet build --no-restore

echo ""
echo "✅ Build completed successfully!"
echo ""
echo "To run the application:"
echo "  dotnet run --urls \"http://localhost:5269\""
echo ""
echo "Then open: http://localhost:5269"
