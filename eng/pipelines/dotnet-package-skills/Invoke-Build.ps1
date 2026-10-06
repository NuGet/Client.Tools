[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('RestoreBuild', 'Test', 'Pack', 'Sign', 'Verify', 'Metadata')]
    [string] $Action,
    [switch] $CI,
    [switch] $Official,
    [switch] $ReleaseBuild,
    [string] $PackageArtifactPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Package-Validation.psm1') -Force
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
Set-Location -LiteralPath $repoRoot

$properties = @(
    "/p:DotnetPackageSkillsReleaseBuild=$($ReleaseBuild.IsPresent.ToString().ToLowerInvariant())",
    '/p:NETCORE_ENGINEERING_TELEMETRY=false'
)
$evaluationProperties = @("/p:ContinuousIntegrationBuild=$($CI.IsPresent.ToString().ToLowerInvariant())")
if ($Official) {
    & (Join-Path $PSScriptRoot 'Validate-OfficialSource.ps1') -ReleaseBuild:$ReleaseBuild
    $properties += @(
        "/p:OfficialBuildId=$($env:BUILD_BUILDNUMBER)",
        '/p:DotNetSignType=real',
        "/p:TeamName=$($env:DOTNET_PACKAGE_SKILLS_SIGNING_TEAM)"
    )
}
else { $properties += '/p:OfficialBuildId=' }
if (-not [string]::IsNullOrWhiteSpace($env:BUILD_SOURCEVERSION)) {
    if ($env:BUILD_SOURCEVERSION -cnotmatch '^[0-9a-f]{40}$') { throw 'Build.SourceVersion must be a full source commit SHA.' }
    $properties += "/p:SourceRevisionId=$($env:BUILD_SOURCEVERSION)"
}
$evaluationProperties += $properties

function Get-ArcadeDotnetPath {
    $configuration = 'Release'
    $prepareMachine = $false
    $restore = $false
    . (Join-Path $repoRoot 'eng\common\tools.ps1')
    $installation = InitializeDotNetCli -install:$false
    $dotnet = Join-Path $installation 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Missing Arcade dotnet installation $dotnet." }
    return $dotnet
}

function Get-BuildMetadata {
    $dotnet = Get-ArcadeDotnetPath
    $arguments = @(
        'msbuild', (Join-Path $repoRoot 'dotnet-package-skills\src\DotnetPackageSkills.csproj'),
        '-nologo', '-verbosity:quiet', '-target:GetDotnetPackageSkillsBuildMetadata',
        '-property:Configuration=Release', '-property:TargetFramework=net8.0',
        '-getProperty:Version,PackageVersion,DotnetPackageSkillsPackagePath,DotnetPackageSkillsBuildOutputPath,DotnetPackageSkillsStrongNameToolPath,ArtifactsTestResultsDir'
    ) + $evaluationProperties
    $metadata = (Invoke-CheckedCommand $dotnet $arguments | ConvertFrom-Json).Properties
    if ($metadata.Version -cne $metadata.PackageVersion -or [string]::IsNullOrWhiteSpace($metadata.PackageVersion)) {
        throw 'Arcade did not evaluate a consistent package and assembly version.'
    }
    $metadata | Add-Member -NotePropertyName DotnetPath -NotePropertyValue $dotnet
    return $metadata
}

function Invoke-ArcadeBuild {
    param([string[]] $Actions, [string[]] $AdditionalProperties = @())

    $arguments = @($Actions) + @('-configuration', 'Release') + $properties + $AdditionalProperties
    if ($CI) { $arguments += '-ci' }
    & (Join-Path $repoRoot 'eng\common\build.cmd') @arguments
    if ($LASTEXITCODE -ne 0) { throw "Arcade $Action failed with exit code $LASTEXITCODE." }
}

switch ($Action) {
    'RestoreBuild' { Invoke-ArcadeBuild @('-restore', '-build') }
    'Test' {
        $start = [datetime]::UtcNow
        Invoke-ArcadeBuild @('-test') @('/p:SkipTests=false', '/p:TestRunnerAdditionalArguments=')
        $metadata = Get-BuildMetadata
        & (Join-Path $PSScriptRoot 'Test-Results.ps1') -ResultsDirectory $metadata.ArtifactsTestResultsDir `
            -NotBeforeUtc $start -MinimumFunctionalTests 792
    }
    'Pack' {
        $metadata = Get-BuildMetadata
        $start = [datetime]::UtcNow
        Invoke-ArcadeBuild @('-pack') @('/p:NoBuild=true')
        $package = $metadata.DotnetPackageSkillsPackagePath
        if (-not (Test-Path -LiteralPath $package -PathType Leaf) -or
            (Get-Item -LiteralPath $package).Length -eq 0 -or
            (Get-Item -LiteralPath $package).LastWriteTimeUtc -lt $start) {
            throw "Arcade did not produce the exact current package $package."
        }
        $packages = @(Get-ChildItem -LiteralPath (Split-Path -Parent $package) -Filter 'dotnet-package-skills.*.nupkg')
        if ($packages.Count -ne 1 -or $packages[0].FullName -cne $package) {
            throw 'Unexpected dotnet-package-skills shipping package output; remove only the named stale tool packages before switching build kinds.'
        }
    }
    'Sign' { Invoke-ArcadeBuild @('-sign') }
    'Verify' {
        $metadata = Get-BuildMetadata
        $parameters = @{
            PackagePath = $metadata.DotnetPackageSkillsPackagePath
            ExpectedVersion = $metadata.PackageVersion
            BuildOutputPath = $metadata.DotnetPackageSkillsBuildOutputPath
            DotnetPath = $metadata.DotnetPath
            StrongNameToolPath = $metadata.DotnetPackageSkillsStrongNameToolPath
            RequireSigned = $Official.IsPresent
        }
        if (-not [string]::IsNullOrWhiteSpace($env:BUILD_SOURCEVERSION)) { $parameters.ExpectedCommit = $env:BUILD_SOURCEVERSION }
        & (Join-Path $PSScriptRoot 'Verify-Package.ps1') @parameters
        if ($PackageArtifactPath -ne '') {
            [IO.Directory]::CreateDirectory($PackageArtifactPath) | Out-Null
            Copy-Item -LiteralPath $metadata.DotnetPackageSkillsPackagePath -Destination $PackageArtifactPath
        }
        if ($CI) { Write-Host '##vso[task.setvariable variable=DotnetPackageSkillsPackageVerified]true' }
    }
    'Metadata' { Get-BuildMetadata | ConvertTo-Json }
}
