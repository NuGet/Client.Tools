# Release dotnet-package-skills to nuget.org

`release.yml` is a **manual-only** 1ES official release pipeline. It publishes an existing
signed `dotnet-package-skills` package, never builds, repacks, signs, or changes its bytes.
There are no CI, PR, schedule, or pipeline-completion triggers.

The intended home is **dnceng / internal**, beside the official `NuGet\client.tools-ci`
pipeline (definition `1692`), using the `NuGet-Client.Tools` Azure Repos mirror.
This keeps artifact access within the source build's organization. It is not configured
for publishing directly from GitHub or for cross-organization artifact access.

## Owner setup (required before use)

This change supplies YAML, not an operational publishing connection or a registered
Azure DevOps pipeline. No accessible nuget.org publishing connection has been confirmed
in dnceng/internal. The similarly named connection in the DevDiv reference pipeline
does not transfer to this project.

1. After review and merge, ensure the mirror's `main` contains this pipeline and the
   reviewed source commit. Register a YAML pipeline in dnceng/internal pointing to
   `eng/pipelines/dotnet-package-skills/release.yml`. Use `main` as its default and
   authorized production branch. Restrict pipeline editing, queueing, and branch access
   to release owners; do not publish from a feature branch.
2. Authorize this pipeline to read `NuGet\client.tools-ci` and its retained
   `dotnet-package-skills-packages` pipeline artifact, the internal 1ES templates at the
   `release` tag, and `NetCore1ESPool-Internal` with `windows.vs2026.amd64`. The job access
   identity needs **View builds** on the source pipeline and **Reader** access to the
   selected service connection's metadata. REST checks use `System.AccessToken`;
   neither API keys nor tokens are printed or passed as script arguments.
3. Have the credential owner supply a **NuGet** service connection in dnceng/internal
   with URL exactly `https://api.nuget.org/v3/index.json` and an approved nuget.org API
   key authorized to push `dotnet-package-skills`. Store credentials only in the service
   connection, never YAML, queue parameters, or logs. Authorize only the intended release
   pipeline. Configure owner-approved resource approvals/checks and permissions outside
   YAML; queue-supplied approvers are an additional review gate, not a substitute for
   protected-resource governance. Keep the connection unchanged during a release.
4. Confirm the `Preferred,NuGet,AzureStorage,CFSClean,CFSClean2,CFSClean3` network policy
   permits artifact download, Azure DevOps metadata reads, NuGet tool download, signature
   chain/revocation verification, and nuget.org publication on the approved image.
   If hosted checks identify a blocked dependency, obtain a narrowly scoped approved
   policy change; do not switch to `Permissive` or disable signature/governance checks.

The service connection is a compile-time queue parameter, not a runtime variable, so
Azure DevOps can authorize it before running the job. All three parameters default to
the unusable sentinel `REQUIRED`. Leaving one unchanged fails validation (or earlier
service-connection authorization); there is no default feed, version, or approver.

## Queue and review a release

Use **Run pipeline** on `main`. In **Resources**, select `official` and explicitly choose
the reviewed successful `client.tools-ci` main run using the resource version picker.
Do not rely on the default latest run. The pipeline independently checks completion,
success, definition, project, trusted mirror, branch, and matching source commit.

Supply:

| Parameter | Required value |
| --- | --- |
| `PackageVersion` | Exact nuspec/package filename version, for example `0.1.0` or `0.1.0-beta.26520.1`. |
| `NuGetOrgServiceConnection` | Exact owner-configured, pipeline-authorized nuget.org connection name. Not a URL, API key, or the DevDiv connection name by assumption. |
| `ReleaseApprovers` | Nonempty comma-separated user email addresses recognized by Azure DevOps. Include an authorized reviewer other than the person queueing the run; group syntax is not supported here. |

The existing official build normally produces a beta package. Its
`DotnetPackageSkillsReleaseBuild=true` opt-in produces the stable version (`0.1.0` at
present). To release a different version, first produce and review the appropriate
official build; this pipeline cannot convert a beta package into a stable one.

The pipeline runs these dependent jobs:

| Job | Behavior |
| --- | --- |
| `Validate` | No checkout or publishing. Checks explicit inputs and official main provenance, downloads only the selected run's package artifact, requires exactly one root package with matching filename and nuspec ID/version, computes SHA-256, and runs `nuget verify -Signatures` using NuGet `6.14.0`. |
| `Approve` | Agentless `ManualValidation@1` displays the selected run, commit, version, connection, and SHA-256. Self-approval is disabled. Reviewers must inspect the official build/signing results and successful signature check before resuming. The gate rejects after 24 hours; the job timeout is longer. |
| `Publish` | Production `releaseJob` consumes the same official artifact through governed 1ES inputs, rechecks the approved SHA-256 and the connection's exact nuget.org URL, then pushes only that `.nupkg` through `1ES.PublishNuGet@1`. No checkout, restore, or tool installation. |

The artifact may also contain 1ES `_manifest` metadata; it is not a package to publish.
Missing, extra, nested, or mismatched packages fail closed. Signature, approval, endpoint,
download, and push failures stop publication. `useDotNetTask: false` keeps nuget.exe
support for encrypted API keys. No duplicate suppression or success-on-error is enabled.

Retain the source run/artifact through the approval window and release audit period.
Nuget.org versions cannot be overwritten. After a failed push, inspect nuget.org and
the task result before retrying; do not assume a failed run means nothing was published.

## Nonpublishing local checks

With PowerShell 7, Python with PyYAML, and `nuget.exe` on PATH, run from the repository root:

```powershell
python eng\pipelines\dotnet-package-skills\test_release.py
git diff --check
```

The tests check the parsed YAML contract and PowerShell syntax, exercise the actual
inline scripts against temporary package ZIPs and mocked nonsecret REST metadata,
and confirm a real `nuget verify` invocation rejects an unsigned fixture.
They never execute the publishing task, sign packages, access credentials, or queue a run.

Local checks do **not** expand the protected 1ES templates or prove hosted permissions,
SBOM/release gating, network access, signing acceptance, service-connection authorization,
or actual publication. Authorized hosted validation is still required before first use;
no hosted pipeline was created or queued as part of this change.

## Reference

The release shape follows
[nuget-client-release.yml](https://devdiv.visualstudio.com/DevDiv/_git/NuGet.Client-Pipelines?path=/nuget-client-release.yml):
manual pipeline resource selection, independent predeployment validation, and production
1ES NuGet publishing without rebuilding the package. It intentionally omits that pipeline's
VS insertions, package exclusions, symbol publishing, BAR configuration, and credentials.

1ES documentation: [release jobs](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/1es-pipeline-templates/features/releasepipelines/overview),
[pipeline artifact inputs](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/1es-pipeline-templates/features/inputs/pipeline-artifact),
and [NuGet publishing](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/1es-pipeline-templates/features/outputs/nuget-packages).
