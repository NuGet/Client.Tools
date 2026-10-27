# Release dotnet-package-skills to nuget.org

`release.yml` is manual-only: approval followed by one 1ES NuGet publishing output.
It consumes the selected official build's `dotnet-package-skills-packages` artifact
without rebuilding, repacking, or signing it again.

## Setup

- Register the YAML pipeline in **dnceng / internal**, using the `NuGet-Client.Tools`
  mirror and `main` as its default production branch.
- Authorize the official `NuGet\client.tools-ci` resource, 1ES templates, and existing
  `NetCore1ESPool-Internal` Windows image. Retain standard 1ES artifact, branch, and
  protected-service checks and the configured restricted network policy.
- An owner must supply and authorize a **NuGet** service connection targeting
  `https://api.nuget.org/v3/index.json`, with permission to publish `dotnet-package-skills`.
  No accessible publishing connection is confirmed in this project; the DevDiv
  reference pipeline's connection is not portable. Keep credentials out of YAML.

## Release

1. Run the pipeline from `main`. In **Resources**, use the `official` version picker
   to select the exact successful, reviewed official main build.
2. Replace both `REQUIRED` parameters: `NuGetOrgServiceConnection` with that authorized
   connection's name, and `ReleaseApprovers` with comma-separated approved users/groups.
3. An independent approver reviews the selected build, package version, signing results,
   and target connection before resuming. Self-approval is disabled; timeout rejects
   after 24 hours. These source/package reviews are manual, not custom YAML validations.
4. `PushNuGetPackagesToNuGetOrgJob` downloads the selected packages through 1ES artifact
   inputs and publishes them using `templateContext.outputs` with `output: nuget`.
   It pushes only `dotnet-package-skills.*.nupkg`, excluding symbol packages, to the
   configured external feed. It does not suppress duplicate-version failures.

Official builds normally produce beta versions; `DotnetPackageSkillsReleaseBuild=true`
produces a stable package. This release pipeline does not change the selected version.
Nuget.org versions cannot be overwritten; inspect the feed before retrying a failed push.

No hosted pipeline was created or queued, and no package was published for this change.
Local YAML checks do not replace authorized hosted 1ES validation or owner resource setup.
