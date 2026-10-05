# .NET Package Skills: Functional Specification

## 1. Purpose and scope

The tool makes agent skills shipped in NuGet packages available inside a developer's repository, where their coding agent can find them. It supports three jobs: discover available skills, install the skills the developer wants, and remove previously installed skills.

It copies whole skill folders, including supporting documents, from the NuGet cache. It does not move or modify the source package, change project package references, install an agent, or configure an MCP server. In this version, only direct package dependencies are scanned.

The default destination is `.agents\skills` under the directory where the command runs. Developers can choose another destination, such as `.claude\skills`. Agent support for a destination remains the agent's responsibility.

The examples below use made-up packages and paths. Interactive page sizes and line wrapping both
vary with the terminal's dimensions. The examples are not fixed screen layouts.

## 2. Command structure

Invoke the tool by its command name:

```powershell
dotnet-package-skills <command> [options]
```

There are three subcommands. Interactive selection, previews, and stale cleanup are options, not additional subcommands.

| Command | Customer intent | Effect on destination skills |
| --- | --- | --- |
| `list` | See which package-provided skills are available. | No changes. |
| `install` | Copy or refresh available skills. | Refreshes the skills it finds, and removes the skills a package's new version no longer ships. Never removes skills because a package left the project. Interactive runs only add. |
| `uninstall` | Remove skills previously installed by this tool. | Removes all matching tracked skills, only the stale ones with `--stale`, or an interactive selection. |
| `--help` | Learn the commands and options. | No changes. |
| `--version` | Identify the tool build. | No changes. |

The tool requires a compatible .NET runtime. Current builds target .NET 8 and .NET 10. Project discovery also uses the installed .NET SDK.

Installing the .NET tool itself is separate from installing skills. For evaluation with a supplied local tool package:

```powershell
dotnet tool install --global --add-source C:\tool-feed dotnet-package-skills --version 0.1.0
```

### Help and version

```powershell
dotnet-package-skills --help
dotnet-package-skills install --help
dotnet-package-skills list --help
dotnet-package-skills uninstall --help
dotnet-package-skills --version
```

Representative root help:

```text
Usage:
  dotnet-package-skills [command] [options]

Options:
  -?, -h, --help  Show help and usage information
  --version      Show version information

Commands:
  install    Copy skills bundled in NuGet packages into the repository.
  list       Show which packages ship skills, without copying anything.
  uninstall  Remove skills this tool previously copied in.
```

Version output starts with `0.1.0` and may include build metadata after `+`.

## 3. Discover available skills: `list`

```powershell
dotnet-package-skills list
```

The tool runs [`dotnet list package`](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-list) on a solution or project to find its top-level package references. It then looks for an immediate subfolder of each package's `skills` directory that contains `SKILL.md`. Without `--target`, the tool uses a solution or project from the current directory, or from one of its subdirectories. The chosen path appears after `Target:`. Name a target when that choice matters.

Sample output:

```text
Target:      C:\src\MyApp\MyApp.slnx
NuGet cache: C:\packages
Destination: C:\src\MyApp\.agents\skills
Scanned 2 packages (direct).

Found 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)
```

`list` shows what is available from packages, not an inventory of installed skills. It never copies skills or writes the ownership manifest. The tool never restores: the .NET 10 SDK restores the target during `dotnet list package` when it needs to, and earlier SDKs report that it has to be restored first. When `dotnet list package` fails, every command that reads packages stops without changes and shows what it reported, so the user can restore or fix the target and run the command again.

Other ways to scope discovery:

```powershell
dotnet-package-skills list --target C:\src\MyApp\MyApp.slnx
dotnet-package-skills list --target C:\src\MyApp\App.Web\App.Web.csproj
dotnet-package-skills list --package Contoso.Widgets@2.3.0
```

An explicit package must already be extracted in the selected NuGet cache. Naming it does not download it or add it to a project. Such reports use `Target: (packages named on the command line)` and `Scanned N packages (named explicitly)`.

