# dotnet-package-skills pipeline

`stage.yml` contributes one independent `DotnetPackageSkills` stage to each root pipeline.
All tool-specific build, test, version, signing, package-verification, and artifact steps live
here. The source, SDK pin, restore configuration, and C# tests live in the repository's
`dotnet-package-skills` folder. There is no Python or Node dependency.

| Entry point | Source | Outputs |
| --- | --- | --- |
| `eng\pipelines\pr.yml` | GitHub, PRs targeting `main`, in `dnceng-public/public` | Unsigned validation package, C# results, logs |
| `eng\pipelines\official.yml` | Trusted `dnceng/internal/NuGet-Client.Tools` Azure Repos mirror, `main` only | Signed package with 1ES-governed outputs, C# results, logs |

The official definition must select the trusted repository as its source. `checkout: self`
does not change a definition that was incorrectly configured to use GitHub.

## Stage contract

The stage accepts `isOfficialBuild` and `releaseBuild`, both defaulting to false.
The root PR pipeline fixes both to false. The official entry point fixes `isOfficialBuild`
to true and passes its `DotnetPackageSkillsReleaseBuild` queue parameter, which defaults to false.
Do not expose real signing as a PR queue option.

The stage has `dependsOn: []`. Other tools must not consume its variables/artifacts or
implicitly depend on its position in the stage list. Pipeline-wide pools, triggers, the trusted
mirror, and the official 1ES wrapper remain repository-owned. Platform-injected 1ES governance
may add stages; do not disable it to force the expanded official pipeline to contain one stage.

Within the tool stage:

1. Install the SDK from the tool's `global.json` and the .NET 8 runtime.
2. Calculate and validate the package version.
3. Restore and build the solution in Release, then run C# tests for net8.0 and net10.0.
4. Official only: sign and verify the tool-owned DLL in each target framework's build output.
5. Pack only the tool project with `--no-build --no-restore`.
6. Official only: scan, sign, and verify the exact `.nupkg`.
7. Verify the package payload, install it for each framework, and smoke-test the installed commands.
8. Publish the successful package. Retain test results and diagnostic logs when a step fails.

All dotnet commands run from the tool folder so SDK selection honors its scoped `global.json`.
PRs do not require the 1ES template repository or any production credential.

## Package versions

The tool project's `VersionPrefix` is the single base-version setting, initially `0.1.0`.
Local builds use the `dev` suffix. CI computes a version with `Get-PackageVersion.ps1` and
passes `DotnetPackageSkillsVersion` to both build and pack. This override is consumed only by
the tool project.

| Run | Example |
| --- | --- |
| Local | `0.1.0-dev` |
| GitHub PR 42, build 12345 | `0.1.0-pr.42.12345` |
| Manually queued public definition, build 12345 | `0.1.0-ci.12345` |
| Ordinary official build 12345 | `0.1.0-preview.12345` |
| Manual official release on trusted main | `0.1.0` |

To produce a stable artifact, review/merge the intended base version, wait for trusted-mirror
parity, and manually queue the official pipeline from `main` with
`DotnetPackageSkillsReleaseBuild=true`. CI-triggered, PR, and non-main release requests fail.
Changing major/minor/patch remains a reviewed source change; CI does not create version commits
or tags. A new BuildId yields a new prerelease version, while retrying the same build retains it.

Package/informational versions carry the suffix and source provenance. The build ID is not a
numeric assembly-version component. Do not override the version only during pack: the packaged
assembly and NuGet metadata must describe the same build.

## Official signing setup

Official builds require owner-approved ESRP v6 WIF/MSI configuration. No connection, identity,
certificate, or signing profile is created or selected automatically. Configure these
non-secret Azure Pipeline variables on the official definition before its first signing run:

| Variable | Required value |
| --- | --- |
| `DotnetPackageSkillsEsrpServiceConnection` | Name of the approved WIF connection, authorized specifically for this pipeline |
| `DotnetPackageSkillsEsrpManagedIdentityClientId` | Managed identity client ID used by that connection |
| `DotnetPackageSkillsEsrpTenantId` | Tenant ID of the managed identity |
| `DotnetPackageSkillsEsrpClientId` | ESRP account's registered client/application ID |
| `DotnetPackageSkillsEsrpKeyVault` | Key vault containing the ESRP request-signing certificate |
| `DotnetPackageSkillsEsrpRequestSigningCertificate` | Request-signing certificate name, not certificate material |
| `DotnetPackageSkillsEsrpBinaryKeyCode` | Approved code-signing profile for the tool's DLLs |
| `DotnetPackageSkillsEsrpNuGetKeyCode` | Approved NuGet author-signing profile |

