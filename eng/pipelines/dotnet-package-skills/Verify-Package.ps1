[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [Parameter(Mandatory)]
    [string] $ExpectedVersion,

    [Parameter(Mandatory)]
    [string] $BuildOutputPath,

    [Parameter(Mandatory)]
    [string] $DotnetPath,

    [Parameter(Mandatory)]
    [string] $StrongNameToolPath,

    [string] $ExpectedCommit,

    [switch] $RequireSigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Package-Validation.psm1') -Force
Test-DotnetPackageSkillsPackage @PSBoundParameters
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
$runtimes = Invoke-CheckedCommand $DotnetPath @('--list-runtimes')
foreach ($major in @('8', '10')) {
    if ($runtimes -notmatch "(?m)^Microsoft\.NETCore\.App $major\.") {
        throw "The Arcade dotnet installation is missing the .NET $major runtime."
    }
}

$temporary = [IO.Directory]::CreateTempSubdirectory('dotnet-package-skills-verify-').FullName
$originalEnvironment = @{}
foreach ($name in @(
    'NUGET_PACKAGES', 'DOTNET_CLI_HOME', 'DOTNET_ROLL_FORWARD',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_ROOT', 'DOTNET_ROOT_X64',
    'DOTNET_MULTILEVEL_LOOKUP'
)) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

try {
    $feed = [IO.Directory]::CreateDirectory((Join-Path $temporary 'feed')).FullName
    Copy-Item -LiteralPath $package -Destination $feed
    $config = Join-Path $temporary 'NuGet.config'
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    [IO.File]::WriteAllText($config, @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="local" value="$escapedFeed" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="local"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@)
    $env:NUGET_PACKAGES = Join-Path $temporary 'nuget-cache'
    $env:DOTNET_CLI_HOME = Join-Path $temporary 'cli-home'
    $env:DOTNET_ROLL_FORWARD = 'LatestPatch'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'

    $fixtureCache = Join-Path $temporary 'fixture-cache'
    $skill = Join-Path $fixtureCache 'contoso.widgets\2.3.0\skills\contoso.widgets-widget-usage'
    [IO.Directory]::CreateDirectory($skill) | Out-Null
    [IO.File]::WriteAllText((Join-Path $skill 'SKILL.md'), "---`nname: contoso.widgets-widget-usage`ndescription: Local package verification fixture.`n---`n")

    foreach ($framework in @('net8.0', 'net10.0')) {
        $toolPath = Join-Path $temporary "tools-$framework"
        Write-Host (Invoke-CheckedCommand $DotnetPath @(
            'tool', 'install', 'dotnet-package-skills', '--tool-path', $toolPath,
            '--version', $ExpectedVersion, '--framework', $framework,
            '--configfile', $config, '--no-http-cache', '--verbosity', 'quiet'
        ))

        $tool = Join-Path $toolPath 'dotnet-package-skills.exe'
        $version = (Invoke-CheckedCommand $tool @('--version')).Trim()
        if (($version -split '\+', 2)[0] -cne $ExpectedVersion) {
            throw "Installed $framework tool version '$version' does not match '$ExpectedVersion'."
        }
        $help = Invoke-CheckedCommand $tool @('--help')
        if ($help -notmatch 'install' -or $help -notmatch 'uninstall') {
            throw "Installed $framework tool does not expose its expected commands."
        }

        $destination = Join-Path $temporary "skills-$framework"
        $handwritten = Join-Path $destination 'handwritten'
        [IO.Directory]::CreateDirectory($handwritten) | Out-Null
        [IO.File]::WriteAllText((Join-Path $handwritten 'SKILL.md'), 'Preserve this skill.')
        $packageArguments = @('--package', 'Contoso.Widgets@2.3.0', '--global-packages', $fixtureCache, '--destination', $destination)

        $listing = Invoke-CheckedCommand $tool (@('list') + $packageArguments)
        if ($listing -notmatch 'contoso.widgets-widget-usage') {
            throw "Installed $framework tool did not discover the local fixture."
        }
        Write-Host (Invoke-CheckedCommand $tool (@('install') + $packageArguments))
        $installedSkill = Join-Path $destination 'contoso.widgets-widget-usage\SKILL.md'
        if (-not (Test-Path -LiteralPath $installedSkill)) {
            throw "Installed $framework tool did not copy the fixture skill."
        }
        $manifestPath = Join-Path $destination '.dotnet-package-skills.json'
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        if ($manifest.packages.'contoso.widgets'.version -cne '2.3.0') {
            throw "Installed $framework tool did not record the fixture package version."
        }
        Write-Host (Invoke-CheckedCommand $tool @('uninstall', '--package', 'Contoso.Widgets', '--destination', $destination))
        if (Test-Path -LiteralPath $installedSkill) {
            throw "Installed $framework tool did not remove the fixture skill."
        }
        if ([IO.File]::ReadAllText((Join-Path $handwritten 'SKILL.md')) -cne 'Preserve this skill.') {
            throw "Installed $framework tool changed a handwritten skill."
        }
    }
}
finally {
    foreach ($name in $originalEnvironment.Keys) {
        $value = $originalEnvironment[$name]
        if ($null -eq $value) { $value = [NullString]::Value }
        [Environment]::SetEnvironmentVariable($name, $value)
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}

Write-Host "Verified dotnet-package-skills $ExpectedVersion on .NET 8 and .NET 10."
