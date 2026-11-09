param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$VersionFile = '',
    [string]$Ga4IdFile = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputPath)
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $output -eq (Join-Path $root 'src\Website\index.html')) { throw 'Page output must stay inside nicobe and must not overwrite the template.' }
if (!$VersionFile) { $VersionFile = Join-Path $root 'release/version.txt' }
if (!$Ga4IdFile) { $Ga4IdFile = Join-Path $root '.local\ga4-id.txt' }
$versions = [IO.File]::ReadAllText($VersionFile)
$html = [IO.File]::ReadAllText((Join-Path $root 'src\Website\index.html'))
foreach ($app in @(@{Name='NicoBE'; Key='NICOBE'}, @{Name='NicoBE Encoder'; Key='ENCODER'})) {
    foreach ($field in @('VERSION', 'DATE')) {
        $name = $app.Name
        if ($field -eq 'DATE') { $name += ' Date' }
        $entries = [regex]::Matches($versions, ('(?m)^' + [regex]::Escape($name) + ':([^\r\n]+)'))
        if ($entries.Count -ne 1) { throw "release/version.txt must contain exactly one ${name}: entry." }
        $value = $entries[0].Groups[1].Value.Trim()
        if ($field -eq 'VERSION') {
            if ($value -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version for $name" }
            $value = 'v' + $value
        } else {
            $date = [datetime]::MinValue
            if (![datetime]::TryParseExact($value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
                throw "Invalid date for $name (expected yyyy-MM-dd)."
            }
        }
        $marker = $app.Key + '_' + $field
        $pattern = '(?s)(<!-- ' + $marker + ' -->).*?(<!-- /' + $marker + ' -->)'
        if ([regex]::Matches($html, $pattern).Count -ne 1) { throw "Missing or duplicate HTML marker: $marker" }
        $html = [regex]::Replace($html, $pattern, ('${1}' + $value + '${2}'))
    }
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
