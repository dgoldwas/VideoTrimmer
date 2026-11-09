param([switch]$Zip)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
# The project's <Version> is the only place the version is set: YYYY.MM.DD.## (release date, then the build number for that day).
$project = Join-Path $projectRoot 'VideoTrimmer.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d{4}\.\d{2}\.\d{2}\.\d{2}$') { throw "The version in $project must be YYYY.MM.DD.##; found '$version'." }
$publishRoot = Join-Path $projectRoot "artifacts\VideoTrimmer-$version-win-x64"
if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed' }
$exe = Join-Path $publishRoot 'VideoTrimmer.exe'
if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $version) { throw "The published VideoTrimmer.exe does not report version $version." }
# VideoTrimmer is a GUI app, so wait for its self-check explicitly.
$check = Start-Process -FilePath $exe -ArgumentList '--check' -Wait -PassThru
$checkResult = Get-Content -LiteralPath (Join-Path $env:TEMP 'vt-check.txt') -Raw
if ($check.ExitCode -ne 0 -or $checkResult.Trim() -ne 'ok') { throw "Self-check failed:`n$checkResult" }
Set-Content -LiteralPath (Join-Path $publishRoot 'VERSION.txt') -Value $version -NoNewline

# Pinned GPL shared Windows FFmpeg build. Exact-frame exports need libx264, which LGPL builds leave out.
$ffmpegRelease = 'autobuild-2026-10-27-18-38'
$ffmpegName = 'ffmpeg-n9.0.2-36-g6882969ce9-win64-gpl-shared-9.0'
$ffmpegSha256 = 'dbfef481021a393424c9bd4e588c8d568f0ebba780c01c63dfcb87c87be901c7'
$cacheRoot = Join-Path $projectRoot "artifacts\.cache\ffmpeg-$ffmpegRelease"
$archivePath = Join-Path $cacheRoot "$ffmpegName.zip"
$extractRoot = Join-Path $cacheRoot 'extracted'
$ffmpegRoot = Join-Path $extractRoot $ffmpegName
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
if (!(Test-Path -LiteralPath $archivePath)) {
  $ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$ffmpegRelease/$ffmpegName.zip"
  Invoke-WebRequest -Uri $ffmpegUrl -OutFile $archivePath
}
$actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $ffmpegSha256) { throw "FFmpeg archive checksum mismatch. Expected $ffmpegSha256; got $actualSha256." }
if (!(Test-Path -LiteralPath (Join-Path $ffmpegRoot 'bin\ffmpeg.exe'))) {
  Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
}

# Replace any development FFmpeg the publish copied with the pinned build.
$toolsRoot = Join-Path $publishRoot 'tools'
if (Test-Path -LiteralPath $toolsRoot) { Remove-Item -LiteralPath $toolsRoot -Recurse -Force }
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot 'bin') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('ffmpeg.exe', 'ffprobe.exe') } | ForEach-Object {
  Copy-Item -LiteralPath $_.FullName -Destination $toolsRoot -Force
}
Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'LICENSE.txt') -Destination (Join-Path $toolsRoot 'FFmpeg-LICENSE.txt') -Force
$ffmpegExe = Join-Path $toolsRoot 'ffmpeg.exe'
# FFmpeg writes its banner to stderr; Windows PowerShell 5.1 treats redirected stderr as errors under 'Stop'.
$ErrorActionPreference = 'Continue'
$versionText = (& $ffmpegExe -version 2>&1 | ForEach-Object { "$_" } | Select-Object -First 1) -join "`n"
$encoders = (& $ffmpegExe -hide_banner -encoders 2>&1 | ForEach-Object { "$_" } | Out-String)
$ErrorActionPreference = 'Stop'
if ($encoders -notmatch 'libx264') { throw 'The bundled FFmpeg has no libx264 encoder, which exact-frame export needs.' }
@(
  $versionText
  "Build source: https://github.com/BtbN/FFmpeg-Builds/releases/tag/$ffmpegRelease"
  "FFmpeg archive: $ffmpegName.zip"
  "FFmpeg archive SHA-256: $ffmpegSha256"
  'This is a GPL build of FFmpeg (it includes libx264). See FFmpeg-LICENSE.txt and https://ffmpeg.org/legal.html.'
) | Set-Content -LiteralPath (Join-Path $toolsRoot 'FFmpeg-BUILD.txt') -Encoding utf8

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'UPDATE.md') -Destination $publishRoot
if ($Zip) {
  $zipPath = "$publishRoot.zip"
  if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
  Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath
  Write-Host "Zipped $zipPath"
}
# build-installer.ps1 reads the publish folder from this last line.
Write-Output $publishRoot
