param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$VersionFile = '',
    [string]$EncoderVersionFile = '',
    [string]$Ga4IdFile = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console-encoding.ps1')
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputPath)
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $output -eq (Join-Path $root 'src\Website\index.html')) { throw 'Page output must stay inside nicobe and must not overwrite the template.' }
if (!$VersionFile) { $VersionFile = Join-Path $root 'release/version.txt' }
if (!$Ga4IdFile) { $Ga4IdFile = Join-Path $root '.local\ga4-id.txt' }
if (!$EncoderVersionFile) { $EncoderVersionFile = Join-Path $root 'release/version_encoder.txt' }
. (Join-Path $PSScriptRoot 'read-release-history.ps1')
function Set-PageMarker([string]$Html, [string]$Name, [string]$Value) {
    $pattern = '(?s)<!-- ' + [regex]::Escape($Name) + ' -->.*?<!-- /' + [regex]::Escape($Name) + ' -->'
    $matches = [regex]::Matches($Html, $pattern)
    if ($matches.Count -ne 1) { throw "Missing or duplicate HTML marker: $Name" }
    return $Html.Replace($matches[0].Value, "<!-- $Name -->" + $Value + "<!-- /$Name -->")
}
$html = [IO.File]::ReadAllText((Join-Path $root 'src\Website\index.html'))
foreach ($app in @(@{Name='NicoBE'; Key='NICOBE'; File=$VersionFile}, @{Name='NicoBE Encoder'; Key='ENCODER'; File=$EncoderVersionFile})) {
    $entries = @(Read-ReleaseHistory -Path $app.File)
    $html = Set-PageMarker $html ($app.Key + '_VERSION') ('v' + $entries[0].Version)
    $html = Set-PageMarker $html ($app.Key + '_DATE') $entries[0].Date
    $items = foreach ($entry in $entries) {
        $changes = foreach ($change in $entry.Changes) { '<li>' + [Net.WebUtility]::HtmlEncode($change) + '</li>' }
        '<div class="history-entry"><p class="history-heading"><strong>v' + $entry.Version + '</strong><time datetime="' + $entry.Date + '">' + $entry.Date + '</time></p><ul>' + ($changes -join '') + '</ul></div>'
    }
    $history = '<div class="release-history"><p class="history-label">更新履歴</p><div class="history-text" tabindex="0" role="region" aria-label="' + $app.Name + 'の更新履歴">' + ($items -join '') + '</div></div>'
    $html = Set-PageMarker $html ($app.Key + '_HISTORY') $history
}
$id = ''
if (Test-Path -LiteralPath $Ga4IdFile -PathType Leaf) {
    $id = [IO.File]::ReadAllText($Ga4IdFile).Trim()
}
if ($id -cnotmatch '^G-[A-Z0-9]+$') {
    Write-Warning 'GA4 ID is missing or invalid in .local/ga4-id.txt. The HTML will be generated without analytics.'
    $id = ''
}
if ([regex]::Matches($html, '__NICOBE_GA4_ID__').Count -ne 1) { throw 'Missing or duplicate GA4 placeholder.' }
$html = $html.Replace('__NICOBE_GA4_ID__', $id)
[IO.File]::WriteAllText($output, $html, (New-Object Text.UTF8Encoding($false)))
