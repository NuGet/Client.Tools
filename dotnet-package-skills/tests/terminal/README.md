# Manual terminal regressions

This is the full 51-case terminal suite, not a smoke test and not a CI job. It drives the
built `dotnet-package-skills` apphost through a real PTY, with `pyte` and
`@xterm/headless` checking rendering, colors, alternate buffers and resize reflow.
It also checks installs, removals, ownership, manifests, errors and cancellation against
isolated fake packages. **Actual macOS execution is still pending.** Linux PTY helper
results and Windows tool results are not a Mac pass.

## Set up on a Mac

Use Terminal, iTerm2 or another shell; no desktop automation is needed. Install:

- The .NET SDK selected by the **repository root** `global.json` (currently 10.0.401).
  Its .NET 10 runtime is required even when testing the net8.0 apphost.
- The .NET 8 runtime in that same installation if also testing net8.0. The apphost
  permits major roll-forward, so require runtime 8 below to actually test .NET 8.
- Python 3.9 or newer, and a supported Node.js LTS release with npm.

Use native arm64 SDK/runtime/Node/Python on Apple Silicon, or native x64 on Intel;
do not mix Rosetta x64 runtimes with an arm64 apphost. Run from this branch's checkout,
including when its path contains spaces:

```bash
cd "/path/to/Client.Tools"
repo="$PWD"
terminal="$repo/dotnet-package-skills/tests/terminal"

dotnet --version
dotnet --list-runtimes
export DOTNET_ROOT="$(python3 -c 'import os,shutil; print(os.path.dirname(os.path.realpath(shutil.which("dotnet"))))')"
export DOTNET_HOST_PATH="$DOTNET_ROOT/dotnet"

python3 -m venv "$terminal/.venv"
"$terminal/.venv/bin/python" -m pip install -r "$terminal/requirements.txt"
npm --prefix "$terminal" ci
```

Python dependencies are pinned; `pywinpty` is installed only on Windows. The POSIX
backend uses Python's standard library, not a new terminal dependency.

## Build and run the full suite

Build from the repository root with the selected SDK and the existing Arcade imports.
This project-scoped build produces both native apphosts without running or changing CI:

```bash
dotnet build "$repo/dotnet-package-skills/src/DotnetPackageSkills.csproj" \
  --configuration Release
```

Alternatively, use the native root build entry point:
`bash eng/common/build.sh --restore --build --configuration Release --projects dotnet-package-skills/src/DotnetPackageSkills.csproj`.
If Arcade selects a checkout-local `.dotnet`, put that installation on `PATH` and set
`DOTNET_ROOT`/`DOTNET_HOST_PATH` to it for the tests; runtime 8 must also be available
there for the optional run. Do not use `--prepareMachine`.

The tool path is the executable **without `.exe`**, not the DLL, a Windows build copied
to the Mac, or a globally installed tool. Give each run a fresh artifact directory:

```bash
run="$(mktemp -d "${TMPDIR:-/tmp}/package-skills-terminal.XXXXXX")"
printf 'Artifacts: %s\n' "$run"
set -o pipefail

"$terminal/.venv/bin/python" "$terminal/verify_picker.py" \
  --tool "$repo/artifacts/bin/DotnetPackageSkills/Release/net10.0/dotnet-package-skills" \
  --artifacts "$run/net10.0" 2>&1 | tee "$run/net10.0.log"
```

Optional full run against .NET 8 (requires the SDK plus both runtimes listed above):

```bash
DOTNET_ROLL_FORWARD=LatestPatch \
"$terminal/.venv/bin/python" "$terminal/verify_picker.py" \
  --tool "$repo/artifacts/bin/DotnetPackageSkills/Release/net8.0/dotnet-package-skills" \
  --artifacts "$run/net8.0" 2>&1 | tee "$run/net8.0.log"
```

Exit 0 means all selected cases passed; a failure, timeout, missing dependency or invalid
apphost exits nonzero. `pipefail` keeps failures visible when using `tee`. A full run
selects 51 cases; do not report a filtered run as a full pass.
The console summary is saved in `net10.0.log` / `net8.0.log`; per-terminal `.ansi.txt`,
`.screen.txt` and (for reflow cases) `.buffers.json` files are under each run's `logs/`.
Keep the printed `$run` path and these files when reporting failures.

The tests create Demo.Alpha/Demo.Beta packages and a local-only feed. Restore/discovery
cases need the SDK but no remote package feeds at runtime. Two lock cases build the
small `mutex_holder.cs.txt` fixture into the artifact directory, using .NET's actual
named mutex rather than an unrelated POSIX file lock. Its `.txt` extension prevents
the regular C# test project from compiling this manual-only fixture.
Package caches, SDK user profiles, HTTP caches, skill destinations and handwritten
sentinels are test-owned. SDK first-run certificate creation, global-tool PATH updates
and workload update checks are disabled for fixtures. Tests never install/replace a global tool or remove the user's
skills/cache directories. Only their freshly created temporary fixture directories
are cleaned; logs and the mutex fixture remain in `$run`.

## Platform coverage and focused checks

The driver selects all 51 tool cases on macOS; there are **no blanket Mac skips**.
PowerShell shell history is replaced by `/bin/sh` history, NTFS junctions by directory symlinks, and the
Win32 `Global\` mutex fixture by a .NET named mutex with POSIX case-sensitive canonical
paths. The Win32 `\\?\` spelling is exercised only on Windows; the Unix serialization
case instead uses an equivalent `..` destination spelling. These Unix equivalents do
not claim coverage of Win32/NTFS mechanisms on a Mac. All shared behavior assertions
are retained; failures are not converted to skips.

Focused helper checks are separate from the full tool suite:

```bash
"$terminal/.venv/bin/python" -m unittest discover \
  -s "$terminal" -p 'test_*.py' -v
```

They cover streaming UTF-8, terminal replies, isolation, named-mutex ownership, shell
quoting, controlling-terminal/session setup, real resize/SIGWINCH, Ctrl-C, exit/drain
behavior and owned-process/descriptor cleanup. POSIX PTY integration checks are
explicitly skipped **on Windows only**; this is not a skip of the real Mac picker suite.
The same PTY backend can be checked on Linux, but macOS and its .NET console behavior
must still be verified by running the full suite on a real Mac.

## Provenance

Adapted from [kartheekp-ms/dotnet-package-skills `tests/terminal`](https://github.com/kartheekp-ms/dotnet-package-skills/tree/59d3bc0d4fc80188d33bcc257d2a83c99a4cbfa3/tests/terminal)
at commit `59d3bc0d4fc80188d33bcc257d2a83c99a4cbfa3`. The five original files were
imported byte-for-byte before adapting the shared Python driver. The emulator and
pinned Python/Node manifests are unchanged. The upstream README declares MIT;
see this repository's [MIT license](../../../LICENSE). Dependencies retain their own
licenses (`@xterm/headless` is MIT).
