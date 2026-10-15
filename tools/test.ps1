<#
.SYNOPSIS
    Builds and runs the unit tests.

.DESCRIPTION
    xUnit v3 test projects build into standalone executables that run on
    Microsoft.Testing.Platform. This script builds them and then invokes each
    executable directly.

    Why not `dotnet test`: the .NET 10 SDK drives Microsoft.Testing.Platform test
    hosts over a named pipe, and a sandboxed environment (such as the DSH harness)
    denies child processes access to named pipes. Invoking the test executable
    directly avoids that IPC hop entirely and produces identical results, so the
    same script works both locally and on CI.

.PARAMETER Filter
    A substring or expression selecting which tests to run, for example
    'FullyQualifiedName~PayloadCodec'. Passed through to the test host.

.PARAMETER Configuration
    The build configuration. Defaults to Release.

.PARAMETER NoBuild
    Skip the build step and run the executables produced by a previous build.

.EXAMPLE
    pwsh ./tools/test.ps1
    pwsh ./tools/test.ps1 -Filter 'FullyQualifiedName~ConfigServiceTests'
#>
[CmdletBinding()]
param(
    [string] $Filter,
    [string] $Configuration = 'Release',
    [switch] $NoBuild,
    [string] $Project = 'tests/Lolitacoding.Nacos.Tests/Lolitacoding.Nacos.Tests.csproj'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot $Project

if (-not $NoBuild) {
    Write-Host "Building $Project ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $projectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$projectDir = Split-Path -Parent $projectPath
$binDir = Join-Path $projectDir "bin/$Configuration/net10.0"
$exe = Join-Path $binDir ((Split-Path -Leaf $projectDir) + '.exe')

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Error "Test executable not found at '$exe'. Run without -NoBuild first."
    exit 1
}

# The test host accepts the Microsoft.Testing.Platform command line directly.
$testArgs = @()
if ($Filter) { $testArgs += @('--filter', $Filter) }

Write-Host "Running $([System.IO.Path]::GetFileName($exe))..." -ForegroundColor Cyan
& $exe @testArgs
$testExit = $LASTEXITCODE

if ($testExit -ne 0) {
    Write-Host "Tests failed (exit code $testExit)." -ForegroundColor Red
}
else {
    Write-Host 'All tests passed.' -ForegroundColor Green
}

exit $testExit
