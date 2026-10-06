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
    if ($entry.Length -eq 0) { throw "Package contains empty $Name." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Get-MetadataElement {
    param([xml] $Nuspec, [string] $Name, [switch] $Optional)

    $nodes = @($Nuspec.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$Name']"))
    if ($Optional -and $nodes.Count -eq 0) { return $null }
    if ($nodes.Count -ne 1) { throw "Package metadata must contain one $Name element." }
    return $nodes[0]
}

function Test-DotnetPackageSkillsPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $PackagePath,
        [Parameter(Mandatory)][string] $ExpectedVersion,
        [Parameter(Mandatory)][string] $BuildOutputPath,
        [Parameter(Mandatory)][string] $DotnetPath,
        [Parameter(Mandatory)][string] $StrongNameToolPath,
        [string] $ExpectedCommit,
        [switch] $RequireSigned
    )

    $package = (Resolve-Path -LiteralPath $PackagePath).Path
    $buildOutput = (Resolve-Path -LiteralPath $BuildOutputPath).Path
    if (-not (Test-Path -LiteralPath $StrongNameToolPath -PathType Leaf)) {
        throw "Missing Arcade strong-name validation tool $StrongNameToolPath."
    }
    [xml] $policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Payload.props') -Raw
    $expectedPayload = [Collections.Generic.Dictionary[string, Xml.XmlElement]]::new([StringComparer]::Ordinal)
    foreach ($item in $policy.Project.ItemGroup.FileSignInfo) {
        $owner = $item.GetAttribute('Owner')
        $certificate = $item.GetAttribute('CertificateName')
        if (($owner -eq 'ThirdParty' -and $certificate -cne '3PartySHA2') -or
            ($owner -in @('Microsoft', 'Tool') -and $certificate -cne 'MicrosoftDotNet500') -or
            $owner -notin @('Microsoft', 'Tool', 'ThirdParty')) {
            throw "Incorrect signing policy for $($item.GetAttribute('Include'))."
        }
        $cultures = @('')
        if ($item.GetAttribute('Cultures') -ne '') { $cultures = $item.GetAttribute('Cultures').Split(';') }
        foreach ($framework in $item.GetAttribute('Frameworks').Split(';')) {
            foreach ($culture in $cultures) {
                $relative = $item.GetAttribute('Include')
                if ($culture -ne '') { $relative = "$culture/$relative" }
                $expectedPayload.Add("tools/$framework/any/$relative", $item)
            }
        }
    }
    if ($expectedPayload.Count -eq 0) { throw 'The signing payload registry is empty.' }

    $scratch = [IO.Directory]::CreateTempSubdirectory('dotnet-package-skills-payload-').FullName
    $archive = $null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($package)
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $payload = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or
                $name.Contains('\') -or $name.Contains(':') -or
                @($name.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -ne 0) {
                throw "Invalid package archive path $name."
            }
            if (-not $seen.Add($name)) { throw "Package contains duplicate archive entry $name." }
            if ($name.StartsWith('tools/', [StringComparison]::Ordinal) -and
                $name -cnotmatch '^tools/(net8\.0|net10\.0)/any/') {
                throw "Nonportable or unexpected tool payload $name."
            }
            $stream = $entry.Open()
            try { $isPE = $stream.ReadByte() -eq 0x4d -and $stream.ReadByte() -eq 0x5a }
            finally { $stream.Dispose() }
            if ($isPE -or $name -match '\.(dll|exe)$') {
                if (-not $expectedPayload.ContainsKey($name)) { throw "Unmapped package payload $name." }
                if ($entry.Length -eq 0) { throw "Package contains empty payload $name." }
                $payload.Add($name, $entry)
            }
        }
        foreach ($name in $expectedPayload.Keys) {
            if (-not $payload.ContainsKey($name)) { throw "Package is missing declared payload $name." }
        }

        [xml] $nuspec = Read-ArchiveText $archive 'dotnet-package-skills.nuspec'
        if ((Get-MetadataElement $nuspec 'id').InnerText -cne 'dotnet-package-skills') {
            throw 'Package ID must be dotnet-package-skills.'
        }
        $version = (Get-MetadataElement $nuspec 'version').InnerText
        if ($version -cne $ExpectedVersion) { throw "Package version '$version' does not match '$ExpectedVersion'." }
        if ((Get-MetadataElement $nuspec 'authors').InnerText -cne 'dotnet-package-skills contributors') {
            throw 'Package authors must remain dotnet-package-skills contributors.'
        }
        $types = @((Get-MetadataElement $nuspec 'packageTypes').ChildNodes | Where-Object LocalName -eq 'packageType')
        if ($types.Count -ne 1 -or $types[0].GetAttribute('name') -cne 'DotnetTool') {
            throw 'Package must declare only the DotnetTool package type.'
        }
        $license = Get-MetadataElement $nuspec 'license'
        if ($license.GetAttribute('type') -cne 'expression' -or $license.InnerText -cne 'MIT') {
            throw 'Package must retain its MIT license expression.'
        }
        $acceptance = Get-MetadataElement $nuspec 'requireLicenseAcceptance' -Optional
        if ($null -ne $acceptance -and $acceptance.InnerText -cne 'false') {
            throw 'Package license acceptance must remain false.'
        }
        $origin = Get-MetadataElement $nuspec 'repository'
        if ($origin.GetAttribute('type') -cne 'git' -or
            $origin.GetAttribute('url') -cne 'https://github.com/NuGet/Client.Tools' -or
            $origin.GetAttribute('commit') -cnotmatch '^[0-9a-f]{40}$' -or
            ($ExpectedCommit -ne '' -and $origin.GetAttribute('commit') -cne $ExpectedCommit)) {
            throw 'Package origin must identify the expected NuGet/Client.Tools source commit.'
        }
        if ((Get-MetadataElement $nuspec 'readme').InnerText -cne 'README.md' -or
            [string]::IsNullOrWhiteSpace((Read-ArchiveText $archive 'README.md'))) {
            throw 'Package README must not be empty.'
        }
        $signature = $archive.GetEntry('.signature.p7s')
        if ($RequireSigned -and $null -eq $signature) {
            throw 'Package is not signed; official artifacts must have a NuGet signature.'
        }
        if (-not $RequireSigned -and $null -ne $signature) { throw 'Public and local packages must be unsigned.' }
        if ($RequireSigned) { Write-Host (Invoke-CheckedCommand $DotnetPath @('nuget', 'verify', $package, '--all')) }

        foreach ($framework in @('net8.0', 'net10.0')) {
            [xml] $settings = Read-ArchiveText $archive "tools/$framework/any/DotnetToolSettings.xml"
            $commands = @($settings.DotNetCliTool.Commands.Command)
            if ($commands.Count -ne 1 -or $commands[0].Name -cne 'dotnet-package-skills' -or
                $commands[0].EntryPoint -cne 'dotnet-package-skills.dll' -or $commands[0].Runner -cne 'dotnet') {
                throw "Incorrect tool command settings for $framework."
            }
            $runtime = Read-ArchiveText $archive "tools/$framework/any/dotnet-package-skills.runtimeconfig.json" | ConvertFrom-Json
            if ($runtime.runtimeOptions.tfm -cne $framework -or
                $runtime.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App') {
                throw "Incorrect runtime configuration for $framework."
            }
            $null = Read-ArchiveText $archive "tools/$framework/any/dotnet-package-skills.deps.json" | ConvertFrom-Json
        }

        foreach ($name in $payload.Keys) {
            $item = $expectedPayload[$name]
            $parts = $name.Split('/')
            $relative = $parts[3..($parts.Length - 1)] -join [IO.Path]::DirectorySeparatorChar
            $original = Join-Path (Join-Path $buildOutput $parts[1]) $relative
            if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { throw "Missing built payload $original." }
            $extracted = Join-Path $scratch ($name.Replace('/', [IO.Path]::DirectorySeparatorChar))
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($extracted)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($payload[$name], $extracted)
            $packageHash = (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash
            $originalHash = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
            if ($item.GetAttribute('Owner') -eq 'Tool' -and $packageHash -cne $originalHash) {
                throw "The $($parts[1]) package payload does not match the tested built assembly."
            }
            $identity = [Reflection.AssemblyName]::GetAssemblyName($extracted)
            $originalIdentity = [Reflection.AssemblyName]::GetAssemblyName($original)
            $token = [Convert]::ToHexString($identity.GetPublicKeyToken()).ToLowerInvariant()
            if ($token -cne $item.GetAttribute('PublicKeyToken') -or
                $identity.FullName -cne $originalIdentity.FullName -or
                ($identity.GetPublicKey() -join ',') -cne ($originalIdentity.GetPublicKey() -join ',')) {
                throw "Strong-name assembly identity changed for payload $name."
            }
            if ($token -ne '') {
                $null = Invoke-CheckedCommand $StrongNameToolPath @('-q', '-vf', $original)
                $null = Invoke-CheckedCommand $StrongNameToolPath @('-q', '-vf', $extracted)
            }
            if ($item.GetAttribute('OriginalSignature') -eq 'true') {
                if ($packageHash -cne $originalHash) { throw "Original signed dependency changed for payload $name." }
                if ((Get-AuthenticodeSignature -LiteralPath $original).Status -ne 'Valid') {
                    throw "Original dependency signature is not valid for $name."
                }
            }
            if ($RequireSigned) {
                $authenticode = Get-AuthenticodeSignature -LiteralPath $extracted
                if ($authenticode.Status -ne 'Valid') {
                    throw "Extracted payload signature is not valid for ${name}: $($authenticode.StatusMessage)"
                }
                if ($item.GetAttribute('Owner') -in @('Microsoft', 'Tool') -and
                    $authenticode.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
                    throw "Extracted first-party payload has an unexpected signing identity: $name."
                }
            }
        }
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
    Write-Host "Verified package metadata, $($expectedPayload.Count) PE payloads, original signatures, and strong-name identities for $ExpectedVersion."
}

Export-ModuleMember -Function Invoke-CheckedCommand, Test-DotnetPackageSkillsPackage
