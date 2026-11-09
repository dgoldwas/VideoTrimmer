param(
  [string]$Compiler = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  # Installs the new Setup.exe into Program Files after building it.
  [switch]$Install,
  # Publishes the Setup.exe as GitHub release v<version>, which installed copies offer as an update.
  [switch]$Release
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Compiler)) { throw "Inno Setup compiler not found: $Compiler" }
if ($Release -and (& git -C $PSScriptRoot status --porcelain)) { throw 'Commit your changes before releasing, so the release tag matches the build.' }
# build.ps1 publishes the app, checks its version and prints the publish folder last.
& (Join-Path $PSScriptRoot 'build.ps1') | ForEach-Object { Write-Host $_; $publishRoot = "$_" }
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$version = Get-Content -LiteralPath (Join-Path $publishRoot 'VERSION.txt') -Raw
if ($version -notmatch '^\d{4}\.\d{2}\.\d{2}\.\d{2}$') { throw "Unexpected version '$version'." }
& $Compiler "/DAppVersion=$version" "/DPayloadRoot=$publishRoot" (Join-Path $PSScriptRoot 'installer\VideoTrimmer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup = Join-Path $PSScriptRoot "artifacts\VideoTrimmer-$version-Setup.exe"
Write-Output "Built $setup (SHA-256 $((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash))."

if ($Install) {
  # Setup elevates through UAC and closes a running VideoTrimmer itself.
  $process = Start-Process -FilePath $setup -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS' -Verb RunAs -Wait -PassThru
  if ($process.ExitCode -ne 0) { throw "Setup exited with code $($process.ExitCode)." }
  $installed = Join-Path $env:ProgramFiles 'VideoTrimmer\VideoTrimmer.exe'
  if ((Get-Item -LiteralPath $installed).VersionInfo.ProductVersion -ne $version) { throw "Installed VideoTrimmer.exe does not report version $version." }
  Write-Output "Installed VideoTrimmer $version to $(Split-Path $installed)."
}

if ($Release) {
  $tag = "v$version"
  $notes = "VideoTrimmer $version. Install with the Setup.exe below; installed copies offer this release from the version button in the status bar."
  # Tag the commit that was built, not whatever the default branch points at.
  $commit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
  & gh release create $tag $setup --repo dgoldwas/VideoTrimmer --target $commit --title "VideoTrimmer $version" --notes $notes
  if ($LASTEXITCODE -ne 0) { throw "Creating GitHub release $tag failed." }
  Write-Output "Released $tag."
}
