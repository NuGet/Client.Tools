# How to contribute

Thank you for your help. This tool is small and simple on purpose. Keep changes small and simple
too.

## Set up your computer

Install the .NET 10 SDK that this folder's `global.json` selects. Install the .NET 8 runtime too.
Install PowerShell 7. The tool and its C# test suite target both net8.0 and net10.0. The CI system
runs tests on Windows only.

```powershell
git clone https://github.com/NuGet/Client.Tools.git
Set-Location .\Client.Tools\dotnet-package-skills
dotnet restore .\DotnetPackageSkills.slnx --configfile .\NuGet.config
dotnet build .\DotnetPackageSkills.slnx -c Release --no-restore
dotnet test .\tests\DotnetPackageSkills.Tests.csproj -c Release --no-build --no-restore
```

Try your build against a real repository without installing it:

```bash
dotnet run --project src -f net10.0 -- list --target /path/to/YourApp.sln
```

Pack and verify your build without replacing a globally installed tool:

```powershell
dotnet pack .\src\DotnetPackageSkills.csproj -c Release --no-build --no-restore -o .\artifacts\packages
pwsh -NoProfile -File ..\eng\pipelines\dotnet-package-skills\Verify-Package.ps1 `
  -PackagePath .\artifacts\packages\dotnet-package-skills.0.1.0-dev.nupkg `
  -ExpectedVersion 0.1.0-dev `
  -BuildOutputPath .\src\bin\Release
```

## Origin

This folder came from
[`kartheekp-ms/dotnet-package-skills` at `59d3bc0d4fc80188d33bcc257d2a83c99a4cbfa3`](https://github.com/kartheekp-ms/dotnet-package-skills/tree/59d3bc0d4fc80188d33bcc257d2a83c99a4cbfa3).
We copied the source files at that commit. We did not copy or change the original repository's
history. The source repository is unchanged.

The source README and package metadata name the MIT license. Client.Tools keeps its own root MIT
license file. We left out the Python and Node.js terminal regression tests on purpose. The C#
picker tests and the interactive tool behavior stay the same. Build configuration and pipeline
integration apply only to this tool. This lets a maintainer remove the tool on its own, for
example when its function moves into the .NET SDK.

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
samples/Contoso.Widgets/            An example of a package that ships a skill
```

## Rules that must stay true

Read this list before you change this tool. Each rule protects something that is not obvious
from the code alone. Do not change a rule without a discussion first.

**Copy files from the global packages folder. Never move them.** NuGet validates this folder
during restore. It is a content-addressable cache, and every project on the machine shares it.
If you move a file out of the cache, restore may treat the cached package as damaged. Moving a
file also removes the skill from every other repository that uses that package.

**Use the manifest to decide what to remove. Never scan the destination folder to decide.**
`.dotnet-package-skills.json` records, for each package ID, the one installed version and the
names of the skill folders it owns. `install` acts only on those names when a package changes
version. `uninstall` acts only on those names too. Users keep their own hand-written skills in
the same folder. Deleting one of those skills by mistake is a serious failure.

**An unreadable manifest stops every operation that needs ownership information.** Do not treat
a damaged or unreadable manifest as an empty manifest. An empty manifest means the existing
folders belong to the user. A damaged manifest means the tool does not know who owns the
folders. `install` and `uninstall` must fail before they change anything. They must keep the
file in place so the user can repair it or restore it. `list` can still run, because `list` does
not read ownership information and does not write anything.

An existing manifest must have these parts. It must have a format `version` property, and the
value must be a whole number that this version of the tool supports. It must have a `packages`
object. Each package ID in that object must be valid under NuGet's own rule. `PackageCoordinate.IsValidId`
checks this rule, and the rule allows letters outside ASCII. Each package needs a version that is
not empty. JSON property names must be unique when you ignore letter case. Destination names
claimed by different packages must be unique when you ignore letter case. When the manifest has
a newer format version than the tool supports, the tool asks the user to update the tool. A
pre-release manifest has an `installed` array instead of a `packages` object. The tool refuses
this old format. It does not convert the file. `SetSkills` refuses any ID that the reader would
refuse. This way, the tool never writes a manifest that it cannot later read.

In v1, use ordinary manifest files, and update them in place. The tool does not create symbolic
links. Linked or redirected manifests fall outside the v1 safety guarantees. Do not replace an
existing manifest with a new file merely to handle links. That action can change file ownership
or access rights on Unix systems.

