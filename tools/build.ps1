param([string]$OutputDirectory='')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console-encoding.ps1')
$root = Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'app'}
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
$env:MSBuildEnableWorkloadResolver = 'false'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:TEMP,(Join-Path $root 'temp\test-results') | Out-Null
& (Join-Path $PSScriptRoot 'build-gpu.ps1') -OutputDirectory $OutputDirectory
dotnet build (Join-Path $root 'src\NicoBE\NicoBE.csproj') -c Release -o $OutputDirectory --configfile (Join-Path $root 'NuGet.Config') -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $root 'temp\test-results\build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $root 'temp\test-results\build.log') -Encoding UTF8 -Tail 65; throw 'Build failed.' }
Get-Content (Join-Path $root 'temp\test-results\build.log') -Encoding UTF8 -Tail 10