The cache directory itself must already exist. If it is absent, restore the project first. `list` skips a package that is not extracted in the selected cache, and it reports no warning for that package. When projects resolve different versions of one package, `list` shows each version. `install` refuses to proceed until you align those versions (section 4). `list` does not read destination ownership. Because of this, its first discovery candidate can differ from the owner-preferred candidate that `install` uses.

## 4. Install or refresh skills: `install`

```powershell
dotnet-package-skills install
```

Without `--interactive`, the tool attempts to install every discovered skill. It preserves the authored folder names and records ownership in `.dotnet-package-skills.json` inside the destination. Refreshing a tracked skill replaces its whole folder, including local edits or added files. Protection for hand-written skills applies to separate, untracked folders.

The report uses the same context header as `list`. Its result section looks like:

```text
Copied 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)

These skills are instructions written by the package authors, and your coding agent will follow them. Review them before relying on them.
```

### Refresh and cleanup rules

| Scope | Behavior |
| --- | --- |
| Any noninteractive run | Refresh the skills of the packages found in the cache. When a package's version differs from the version that the manifest records, refresh its skills, and remove its installed skills that the new version does not ship. A package at the same version never loses a skill. The tool retains a path that an ownership conflict protects. |
| Target | Keep the installed skills of packages the target no longer references, and list them with a pointer to `uninstall --stale` (section 6). |
| `--package` | Touch only the named packages. Other installed skills are left in place without comment. |
| Interactive install | Add only the skills that are not installed (section 5). Never refresh a skill, and never remove a skill. |
| A different package claims an installed name | Preserve the existing owner and skip the conflicting copy with a warning in every mode. If the run also supplies the current owner's candidate, refresh that candidate rather than letting the conflict block it. Replacement requires an explicit uninstall first. |
| The owner's package moves to a version that no longer ships that name | Stop before any change, including with `--dry-run`. Removing the old copy would hand the name to the other package, and keeping it would record the old version's copy under the new version. The error suggests `uninstall --package <ID>` for the owner. |

A target report ends with the skills it kept:

```text
2 installed skills belong to a package that the target no longer references:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
Run 'dotnet-package-skills uninstall --stale' to remove them.
```

Installed skills are named by the lowercase package ID the manifest records. A reference can disappear temporarily, for example during a refactor, so removal after a package leaves the project is always an explicit command. The suggested command, like every command that an error suggests, repeats the `--target` and a non-default `--destination` of the run, so it can be run as printed.

If any resolved package is missing from a target's cache, `install` stops before any skill or manifest change, including with `-i` or `--dry-run`. Restore into the selected cache before retrying. With `--package`, a package missing from the cache contributes no skills.

There is no separate update command: running `install` again refreshes the applicable skill copies.

```powershell
dotnet-package-skills install --package Contoso.Widgets@2.3.0 --package Fabrikam.Testing@1.4.0
dotnet-package-skills install --destination .claude\skills
```

### One version per package

Repositories are expected to keep one version of each package, for example with [Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management). The manifest records one version per package. When the target resolves more than one version of a package, or `--package` names more than one, every `install` mode stops before any change and names the versions:

```text
error: Cannot install skills because these packages resolve to more than one version: Contoso.Widgets (2.2.0, 2.3.0). Skills can come from only one version of each package. Align the versions, for example with Central Package Management, and then try again. No skills were changed.
```

Equivalent versions, such as `1.10` and `1.10.0`, count as one. `list` still shows every version, and `uninstall --stale` still works.

### Preview without installing

```powershell
dotnet-package-skills install --dry-run
```

Result excerpt:

```text
Would copy 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)
```

Any planned removals appear under `Would remove`. A dry run does not copy or delete skills or create/update the ownership manifest. The .NET SDK can still restore the target while `dotnet list package` runs.

## 5. Choose skills interactively

