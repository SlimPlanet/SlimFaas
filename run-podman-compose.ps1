param(
    # Forward all remaining arguments to "podman compose"
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ComposeArgs
)

$ErrorActionPreference = 'Stop'

# Enable verbose mode if VERBOSE=1
$verboseEnv = $env:VERBOSE
$VerboseEnabled = $false
if ($verboseEnv -eq '1') {
    $VerboseEnabled = $true
    $VerbosePreference = 'Continue'
    Write-Verbose "Verbose mode enabled (VERBOSE=1)"
}

# Podman log level (debug if VERBOSE=1, otherwise info)
if (-not $env:PODMAN_LOG_LEVEL) {
    if ($VerboseEnabled) {
        $env:PODMAN_LOG_LEVEL = 'debug'
    } else {
        $env:PODMAN_LOG_LEVEL = 'info'
    }
}
Write-Host ">> Using PODMAN_LOG_LEVEL=$($env:PODMAN_LOG_LEVEL)"

# Use the active Podman connection, including machines with custom names.
if (-not $env:DOCKER_SOCKET_PATH) {
    $env:DOCKER_SOCKET_PATH = "/run/docker.sock"
}
Write-Host ">> Using DOCKER_SOCKET_PATH=$($env:DOCKER_SOCKET_PATH)"

# podman compose configures the provider's host connection itself. This value
# is interpolated into SlimFaas, where the socket is mounted.
$env:DOCKER_HOST = "unix:///var/run/docker.sock"

$cmdPreview = "podman --log-level=$($env:PODMAN_LOG_LEVEL) compose $($ComposeArgs -join ' ')"
Write-Host ">> Running: $cmdPreview"

podman --log-level=$env:PODMAN_LOG_LEVEL compose @ComposeArgs
exit $LASTEXITCODE
