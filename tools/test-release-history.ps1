param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'read-release-history.ps1')
$work = if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'temp\test-results\release-history-test'}
New-Item -ItemType Directory -Force -Path $work | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$player = Join-Path $work 'version.txt'
$encoder = Join-Path $work 'version_encoder.txt'
$page = Join-Path $work 'index.html'
$results = New-Object 'Collections.Generic.List[string]'
function Check($Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $results.Add($Name)
}
[IO.File]::WriteAllText($player, @'
## 2.0.0 (2026-11-10)
- <script>alert(1)</script> & $1

## 1.2.0 (2026-11-09)
- Previous release.

## 1.0.0 (2026-11-08)
- First release.
'@, $utf8)
[IO.File]::WriteAllText($encoder, "## 3.0.0 (2026-11-10)`n- Encoder release.`n", $utf8)
& (Join-Path $PSScriptRoot 'create-release-page.ps1') -OutputPath $page -VersionFile $player -EncoderVersionFile $encoder -Ga4IdFile (Join-Path $work 'missing-ga4.txt')
$html = [IO.File]::ReadAllText($page)
$history = [regex]::Match($html, '(?s)<!-- NICOBE_HISTORY -->(.*?)<!-- /NICOBE_HISTORY -->').Groups[1].Value
Check ($html.Contains('<!-- NICOBE_VERSION -->v2.0.0<!-- /NICOBE_VERSION -->') -and $html.Contains('<!-- ENCODER_VERSION -->v3.0.0<!-- /ENCODER_VERSION -->')) 'Separate product versions are injected'
$markup = [xml]$history
$releases = @($markup.SelectNodes('//div[@class="history-entry"]'))
$source = @(Read-ReleaseHistory -Path $player)
Check ($releases.Count -eq $source.Count) 'All releases are rendered'
for ($i=0; $i -lt $source.Count; $i++) {
    Check ($releases[$i].SelectSingleNode('p/strong').InnerText -ceq ('v'+$source[$i].Version) -and $releases[$i].SelectSingleNode('p/time').InnerText -ceq $source[$i].Date) 'Version and date are rendered as a heading'
    $notes = @($releases[$i].SelectNodes('ul/li') | ForEach-Object { $_.InnerText })
    Check (($notes -join "`n") -ceq ($source[$i].Changes -join "`n")) 'Change text and order are preserved without Markdown markers'
}
Check (!$history.Contains('<pre') -and !$history.Contains('## ')) 'History is formatted rather than raw Markdown'
Check (!$history.Contains('<details') -and $history.Contains('tabindex="0"') -and $history.Contains('role="region"')) 'History is a keyboard-focusable scroll region without foldouts'
Check ($history.Contains('&lt;script&gt;alert(1)&lt;/script&gt; &amp; $1') -and !$history.Contains('<script>')) 'Release notes are escaped as plain text, including replacement metacharacters'
$encoderHistory = [regex]::Match($html, '(?s)<!-- ENCODER_HISTORY -->(.*?)<!-- /ENCODER_HISTORY -->').Groups[1].Value
$encoderMarkup = [xml]$encoderHistory
Check ($encoderMarkup.SelectSingleNode('//strong').InnerText -ceq 'v3.0.0' -and $encoderMarkup.SelectSingleNode('//li').InnerText -ceq 'Encoder release.' -and !$encoderHistory.Contains('Previous release.')) 'Encoder history remains independent'
foreach ($invalid in @(
    "## 1.0.0 (2026-11-10)`n- A`n## 2.0.0 (2026-11-09)`n- B",
    "## 2.0.0 (2026-11-09)`n- A`n## 1.0.0 (2026-11-10)`n- B",
    "## 1.0.0 (2026-11-10)`n- A`n## 1.0.0 (2026-11-09)`n- B",
    "## 1.0.0 (2026-02-30)`n- Invalid date",
    '## 1.0.0 (2026-11-10)',
    "`n## 1.0.0 (2026-11-10)`n- Initial blank line",
    "## 1.0.0 (2026-11-10)`nUnmarked change",
    "## 65535.0.0 (2026-11-10)`n- Version too large",
    ''
)) {
    [IO.File]::WriteAllText($player, $invalid, $utf8)
    $rejected = $false
    try { $null = @(Read-ReleaseHistory -Path $player) } catch { $rejected = $true }
    Check $rejected 'Invalid history is rejected'
}
[IO.File]::WriteAllText((Join-Path $work 'result.json'), (@{passed=$true;checks=$results.Count;results=$results.ToArray()} | ConvertTo-Json -Depth 4), $utf8)
Write-Output "Release history checks passed: $($results.Count)"
