<#
.SYNOPSIS
Aggregates VSTest TRX files and creates a Markdown CI report.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsDirectory,

    [Parameter(Mandatory)]
    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string] $BuildOutcome,

    [Parameter(Mandatory)]
    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string] $TestOutcome,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-OutcomeDisplayName
{
    param(
        [Parameter(Mandatory)]
        [string] $Outcome
    )

    switch ($Outcome)
    {
        "success"
        {
            return "$([char] 0x2705) Passed"
        }

        "failure"
        {
            return "$([char] 0x274C) Failed"
        }

        "cancelled"
        {
            return "$([char] 0x26A0) Cancelled"
        }

        "skipped"
        {
            return "$([char] 0x23ED) Skipped"
        }

        default
        {
            return "$([char] 0x2754) Unknown"
        }
    }
}

function Get-TrxCounter
{
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlElement] $Counters,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $value = $Counters.GetAttribute($Name)

    if ([string]::IsNullOrWhiteSpace($value))
    {
        return 0
    }

    return [int] $value
}

$trxFiles = @(
    Get-ChildItem `
        -Path $ResultsDirectory `
        -Filter "*.trx" `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue
)

if ($TestOutcome -eq "success" -and $trxFiles.Count -eq 0)
{
    throw "The test step succeeded, but no TRX result files were found."
}

$total = 0
$passed = 0
$failed = 0
$skipped = 0

foreach ($trxFile in $trxFiles)
{
    [xml] $trx = [System.IO.File]::ReadAllText($trxFile.FullName)

    $counters = $trx.SelectSingleNode(
        "/*[local-name()='TestRun']" +
        "/*[local-name()='ResultSummary']" +
        "/*[local-name()='Counters']")

    if ($null -eq $counters)
    {
        throw "TRX counters were not found in '$($trxFile.FullName)'."
    }

    $total += Get-TrxCounter -Counters $counters -Name "total"
    $passed += Get-TrxCounter -Counters $counters -Name "passed"
    $failed += Get-TrxCounter -Counters $counters -Name "failed"
    $skipped += Get-TrxCounter -Counters $counters -Name "notExecuted"
}

$shortCommitSha = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SHA))
{
    "local"
}
else
{
    $length = [Math]::Min(7, $env:GITHUB_SHA.Length)
    $env:GITHUB_SHA.Substring(0, $length)
}

$workflowReference = if (
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_SERVER_URL) -and
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY) -and
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID))
{
    $workflowUrl =
        "$($env:GITHUB_SERVER_URL)/" +
        "$($env:GITHUB_REPOSITORY)/actions/runs/" +
        "$($env:GITHUB_RUN_ID)"

    "[Open workflow]($workflowUrl)"
}
else
{
    "Local execution"
}

$buildResult = Get-OutcomeDisplayName -Outcome $BuildOutcome
$testResult = Get-OutcomeDisplayName -Outcome $TestOutcome

$report = @"
| Check | Result |
| --- | --- |
| Build | $buildResult |
| Tests | $testResult |
| Test projects | $($trxFiles.Count) |
| Total | $total |
| Passed | $passed |
| Failed | $failed |
| Skipped | $skipped |
| Commit | ``$shortCommitSha`` |
| Workflow | $workflowReference |
"@

$outputDirectory = Split-Path -Path $OutputPath -Parent

if (-not [string]::IsNullOrWhiteSpace($outputDirectory))
{
    New-Item `
        -Path $outputDirectory `
        -ItemType Directory `
        -Force |
        Out-Null
}

Set-Content `
    -Path $OutputPath `
    -Value $report `
    -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY))
{
    Add-Content `
        -Path $env:GITHUB_STEP_SUMMARY `
        -Value "## Automated CI verification`n`n$report`n" `
        -Encoding utf8
}

Write-Host $report