**The manifest is a public contract.** Reports have no machine-readable form. Scripts read the
manifest instead, and teams commit the manifest to source control. Its shape follows
`dotnet-tools.json`. It has a format `version` property and a `packages` object. The object key
is the lowercase package ID. A change that an older version of the tool would misread needs a new
format `version` number. This way, an older tool refuses the file instead of misreading it.

Write the manifest the same way on every platform. Use UTF-8 encoding without a byte order mark.
Use LF line endings, with a final newline at the end of the file. List packages and skills in a
fixed order. Escape values the same way on `net8.0` and `net10.0`. .NET 8 has no
`JsonWriterOptions.NewLine` property, so the writer replaces its CRLF output with LF after it
serializes the file. A byte that depends on the platform creates needless differences in a pull
request.

**When no skill is tracked, the manifest and the folder both disappear.** When the last entry
leaves the manifest, `install` and `uninstall` both delete `.dotnet-package-skills.json`. They
also delete the destination folder, but only if the folder is empty. This way, a repository where
no package ships a skill never grows a stray `.agents/skills/` folder. The tool removes the
folder only when the folder is truly empty. Hand-written skills in that folder keep it from being
deleted.

**Descriptions are read-only text for display. They are not a requirement for installation.**
Skill discovery still identifies a skill by its folder structure alone. Only the interactive
pickers read the top-level YAML `description` property in `SKILL.md`. They read it with a
bounded frontmatter reader and a standard YAML parser. Never interpret the Markdown body of
`SKILL.md`. Never run any part of its metadata as code. Never invent a description. Never
rewrite the file.

When a skill has no description, the picker shows an explicit placeholder text. When the
metadata is unreadable or invalid, the picker shows a visible warning, but it still shows the
skill. This rule replaces an earlier rule that forbade reading frontmatter at all. The new rule
lets users make an informed choice. Reports and the manifest never include descriptions.

**Treat skill names from packages as untrusted input.** A skill name becomes a path segment
inside the user's repository. `SkillDiscovery.IsSafeSkillName` is the one gate that both
discovery and the manifest use. Keep this gate strict. Reject a name that ends in a dot or a
space, on every platform. Windows can resolve such a name to a different folder, or to the
destination folder itself. Before you change any file, resolve every affected skill path as a
direct child of the destination folder. When the tool removes a skill, it must not walk up the
folder tree and delete a parent folder.

**`install` removes a skill only when its package changes to a version that drops the skill.** A
noninteractive install offers the packages that it resolved and found in the cache. A tracked
skill is removed only when all of these conditions are true. First, the tool offers its package
at a different normalized version. Second, the new version does not ship that skill. Third, no
ownership conflict protects the skill.

When an ownership conflict does protect the skill, `install` stops instead of removing it.
Removing the skill would hand its folder name to a different package. Relabeling the skill would
record the old version's files under the new version's name, and a later run would then never
remove those old files. A package that left the project is never a reason to delete its skills.
A package reference can disappear for a moment, for example during a refactor, so `install`
reports these skills as unreferenced instead of deleting them. `uninstall --stale` removes them
when the user asks for that.

A version that is missing from the cache never causes a removal. When a target install is
missing a resolved package, the install stops before it writes anything, including during a
preview. An incomplete cache must never look like permission to delete skills.

**`install -i` only adds skills. It never refreshes or removes a skill.** The checklist lists
only skills that would install cleanly and that are not already tracked. Nothing starts checked.
The installer receives an empty map of offered packages, so it cannot refresh or remove anything.

Every check that could stop the run happens before the checklist opens. The checks look for
three problems: two versions of one package, a missing package or a stale skill when you use a
target, and a named package tracked at a different version when you use `--package`. If the tool
let you add a skill next to a stale skill, the manifest would disagree with the project. If the
tool let you add a skill next to another version of the same package, one package would have two
versions in the manifest. The tool stops before the checklist opens to prevent both problems.

**`uninstall --stale` reads package references. It does not read packages.** This command needs
a solution or project. It compares the manifest with the target's direct package references from
`dotnet list package`. It never asks where the NuGet cache is. A missing or partial cache cannot
change what counts as stale, because the command never looks at the cache. A skill is stale when
no referenced package matches both its ID and its installed version. This rule also works
correctly when a target resolves a package to two versions. `--stale` cannot combine with
`--package`. `uninstall` accepts `--target` only together with `--stale`.

