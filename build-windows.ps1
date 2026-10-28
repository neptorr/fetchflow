$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$source = Join-Path $root 'native-app.cs'
$exe = Join-Path $root 'Fetchflow.exe'
$staging = Join-Path $root 'work\installer-build'
$payload = Join-Path $staging 'Fetchflow.exe'
$installerSource = Join-Path $root 'installer.cs'
$installer = Join-Path $root 'Fetchflow-Setup.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }
if (-not (Test-Path -LiteralPath $source)) { throw "Windows app source missing: $source" }
if (-not (Test-Path -LiteralPath $installerSource)) { throw "Installer source missing: $installerSource" }
New-Item -ItemType Directory -Force $staging | Out-Null

$arguments = @(
  '/nologo', '/target:winexe', '/platform:anycpu', "/out:$payload",
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll', '/reference:System.Net.Http.dll',
  '/reference:System.Runtime.Serialization.dll',
  $source
)

& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Windows app compilation failed with exit code $LASTEXITCODE" }
try {
  Copy-Item -LiteralPath $payload -Destination $exe -Force -ErrorAction Stop
} catch {
  Write-Warning "The standalone app is in use, so the root Fetchflow.exe was left unchanged. The installer will contain the newly built app."
}
$setupArguments = @(
  '/nologo', '/target:winexe', '/platform:anycpu', "/out:$installer",
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll', '/reference:System.Runtime.Serialization.dll',
  "/resource:$payload,Fetchflow.Payload.exe",
  $installerSource
)
& $csc @setupArguments
if ($LASTEXITCODE -ne 0) { throw "Windows installer compilation failed with exit code $LASTEXITCODE" }
Write-Output "Built app payload: $payload"
Write-Output "App size: $([math]::Round((Get-Item -LiteralPath $payload).Length / 1KB, 1)) KB"
Write-Output 'This is a native Windows Forms app. It does not use WebView2 or start a local web server.'
Write-Output "Created installer $installer"
Write-Output "Installer size: $([math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 2)) MB"
