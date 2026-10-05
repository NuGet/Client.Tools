# Client.Tools

This repository contains tools shipped by the NuGet Client team to help developers effectively use the latest NuGet features.

## Build infrastructure

This repository uses [Arcade](https://github.com/dotnet/arcade) for SDK bootstrap,
MSBuild infrastructure, and Azure Pipelines templates, following the structure of
[dotnet/sign](https://github.com/dotnet/sign). There are no tool projects or product
tests yet. The current build restores the toolset and validates a non-shipping
infrastructure project; it does not compile a tool, create packages, or run tests.

`global.json` pins .NET SDK 10.0.401, Arcade SDK 10.0.0-beta.26501.4, and
Microsoft.Build.NoTargets 3.7.0. The bootstrap scripts use the matching installed
.NET SDK when available or download it into `.dotnet`. Package sources are the
public feeds in `NuGet.Config`; public validation does not require internal feeds
or signing credentials.

### Local builds

On Windows, use PowerShell or cmd.exe with Windows PowerShell available:

```powershell
.\build.cmd -configuration Release
```

To run the same restore/build operation as CI and produce a binary log:

```powershell
.\build.cmd -configuration Release -ci
```

The Windows entry point is `build.cmd`, including when working from Git Bash or
Cygwin. Arcade's Unix build script is not supported in those Windows shells.

On Linux or macOS with Bash, curl, and tar available:

```bash
./build.sh --configuration Release --ci
```

The wrappers forward additional arguments and return the underlying build's exit
code. Logs are written under `artifacts/log/<configuration>`; a CI build produces
`artifacts/log/Release/Build.binlog`. SDK downloads, repository-local package
caches, and build outputs are ignored by Git.

`eng/Build.props` selects `eng/Infrastructure.proj`, a non-packable NoTargets
project. Its restore/build validates the Arcade imports, repository paths, and
version configuration. Invalid configuration fails the build with an explicit
error rather than silently skipping validation.

### Pipelines

| Pipeline | YAML | Existing definition |
| --- | --- | --- |
| Public PR validation | `eng/pipelines/pr.yml` | [dnceng-public/public #351](https://dev.azure.com/dnceng-public/public/_build?definitionId=351) |
| Official CI | `eng/pipelines/official.yml` | [dnceng/internal #1692](https://dev.azure.com/dnceng/internal/_build?definitionId=1692) |

PR validation runs for pull requests targeting `main`, using `NetCore-Public`
and `windows.vs2026.amd64.open`. Official CI runs on pushes to `main`, using
`NetCore1ESPool-Internal` and `windows.vs2026.amd64`, and retains the existing
1ES official pipeline wrapper.

Both pipelines run Arcade restore/build in Release and publish available
diagnostic logs on success or failure. Public jobs use `eng/common/templates`;
official jobs use `eng/common/templates-official` and 1ES-managed log outputs.
Build failures fail the job; optional diagnostic upload failures follow the
upstream templates' policy. No product artifacts or empty test results are
published.

Signing, packing, product publishing, BAR/Maestro registration, automated
dependency subscriptions, Helix, and new compliance pipelines are not enabled.
The existing official 1ES compliance policies remain in place. The pipelines
invoke `eng/common/build.cmd` with explicit restore/build flags, not
`eng/common/CIBuild.cmd`, which also enables test/sign/pack/publish.

### Azure DevOps rollout validation

The pipeline definitions are already configured. Repository onboarding does not
create pipelines or change service connections, permissions, or branch policies.
After the changes are available in the pipeline's source branch, the pipeline
owner should:

1. Preview/expand the public YAML in definition 351 and the official YAML in
   definition 1692, including its existing 1ES resource. Local YAML parsing alone
   does not verify Azure DevOps template expansion.
2. If Azure DevOps reports an unauthorized pool, template resource, or task,
   authorize only the required resource through the existing pipeline.
3. Run PR validation and official CI through the existing definitions and verify
   restore/build success, retained logs, and 1ES-managed official log outputs.
4. On a validation branch, verify that an intentionally invalid infrastructure
   configuration produces a failed build with an actionable error.

Queuing builds or authorizing resources is an operator action separate from the
repository changes; successful local builds do not complete these rollout checks.

### Maintaining Arcade and adding tools

Update the Arcade SDK pin in `global.json`, its dependency version/source commit
in `eng/Version.Details.xml`, and the complete `eng/common` tree together. Copy
`eng/common` from the source commit recorded for the chosen Arcade package,
preserving upstream contents and executable file modes. Do not customize vendored
files; repository-specific behavior belongs outside `eng/common`.

The vendored tooling is covered by [Arcade's MIT license](eng/Arcade.LICENSE.TXT).
Preserve its license and upstream notices when updating it. Automatic dependency
subscriptions are not established by `Version.Details.xml` alone.

When real tools are added, update `eng/Build.props` to select their projects or a
root solution and enable actual tests in the build/pipelines. The infrastructure
project's `net10.0` target and initial `0.1.0-beta` version do not determine future
tools' target frameworks or release versions. Signing, packing, and release
publishing require a separate design with real artifacts and authorized resources.
