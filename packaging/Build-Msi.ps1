param(
  [switch]$SkipValidation,
  [string]$FFmpegPath
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
$versionMatch = [regex]::Match((Get-Content -Raw -LiteralPath $source), 'Version="([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) { throw 'Could not read the MSI version from Fetchflow.wxs.' }
$msiVersion = $versionMatch.Groups[1].Value
$msi = Join-Path $root "Fetchflow-v$msiVersion.msi"
$latestMsi = Join-Path $root 'Fetchflow.msi'

if (-not (Test-Path -LiteralPath $candle)) {
  throw "WiX Toolset 3.14.1 binaries are missing. Extract wix314-binaries.zip into '$($root)\work\tooling\wix314'."
}
if (-not (Test-Path -LiteralPath $light)) { throw "WiX linker not found: $light" }

if (-not (Test-Path -LiteralPath $staging)) { New-Item -ItemType Directory -Force $staging | Out-Null }
$browserExtensionSource = Join-Path $root 'browser-extension'
if (-not (Test-Path -LiteralPath (Join-Path $browserExtensionSource 'manifest.json'))) { throw "Fetchflow Chrome extension is missing: $browserExtensionSource" }
$extensionStaging = Join-Path $staging 'chrome-extension'
if (Test-Path -LiteralPath $extensionStaging) { Remove-Item -LiteralPath $extensionStaging -Recurse -Force }
Copy-Item -LiteralPath $browserExtensionSource -Destination $extensionStaging -Recurse -Force
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$appSource = Join-Path $root 'native-app.cs'
$appIcon = Join-Path $root 'public\fetchflow.ico'
$appLicenseSource = Join-Path $root 'LICENSE'
if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }
if (-not (Test-Path -LiteralPath $appSource)) { throw "Fetchflow app source missing: $appSource" }
if (-not (Test-Path -LiteralPath $appIcon)) { throw "Fetchflow app icon missing: $appIcon" }
if (-not (Test-Path -LiteralPath $appLicenseSource)) { throw "Fetchflow license missing: $appLicenseSource" }
$appArguments = @(
  '/nologo', '/target:winexe', '/platform:anycpu', "/win32icon:$appIcon", "/out:$payload",
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll', '/reference:System.Net.Http.dll',
  '/reference:System.Runtime.Serialization.dll',
  $appSource
)
& $csc @appArguments
if ($LASTEXITCODE -ne 0) { throw "Fetchflow MSI app build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $payload)) { throw "Fetchflow app payload was not produced: $payload" }

# Bundle a pinned, static LGPL FFmpeg build so YouTube video/audio streams can be merged.
$ffmpegPayload = Join-Path $staging 'ffmpeg.exe'
$ffmpegNotice = Join-Path $staging 'FFmpeg-Third-Party-Notices.txt'
$ffmpegLicense = Join-Path $staging 'LGPL-3.0.txt'
$fetchflowLicense = Join-Path $staging 'Fetchflow-License.txt'
Copy-Item -LiteralPath $appLicenseSource -Destination $fetchflowLicense -Force
if ([string]::IsNullOrWhiteSpace($FFmpegPath)) {
  $ffmpegTooling = Join-Path $root 'work\tooling\ffmpeg-n9.0.2-36-g6882969ce9'
  $ffmpegPath = Join-Path $ffmpegTooling 'ffmpeg.exe'
  if (-not (Test-Path -LiteralPath $ffmpegPath)) {
    $assetName = 'ffmpeg-n9.0.2-36-g6882969ce9-win64-lgpl-9.0.zip'
    $releaseTag = 'autobuild-2026-10-25-13-00'
    $releaseUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$releaseTag"
    $archive = Join-Path $ffmpegTooling "$assetName.download"
    New-Item -ItemType Directory -Force $ffmpegTooling | Out-Null
    Write-Output 'Fetching the pinned LGPL FFmpeg build and its published SHA-256 checksum.'
    $checksumResponse = Invoke-WebRequest -UseBasicParsing -Uri "$releaseUrl/checksums.sha256" -TimeoutSec 90
    $checksumText = if ($checksumResponse.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($checksumResponse.Content) } else { [string]$checksumResponse.Content }
    $checksumLine = ($checksumText -split "`r?`n" | Where-Object { $_ -match ("\s+\*?" + [regex]::Escape($assetName) + '$') } | Select-Object -First 1)
    if (-not $checksumLine -or $checksumLine -notmatch '^\s*([a-fA-F0-9]{64})\s+') { throw "The FFmpeg release checksum list did not contain $assetName." }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $previousProgressPreference = $ProgressPreference
    try {
      $ProgressPreference = 'SilentlyContinue'
      Invoke-WebRequest -UseBasicParsing -Uri "$releaseUrl/$assetName" -OutFile $archive -TimeoutSec 1800
    } finally {
      $ProgressPreference = $previousProgressPreference
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) { Remove-Item -LiteralPath $archive -Force; throw 'The downloaded FFmpeg archive failed its SHA-256 check.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
      $entry = $zip.Entries | Where-Object { $_.FullName -match '(^|/)bin/ffmpeg\.exe$' } | Select-Object -First 1
      if ($null -eq $entry) { throw 'The verified FFmpeg archive did not contain bin/ffmpeg.exe.' }
      $inputStream = $entry.Open()
      try {
        $outputStream = [System.IO.File]::Create($ffmpegPath)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
      } finally { $inputStream.Dispose() }
    } finally { $zip.Dispose() }
    Remove-Item -LiteralPath $archive -Force
  }
}
if (-not (Test-Path -LiteralPath $FFmpegPath -PathType Leaf)) { throw "FFmpeg executable not found: $FFmpegPath" }
$sourceFfmpegHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $FFmpegPath).Hash
$stagedFfmpegHash = if (Test-Path -LiteralPath $ffmpegPayload -PathType Leaf) { (Get-FileHash -Algorithm SHA256 -LiteralPath $ffmpegPayload).Hash } else { $null }
if ($sourceFfmpegHash -ne $stagedFfmpegHash) { Copy-Item -LiteralPath $FFmpegPath -Destination $ffmpegPayload -Force }
$ffmpegVersion = (& $ffmpegPayload -L 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $ffmpegVersion -notmatch 'GNU Lesser General Public License' -or $ffmpegVersion -notmatch 'version 3') {
  throw 'The FFmpeg executable does not identify itself as the expected LGPL v3 build.'
}
$ffmpegBinaryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ffmpegPayload).Hash.ToLowerInvariant()
$ffmpegLicenseSource = Join-Path $PSScriptRoot 'third-party\LGPL-3.0.txt'
if (-not (Test-Path -LiteralPath $ffmpegLicenseSource)) { throw "FFmpeg license text is missing: $ffmpegLicenseSource" }
Copy-Item -LiteralPath $ffmpegLicenseSource -Destination $ffmpegLicense -Force
@"
Fetchflow bundles the standalone FFmpeg command-line utility.

