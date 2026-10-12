param(
    [switch]$Validate,
    [switch]$Clean,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ExtraArguments
)
$ErrorActionPreference = 'Stop'
$env:SLIMFAAS_DEMO_ROOT = $PSScriptRoot.Replace('\', '/')
$env:SLIMFAAS_DEMO_EXE_SUFFIX = '.exe'
$runtime = Join-Path $PSScriptRoot 'runtime/SlimFaas.exe'
$manifestArguments = @('-f', (Join-Path $PSScriptRoot 'slimfaas.local.yaml'), '-f', (Join-Path $PSScriptRoot 'slimfaas.local.prebuilt.yaml'))
& $runtime local validate @manifestArguments @ExtraArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Validate) { exit 0 }
Write-Host 'Starting the demo. The configured dashboard URL is printed below; wait for readiness. Press Ctrl+C to stop.'
if ($Clean) { $manifestArguments += '--clean' }
& $runtime local up @manifestArguments @ExtraArguments
exit $LASTEXITCODE
