param([string]$OutputDirectory='')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console-encoding.ps1')
$root = Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'app-encoder'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(!$OutputDirectory.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must stay inside nicobe.'}
$env:DOTNET_CLI_HOME = Join-Path $root '.build-home'
$env:NUGET_PACKAGES = Join-Path $root '.packages'
$env:TEMP = Join-Path $root 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
New-Item -ItemType Directory -Force -Path $env:TEMP,(Join-Path $root 'temp\test-results') | Out-Null
dotnet build (Join-Path $root 'src\NicoBE.Encoder\NicoBE.Encoder.csproj') -c Release -o $OutputDirectory --configfile (Join-Path $root 'NuGet.Config') -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $root 'temp\test-results\encoder-build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $root 'temp\test-results\encoder-build.log') -Encoding UTF8 -Tail 45; throw 'Encoder build failed.' }
Get-Content (Join-Path $root 'temp\test-results\encoder-build.log') -Encoding UTF8 -Tail 10
dotnet build (Join-Path $root 'src\NicoBE.Encoder.Cli\NicoBE.Encoder.Cli.csproj') -c Release -o $OutputDirectory --configfile (Join-Path $root 'NuGet.Config') -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $root 'temp\test-results\encoder-cli-build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $root 'temp\test-results\encoder-cli-build.log') -Encoding UTF8 -Tail 45; throw 'Encoder CLI build failed.' }
Get-Content (Join-Path $root 'temp\test-results\encoder-cli-build.log') -Encoding UTF8 -Tail 10
