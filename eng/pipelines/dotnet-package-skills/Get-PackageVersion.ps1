[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseVersion,

    [Parameter(Mandatory)]
    [AllowEmptyString()]
    [string] $BuildId,

    [Parameter(Mandatory)]
    [string] $BuildReason,

    [Parameter(Mandatory)]
    [string] $SourceBranch,

    [string] $PullRequestNumber,

    [switch] $Official,

    [switch] $ReleaseBuild
)

$ErrorActionPreference = 'Stop'

if ($BaseVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'BaseVersion must be a three-part semantic version without leading zeros or a suffix.'
}
if ($BuildId -cnotmatch '^[1-9][0-9]*$') {
    throw 'BuildId must be a positive integer supplied by Azure Pipelines.'
}
if ($Official -and $BuildReason -eq 'PullRequest') {
    throw 'Pull requests cannot use the official signing path.'
}
if ($ReleaseBuild) {
    if (-not $Official -or $BuildReason -ne 'Manual' -or $SourceBranch -ne 'refs/heads/main') {
        throw 'A stable release requires a manual official build from refs/heads/main.'
    }
    return $BaseVersion
}
if ($Official) {
    return "$BaseVersion-preview.$BuildId"
}
if ($BuildReason -eq 'PullRequest') {
    if ($PullRequestNumber -cnotmatch '^[1-9][0-9]*$') {
        throw 'PullRequestNumber must be a positive integer supplied by the GitHub PR build.'
    }
    return "$BaseVersion-pr.$PullRequestNumber.$BuildId"
}

return "$BaseVersion-ci.$BuildId"
