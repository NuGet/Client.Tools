# Client.Tools

This repository contains tools shipped by the NuGet Client team to help developers effectively use the latest NuGet features.

## Tools

- [dotnet-package-skills](dotnet-package-skills/README.md) copies agent skills bundled in NuGet packages into a repository's skills folder.

## Build

Run from the repository root on Windows, with PowerShell 7 and the root-pinned .NET SDK:

```powershell
eng\common\build.cmd -restore -build -test -pack -configuration Release
```

Install the .NET 8 runtime into the same .NET installation that Arcade selects. The SDK supplies
the .NET 10 runtime. The build graph retains `eng\Infrastructure.proj` and includes the tool
solution. Artifacts use Arcade's `artifacts\bin`, `obj`, `log`, `TestResults`, and
`packages\Release\Shipping` layout.

The public pipeline produces unsigned packages. The official pipeline uses Arcade's recursive
signing with owner-approved MicroBuild/ESRP resources. Both publish NuGet packages as build
artifacts only. See the [tool pipeline guide](eng/pipelines/dotnet-package-skills/README.md).