```powershell
dotnet-package-skills install --interactive
dotnet-package-skills install --package Contoso.Widgets@2.3.0 -i
dotnet-package-skills install -i --dry-run
```

Representative page after making a selection, with `contoso.widgets-widget-testing` already installed:

```text
Which skills should be installed? (MyApp.slnx)
Installed skills aren't listed.

> [X] contoso.widgets-widget-usage - Correct usage patterns for the
      Contoso.Widgets library, including lifetime rules and the batching API.
      Use whenever code creates, configures, or disposes a Widget.
  [ ] fabrikam.testing-fakes - Create fakes and verify their calls in unit
      tests.
  [ ] fabrikam.testing-fixtures - Share expensive setup across tests with
      fixtures.

1 of 3 selected
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Blue X: selected
```

### Selection rules

- The checklist lists only skills that would install cleanly and that are not installed. Nothing starts checked. The line under the title states that installed skills are not listed.
- Acceptance copies only the checked skills. An interactive install never refreshes a skill, and it never removes a skill. A noninteractive `install` refreshes a skill, and `uninstall` removes a skill.
- The checklist does not list a candidate that the tool would skip, for example a name that another package owns, or a name that an untracked folder uses. The final report names these candidates under the skipped warning.
- When every discovered skill is already installed, the command prints the following and exits with code `0` without opening the checklist. When some candidates were skipped, only the first sentence is printed, followed by the skipped warning.

  ```text
  Nothing new to install. Every skill that these packages ship is already installed.
  ```

Because the checklist only adds, it needs the installed skills to agree with the packages. Before the checklist opens, an interactive install fails with exit code `1`, changing nothing, in these cases:

| Case | Message excerpt | Resolution |
| --- | --- | --- |
| More than one version of a package, in any mode | `...resolve to more than one version...` or `--package names more than one version...` | Align the versions, or name one version per package. |
| Target: a resolved package is missing from the cache | `...resolved packages are missing from...` | Restore the target. |
| Target: an installed skill is stale | `Cannot choose skills interactively because 2 installed skills don't match the target...` | Run `dotnet-package-skills uninstall --stale`. |
| `--package`: a named package is installed at another version | `Contoso.Widgets 2.2.0 is already installed, and an interactive install only adds skills...` | Run `install --package` without `-i` to change the version, or `uninstall --package <ID>` first. |

A skill is **stale** when the target no longer references its package, or references a different version than the manifest records. Skills installed with `install --package` from packages outside the target count as stale for target commands, so a target `install -i` stops until they are removed.

### Presentation

- The format is always `skill-name - description`. There is no package or version suffix, and there is no separate description column. Authored package prefixes in a skill name remain intact. The tool may clip a long display name with `...`. A clipped name's canonical identity stays unchanged.
- Descriptions wrap beneath the skill text with a small list indent, using the available width. Page sizes reflect rendered lines, not a fixed number of skills. A list that fits needs no paging.
- Live resizing recalculates wrapping and pagination while preserving focus and selection. Oversized descriptions can be scrolled while their skill row remains visible.
- The note under the title is omitted when the window is too small to fit it, rather than failing.

The live checklist is displayed at the top of a temporary terminal screen, separate from the shell's scrollback. Its initial position does not depend on where the shell cursor was before invocation. On acceptance, cancellation, or a handled error, the original shell screen is restored and receives the final report. The checklist itself is not retained in normal history, preventing host-driven resize reflow from leaving duplicate headings or partial old frames.

| Visual cue | Meaning |
| --- | --- |
| Blue row text | Keyboard focus, including the skill name and wrapped description lines. |
| Blue `X` | Checked item: install in the install picker, or remove in the uninstall picker. Each picker does one thing, so a checked row looks the same in both, and the title and summary say what a check does. |
| Normal name/description text | The item is not focused. Selection does not change the color of its name or its brackets. |
| `>` and `[X]` when color is disabled | Focus and checked state, with no other marker and no color legend. `NO_COLOR` is honored. |

