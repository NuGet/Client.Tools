# dotnet-package-skills

This tool copies agent skills from inside NuGet packages into a folder that your coding agent
reads.

## The problem

Package authors know their own libraries best. Some package authors now ship an **agent skill**
inside the package. A skill is a set of instructions that covers the conventions, the pitfalls,
and the correct usage patterns for that library. The package stores each skill at
`skills/<package-id>-<skill-name>/SKILL.md`.

Restore extracts the package into the **NuGet global-packages directory**. This directory is
`~/.nuget/packages` by default. It sits outside your repository, and every project on the
machine shares it. A coding agent scans a skills folder only *inside* the working repository.
Because of this, the skill sits correctly on disk, but the agent cannot see it.

This tool closes that gap.

```
~/.nuget/packages/mockly/1.10.0/skills/mockly-usage/SKILL.md  ← where restore puts it
        ↓
.agents/skills/mockly-usage/SKILL.md                          ← where your agent looks
```

## Install

```bash
dotnet tool install --global dotnet-package-skills
```

## Use

Run this command from your repository root:

```bash
dotnet-package-skills install
```

This single command does the whole job. It finds your solution or project. It lists that
project's packages. It locates each direct dependency in the NuGet global-packages directory.
It copies any bundled skills into `.agents/skills/`.

Run the command again after you add or upgrade a package. The command refreshes the skills of
the packages it finds. When a package moves to a new version, the command removes the skills
that version no longer ships. The command never removes a skill just because a package left the
project. Instead, it lists that skill, and you remove it later with
`dotnet-package-skills uninstall --stale`.

### Commands

| Command | What it does |
| --- | --- |
| `install` | Copies bundled skills into the destination. Add `--interactive` to choose which new skills to add. |
| `list` | Shows which packages ship skills. It copies nothing. |
| `uninstall` | Removes skills that this tool copied in. Add `--stale` to remove only the skills whose package the project no longer references. Add `--interactive` to pick them yourself. |

### Choose what to read skills from

You can inspect a whole target, select exact package versions, or resolve bare package IDs from
the destination manifest and a target.

```bash
dotnet-package-skills install                              # auto-detect solution or project
dotnet-package-skills install --source src/MyApp.slnx      # a specific solution or project
dotnet-package-skills install --package Mockly@1.10.0      # exact packages, no project needed
dotnet-package-skills list --package Mockly                # manifest versions, then auto-detected target
dotnet-package-skills install -p Mockly -s src/MyApp.slnx  # manifest versions, then this source
```

`-p` and `--package` accept `ID` or `ID@VERSION` for install and list. Package IDs are matched
without regard to case. For each bare ID, the tool first uses all exact versions recorded in the
selected destination's `.dotnet-package-skills.json`. Use `--destination` to select a different
manifest. If that valid manifest does not track the ID, the tool resolves a target: the one from
`--source`, or the solution or project it auto-detects under the current directory. It runs
`dotnet list <target> package --no-restore --format json` and uses all of the package's resolved direct
versions. Repeated appearances at the same normalized version across projects are one match.
Use `ID@VERSION` when you want only one version.

`--package` is repeatable. A supplied version must be **exact**. The tool refuses `Mockly@1.*`
and `Mockly@[1.0,2.0)`. Resolving a range means picking one version from it, and the only correct
answer to "which version" comes from a project's own restore step. `--source` gives you that
answer. If the tool guessed a version instead, it could copy skills that describe a release you
do not actually reference.

`--source` and `--package` can combine. The target is consulted only for bare IDs that the
manifest does not track. Manifest versions stay authoritative even when `--source` is present.
An explicit `ID@VERSION` does not read the manifest or target to resolve its version. If every
selector is explicit or manifest-resolved, the tool does not run `dotnet list package`.
The tool never chooses a version by scanning the NuGet global-packages directory.

NuGet's version parser checks exact versions, including the older one-, two-, and four-part
forms. Invalid prerelease or metadata labels are rejected. Package comparisons normalize
padding and letter case and ignore build metadata, so `1.10` and `1.10.0+build.1` identify the
same release. Ownership records use normalized versions. Equivalent explicit,
manifest-resolved, and target-resolved selections are inspected only once.

