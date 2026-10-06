[CmdletBinding()]
param([switch] $ReleaseBuild)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:BUILD_REPOSITORY_PROVIDER -cne 'TfsGit' -or
    $env:BUILD_REPOSITORY_NAME -cne 'NuGet-Client.Tools' -or
    $env:SYSTEM_TEAMPROJECT -cne 'internal' -or
    $env:SYSTEM_COLLECTIONURI -cne 'https://dev.azure.com/dnceng/' -or
    $env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or
    [string]::IsNullOrWhiteSpace($env:BUILD_REASON) -or $env:BUILD_REASON -eq 'PullRequest') {
    throw 'Official signing requires main from the trusted dnceng/internal/NuGet-Client.Tools repository, with non-PR source metadata.'
}
if ($ReleaseBuild -and $env:BUILD_REASON -cne 'Manual') {
    throw 'DotnetPackageSkillsReleaseBuild requires a manual official build on trusted main.'
}
$team = $env:DOTNET_PACKAGE_SKILLS_SIGNING_TEAM
if ([string]::IsNullOrWhiteSpace($team) -or $team -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$') {
    throw 'Configure the owner-approved DotnetPackageSkillsSigningTeamName on the official definition; a missing or unresolved signing team is not allowed.'
}
Write-Host 'Trusted source and configured signing team validated. Signing resource authorization remains an owner prerequisite.'
