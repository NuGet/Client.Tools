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

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Package-Validation.psm1') -Force
Test-DotnetPackageSkillsPackage @PSBoundParameters
