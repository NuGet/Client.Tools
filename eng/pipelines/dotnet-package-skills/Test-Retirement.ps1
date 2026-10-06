[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$fixture = [IO.Directory]::CreateTempSubdirectory('client-tools-retirement-fixture-').FullName
$originalLocation = Get-Location

try {
    foreach ($file in @('global.json', 'NuGet.Config', 'Directory.Build.props', 'Directory.Build.targets', 'build.cmd', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination $fixture
    }
    Copy-Item -LiteralPath (Join-Path $source 'eng') -Destination $fixture -Recurse
    Copy-Item -LiteralPath (Join-Path $source 'dotnet-package-skills') -Destination $fixture -Recurse
    $future = Join-Path $fixture 'eng\FutureTool.proj'
    [IO.File]::WriteAllText($future, @'
<Project Sdk="Microsoft.Build.NoTargets">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsShipping>false</IsShipping>
  </PropertyGroup>
  <Target Name="ValidateFutureToolFoundation" BeforeTargets="Build">
    <Error Condition="'$(ClientToolsArcadePropsImported)' != 'true' or '$(ClientToolsArcadeTargetsImported)' != 'true'"
           Text="The future tool lost the shared Arcade imports." />
    <Message Importance="high" Text="Future tool foundation validation succeeded." />
  </Target>
</Project>
'@)
    $futureSigning = Join-Path $fixture 'eng\pipelines\future-tool'
    [IO.Directory]::CreateDirectory($futureSigning) | Out-Null
    [IO.File]::WriteAllText((Join-Path $futureSigning 'Signing.props'), '<Project />')

    [xml] $build = Get-Content -LiteralPath (Join-Path $fixture 'eng\Build.props') -Raw
    $entry = $build.CreateElement('ProjectToBuild')
    $entry.SetAttribute('Include', '$(RepoRoot)eng\FutureTool.proj')
    $null = $build.Project.ItemGroup.AppendChild($entry)
    $build.Save((Join-Path $fixture 'eng\Build.props'))
    [xml] $signing = Get-Content -LiteralPath (Join-Path $fixture 'eng\Signing.props') -Raw
    $entry = $signing.CreateElement('Import')
    $entry.SetAttribute('Project', 'pipelines\future-tool\Signing.props')
    $null = $signing.Project.AppendChild($entry)
    $signing.Save((Join-Path $fixture 'eng\Signing.props'))

    foreach ($file in @('Build.props', 'Versions.props', 'Signing.props')) {
        $path = Join-Path $fixture "eng\$file"
        [xml] $document = Get-Content -LiteralPath $path -Raw
        foreach ($element in @($document.SelectNodes('//*[@Include or @Project]'))) {
            if ($element.GetAttribute('Include').Contains('dotnet-package-skills') -or
                $element.GetAttribute('Project').Contains('dotnet-package-skills')) {
                $null = $element.ParentNode.RemoveChild($element)
            }
        }
        $document.Save($path)
        if ($document.OuterXml.Contains('dotnet-package-skills')) { throw "Retirement left a tool reference in $file." }
    }
    [xml] $versions = Get-Content -LiteralPath (Join-Path $fixture 'eng\Versions.props') -Raw
    if ($versions.Project.PropertyGroup.VersionPrefix -cne '0.1.0' -or
        $versions.Project.PropertyGroup.PreReleaseVersionLabel -cne 'beta') {
        throw 'Retirement changed the shared base-version properties.'
    }
    [xml] $build = Get-Content -LiteralPath (Join-Path $fixture 'eng\Build.props') -Raw
    $projects = @($build.Project.ItemGroup.ProjectToBuild | ForEach-Object { $_.GetAttribute('Include') })
    if ($projects -notcontains '$(RepoRoot)eng\Infrastructure.proj' -or
        $projects -notcontains '$(RepoRoot)eng\FutureTool.proj') {
        throw 'Retirement removed infrastructure or another tool from the root graph.'
    }
    if (-not ([IO.File]::ReadAllText((Join-Path $fixture 'eng\Signing.props'))).Contains('future-tool\Signing.props')) {
        throw 'Retirement removed another signing module.'
    }

    $publicPath = Join-Path $fixture 'eng\pipelines\pr.yml'
    $public = [IO.File]::ReadAllText($publicPath).Replace("`r`n", "`n")
    $public = $public.Replace('- template: /eng/pipelines/dotnet-package-skills/stage.yml', @'
- stage: ArcadeInfrastructure
  jobs:
  - template: /eng/common/templates/jobs/jobs.yml
    parameters:
      runAsPublic: true
      enableMicrobuild: false
      enableTelemetry: false
      enablePublishBuildAssets: false
      jobs:
      - job: Infrastructure
        steps:
        - script: eng\common\build.cmd -restore -build -configuration Release -ci
'@)
    [IO.File]::WriteAllText($publicPath, $public)
    $officialPath = Join-Path $fixture 'eng\pipelines\official.yml'
    $official = [IO.File]::ReadAllText($officialPath).Replace("`r`n", "`n")
    $removedParameters = @'
parameters:
- name: DotnetPackageSkillsReleaseBuild
  type: boolean
  default: false

'@
    $official = $official.Replace($removedParameters.Replace("`r`n", "`n"), '')
    $removedStage = @'
    - template: /eng/pipelines/dotnet-package-skills/stage.yml@self
      parameters:
        isOfficialBuild: true
        releaseBuild: ${{ parameters.DotnetPackageSkillsReleaseBuild }}
'@
    $infrastructureStage = @'
    - stage: ArcadeInfrastructure
      jobs:
      - template: /eng/common/templates-official/jobs/jobs.yml@self
        parameters:
          enableMicrobuild: false
          enableTelemetry: false
          enablePublishBuildAssets: false
          jobs:
          - job: Infrastructure
            steps:
            - script: eng\common\build.cmd -restore -build -configuration Release -ci
'@
    $official = $official.Replace($removedStage.Replace("`r`n", "`n"), $infrastructureStage.Replace("`r`n", "`n"))
    [IO.File]::WriteAllText($officialPath, $official)
    foreach ($path in @($publicPath, $officialPath)) {
        if ([IO.File]::ReadAllText($path) -match 'dotnet-package-skills|DotnetPackageSkillsReleaseBuild') {
            throw "Retirement left a tool reference in $path."
        }
    }
    if ($official -notmatch 'v1/1ES\.Official\.PipelineTemplate\.yml@1esPipelines') {
        throw 'Retirement removed 1ES governance.'
    }

    Remove-Item -LiteralPath (Join-Path $fixture 'dotnet-package-skills') -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $fixture 'eng\pipelines\dotnet-package-skills') -Recurse -Force
    foreach ($file in @('global.json', 'NuGet.Config', 'Directory.Build.props', 'Directory.Build.targets', 'LICENSE')) {
        if ((Get-FileHash -LiteralPath (Join-Path $source $file)).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $fixture $file)).Hash) {
            throw "Retirement changed the shared foundation file $file."
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $source 'eng\common') -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $fixture $relative)).Hash) {
            throw "Retirement changed vendored Arcade $relative."
        }
    }
    Set-Location -LiteralPath $fixture
    & (Join-Path $fixture 'eng\common\build.cmd') -restore -build -configuration Release -ci /p:NETCORE_ENGINEERING_TELEMETRY=false
    if ($LASTEXITCODE -ne 0) { throw 'The retired fixture could not restore and build the shared Arcade graph.' }
    Write-Host 'Isolated retirement validation succeeded; shared infrastructure and future tool references are intact.'
}
finally {
    Set-Location -LiteralPath $originalLocation
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
