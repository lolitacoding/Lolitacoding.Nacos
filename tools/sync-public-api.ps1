<#
.SYNOPSIS
    Synchronizes PublicAPI.Unshipped.txt with the assembly's actual public surface.

.DESCRIPTION
    Microsoft.CodeAnalysis.PublicApiAnalyzers reports each public symbol that is missing
    from PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt as diagnostic RS0016. That
    diagnostic message contains the exact line the analyzer expects in the baseline file,
    so this script compiles the library, harvests those lines and folds them into
    PublicAPI.Unshipped.txt.

    Symbols that were removed from the code are reported as RS0017 and are pruned from the
    unshipped file in the same pass.

    The build is forced to English output because the diagnostic text is parsed; a
    localized SDK would otherwise break extraction.

.PARAMETER Project
    Path to the core library project file. Defaults to the repository's core library.

.EXAMPLE
    pwsh ./tools/sync-public-api.ps1
#>
[CmdletBinding()]
param(
    [string] $Project = (Join-Path $PSScriptRoot '..\src\Lolitacoding.Nacos\Lolitacoding.Nacos.csproj'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$projectDir = Split-Path -Parent $Project
$unshipped = Join-Path $projectDir 'PublicAPI.Unshipped.txt'

Write-Host "Building $Project to harvest the public API surface..." -ForegroundColor Cyan

# English output keeps the diagnostic text machine-readable regardless of host locale.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'

$output = & dotnet build $Project -c $Configuration --nologo 2>&1 | Out-String

# RS0016: Symbol 'X' is not part of the declared API.
# RS0017: Symbol 'X' is part of the declared API, but is either not public or could not be found.
$missing = [System.Collections.Generic.HashSet[string]]::new()
$removed = [System.Collections.Generic.HashSet[string]]::new()

foreach ($match in [regex]::Matches($output, "error RS0016: Symbol '(.+?)' is not part of the declared (?:public )?API")) {
    [void]$missing.Add($match.Groups[1].Value)
}

foreach ($match in [regex]::Matches($output, "error RS0017: Symbol '(.+?)' is part of the declared (?:public )?API")) {
    [void]$removed.Add($match.Groups[1].Value)
}

Write-Host ("  missing: {0}  removed: {1}" -f $missing.Count, $removed.Count)

# Preserve the existing baseline, then apply removals and additions.
$existing = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $unshipped) {
    foreach ($line in Get-Content -LiteralPath $unshipped) {
        if ($line -eq '#nullable enable' -or [string]::IsNullOrWhiteSpace($line)) { continue }
        if ($removed.Contains($line)) { continue }
        $existing.Add($line)
    }
}

$final = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($line in $existing) { [void]$final.Add($line) }
foreach ($line in $missing) { [void]$final.Add($line) }

$content = @('#nullable enable') + @($final)
Set-Content -LiteralPath $unshipped -Value $content -Encoding utf8

Write-Host "Wrote $($final.Count) entries to $unshipped" -ForegroundColor Green

# Rebuild to confirm the baseline now matches exactly.
$verify = & dotnet build $Project -c $Configuration --nologo 2>&1 | Out-String
if ($verify -match 'error RS00(16|17)') {
    Write-Host 'Public API baseline still out of sync:' -ForegroundColor Red
    foreach ($line in ($verify -split "`n" | Where-Object { $_ -match 'error RS00(16|17)' })) {
        Write-Host "  $line"
    }
    exit 1
}

Write-Host 'Public API baseline is in sync.' -ForegroundColor Green