The service-connection variable must be available during pipeline resource authorization, not
created by a runtime step. Keep secrets and certificate material in the approved service, never
in YAML or pipeline logs. Do not authorize all pipelines as a shortcut.

The owner must also authorize the internal pool, confirm access to the organization-local 1ES
template repository, install/enable the ESRP v6 signing and scanning tasks, and complete required
signing approvals and branch controls. The stage checks that it is running on `main` from the
trusted repository. Missing settings or access fail the run; there is no unsigned official mode.
Azure DevOps may reject a missing/unauthorized service connection before any job starts.

`steps-sign.yml` targets only `dotnet-package-skills.dll` in net8.0 and net10.0, followed by the
single generated tool `.nupkg`. Tool packing publishes the intermediate assemblies under
`obj\Release`, not the copies under `bin\Release`: the signing step first checks that these
match the tested build outputs, signs the intermediate assemblies, then copies the verified
signed bytes back to `bin` for final payload comparison. Signing only `bin` would allow pack
to replace the signed payload. No test assemblies or third-party dependencies are
signed, and strong-name identities are unchanged.

`Verify-Package.ps1 -RequireSigned` requires a NuGet signature, checks `dotnet nuget verify --all`,
validates assembly signatures, and compares each packaged DLL with its signed build output.
Packing must not rebuild or replace those assemblies.

The first successful signed run remains an onboarding acceptance gate. Local and public PR
validation alone do not establish that ESRP permissions are configured.

### Alternative considered: `dotnet/sign`

[`dotnet/sign`](https://github.com/dotnet/sign) is a .NET Foundation CLI tool that signs
`.nupkg`/`.dll`/`.vsix`/ClickOnce files by delegating to an Azure Key Vault certificate, with no
ESRP client, OneCert registration, or SAW access required. A team only needs a managed identity
granted `sign`/`get` on its own Key Vault certificate, behind an ordinary workload-identity-federated
service connection. This is a materially lighter onboarding path than ESRP.

It is not used here because Microsoft's internal SFI compliance for production/official Azure
Pipelines requires the `EsrpCodeSigning` task itself to execute; a cryptographically valid
signature produced by `dotnet sign` against a self-owned certificate does not satisfy that
requirement, and the resulting artifact would not carry Microsoft's own code-signing identity.
`dotnet/sign` remains worth a second look if this tool's signing requirements ever change, for
example a community-owned fork that signs with its own certificate instead of Microsoft's.

## Artifacts and local verification

Artifacts are named `dotnet-package-skills-packages`, `dotnet-package-skills-testresults`, and
`dotnet-package-skills-logs`. Their staging directory is
`$(Build.ArtifactStagingDirectory)\dotnet-package-skills`. Official artifacts use 1ES
`templateContext.outputs`; only the public path uses ordinary publish tasks. Nothing is pushed
to an internal feed or nuget.org.

From the tool source folder, after building and packing:

```powershell
pwsh -NoProfile -File ..\eng\pipelines\dotnet-package-skills\Verify-Package.ps1 `
  -PackagePath .\artifacts\packages\dotnet-package-skills.0.1.0-dev.nupkg `
  -ExpectedVersion 0.1.0-dev `
  -BuildOutputPath .\src\bin\Release
```

Use the actual package version when verifying CI artifacts. The helper uses a local-only
temporary feed, separate NuGet cache/CLI home, and isolated tool paths. It exercises both
target frameworks with patch-only runtime roll-forward, preserves handwritten fixture skills,
and removes only the temporary directory it created.

## Retirement when the tool moves into the .NET SDK

1. Delete the repository-root `dotnet-package-skills` source folder and this
   `eng\pipelines\dotnet-package-skills` pipeline folder.
2. Remove their single stage-template reference from `pr.yml` and `official.yml`, and remove
   `DotnetPackageSkillsReleaseBuild` from `official.yml`.
3. Remove the tool entry from the root README. No root SDK pin, NuGet configuration, or MSBuild
   hook was added for this tool.
4. Verify that remaining stages reference none of the removed files, variables, or artifacts.
   Other tools should require no implementation changes.
5. Have the pipeline owner remove tool-specific settings/permissions where appropriate. Keep
   shared pools, connections, the trusted mirror, and the official 1ES wrapper.
6. If no workload remains, retire the unused pipeline definitions/entry points with their
   owner's approval instead of leaving invalid empty-stage YAML.
