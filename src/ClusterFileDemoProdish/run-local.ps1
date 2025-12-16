$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$project = Get-ChildItem -Recurse -Filter *.csproj | Select-Object -First 1
if (-not $project) { throw "No .csproj found." }

Write-Host "Using project: $($project.FullName)"

$ports = @(3262, 3263, 3264)
$dataDirs = @(
  (Join-Path $root ".data/node1"),
  (Join-Path $root ".data/node2"),
  (Join-Path $root ".data/node3")
)

$dataDirs | ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

$procs = @()

try {
  for ($i=0; $i -lt $ports.Count; $i++) {
    $port = $ports[$i]
    $dir  = $dataDirs[$i]

    Write-Host "Starting node $($i+1) on http://127.0.0.1:$port"
    Write-Host "  -> fileStorage dir: $dir"

    $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"

    # ✅ override fileStorage section (et quelques alias au cas où)
    $env:fileStorage__dataDirectory = $dir
    $env:fileStorage__directory     = $dir
    $env:fileStorage__rootDirectory = $dir
    $env:DataFiles__DataDirectory   = $dir

    $p = Start-Process -FilePath "dotnet" `
      -ArgumentList @("run","--project",$project.FullName,"--no-launch-profile") `
      -RedirectStandardOutput (Join-Path $root "node-$port.log") `
      -RedirectStandardError  (Join-Path $root "node-$port.log") `
      -PassThru

    $procs += $p
  }

  Write-Host ""
  Write-Host "Nodes started:"
  Write-Host " - http://127.0.0.1:3262  (data: $($dataDirs[0]))"
  Write-Host " - http://127.0.0.1:3263  (data: $($dataDirs[1]))"
  Write-Host " - http://127.0.0.1:3264  (data: $($dataDirs[2]))"
  Write-Host ""
  Write-Host "Logs: node-3262.log / node-3263.log / node-3264.log"
  Write-Host "Press Ctrl+C to stop."

  while ($true) { Start-Sleep -Seconds 5 }
}
finally {
  Write-Host "Stopping nodes..."
  $procs | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
  }
}
