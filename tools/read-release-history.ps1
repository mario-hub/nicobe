function Read-ReleaseHistory {
    param([Parameter(Mandatory=$true)][string]$Path)
    $entries = New-Object 'Collections.Generic.List[object]'
    $current = $null
    $seen = @{}
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $lineNumber++
        if ($lineNumber -eq 1 -and !$line.StartsWith('## ')) { throw "The first line must be a release heading: $Path" }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^## ((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)) \(([0-9]{4}-[0-9]{2}-[0-9]{2})\)$') {
            $version = $Matches[1]; $dateText = $Matches[2]
            $date = [datetime]::MinValue
            if (![datetime]::TryParseExact($dateText, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
                throw "Invalid release date at ${Path}:${lineNumber}."
            }
            $number = [version]$version
            if ($number.Major -gt 65534 -or $number.Minor -gt 65534 -or $number.Build -gt 65534) { throw "Version components must be 0-65534: $version" }
            if ($seen.ContainsKey($version)) { throw "Duplicate release version in ${Path}: $version" }
            if ($current -and ($number -ge [version]$current.Version -or $date -gt $current.DateValue)) {
                throw "Release history must be newest first in ${Path}: $version"
            }
            $seen[$version] = $true
            $current = [pscustomobject]@{Version=$version; Date=$dateText; DateValue=$date; Changes=(New-Object 'Collections.Generic.List[string]')}
            $entries.Add($current)
        } elseif ($current -and $line -match '^- (\S.*)$') {
            $current.Changes.Add($Matches[1].TrimEnd())
        } else {
            throw "Invalid history line at ${Path}:${lineNumber}. Use '## major.minor.patch (yyyy-MM-dd)' or '- change'."
        }
    }
    if ($entries.Count -eq 0) { throw "Release history is empty: $Path" }
    foreach ($entry in $entries) {
        if ($entry.Changes.Count -eq 0) { throw "No changes listed for $($entry.Version) in $Path" }
    }
    return $entries.ToArray()
}
