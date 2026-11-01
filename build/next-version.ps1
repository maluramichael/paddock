<#
.SYNOPSIS
  Ermittelt die nächste Version (major.minor.patch) aus den Commits seit dem letzten Tag v*.

.DESCRIPTION
  auto  : feat!: / BREAKING CHANGE → major, feat: → minor, alles andere → patch
  patch|minor|major : erzwingt den Sprung (manueller Workflow-Start)
  Ohne bisherigen Tag ist die erste Version 1.0.0.
  "[skip release]" im letzten Commit oder keine neuen Commits → skip=true.

  Schreibt version, previous, bump, skip nach $GITHUB_OUTPUT (falls gesetzt) und gibt sie aus.
#>
param([ValidateSet('auto', 'patch', 'minor', 'major')][string]$Bump = 'auto')
$ErrorActionPreference = 'Stop'

$last = git describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
$range = if ($last) { "$last..HEAD" } else { 'HEAD' }
$subjects = @(git log $range --format='%s')
$bodies = (git log $range --format='%B') -join "`n"
$head = git log -1 --format='%B'

$skip = $false
if ($subjects.Count -eq 0 -and $Bump -eq 'auto') { $skip = $true }
if ($head -match '\[skip release\]' -and $Bump -eq 'auto') { $skip = $true }

$reason = $Bump
if ($Bump -eq 'auto') {
    if ($bodies -match '(?m)^BREAKING[ -]CHANGE:' -or ($subjects -match '^[a-z]+(\([^)]*\))?!:').Count -gt 0) { $Bump = 'major' }
    elseif (($subjects -match '^feat(\([^)]*\))?:').Count -gt 0) { $Bump = 'minor' }
    else { $Bump = 'patch' }
    $reason = "auto: $Bump"
}

if (-not $last) {
    $next = '1.0.0'
    $previous = ''
}
else {
    $v = [version]($last.TrimStart('v'))
    $next = switch ($Bump) {
        'major' { "$($v.Major + 1).0.0" }
        'minor' { "$($v.Major).$($v.Minor + 1).0" }
        default { "$($v.Major).$($v.Minor).$($v.Build + 1)" }
    }
    $previous = $last
}

$out = [ordered]@{ version = $next; previous = $previous; bump = $Bump; skip = $skip.ToString().ToLowerInvariant() }
if ($env:GITHUB_OUTPUT) { $out.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Add-Content $env:GITHUB_OUTPUT }
Write-Host "Letzter Tag: $(if ($last) { $last } else { '(keiner)' }) | Commits: $($subjects.Count) | $reason -> $next | skip=$skip"
