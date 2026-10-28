param(
  [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$wixBin = Join-Path $root 'work\tooling\wix314'
$candle = Join-Path $wixBin 'candle.exe'
$light = Join-Path $wixBin 'light.exe'
$source = Join-Path $PSScriptRoot 'Fetchflow.wxs'
$staging = Join-Path $root 'work\installer-build'
$payload = Join-Path $staging 'Fetchflow.exe'
$obj = Join-Path $root 'work\Fetchflow.wixobj'
$linkerTemp = Join-Path $root 'work\linker-temp'
$msi = Join-Path $root 'Fetchflow.msi'

if (-not (Test-Path -LiteralPath $candle)) {
  throw "WiX Toolset 3.14.1 binaries are missing. Extract wix314-binaries.zip into '$($root)\work\tooling\wix314'."
}
if (-not (Test-Path -LiteralPath $light)) { throw "WiX linker not found: $light" }

& (Join-Path $root 'build-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw "Fetchflow app build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $payload)) { throw "Fetchflow app payload was not produced: $payload" }

New-Item -ItemType Directory -Force (Split-Path -Parent $obj) | Out-Null
New-Item -ItemType Directory -Force $linkerTemp | Out-Null
& $candle -nologo "-dBuildDir=$staging" -out $obj $source
if ($LASTEXITCODE -ne 0) { throw "WiX compilation failed with exit code $LASTEXITCODE" }

 $previousTemp = $env:TEMP
 $previousTmp = $env:TMP
try {
  # Keep WiX's intermediate MSI on the same writable volume as the output.
  $env:TEMP = $linkerTemp
  $env:TMP = $linkerTemp
  if ($SkipValidation) {
    Write-Warning 'Skipping WiX ICE validation because the Windows Installer service is unavailable in this build environment.'
    & $light -nologo -sval -out $msi $obj
  } else {
    & $light -nologo -out $msi $obj
  }
} finally {
  $env:TEMP = $previousTemp
  $env:TMP = $previousTmp
}
if ($LASTEXITCODE -ne 0) { throw "MSI linking failed with exit code $LASTEXITCODE" }

Write-Output "Created MSI: $msi"
Write-Output "MSI size: $([math]::Round((Get-Item -LiteralPath $msi).Length / 1MB, 2)) MB"