When you name packages explicitly, the tool touches only the packages you name. It leaves every
other installed skill alone. Using a target to resolve a bare `--package` ID does not turn that
selection into a full-target install. Without `--package`, a target describes the project's
complete set of packages. Because of this, a full-target install can also tell you which installed
skills belong to a package that the project no longer references. When a package that a full
target resolves is missing from the NuGet global-packages directory, `install` stops before it
changes anything. Restore the project first, and then try again.

For named selections, a missing extracted version is not evidence that it is skill-free or
that its installed skills should be removed. Available versions can still install skills, but
dropped-skill cleanup for that package waits until every selected version can be inspected.

Multiple versions of a package are allowed. The tool classifies the skills, not just the package
versions:

| Packages discovered | Installation result |
| --- | --- |
| Multiple versions without valid bundled skills | Ignore and report those versions; no error or multi-version warning. |
| One skill-bearing version and other skill-free versions | Install the valid skills and report the ignored versions. |
| Multiple skill-bearing versions with different skill names | Install all their skills and warn about the versions. |
| Two or more candidates with the same destination name | Skip every colliding candidate, report an error, and install the other valid skills. |

These rules apply within one package ID and across different packages. A warning identifies
only versions that actually contribute discovered skills. A missing extracted package is not
classified as skill-free. Central Package Management can help coordinate dependencies, but
different projects and target frameworks can legitimately resolve different versions.

### Keep skills in step with the project

When a package moves to a new version, `install` copies that version's skills over the old
copies. If the old owning version is no longer offered, it removes skills that the replacement
no longer ships. With multiple offered versions, each installed skill retains its actual source
version. A colliding skill keeps its existing files and ownership unchanged, even if its old
version is no longer offered.

When a package leaves the project, `install` keeps its skills and lists them:

```
2 installed skills belong to a package that the target no longer references:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
Use uninstall with --stale to remove skills that no longer match the project.
```

The tool gives removal-option guidance, not a paste-ready command with repository paths.
Use the reported Source and Destination context to choose the project and skills folder for
your uninstall.

A package reference can disappear for a moment, for example halfway through a refactor. Because
of this, removing skills is always a command that you run on purpose. `uninstall --stale` removes
every **stale** skill. A stale skill is one whose package the target no longer references, or
whose exact owning version is no longer among the versions the target references. Preview this removal with
`--dry-run`, or pick among the stale skills yourself with `--interactive`:

```bash
dotnet-package-skills uninstall --stale --dry-run
dotnet-package-skills uninstall --stale
```

`--stale` reads the project's package references, so it needs a solution or project. The tool
uses the one in the current directory, or the one you pass with `--source`. A skill that you
added with `install --package`, for a package outside the project, also counts as stale.

### Choose which skills to install

By default, `install` copies every unambiguous skill that it finds. Add `--interactive` to choose which new
skills to add:

```bash
dotnet-package-skills install --interactive                          # everything the project references
dotnet-package-skills install --package Mockly@1.10.0 --interactive  # just one package's skills
```

`--interactive` combines with `--source` and `--package`. You can narrow the list to a single
package first, and then pick among the skills that package ships. Use this method when one
package bundles a dozen skills together.