| Key | Behavior |
| --- | --- |
| Up / Down | Move between skills. Wrap at the beginning or the end. |
| Left / Right or PageUp / PageDown | Move between pages. |
| Home / End | Go to the first/last skill. |
| Space | Toggle the focused skill. |
| A / C | Select all / clear all, across all pages. |
| Ctrl+Up / Ctrl+Down | Scroll an oversized description. |
| Enter | Accept. With `--dry-run`, report only. |
| Esc / Q / Ctrl+C | Cancel without changing destination skills. |

Every keyboard-help line begins with `Press`. Paging/scrolling hints appear only when applicable. An unusably small terminal, at startup or after a resize, fails with exit code `1` and guidance to enlarge it. Destination skills are unchanged, but the in-memory selection must be made again. If ownership changes while a picker is open, acceptance also fails without applying the stale choice.

Descriptions are read from YAML frontmatter in `SKILL.md`. Missing descriptions show `No description provided.` Invalid or unreadable descriptive metadata produces a visible warning, but does not prevent selecting the skill. Descriptions are not rewritten, executed, or added to reports or the ownership manifest.

Cancellation output:

```text
Cancelled. Nothing was copied or removed.
```

## 6. Remove installed skills: `uninstall`

```powershell
dotnet-package-skills uninstall
```

This removes all skills tracked by the destination's ownership manifest. It does not remove NuGet packages or hand-written skills, and does not require a solution or the NuGet cache.

Sample output:

```text
Destination: C:\src\MyApp\.agents\skills

Removed 2 skills:
  contoso.widgets-widget-testing (contoso.widgets 2.3.0)
  contoso.widgets-widget-usage (contoso.widgets 2.3.0)
```

Removal reports name each skill's package by the lowercase ID the manifest records.

Common variants:

```powershell
dotnet-package-skills uninstall --package Contoso.Widgets
dotnet-package-skills uninstall --package Contoso.Widgets@2.3.0
dotnet-package-skills uninstall --destination .claude\skills
dotnet-package-skills uninstall --dry-run
dotnet-package-skills uninstall --interactive
dotnet-package-skills uninstall --stale
```

A bare package ID matches the package's tracked skills. `ID@VERSION` matches them only if that version is the one installed. The manifest records one version for each package, so there is never more than one version to choose from. Both modes use the same case-insensitive package matching and the same normalized version comparison. For example, `1.10` matches `1.10.0`. An explicitly blank, whitespace-only, or missing filter value is an error. It never means an instruction to remove everything. `uninstall` accepts the package option only once. It rejects a repeated occurrence or an alias, even when the final value is absent. Only a completely omitted `--package` means no filter. A dry run reports `Would remove`, and it leaves the files in place.

The interactive picker lists only manifest-owned skills and reads descriptions from their installed copies. Nothing starts checked. A check means removal, not retention:

```text
Which skills should be uninstalled?

> [X] contoso.widgets-widget-testing - Testing patterns for code that uses
      Contoso.Widgets. Use when writing unit or integration tests involving
      widgets.
  [ ] contoso.widgets-widget-usage - Correct usage patterns for the
      Contoso.Widgets library, including lifetime rules and the batching API.
      Use whenever code creates, configures, or disposes a Widget.

1 of 2 selected; 1 to remove
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Blue X: selected
```

Accepting removes only the checked skill. If ownership changes while the picker is open or while waiting for another operation, acceptance fails without applying that stale selection. A missing installed `SKILL.md` does not prevent removing its tracked folder. With no tracked skills, the command succeeds without opening a picker:

```text
Destination: C:\src\MyApp\.agents\skills

Nothing to remove. No skills installed by this tool were found there.
```

### Remove stale skills: `uninstall --stale`

`install` never removes the skills of a package that left the project. `uninstall --stale` removes every stale skill: a tracked skill whose package the target no longer references, or references at a different version than the manifest records.