**The picker shows one page at a time. This design choice is deliberate.** A solution can
reference many packages that ship skills. `SkillPicker` renders a frame that fits inside the
window, and it redraws that frame in place. The list can never scroll off the top of the screen
unread. We designed the picker this way to prevent the worst failure: a user who approves skills
that they never saw. Page size follows the rendered height of the content, including descriptions
and keyboard help. Page size does not follow a fixed count of items. The picker itself has no
access to the file system. `InteractiveSkills` supplies package descriptions for `install`, and it
supplies installed-file descriptions for `uninstall`.

**The tool measures the frame from its content. The window bounds the frame's size.** A skill's
name and description share one row. The text ` - ` follows the authored name directly. The
layout does not use a padded name column. Do not append package or version metadata to a name.
Do not strip the package prefix from a name. Each skill's wrapped description text lines up with
the start of the skill's own text. The description uses the rest of the row's width. The
description does not use an indent as wide as the name. Measure every line, including every
wrapped footer line, before you assign whole skill entries to pages. A user must be able to
scroll an oversized description. The tool must never truncate a description silently. The layout
must reflow when the window resizes, and it must keep the current focus and the current
selections. A checkbox change or a cursor move must never shift a page boundary. A partial last
page ends at its actual content. It never shows a run of blank rows.

**Keyboard hints follow the style of Aspire's checklist.** The primary hint reads
`(Press <space> to select, <enter> to accept)`. It appears below the list. Use the same
angle-bracket key notation for paging, select-all, clear-all, cancel, and description scrolling.
Start every keyboard-help line with the word `Press`. Wrap the help text instead of cutting off
any key name. Show the paging and scrolling hints only when the user can actually use paging or
scrolling.

**Focus and checked state are the only visual cues.** The focused skill's text turns blue,
including all of its wrapped description lines. A checked item shows a blue uppercase `X` in
both pickers. Selection never changes the color of a name or a bracket. Neither picker marks what
a check mark does with a separate cue. Each picker does only one thing, so its title and its
summary state that action. The uninstall summary also states how many skills will be removed. A
terminal without color shows `>` for focus and `[X]` for a checked item. It shows no other
marker and no color legend. The tool respects the `NO_COLOR` setting. A separate status column
would take space away from the descriptions, so the tool does not use one.

**A check mark means install in one picker. It means remove in the other picker.** Neither
picker starts with any item checked. If a user presses enter without checking anything, both
pickers change nothing. `PickerMode` carries this difference, and the difference shows only in
the summary text. The code that calls the picker supplies the title text. The uninstall list
comes from the manifest, so the picker never offers a hand-written skill for deletion.

**The picker takes control of the terminal, so it must give control back.** `Choose` hides the
cursor. It reads Ctrl+C as ordinary input instead of letting the runtime handle it. It restores
the cursor and the input mode inside a `finally` block. This matters because of Ctrl+C: if the
runtime handled Ctrl+C directly, it would end the process in the middle of a frame, the restore
code would never run, and the user would be left typing into a terminal with no visible cursor.
The code reads Ctrl+C as a key instead, and it cancels through the same path as the `esc` key.
The code tests for the Ctrl modifier before it checks the key itself, because a plain `c` key
clears the current selection.

**An interactive choice applies only to the ownership state that was in effect when the user
made it.** The tool rechecks its ownership snapshot before it applies an interactive choice. When
ownership changed at the same time, the tool rejects the stale choice. It does not apply that
choice to a different package's files.

Hold the destination lock from the moment the tool loads ownership information through the
moment it writes the final manifest. Both installation and removal take part in this lock. This
way, no other run of the tool can change ownership between the check and the change. Resolve
destination aliases to one canonical path before the tool chooses which lock to use.

**A resize while a frame is on screen makes that frame invalid.** Read the new width and the new
height together. Restart the redraw when the viewport size changes. Clear cells in place instead
of scrolling blank lines past them. If the code scrolls blank lines instead, old picker frames
build up in the terminal's history, and they can wrap incorrectly when the host resizes again.
Keep the prior scrollback content, the current focus, and the current selections. Never swallow a
rendering failure that is not related to the resize.

The picker owns an alternate screen for as long as it runs. Clearing only the current viewport
cannot erase old rows, because the host may have already moved those rows into its normal
history. Restore the original screen and the original output mode on every managed exit. Then
write the final report on the normal screen, not on the alternate screen. Clear the alternate
viewport and move the cursor to its home position before the first frame, and do the same thing
again after every resize. When the terminal enters the alternate screen, it can keep the shell's
old cursor position. If the code then reserves space with blank lines, those lines leave a gap
above a compact checklist. Never clear the normal screen to remove that gap.