```
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

The checklist lists only skills that are not already installed. Nothing starts checked. When you
accept, the tool adds exactly the skills that you checked. An interactive install never refreshes
a skill and never removes a skill. Run `install` without `--interactive` to refresh a skill. Run
`uninstall` to remove a skill. When the packages ship only skills that are already installed, the
command prints `Nothing new to install.` and does not open the checklist. A skill that the tool
cannot add, for example because its name is already taken by another package or by a folder you
wrote yourself, is not listed. The report names these skills in its skipped section. Incoming
candidate collisions and conflicts with a different installed package owner are errors;
unmanaged-destination skips remain warnings.

Adding a skill makes sense only when the installed skills already match the packages. Because of
this, an interactive install stops before the checklist opens, and changes nothing, in these
cases:

- With a full target (no `--package`), a resolved package is missing from the NuGet
  global-packages directory.
- With a full target (no `--package`), an installed skill is stale. Use uninstall with `--stale`
  first.

Different installed and offered versions alone do not block the picker. With `--package`, you
can add a disjoint skill from a new version while retaining installed skills from an older
version. The picker never offers colliding candidates or refreshes an installed name.
After acceptance, collision errors remain in the final report and the command returns 1;
cancellation returns 0 without writes. When no valid new candidates remain, the tool reports
the skips and errors without opening a picker.

Each description follows the authored skill name right after ` - `. There is no padded column and
no package or version suffix. The tool keeps any package prefix in an authored name. A
continuation line flows beneath the skill's own text, using the available width, instead of
leaving a name-sized gap. The focused skill's text turns blue, including every wrapped
description line. A checked item shows a blue `X`. Every other skill name and description keeps
its normal color. The summary counts only the checked skills. There is no separate status
column. When you set `NO_COLOR`, or when your terminal has no color support, `>` marks the
focused skill and `[X]` marks each checked skill, with no other symbol beside them.

The keyboard hints appear below the list, in the style of Aspire's
`(Press <space> to select, <enter> to accept)` message. Every keyboard-help line starts with the
word `Press`. This applies to movement, paging, select-all, clear-all, cancel, and description
scrolling. The tool leaves out a control that would do nothing. These keys work:

| Key | Does |
| --- | --- |
| `up` / `down` | Moves to the next or previous skill, and wraps around at either end |
| `left` / `right`, `pgup` / `pgdn` | Moves to the previous or next page |
| `home` / `end` | Jumps to the first or last skill |
| `space` | Toggles the highlighted skill |
| `a` / `c` | Selects all skills, or clears all skills, across every page |
| `ctrl+up` / `ctrl+down` | Scrolls a description when one skill is taller than a page |
| `enter` | Confirms the selection |
| `esc` / `q` / `ctrl+c` | Cancels, and changes nothing |

The tool measures a page in rendered lines, including wrapped descriptions and keyboard hints. It
does not measure a page by a fixed number of skills. Each ordinary skill stays together on one
page. A user can scroll a description that is too long for one page, without changing the
selection. When you resize the terminal, the tool reflows the page in place. It keeps the
highlighted skill and the checked items, even during a redraw. The tool does not push old picker
frames into your scrollback. When you scroll an oversized description, the skill's own row stays
visible while its continuation lines scroll. A short list, or a partial final page, does not
leave a screenful of blank rows. A single page shows no page counter. The note under the title
gives way first when the window is too small to fit it. This way, a small window still shows the
checklist instead of refusing to open.

The live picker uses a temporary terminal screen. It starts at the top of that screen, regardless
of where the shell's cursor was before you ran the command. A host-driven reflow cannot leave
duplicate copies of the checklist in your normal scrollback. When you accept, cancel, or hit a
handled failure, the tool restores the previous shell screen. It writes the final report there,
not next to an old checklist.

Descriptions come from the top-level YAML `description` property in each package's `SKILL.md`
file. A missing description shows the text `No description provided.` Unreadable or malformed
metadata shows an explicit description warning, but it does not hide the skill or block its
selection. Only the interactive checklists read this metadata. Reports and the ownership
manifest never include a description.

`--interactive` needs a terminal. Pair it with `--dry-run` to see what a selection would change,
before you commit to that change.

### Choose what to remove

`uninstall` also takes `--interactive`. It lists only what this tool installed. It never lists a
skill that you wrote yourself, because it reads the manifest instead of scanning the folder:

```bash
dotnet-package-skills uninstall --interactive
```

```
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

Nothing starts checked, so a mistaken enter removes nothing. A checked row looks the same as it
does in the install checklist. Each checklist does only one thing, so its title and its summary
state what a check mark does. Narrow the list first with `--package` when you care about only one
package. Narrow it with `--stale` to see only the skills that no longer match the project:

```
Which skills should be uninstalled?
Only skills that don't match the target are listed.

> [X] fabrikam.testing-fakes - Create fakes and verify their calls in unit
      tests.
  [ ] fabrikam.testing-fixtures - Share expensive setup across tests with
      fixtures.

1 of 2 selected; 1 to remove
```