```powershell
dotnet-package-skills uninstall --stale --dry-run
dotnet-package-skills uninstall --stale
dotnet-package-skills uninstall --stale --target C:\src\MyApp\MyApp.slnx --interactive
```

Sample output:

```text
Target:      C:\src\MyApp\MyApp.slnx
Destination: C:\src\MyApp\.agents\skills

Removed 2 skills:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
```

With nothing stale, the command succeeds and says so:

```text
Target:      C:\src\MyApp\MyApp.slnx
Destination: C:\src\MyApp\.agents\skills

Nothing to remove. No stale skills were found.
```

- `--stale` reads the target's package references, so it requires a solution or project. The tool finds this target the same way as in section 3, or you name it with `--target`. Without a target, the command fails with `No solution or project found under ...`. Like `install` and `list`, `--stale` runs `dotnet list package`. The .NET SDK can restore the target during that step. When the step fails, the command stops, and it shows what it reported.
- `--stale` needs only the target's package references, not the packages themselves. Because of this, it does not look in the NuGet cache for skills.
- It works when the target resolves more than one version of a package: a skill is stale only if no project references its installed version.
- With `--interactive`, only stale skills are listed, under the note `Only skills that don't match the target are listed.`
- `--stale` cannot be combined with `--package`. `--target` is accepted by `uninstall` only together with `--stale`.

## 7. Complete option reference

| Option | Applies to | Contract |
| --- | --- | --- |
| `-t, --target <PATH>` | `list` and `install` always. `uninstall` only with `--stale`. | Solution, project, or directory to search. Supported files: `.slnx`, `.sln`, `.csproj`, `.fsproj`, `.vbproj`. Defaults to discovery from the current directory. |
| `-p, --package <ID@VERSION>` | `list`, `install` | Exact package coordinates instead of a target. Repeatable, with one version for each package. The tool removes a duplicate when you repeat an equivalent coordinate. The tool rejects a floating version and a version range. |
| `-p, --package <ID[@VERSION]>` | `uninstall` | One occurrence of a nonempty package filter, optionally restricted to a normalized version. Same matching in both modes. |
| `-d, --destination <PATH>` | All three | The skills destination. Default `.agents\skills`. `uninstall` must use the same destination that installation used. |
| `--global-packages <PATH>` | `list`, `install` | Existing extracted-package cache to use. Overrides `NUGET_PACKAGES` and the NuGet-configured cache. |
| `--dry-run` | `install`, `uninstall` | Report planned destination changes without applying them. |
| `-i, --interactive` | `install`, `uninstall` | Opens the paginated picker. On `install`, the picker lists only skills that are not installed, and accepting only adds them. On `uninstall`, accepting removes the checked skills. This option needs a terminal when there are rows to choose from. |
| `--stale` | `uninstall` | Remove only stale skills: those whose package the target no longer references, or references at a different version. Requires a solution or project. |
| `-?, -h, --help` | Root and all three | Display usage and supported options. |
| `--version` | Root | Display version information. |

**Combination rules:** You cannot combine `--target` with `--package`. You cannot combine `--stale` with `--package`, and `uninstall` accepts `--target` only together with `--stale`. You can combine interactive selection with `--dry-run`, with a package filter, and with `--stale`. `list` has neither `--interactive` nor `--dry-run`. No command has a JSON output option or a restore option. The tool rejects `--json` and `--no-restore` as unrecognized arguments.

**Path rule:** The tool bases a relative destination on the invocation directory. It does not automatically base the destination on the directory that contains `--target`. The tool resolves a relative `--global-packages` override from the target's directory in target mode, and from the invocation directory in named-package mode. That override selects the cache that the tool reads from. It does not reconfigure NuGet restore.

## 8. Ownership manifest

The destination's `.dotnet-package-skills.json` records what the tool installed. It is the tool's only machine-readable output, so its format is a public contract, guarded by its format `version`. Its shape follows the .NET local tool manifest, `dotnet-tools.json`:

```json
{
  "version": 1,
  "packages": {
    "contoso.widgets": {
      "version": "2.3.0",
      "skills": [
        "contoso.widgets-widget-testing",
        "contoso.widgets-widget-usage"
      ]
    }
  }
}
```

| Element | Required | Contract |
| --- | --- | --- |
| `version` | Yes | Format version, a whole number. This release reads and writes `1`. |
| `packages` | Yes | An object keyed by package ID. The tool writes a lowercase ID. The tool matches an ID without regard to case, so two keys that differ only in case are invalid. |
| `packages.<id>.version` | Yes | The one package version the skills were installed from, normalized as NuGet does (`1.10` is written `1.10.0`). |
| `packages.<id>.skills` | Yes | Skill folder names directly under the destination that this package owns. Each name is claimed once across the whole manifest. |

Writing rules: The tool uses UTF-8 encoding without a byte order mark. It uses two-space indentation. It uses LF line endings, with a final newline, on every platform. It lists packages and skills in a stable order. A repository can commit this file without line-ending differences between a Windows checkout and a Unix checkout. The tool ignores a property that it does not recognize when it reads the file. It does not preserve that property when it rewrites the file.

Reading rules:

- A newer format version fails with guidance to update the tool.
- A manifest written by a pre-release build of the tool, which has an `installed` array and no `packages` object, is not converted. The tool asks the user to move the skills folder aside and install again.
- Other damage, such as a merge conflict, a missing `version` or `packages`, an invalid package ID, a package without a version, a duplicate claim, or an unsafe skill name, fails as described in section 9.

A script must rely on exit codes, and it must read this file to find what is installed. The reports exist for people to read, and their wording can change between releases.

Human-readable reports and diagnostics, including argument-validation errors and parser suggestions, remove terminal escape sequences and unsafe control characters from metadata, paths, and diagnostic text. This affects presentation only: arguments are validated as supplied, and stored identities remain unchanged.

## 9. Safety, empty results, and errors

| Situation | User-visible behavior |
| --- | --- |
| No package ships a discoverable skill | Successful report: `No bundled skills found.` |
| Skills exist but none are accepted | Report `Copied no skills.` or `Would copy no skills.`, with any skipped-item details. |
| Every discovered skill is already installed (`install -i`) | The command prints `Nothing new to install.`, with the explanation or the skipped warning. It opens no checklist. Exit code `0`. |
| A resolved package is absent from the cache | A target-based `install` fails before changes, in every mode. `list` skips it silently. With `--package`, it contributes no skills. `uninstall --stale` is unaffected. |
| Packages resolve to more than one version | Every `install` mode fails before changes, and it names the versions that you need to align. `list` shows each version. `uninstall --stale` still works. |
| A package left the target | `install` keeps its skills and lists them with a pointer to `uninstall --stale`. `install -i` fails until they are removed. |
| A package moved to a new version | `install` refreshes its skills, and it removes the ones that the new version does not ship. `install -i` fails with a target, because the skills are stale, and it fails with `--package`, because another version is installed. |
| A destination name conflicts with another skill, an untracked folder, or a different installed owner | Warn and skip the conflicting copy. Preserve the current owner, including when the run leaves its package out. |
| The owner's new version drops a skill that another package in the run ships | `install` fails before changes and suggests `uninstall --package <ID>` for the owner (section 4). |
| A package filter is explicitly blank or missing its value | Fails. Never broadens a selective uninstall to all tracked skills. |
| `uninstall --stale` finds no solution or project | Fails with `No solution or project found under ...`. Removes nothing. |
| The ownership manifest is absent | Existing folders are not assumed to belong to the tool. |
| The ownership manifest is unreadable, malformed, or unsafe | Fails before it changes any destination skill. Preserves the manifest, and explains how to repair it or restore it. A missing `version` or `packages` property is invalid. A duplicate JSON property, including a case variant, is invalid. An invalid package ID is invalid. A package without a version is invalid. A duplicate case-insensitive skill claim is invalid. An unsafe skill folder name is invalid. The tool rejects a name that ends in a dot or a space, including the name `...`, because Windows can resolve such a name to a different folder or to the destination folder itself. This rule also applies to interactive mode and dry-run mode. `list` remains available. |
| The manifest has a newer format version | Fail before changes and ask the user to update the tool. |
| The manifest was written by a pre-release build | Fail before changes and ask the user to move the skills folder aside and install again. |
| `dotnet list package` fails, for example because the restore it runs fails, or an earlier SDK says the target needs restoring | Stop before changes with exit code `1`, show the problems it reported, and ask the user to resolve them and run the command again. The tool never restores. |
| Invalid option combination, missing target, failed restore, or filesystem error | Report an actionable error and return a non-zero exit code. |