Every render also places the cursor directly below the last line that it drew. The cursor does
not go to the bottom of the layout's maximum possible height. `SkillPickerTests` checks this
exact placement for short pages. A managed exit restores the original shell cursor position on
its own, separately, when it leaves the alternate screen.

**The picker's own chrome uses only ASCII characters. Author-written text can use any
language.** Keep every control hint and every marker in ASCII, so that an older console encoding
does not lose or corrupt them. Display Unicode descriptions without splitting a text element in
the middle. Measure terminal cells for this purpose, not UTF-16 code units. Remove any unsafe
terminal control sequence from text that a package author supplied. Send all color output
through `ITerminal`. The picker uses UTF-8 without a byte order mark while it prompts the user.
Restore the original text encoding and the original terminal styling when the picker exits, and
also when it fails. This way, ordinary command output keeps behaving the way it always did.

**A report written for people must not let metadata run as a terminal command.** Sanitize every
untrusted display field with `TerminalText.Sanitize`. This includes package and version metadata,
file paths, reasons for a skip, and operational errors. Framework parser diagnostics and
suggestions use a separate output path. Sanitize that path too, including any write that is split
into parts. Keep multiline error guidance readable.

Never sanitize an argument before the tool validates it. Never save a sanitized value as a
stored value. A canonical identity, such as a package ID, must stay intact and unmodified.

**Reports exist for people to read. The tool has no JSON report.** The manifest is the one
machine-readable record. Exit codes carry success or failure. Do not add a `--json` report back
without a new discussion of that decision. `--package` accepts several values, so the parser
hands it any unknown option that follows it. This can happen when an old script still has a
`--json` flag left in it. The validator for `--package` reports a value that starts with `-` as
an unrecognized argument. It does not report that value as a malformed package coordinate.

**`--package` refuses a floating version and refuses a version range.** Resolving a range means
choosing one version from it. The only correct answer to that choice comes from a project's own
restore step. `PackageCoordinate.Parse` is the one gate that enforces this rule.

**The tool scans only direct dependencies.** Application code depends on its direct package
references. It does not depend on implementation details that come in through a transitive
reference. Do not add a `--include-transitive` option. Do not parse `transitivePackages`. Both
changes need a new product discussion first.

**The authored skill folder name becomes the destination folder name.** A package skill at
`skills/contoso.widgets-widget-usage/` lands at `<destination>/contoso.widgets-widget-usage/`.
The package ID and the package version stay in the manifest as metadata. They do not become part
of the destination path. A skill must be an immediate subfolder that contains `SKILL.md`. A lone
`skills/SKILL.md` file, with no subfolder, is not supported. This is a deliberate choice.

**When names collide, the tool warns and skips. It never overwrites a file silently.** The tool
compares destination names without regard to letter case. Package enumeration and skill discovery
both stay deterministic, so the same package always wins a collision, every time you run the
tool. An existing untracked destination folder belongs to the user. The tool must not touch it.
No install mode can transfer an already-tracked destination from one owner package to another
package. A conflicting path that the tool skips is also protected from removal when the owner's
version changes, in the same way that it is protected from copying. A version refresh of the
same package remains allowed. The tool never relabels a protected skill under a version that does
not ship it. That case stops the install instead, as an earlier rule in this list describes.

Keep every discovery candidate available internally until the tool knows ownership at install
time. Prefer the current owner's candidate when more than one package offers the same name.
`list` stays a discovery report that does not depend on the destination folder's current state.

Package authors can avoid collisions by prefixing their skill folders with their lowercased
package ID. The tool does not enforce this naming convention.

In v1, the tool matches names case-insensitively, as a logical rule. It does not reconcile two
folders that differ only in physical case on a case-sensitive file system. Package authors must
keep their folder casing stable across versions. A case-only rename, or two physical folders
that differ only in case on a case-sensitive file system, both fall outside the v1 ownership
guarantees. Do not promise safe migration for these cases. Do not add special reconciliation
logic for them without a new discussion of this scope.

**A package filter must never broaden a destructive operation.** An explicitly blank filter
value is an error. Only a completely absent `--package` option means all packages. Interactive
uninstall and noninteractive uninstall both use the same normalized version matcher.