Add `--dry-run` to see the outcome without it happening. Descriptions come from the installed
copies, not from the NuGet global-packages directory. A missing or damaged `SKILL.md` does not
block removal of a manifest-owned skill. Package matching ignores letter case. Both modes normalize version filters,
so `1.10` matches `1.10.0`. A blank, missing, or repeated `--package` value on `uninstall` is an
error. It never broadens the command to an unfiltered uninstall. A bare ID removes all tracked
versions of that package; `ID@VERSION` removes only that version's skills. `--stale` and
`--package` cannot combine.

### Options

| Option | Applies to | Description |
| --- | --- | --- |
| `-s, --source <PATH>` | install, list | Solution, project, or directory to read package references from, including to resolve bare package IDs that the manifest does not track. Defaults to searching the current directory. |
| `-p, --package <ID[@VERSION]>` | install, list | Select packages. A bare ID expands all destination-manifest versions first, otherwise all versions from `--source` or an auto-detected target. An explicit version bypasses resolution. Repeatable. Refuses a floating version. |
| `-d, --destination <PATH>` | install, list | Skills destination and manifest location. Default `.agents/skills`. |
| `-d, --destination <PATH>` | uninstall | Where the tool removes skills from. Must match the destination you installed to. |
| `--global-packages <PATH>` | install, list | Overrides the NuGet global-packages directory. A relative path is resolved from the invocation directory. |
| `-i, --interactive` | install | Lets you choose which new skills to add, with descriptions and pagination. Lists only skills that are not installed. Combines with `--source` or `--package`. |
| `-i, --interactive` | uninstall | Lets you choose which installed skills to remove, with descriptions and pagination. Lists only skills that this tool installed. |
| `-p, --package <ID[@VERSION]>` | uninstall | Removes this package's skills from all tracked versions, or only the exact version you name. |
| `--stale` | uninstall | Removes only skills whose exact package/version is no longer referenced by the target. Needs a solution or project. Cannot combine with `--package`. |
| `-s, --source <PATH>` | uninstall | With `--stale`, the solution, project, or directory to read package references from for comparison. Defaults to searching the current directory. |
| `--dry-run` | install, uninstall | Reports what would change, and writes nothing. |

### Target another agent's folder

`.agents/skills` is the vendor-neutral default destination. Point `--destination` anywhere else:

```bash
dotnet-package-skills install --destination .claude/skills
dotnet-package-skills install --destination .codex/skills
```

`uninstall` takes the same option, and it needs that option. `uninstall` looks only where you
point it. To remove what you put in `.claude/skills`, you must name that folder again.

```bash
dotnet-package-skills uninstall --destination .claude/skills
```

### Scripts and CI

The tool writes reports for people to read. The manifest is its only machine-readable output. In
a script, rely on the exit code:

| Outcome | Exit code |
| --- | --- |
| Install completes without errors, including warning-only or ignored-package results | `0` |
| Install encounters skill collisions, after installing the other valid skills | `1` |
| Install dry-run reports collisions without writing | `1` |
| List completes discovery, including collision diagnostics, without writing | `0` |
| Interactive operation is cancelled without writing | `0` |
| Argument or safety validation fails | `1` |

Completed work, ignored packages, warnings, and skipped candidates appear on stdout. Errors
appear on stderr. Exit code 1 can therefore mean a **partial installation**, not that nothing
changed. Preflight failures such as an old/corrupt manifest or a missing target package stop
installation before writes. An argument error also prints help text on stdout.

A job that keeps a committed skills folder in step with the project can run these two commands:

```bash
dotnet-package-skills install
dotnet-package-skills uninstall --stale
```

