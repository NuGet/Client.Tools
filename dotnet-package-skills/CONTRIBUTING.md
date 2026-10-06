# How to contribute

Thank you for your help. This tool is small and simple on purpose. Keep changes small and simple
too.

## Set up your computer

Install the .NET SDK that the repository root's `global.json` selects. Install PowerShell 7.
Install the .NET 8 runtime into the same .NET installation that Arcade uses. The SDK supplies the .NET 10 runtime. 
The tool and its C# test suite target both net8.0 and net10.0. CI runs on Windows only. 
Use the root SDK, Arcade imports, and public feeds. Do not add a tool-local `global.json` or `NuGet.config`.

```powershell
git clone https://github.com/NuGet/Client.Tools.git
Set-Location .\Client.Tools
eng\common\build.cmd -restore -build -test -pack -configuration Release
```

Try your build against a real repository without installing it:

```powershell
eng\common\dotnet.cmd artifacts\bin\DotnetPackageSkills\Release\net10.0\dotnet-package-skills.dll `
  list --target C:\path\to\YourApp.sln
```

The build creates an unsigned tool package under `artifacts\packages\Release\Shipping`.
It does not replace a globally installed tool. Only the official pipeline signs the package.

## Layout

```
src/
├── Program.cs              CLI surface: commands, options, exit codes
├── SkillInstallService.cs     Orchestration. This is the only file that puts the steps in order.
├── Cli/OutputWriter.cs     Writes reports for people to read
├── Cli/SkillPicker.cs      The --interactive picker. It shows one page per screen.
├── Cli/ITerminal.cs        An interface for console access, so tests can replace the console
├── Cli/InteractiveSkills.cs  Picker-only metadata and selection mapping
├── Infrastructure/         Process execution and the dotnet CLI wrapper
├── NuGet/                  Target detection, package listing, cache path resolution
└── Skills/                 Discovery, copying, version-change removal, the install manifest

tests/                              xunit tests. Application tests use in-process fakes.
```