The tool writes the manifest after a successful installation or removal. It does not write the manifest after `list`, a cancelled picker, or a dry run. Removing the last tracked entry deletes the manifest. The tool deletes the destination folder only if that folder is empty. A hand-written skill keeps the folder alive. The tool reports the removal of a tracking entry even when its skill folder was already deleted.

Cooperating tool processes serialize reads and changes for a destination. Ownership is loaded and checked inside that critical section, which remains held through copying, removal, and manifest persistence. A busy destination produces retry guidance rather than overlapping mutations. This is local-process coordination, not a distributed filesystem transaction. Equivalent Windows path spellings share that coordination. If a destination alias changes while an operation waits for access, the operation fails before modifying the newly resolved location.

A successful operation, an empty result, and a cancellation all return exit code `0`. A command failure returns exit code `1`. A warning or a skipped skill can still accompany exit code `0`. Automation must inspect the report when completeness matters.

Skill metadata is not a security review of the skill's instructions. A developer remains responsible for deciding what their agent can trust. The tool reports an unexpected file system failure, but this version does not provide a transactional rollback of a partially completed copy or removal. Such a failure can leave a copied folder absent from the manifest. Restore a verified backup, or move the affected folders aside, before you retry. Do not delete untracked guidance without first checking what it is.

## 10. Product review checklist

| Scenario | Expected customer outcome |
| --- | --- |
| Discover before deciding | `list` shows available skills without installing them. |
| Try the picker safely | `install -i --dry-run` previews a selection without writing skills or a manifest. |
| Make an informed choice | Read descriptions, navigate pages, select skills, and accept. |
| Add a few more skills later | `install -i` lists only skills that are not installed. Accepting adds them, and changes nothing else. With nothing new to add, the command says so without opening a checklist. |
| Resize during selection | Text reflows, and the tool keeps the current selections, while the window stays large enough. The note under the title gives way first. An unusable size fails, and changes no file. |
| Upgrade a package | `install` refreshes its skills and removes the ones the new version dropped. |
| Remove a package from the project | `install` keeps its skills, and it states which command removes them. `uninstall --stale`, with `--dry-run` or `-i`, removes them. |
| Mix package versions in one repository | `install` stops before changes and asks for the versions to be aligned, for example with Central Package Management. |
| Encounter incomplete discovery | A target-based install fails before it copies or removes anything. Restore the target, and retry. |
| Encounter another package's owned name | Preserves the installed owner, and shows a warning. Replacing that owner requires an explicit removal first. |
| Remove selectively | `uninstall -i` offers tracked skills only and removes only checked items. |
| Use package filters in scripts | Blank filters fail, and normalized version matching is identical with and without `-i`. |
| Keep locally authored guidance | Keep guidance in separate, untracked skill folders. |
| Automate reliably | Check exit codes, read the ownership manifest, and run `install` followed by `uninstall --stale`. |
| Commit the skills folder | The manifest has the same bytes on every platform, so a commit does not create line-ending differences. |
| Recover from a damaged ownership record | You receive an explicit error. Repair the preserved manifest, or restore it, before you retry. |