To see what the tool installed, read `.agents/skills/.dotnet-package-skills.json`. [What you
get](#what-you-get) describes this file. `--interactive` needs a terminal, so leave it out of a
script.

A report and a diagnostic message are both written for people, including an argument-validation
error and a parser suggestion. The tool removes terminal escape sequences and unsafe control
characters from metadata, paths, and diagnostic text before it shows them. This change affects
only the display. The tool still validates arguments exactly as you supplied them, and a stored
identity stays unchanged.

## What you get

Each authored skill folder lands directly under the destination:

```
.agents/skills/
├── .dotnet-package-skills.json            # what this tool copied in; do not hand-edit
├── contoso.widgets-widget-usage/
│   ├── SKILL.md
│   └── references/
│       └── batching.md
└── contoso.widgets-widget-testing/
    └── SKILL.md
```

The tool keeps the skill folder's name from the package. The package ID and version stay in the
install manifest, for attribution and for uninstall filtering, but the tool does not add them to
the path.

The manifest has a format `version` and one entry for each package, keyed by the lowercase
package ID. Each package contains a `versions` map, keyed by normalized exact versions. Each
version owns its own skill folders:

```json
{
  "version": 1,
  "packages": {
    "contoso.widgets": {
      "versions": {
        "2.3.0": {
          "skills": ["contoso.widgets-widget-usage"]
        },
        "3.0.0": {
          "skills": ["contoso.widgets-widget-testing"]
        }
      }
    }
  }
}
```

You can safely commit this file. The tool writes it the same way on every platform. It uses
UTF-8 without a byte order mark, LF line endings, and a stable order for its entries. Because of
this, a Windows checkout and a Linux checkout produce the same bytes. The tool ignores a property
that it does not recognize. It refuses a manifest with a newer format `version`, and it asks you
to update the tool instead.

This prerelease redesign retains format number `1` but does not migrate the old
single-version-per-package layout. The tool rejects that layout before writes, including on
uninstall, rather than losing ownership information. Before upgrading, use the older compatible
tool to uninstall its managed skills. Alternatively, back up and move aside the old manifest
**together with only the skill folders it lists**, then install again. Preserve hand-authored
folders. Deleting only the manifest leaves unmanaged folders that the tool will not overwrite.

We recommend that a package author prefix every skill folder with the package's lowercased ID, as the
example above shows. This convention keeps names globally unique when skills from many packages
share one destination. The tool documents this convention. It does not enforce it. An existing
safe name still works.

### Name collisions

The tool compares destination names without regard to letter case. When two package skills
choose the same name, including two versions of the same package, the tool skips **all** of
them and reports the destination and every candidate as an error. It installs other valid
skills and returns exit code 1. There is no first-wins behavior. Existing bytes and ownership
at a skipped destination remain unchanged, even if one candidate matches the current owner.

The same protection applies to a name that a different package already owns. Every install mode
skips the incoming candidate with an error and keeps the current owner. It does not transfer or
relabel the name automatically, including when an upgrade drops that skill. Uninstall the old
skill explicitly before installing its replacement. A single incoming candidate from the same
package may still refresh an older installed version; an existing owner alone is not a second
discovered candidate.

An existing nonempty destination folder that this tool does not track belongs to the user.
The tool skips it with a warning and never overwrites it. These unmanaged skips alone do not
produce exit code 1. It can reuse an ordinary empty folder. Symbolic links, junctions, and other
reparse points remain protected even when their targets are empty. Existing safety failures,
such as a file obstructing a tracked cross-version replacement, still stop installation before
writes instead of becoming candidate-collision skips.

V1 does not reconcile pre-existing folders that differ only in physical case on a case-sensitive
file system. Keep an authored skill folder's casing stable across versions. Avoid folders such
as `guide` and `GUIDE` in the same destination. Incoming candidates differing only in case are
collisions, but physical case-only renames remain outside the v1 guarantees.

A refresh of a tracked skill replaces its entire folder. This includes any local edits and any
files that you added. Keep hand-written guidance in a separate, untracked skill folder instead.

### Package versions

The destination can hold disjoint skills from multiple versions of one package. The manifest
records the exact version for each skill, and the report warns when multiple versions contain
skills. Skill-free versions are reported as ignored, not as conflicts. Only colliding skill
destinations cause partial-install errors. `list` uses the same classification but remains
read-only and returns 0; `install` returns 1 if it reports errors, after completing safe work.

### Commit or ignore this folder

Both choices are reasonable. Commit the folder so the whole team and your CI system get the
skills without running anything. Or add the folder to `.gitignore` and let each machine refresh
it on its own. Pick one choice, and state that choice in your own contributing guide.

## For package authors: ship a skill

Put each skill under `skills/<package-id>-<skill-name>/`. Give it its own `SKILL.md` file, plus
any supporting files it needs. Prefix the folder with your lowercased package ID. This keeps your
skills from colliding with another package's skills on the consumer's machine.

```xml
<ItemGroup>
  <!-- %(RecursiveDir) is what preserves the folder structure. Without it every file
       collapses into one directory and the skill loses its reference documents. -->
  <None Include="skills/**/*"
        Pack="true"
        PackagePath="skills/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

Every skill must have its own immediate subfolder under `skills/`. The tool does not discover a
lone `skills/SKILL.md` file.

Give each skill a useful `description` in its YAML frontmatter, so a customer can decide whether
they need it:

```yaml
---
name: contoso.widgets-widget-usage
description: >
  Correct usage patterns for Contoso.Widgets, including lifetime rules and batching.
  Use when creating, configuring, or disposing a Widget.
---
```

The tool supports a plain description, a quoted description, a literal (`|`) description, and a
folded (`>`) description. The interactive picker reads only bounded frontmatter. It never
interprets the Markdown instructions in the file, and it never rewrites the file. Description
metadata exists to inform the user. It is not an additional requirement for installation.
Frontmatter is limited to 65,536 decoded characters and 32 collection levels. The description
reader does not support an explicit YAML tag, anchor, or alias. These produce a visible metadata
warning instead of blocking installation.

## How it works

Auto-detection checks the top level before nested targets, and prefers solutions over projects
within each stage. It never enters `bin`, `obj`, `.git`, `node_modules`, or `artifacts`, regardless
of letter case. It skips inaccessible children and directory links. An explicitly named target
directory can still be a link, but an unreadable requested directory produces an error.

1. `dotnet list <target> package --no-restore --format json` finds the resolved direct packages
   without restoring the target, including during a dry run. Restore the target before running
   the tool. When this step fails, the tool shows what it reported, so you can restore or fix
   the target and run the tool again.
2. `dotnet nuget locals global-packages --list` finds where restore extracted those packages.
   `NUGET_PACKAGES` and `--global-packages` both take precedence over this step, in that order.
3. `install` stops without changing anything when a package resolves to more than one version, or
   when a package that the target resolves is missing from the NuGet global-packages directory.
4. For each package, the tool looks in `<global-packages>/<id>/<version>/skills/`.
5. The tool copies each `skills/<name>/` folder to `<destination>/<name>/`. It can reuse an
   ordinary empty destination folder, but skips nonempty or linked unmanaged collisions and shows
   a warning instead. For a package that moved to a new version, the tool removes the skills that
   the new version no longer ships.
6. The tool records what it copied in `<destination>/.dotnet-package-skills.json`.

With `--package`, the tool first resolves each selector. Explicit versions need neither the
manifest nor step 1. Bare IDs use a valid manifest entry when one exists. Only remaining bare IDs
run step 1, against `--source` or an auto-detected target, and only those selected packages
continue through the later steps.

`uninstall --stale` needs only step 1. It compares the manifest with the target's package
references, and it never looks in the NuGet global-packages directory for skills.

The tool never reads or interprets anything inside a skill. The package author decides what a
skill contains. This tool only places that content where an agent will look for it.

### The tool only copies skills

The NuGet global-packages directory contains the packages that restore extracted. NuGet
validates this directory during restore, and every project on the machine shares it. If you
move a file out of this directory, restore may treat the package as damaged. Moving a file would
also remove the skill from every other repository that uses that package.

### Removal is manifest-driven

`.dotnet-package-skills.json` records what the tool copied in. `install` removes only the paths
listed there, when a package moves to a new version. `uninstall` removes only those listed paths
too. Neither command scans arbitrary folders. Keep hand-written guidance in a separate, untracked
folder. This folder is still subject to the v1 case-variant limitations that this document
describes.

When that manifest exists but the tool cannot read it, `install` and `uninstall` both stop
without changing anything. They keep the file in place so you can repair it. Resolve any merge
conflict in the file, or restore it from source control, before you try again. If you cannot
recover the file, move the whole destination folder aside before you install again. The tool will
not guess which existing folders it owns.

The tool also refuses a manifest in three other cases. It refuses a manifest that names a newer
format version. Update the tool instead. It refuses a manifest that a pre-release build of this
tool wrote. Move the skills folder aside and install again instead. It refuses a manifest where a
package has an invalid or missing exact version, where a package ID is invalid, or where a skill
is claimed twice.

A skill name must identify a single folder directly inside the destination. The tool rejects a
name that ends in a dot or a space, including the name `...`, because Windows can resolve such a
name to a different folder or to the destination folder itself. A manifest that contains such a
name blocks both install and uninstall, including interactive mode and dry-run mode, before the
tool changes any skill file or manifest byte.

By default, the tool creates an ordinary manifest file, and it updates an existing manifest in
place. It rejects a manifest file that is a symbolic link or reparse point, including a dangling
link, before install or uninstall changes skills. Use a regular manifest file instead.
A destination directory reached through a link or junction remains supported. This check
is limited to the manifest file entry; it does not guarantee safety against a link replaced
concurrently between checking and writing.

Install and uninstall are not transactional in this prerelease. A failed skill copy or manifest
write (for example a read-only or locked manifest, or exhausted disk space), or an interruption,
can leave changed skills and a stale or partial manifest. Detected failures return a nonzero
exit; changes are not rolled back or recovered automatically.

The tool serializes concurrent operations on the same destination. It rejects an interactive
choice if ownership changed before the tool could apply that choice.

## A note on trust

A bundled skill is a set of instructions that a third party wrote. Your agent will follow those
instructions, so a bundled skill is part of your software supply chain. This tool only copies
skills from a package that your project already depends on, and it prints every skill that it
copies, so you can review them. Treat a new skill the way you would treat any new dependency.

## Common problems

**"No bundled skills found"** This is the common and correct outcome. Most packages do not ship
skills.

**"'dotnet list ... package' failed"** The tool reads the target's packages with `dotnet list
package --no-restore`, and it shows what that command reported. For example, the target may need
restoring. The tool never restores a project. Resolve what the tool reports, for example by
running `dotnet restore`, and run the tool again.

**"resolved packages are missing from"** the NuGet global-packages directory. Run `dotnet restore`
for the target and try again. This message also appears when your packages come from a NuGet *fallback folder*,
which is common in a container or on a hosted build agent. Point `--global-packages` at that
folder.

**"is not a direct package resolved for target"** The valid destination manifest did not track
the bare package ID, and the target did not resolve it as a direct package. Check the named
target, choose another one with `--source`, or specify `ID@VERSION`.

**"target resolution was unavailable"** The valid destination manifest did not track the bare
package ID, and the tool could not auto-detect a solution or project. Pass one with `--source`,
or specify `ID@VERSION`.

**"resolves to more than one version in target"** A solution resolves the bare package ID to
different versions. Narrow `--source` to one project that resolves a single version, or specify
`ID@VERSION`.

**"resolve to more than one version"** Projects in the target reference different versions of
one package. Align those versions, for example with Central Package Management, and try again.

**"installed skills don't match the target"** This message comes from `install --interactive`.
Some installed skills are stale. Preview them with
`dotnet-package-skills uninstall --stale --dry-run`. Remove them with `uninstall --stale`, and
try again.

**"is already installed, and an interactive install only adds skills"** `install --interactive
--package` named a package that is installed at a different version. Use `ID@VERSION` with
install without `--interactive` to move the package to the new version, or use uninstall with
`--package` first. A bare ID retains the destination manifest's tracked version when one exists;
otherwise it uses the selected target's resolved version.

**"Could not read the install manifest"** The manifest has a merge conflict, or someone edited it
into a shape that the tool cannot trust. See
[Removal is manifest-driven](#removal-is-manifest-driven).

**"Unrecognized option '--format'"** Your SDK is older than 7.0.200. Upgrade it.

**The tool reads the wrong NuGet global-packages directory.** `nuget.config` discovery walks up
from the selected target's directory. Put the config above that target, or pass
`--global-packages` explicitly. A relative override is resolved from the directory where you
invoke the tool.

**A solution filter (`.slnf`) is rejected.** Not every SDK accepts a solution filter with
`dotnet list package`. Pass the underlying `.sln` file instead, or run the tool once for each
project with `--source`.
