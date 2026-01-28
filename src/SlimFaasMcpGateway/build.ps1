# Build script for SlimFaasMcpGateway (Windows)

Write-Host "==================================" -ForegroundColor Cyan
Write-Host "Building SlimFaasMcpGateway" -ForegroundColor Cyan
Write-Host "==================================" -ForegroundColor Cyan
Write-Host ""

# Navigate to script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

try {
    Write-Host "📦 Installing frontend dependencies..." -ForegroundColor Yellow
    Set-Location ClientApp
    npm install
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }

    Write-Host ""
    Write-Host "🏗️  Building frontend..." -ForegroundColor Yellow
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm build failed" }

    Write-Host ""
    Write-Host "📋 Copying frontend to wwwroot..." -ForegroundColor Yellow
    Set-Location ..
    if (Test-Path wwwroot) {
        Remove-Item -Recurse -Force wwwroot
    }
    New-Item -ItemType Directory -Force -Path wwwroot | Out-Null
    Copy-Item -Path "ClientApp\dist\*" -Destination "wwwroot\" -Recurse -Force

    Write-Host ""
    Write-Host "🔨 Building .NET backend..." -ForegroundColor Yellow
    dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    Write-Host ""
    Write-Host "✅ Build completed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "To run the application:" -ForegroundColor Cyan
    Write-Host "  dotnet run --urls `"http://localhost:5269`"" -ForegroundColor White
    Write-Host ""
    Write-Host "Then open: http://localhost:5269" -ForegroundColor Cyan
}
catch {
    Write-Host ""
    Write-Host "❌ Build failed: $_" -ForegroundColor Red
    exit 1
}
