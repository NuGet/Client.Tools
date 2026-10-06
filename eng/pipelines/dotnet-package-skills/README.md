# dotnet-package-skills pipeline

`stage.yml` contributes one authored `DotnetPackageSkills` stage. The tool's pipeline code
lives in this folder. The root Arcade graph includes the tool solution and
`eng\Infrastructure.proj`. The stage runs that infrastructure check as part of its build.
It does not add a second smoke job. There are no Python, Node, Linux, or macOS CI workloads.

| Entry point | Source | Output |
| --- | --- | --- |
| `eng\pipelines\pr.yml` | GitHub, PRs targeting `main`, in `dnceng-public/public` | Unsigned package, both framework results, logs |
| `eng\pipelines\official.yml` | Trusted `dnceng/internal/NuGet-Client.Tools` Azure Repos mirror, `main`, non-PR | Real-signed package and 1ES-governed artifacts |

The main-only PR filter belongs to the shared foundation. Keep it. A PR stacked above the
onboarding branch might not trigger Azure Pipelines. Queue public definition 351,
`client.tools-ci`, for the exact pushed upper commit when needed. A successful infrastructure
run for the lower PR does not validate the tool.

## Build and artifact contract

Run every build action from the repository root through `eng\common\build.cmd`.
The root `global.json`, `NuGet.Config`, and `eng\Versions.props` are shared inputs.
The tool imports the root Arcade props and keeps its scoped compiler settings.
Do not add a tool-local SDK pin or restore configuration.
The root MIT file uses Arcade's required heading and reserved-rights line. Its NuGet copyright
and MIT permission grant stay unchanged; repository license validation remains enabled.

The tool keeps `SignAssembly=false`, `IsPackable=true`, and `IsShipping=true`.
Tests stay unsigned, non-packable, and non-shipping. The infrastructure project stays
non-packable and non-shipping. Keep the package and command name `dotnet-package-skills`,
the existing authors, MIT license, and false license-acceptance setting. Keep both
`tools/net8.0/any` and `tools/net10.0/any`. Do not set a runtime identifier.

| Content | Native path |
| --- | --- |
| Tool binaries | `artifacts\bin\DotnetPackageSkills\Release\<tfm>` |
| Intermediate files | `artifacts\obj\DotnetPackageSkills` |
| Build and signing logs | `artifacts\log\Release` |
| Both VSTest result files | `artifacts\TestResults\Release` |
| Shipping package | `artifacts\packages\Release\Shipping` |

The pipeline installs the root-pinned SDK and the .NET 8 runtime into the same `.dotnet`
installation. It sets `DOTNET_INSTALL_DIR` so Arcade selects that installation. Local
verification uses Arcade's own `InitializeDotNetCli` selection. A runtime installed beside
an unrelated PATH `dotnet` does not satisfy this contract.

The order is:

1. Restore and build the root graph.
2. Run C# tests on both frameworks.
3. Pack without rebuilding.
4. Validate the signing inputs. Official builds recursively sign the innermost DLLs,
   repack the package, and sign its NuGet container. Public builds use a dry run.
5. Verify the final package and install that exact package on each runtime.
6. Publish the verified package. Keep logs and results on failure.

Do not build or pack after signing. There is no intermediate `obj` signing workaround.
Arcade's VSTest runner executes the xUnit suite. Supported version properties retain xUnit
2.9.3, its original analyzer version, VS runner 3.1.4, and Test SDK 17.14.1. Coverlet stays
6.0.4. Test publication fails on failed or missing files. The separate results check also
requires fresh, complete, successful files for both frameworks and preserves the 792
application tests that remain after retiring the 15 custom-version-script cases.

## Native versions

`eng\Versions.props` holds the reviewed `VersionPrefix=0.1.0` and
`PreReleaseVersionLabel=beta`. Arcade computes the versions. There is no custom date or
package-version calculator.

| Build kind | Package version |
| --- | --- |
| Local | `0.1.0-dev` |
| Public CI or PR | `0.1.0-ci` |
| Ordinary official | `0.1.0-beta.<Arcade-short-date>.<revision>` |
| Explicit manual release from trusted main | `0.1.0` |

