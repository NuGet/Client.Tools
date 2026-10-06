[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [datetime] $NotBeforeUtc = [datetime]::MinValue,
    [ValidateRange(0, [int]::MaxValue)][int] $MinimumFunctionalTests = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($framework in @('net8.0', 'net10.0')) {
    $path = Join-Path $ResultsDirectory "DotnetPackageSkills.Tests_${framework}_x64.trx"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required $framework test results: $path." }
    if ((Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $NotBeforeUtc.ToUniversalTime()) {
        throw "Stale $framework test results cannot validate the current run."
    }
    [xml] $results = Get-Content -LiteralPath $path -Raw
    $summary = $results.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']")
    if ($null -eq $summary) { throw "Missing $framework results summary." }
    $counters = $summary.SelectSingleNode("*[local-name()='Counters']")
    if ($null -eq $counters) { throw "Missing $framework results counters." }
    $counts = @{}
    foreach ($name in @('total', 'executed', 'passed', 'failed')) {
        $value = $counters.GetAttribute($name)
        if ($value -cnotmatch '^[0-9]+$') { throw "Malformed $framework results counter $name." }
        $counts[$name] = [long]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
    }
    foreach ($attribute in $counters.Attributes) {
        if ($attribute.Name -in @('error', 'timeout', 'aborted', 'inconclusive', 'notRunnable', 'notExecuted', 'disconnected', 'pending', 'inProgress') -and
            $attribute.Value -cne '0') {
            throw "Unsuccessful $framework results counter $($attribute.Name)."
        }
    }
    $tests = @($results.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    if ($summary.GetAttribute('outcome') -cne 'Completed' -or
        $counts.total -le 0 -or $counts.executed -ne $counts.total -or
        $counts.passed -ne $counts.total -or $counts.failed -ne 0 -or
        $tests.Count -ne $counts.total -or
        @($tests | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' }).Count -ne 0) {
        throw "Empty, failed, or incomplete $framework test results."
    }
    $functional = @($tests | Where-Object {
        $name = $_.GetAttribute('testName')
        $name.StartsWith('DotnetPackageSkills.Tests.', [StringComparison]::Ordinal) -and
            -not $name.StartsWith('DotnetPackageSkills.Tests.Pipeline', [StringComparison]::Ordinal)
    }).Count
    if ($functional -lt $MinimumFunctionalTests) {
        throw "The $framework results contain $functional functional tests; at least $MinimumFunctionalTests are required."
    }
    Write-Host "$framework results: $($counts.passed) passed, $functional functional tests."
}
