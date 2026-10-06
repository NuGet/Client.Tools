# dotnet-package-skills pipeline

The root public and official entry points share one `DotnetPackageSkills` stage in `stage.yml`.
Each Windows job installs the root-pinned SDK and the .NET 8 runtime into the same Arcade
installation, then runs one command from the repository root:

```powershell
eng\common\build.cmd -restore -build -test -pack -configuration Release -ci
```

The official job adds `-sign`, `DotNetSignType=real`, `OfficialBuildId=$(Build.BuildNumber)`,
and the approved `TeamName`. Arcade packs first, signs the nested assemblies, repacks, and
signs the NuGet package. Do not build or pack again after signing. The public job does not
invoke signing or use production resources.

Arcade retains infrastructure validation, runs the original application tests on net8.0 and
net10.0, publishes VSTest results, and keeps diagnostic logs. The successful NuGet package is
published as `dotnet-package-skills-packages`. Official outputs use the inherited 1ES wrapper.
There are no custom pipeline validators, package smoke scripts, or pipeline-only test suites.
Nothing is published to feeds, BAR/Maestro, symbol servers, or GitHub releases.

## Versions and outputs

The reviewed base and prerelease label live in root `eng\Versions.props`. Arcade computes:

| Build | Version |
| --- | --- |
| Local | `0.1.0-dev` |
| Public CI or PR | `0.1.0-ci` |
| Official | `0.1.0-beta.<Arcade-short-date>.<revision>` |
| Manual official stable opt-in | `0.1.0` |

The official build number uses `yyyyMMdd.r`, not Azure's numeric BuildId.
`DotnetPackageSkillsReleaseBuild` defaults to false. Only a manual trusted-main run can set it
to true; that sets the native `DotNetFinalVersionKind=release` property.

Packages are under `artifacts\packages\Release\Shipping`; binaries are under
`artifacts\bin\DotnetPackageSkills\Release\<tfm>`. The package remains a portable
`tools/<tfm>/any` tool for both frameworks. Only the product is packable and shipping.
The product and tests keep `SignAssembly=false`; dependencies keep their strong-name identities.

## Official signing prerequisites

The official entry point includes MicroBuild resources only for non-PR main builds from
TfsGit `NuGet-Client.Tools` in `dnceng/internal`. Untrusted source plans reject the run without
including those signing resources. Keep the inherited 1ES governance and main-only PR filter.

The owner must provide the approved `DotnetPackageSkillsSigningTeamName` and authorize the
existing internal pool, 1ES templates, MicroBuild feed/shared connections, and signing profiles.
No credentials or permissions are created by this pipeline. The trusted mirror must contain
the reviewed main commit before a real-signing run.

Root `eng\Signing.props` imports the concise module `Signing.props`. It lists the tool package
and both loose owned DLLs. Arcade uses the .NET certificate for first-party DLLs and the
standard NuGet profile for the package. SharpYaml uses `3PartySHA2`. Already signed Microsoft
dependencies keep their original signatures. This follows how
[dotnet/sign builds its own releases](https://github.com/dotnet/sign/blob/f3cc758ffd4f5edccb567820606b17eea49745c0/.vsts-ci.yml),
not the public sign CLI. Real-signing acceptance requires a successful authorized official run;
local or public builds do not prove it.

## Local build and retirement

From the root, run `eng\common\build.cmd -restore -build -test -pack -configuration Release`.
Install the .NET 8 runtime beside the SDK that Arcade selects. Never use `-prepareMachine`
on a shared local machine.

To retire the tool, remove its solution entry, root signing import, stage references, and
source/pipeline folders in a reviewed change. Keep the shared SDK, feeds, Arcade imports,
`eng\Infrastructure.proj`, other tools, and `eng\common` intact.