The fixed public CI suffix is deliberate. These packages are artifacts, not immutable feed
publications. The official pipeline name is `$(Date:yyyyMMdd).$(Rev:r)`. Pass that build number
as `OfficialBuildId`, not Azure's numeric `Build.BuildId`. The guard requires a real date and
a revision from 1 through 99 without leading zeros. Use the same inputs for build, test, pack,
sign, and verification.

`DotnetPackageSkillsReleaseBuild` defaults to false. After reviewing the base version and
confirming trusted-mirror parity, manually queue the official main definition with this option
set to true. Only this request maps to `DotNetFinalVersionKind=release`. Public, PR, automatic,
non-main, missing, and malformed release requests fail. A direct final-kind or package-version
override cannot bypass the guard. Builds do not create version commits, tags, or releases.

`Invoke-Build.ps1 -Action Metadata` runs the native MSBuild evaluation with matching inputs.
The wrapper uses the evaluated `Version`, `PackageVersion`, package path, binary path, and
strong-name tool path. It never selects a package by a stale glob. Pack also rejects an
unexpected second tool package. Before changing build kinds locally, remove only the
specific old tool package that you generated, not a broad artifacts directory.

## Arcade signing and owner prerequisites

The reference is how
[dotnet/sign builds its own releases](https://github.com/dotnet/sign/blob/f3cc758ffd4f5edccb567820606b17eea49745c0/.vsts-ci.yml),
not the public `sign` CLI as a production signer. The official job uses Arcade's
`templates-official` wrapper with `enableMicrobuild=true`, `_SignType=real`, and
`DotNetSignType=real`. The inherited `MicroBuildSigningPlugin@4` and ESRP integration supply
the DNCEng shared feed and connections. Do not replace them with direct `EsrpCodeSigning@6`
steps, invent connection values, or create credentials.

The stage includes signing resources only when template expansion identifies all of these:
TfsGit, repository `NuGet-Client.Tools`, project `internal`, collection
`https://dev.azure.com/dnceng/`, main, and a nonempty non-PR reason. Any other official source
expands to a rejecting 1ES job with `runAsPublic=true` and MicroBuild disabled. A runtime
source guard repeats the checks before plugin installation. The public entry point has no
1ES repository or production signing connection.

The owner must configure `DotnetPackageSkillsSigningTeamName` with the approved team
identifier. The job maps this one non-secret setting to `_TeamName` and `TeamName`.
Missing or unresolved values fail. The old eight custom ESRP settings are retired.

Before a real-signing run, the owner must confirm:

1. The internal definition uses the trusted mirror and has the reviewed source commit.
2. The internal pool and 1ES template repository are authorized.
3. The MicroBuild v4 extension, MicroBuildToolset feed, inherited shared connections, team,
   and signing profiles are authorized for this definition.
4. The required signing approvals and branch controls are satisfied.

Do not authorize every pipeline as a shortcut. Do not grant permissions as part of this
migration. Extension installation alone does not prove connection or profile authorization.
The first successful authorized official run remains an acceptance gate.

### Signing registry and payload policy

Root `eng\Signing.props` is a thin registry. It imports this folder's signing configuration.
The configuration removes the broad automatic artifact list and evaluates exactly three
inputs: this build's shipping `.nupkg` and the tested loose owned DLL for each framework.
Missing, empty, or unmapped inputs fail.

`Payload.props` is the reviewed inventory. It covers all 33 shipped PE entries:

| DLL | Ownership and policy |
| --- | --- |
| `dotnet-package-skills.dll` | Tool-owned, `MicrosoftDotNet500`, still not strong-named |
| `SharpYaml.dll` | Third-party, `3PartySHA2`; retain its existing strong name |
| `System.Collections.Immutable.dll` | .NET, net8.0 only; retain its original Microsoft signature and strong name |
| `System.CommandLine.dll` | Microsoft; retain its original signature and strong name |
| `System.CommandLine.resources.dll` | All 13 shipped cultures on both TFMs; retain their original signatures and strong names |
| Outer `.nupkg` | Standard `NuGet` profile |

The dependency versions stay SharpYaml 3.13.1 and System.CommandLine 2.0.10. The
net8.0 payload also contains System.Collections.Immutable 9.0.0. Do not assign a Microsoft
product certificate to a third-party DLL. Do not suppress the third-party checks or add
blanket ignore rules. No new strong-name key is assigned.

The pinned SignTool deduplicates identical content and updates the explicitly listed loose
copies. This retains the strict SHA256 comparison between each packaged owned DLL and its
tested loose copy. The preflight and final verifier check redistributed full assembly/key
identities and validate their strong-name signatures with Arcade's `sn.exe`. Existing
Microsoft dependency signatures must remain byte-for-byte unchanged.

`Verify-Package.ps1 -RequireSigned` checks the actual NuGet signature with
`dotnet nuget verify --all` and validates every extracted owned and dependency DLL signature.
It checks identity, version, MIT, authors, origin commit, README, tool settings, portable
layout, dependency inventory, and owned DLL hashes. It rejects duplicate or unsafe ZIP paths.
The isolated installation uses only the exact package in a local-only feed, separate NuGet
cache and CLI home, and separate tool paths. It runs `--version`, `--help`, `list`, `install`,
and `uninstall` on each runtime and preserves a handwritten fixture skill.

A successful dry run proves the signing plan, not production signatures or authorization.

## Local commands and artifacts

From the repository root:

```powershell
eng\common\build.cmd -restore -build -test -configuration Release
eng\common\build.cmd -pack -configuration Release /p:NoBuild=true
eng\common\build.cmd -sign -configuration Release /p:NETCORE_ENGINEERING_TELEMETRY=false
eng\pipelines\dotnet-package-skills\Invoke-Build.ps1 -Action Metadata
eng\pipelines\dotnet-package-skills\Invoke-Build.ps1 -Action Verify
```

To run the same public-CI actions locally, pass `-CI` to each wrapper action:
`RestoreBuild`, `Test`, `Pack`, `Sign`, then `Verify`. Do not mix local and CI inputs.
Never use `-prepareMachine` on a shared local machine. Do not run broad clean commands.

Artifacts are named `dotnet-package-skills-packages`, `dotnet-package-skills-testresults`,
and `dotnet-package-skills-logs`. The wrapper stages only the verified exact package under
`$(Build.ArtifactStagingDirectory)\dotnet-package-skills\packages`. Official outputs use 1ES
`templateContext.outputs`; public outputs use ordinary artifact tasks. The shared Arcade
wrapper also retains its diagnostic log artifact. No feeds, BAR/Maestro assets, symbols,
GitHub releases, or tags are published.

The C# tests expand the owned templates for public, trusted, and rejected source cases.
They do not substitute for Azure's server expansion or the authorized official 1ES run.

## Retirement when the tool moves into the .NET SDK

Perform this work in a separate reviewed change. Validate removal in an isolated fixture
first, not by deleting the active product checkout:

1. Remove the tool solution from `eng\Build.props`. Keep `eng\Infrastructure.proj` and any
   other tool entries.
2. Remove this module's import from `eng\Versions.props` and `eng\Signing.props`. Keep the
   reviewed base-version properties and other signing modules. If the signing registry is
   empty, remove that registry file.
3. Remove the stage-template references and `DotnetPackageSkillsReleaseBuild` parameter
   from the root entry points. Retain shared pools, main-only triggers, and the 1ES wrapper.
   If this is the only workload, replace its reference with infrastructure validation or
   another active workload. Do not leave an invalid empty stage list.
4. Remove the `dotnet-package-skills` source folder and this pipeline folder. Remove the
   tool's root README entry. Do not restore old nested sources, samples, or deleted docs.
5. Run the root Arcade restore/build and check that the infrastructure and other tools
   remain valid. Keep root SDK/feed files, Arcade imports, and `eng\common` unchanged.
6. Have the owner retire tool-specific definition settings and authorizations where
   appropriate. Keep shared pools, connections, the trusted mirror, and 1ES governance.

The retirement fixture checks both the infrastructure project and a separate future-tool
entry so removing this module cannot remove the shared foundation.