Build: BtbN FFmpeg-Builds, pinned release autobuild-2026-10-25-13-00
Variant: Windows x64, static LGPL v3 build
Bundled ffmpeg.exe SHA-256: $ffmpegBinaryHash
FFmpeg source: https://github.com/FFmpeg/FFmpeg/tree/n9.0.2-36-g6882969ce9
Build scripts and source package: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-25-13-00
FFmpeg license information: https://ffmpeg.org/legal.html
LGPL v3 license text is included as LGPL-3.0.txt.

FFmpeg is a third-party project. Fetchflow does not claim ownership of FFmpeg.
"@ | Set-Content -LiteralPath $ffmpegNotice -Encoding UTF8

New-Item -ItemType Directory -Force (Split-Path -Parent $obj) | Out-Null
New-Item -ItemType Directory -Force $linkerTemp | Out-Null
& $candle -nologo -ext WixUIExtension -ext WixUtilExtension "-dBuildDir=$staging" -out $obj $source
if ($LASTEXITCODE -ne 0) { throw "WiX compilation failed with exit code $LASTEXITCODE" }

 $previousTemp = $env:TEMP
 $previousTmp = $env:TMP
try {
  # Keep WiX's intermediate MSI on the same writable volume as the output.
  $env:TEMP = $linkerTemp
  $env:TMP = $linkerTemp
  if ($SkipValidation) {
    Write-Warning 'Skipping WiX ICE validation because the Windows Installer service is unavailable in this build environment.'
    & $light -nologo -ext WixUIExtension -ext WixUtilExtension -cultures:en-us -sval -out $msi $obj
  } else {
    & $light -nologo -ext WixUIExtension -ext WixUtilExtension -cultures:en-us -out $msi $obj
  }
} finally {
  $env:TEMP = $previousTemp
  $env:TMP = $previousTmp
}
if ($LASTEXITCODE -ne 0) { throw "MSI linking failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath $msi -Destination $latestMsi -Force

Write-Output "Created MSI: $msi"
Write-Output "MSI size: $([math]::Round((Get-Item -LiteralPath $msi).Length / 1MB, 2)) MB"
Write-Output "Updated latest MSI copy: $latestMsi"