**The tool allows one version of each package, or it does not install at all.** The manifest
records exactly one version for each package. The resolved packages, or the `--package`
coordinates, can sometimes include two normalized versions of one package ID. When that happens,
every install mode stops before it changes anything, and it asks the user to align the versions.
We expect repositories to use NuGet Central Package Management for this. `PackageLister.Parse`
keeps each distinct `(id, version)` pair separate, so this check can see both versions. `list`
still shows both versions too.

**Write every error message as guidance, not as a description of the failure alone.** Throw
`PackageSkillsException` with a message that tells the user what to do next. `Program.cs` prints
this message without a stack trace. If a message would leave a user stuck with no next step, add
more words to it. A command shown inside a message must work exactly as printed, when the user
copies and pastes it. Build that command with `SkillInstallService.UninstallCommand`. This
method repeats the run's `--target` value and its non-default `--destination` value.

## Tests

Application unit tests run offline. They never run the `dotnet` command. Any test code that
needs the CLI goes through `IProcessRunner`. `SkillInstallServiceTests` fakes this interface. See
`FakeDotnet` in that file for the pattern to follow. Use `TempDirectory` for any test that
touches the file system. `TempDirectory` cleans up its own files afterward.

The interactive picker goes through `ITerminal`. `FakeTerminal` drives this interface from a
scripted sequence of keys, and it reads the result back as a screen buffer. `FakeTerminal` models
a buffer instead of joining writes end to end. The picker redraws its frame in place, so joining
every write together would stack frames on top of each other. A real user sees only one page at
a time, and the test model must match that.

### Checks for the pipeline and the package

`PipelineVersionTests` calls the tool's PowerShell version calculator. These tests cover PR
versions, preview versions, manual versions, and stable release versions. They also cover
invalid identifiers and invalid release requests. These tests need `pwsh` on the PATH. They need
no network access and no signing credentials.

A separate package verifier installs the produced `.nupkg` file for each target framework. It
checks the package version, the package payload, and the install, list, and uninstall behavior,
all inside temporary directories. Official builds also require a valid package signature and
valid assembly signatures.

Keep all pipeline logic that is specific to this tool under `eng\pipelines\dotnet-package-skills`,
at the root of the repository. See its [guide](../eng/pipelines/dotnet-package-skills/README.md)
for version numbering, official signing setup, and the checklist for retiring this tool.

### Name unit tests

Name each test as a sentence that describes the behavior. Do not name a test after the method
under test:

```csharp
[Fact]
public void Install_skips_a_later_skill_when_destination_names_collide()
```

Every new behavior needs a test. Every bug fix needs a test that fails without the fix. The
`.slnx` preference bug shipped with a test like this, and that test is the reason the bug has
stayed fixed since then.

## Style

`TreatWarningsAsErrors` is on. A build must produce no warnings. Beyond that rule, match the
style of the surrounding code. Write a comment to explain why the code does something. Do not
write a comment that only restates what the code does. If you find a comment that only restates
the code, delete it.

## Compatibility

- The tool targets `net8.0` and `net10.0`. Do not drop `net8.0` without a discussion first. Many
  teams still run the `net8.0` long-term support release.
- `dotnet list package --format json` needs SDK 7.0.200 or later. This is the lowest SDK version
  that the tool can inspect, and the error message states this requirement when the installed SDK
  does not meet it.
- **The tool never restores a project.** It runs `dotnet list package` without a `--no-restore`
  flag, and it never runs `dotnet restore` on its own. The .NET 10 SDK restores the project during
  this listing step, when the project needs it. An earlier SDK instead reports that the target
  needs to be restored first. When the listing fails, the command stops. It shows what the SDK
  reported, including the JSON `problems` array when the SDK provides one, so the customer can
  restore the target, or fix the target, and run the command again. `PackageListerTests` checks
  two things together: the exact arguments that the tool passes, and the fact that the tool never
  attempts a restore.
- The output shape of `dotnet nuget locals` has changed across SDK versions. The parser reads the
  key from the `global-packages:` label in that output. It does not read the key by line
  position. Keep the parser written this way.

## Pull requests

- Make one change in each pull request.
- Make sure `dotnet build` and `dotnet test` both pass.
- Update the README when you change the CLI surface.
- State what you tested your change against. For example, write "Ran `install` on a solution
  with 40 packages. Two of those packages ship skills." This kind of statement is worth more than
  a description of the code diff.
