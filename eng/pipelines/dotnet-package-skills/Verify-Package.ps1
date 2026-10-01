[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [Parameter(Mandatory)]
    [string] $ExpectedVersion,

    [Parameter(Mandatory)]
    [string] $BuildOutputPath,

    [switch] $RequireSigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedCommand {
    param([string] $Command, [string[]] $Arguments)

    $output = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "'$Command' failed with exit code ${LASTEXITCODE}:`n$($output -join "`n")"
    }
    return $output -join "`n"
}

function Read-ArchiveText {
    param([IO.Compression.ZipArchive] $Archive, [string] $Name)

    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) { throw "Package is missing $Name." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
$buildOutput = (Resolve-Path -LiteralPath $BuildOutputPath).Path
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    [xml] $nuspec = Read-ArchiveText $archive 'dotnet-package-skills.nuspec'
    if ($nuspec.package.metadata.id -cne 'dotnet-package-skills') {
        throw 'Package ID must be dotnet-package-skills.'
    }
    if ($nuspec.package.metadata.version -cne $ExpectedVersion) {
        throw "Package version '$($nuspec.package.metadata.version)' does not match '$ExpectedVersion'."
    }
    if ($nuspec.package.metadata.packageTypes.packageType.name -cne 'DotnetTool') {
        throw 'Package must declare the DotnetTool package type.'
    }
    if ($nuspec.package.metadata.license.type -cne 'expression' -or
        $nuspec.package.metadata.license.InnerText -cne 'MIT') {
        throw 'Package must retain its MIT license expression.'
    }
    if ([string]::IsNullOrWhiteSpace((Read-ArchiveText $archive 'README.md'))) {
        throw 'Package README must not be empty.'
    }
    if ($RequireSigned -and $null -eq $archive.GetEntry('.signature.p7s')) {
        throw 'Package is not signed; official artifacts must have a NuGet signature.'
    }

    foreach ($framework in @('net8.0', 'net10.0')) {
        [xml] $settings = Read-ArchiveText $archive "tools/$framework/any/DotnetToolSettings.xml"
        $command = $settings.DotNetCliTool.Commands.Command
        if ($command.Name -cne 'dotnet-package-skills' -or $command.EntryPoint -cne 'dotnet-package-skills.dll') {
            throw "Incorrect tool command settings for $framework."
        }

        $entry = $archive.GetEntry("tools/$framework/any/dotnet-package-skills.dll")
        if ($null -eq $entry) { throw "Package is missing the $framework assembly." }
        $assembly = Join-Path $buildOutput "$framework\dotnet-package-skills.dll"
        $stream = $entry.Open()
        try { $packageHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        finally { $stream.Dispose() }
        if ($packageHash -cne (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash) {
            throw "The $framework package payload does not match the built assembly."
        }
        if ($RequireSigned) {
            $signature = Get-AuthenticodeSignature -LiteralPath $assembly
            if ($signature.Status -ne 'Valid') {
                throw "The $framework assembly signature is not valid: $($signature.StatusMessage)"
            }
        }
    }
}
finally {
    $archive.Dispose()
}

if ($RequireSigned) {
    Write-Host (Invoke-CheckedCommand dotnet @('nuget', 'verify', $package, '--all'))
}

$temporary = [IO.Directory]::CreateTempSubdirectory('dotnet-package-skills-verify-').FullName
$originalEnvironment = @{}
foreach ($name in @(
    'NUGET_PACKAGES', 'DOTNET_CLI_HOME', 'DOTNET_ROLL_FORWARD',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO'
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

    $fixtureCache = Join-Path $temporary 'fixture-cache'
    $skill = Join-Path $fixtureCache 'contoso.widgets\2.3.0\skills\contoso.widgets-widget-usage'
    [IO.Directory]::CreateDirectory($skill) | Out-Null
    [IO.File]::WriteAllText((Join-Path $skill 'SKILL.md'), "---`nname: contoso.widgets-widget-usage`ndescription: Local package verification fixture.`n---`n")

    foreach ($framework in @('net8.0', 'net10.0')) {
        $toolPath = Join-Path $temporary "tools-$framework"
        Write-Host (Invoke-CheckedCommand dotnet @(
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
