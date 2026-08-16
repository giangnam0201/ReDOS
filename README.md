# ReDOS

Run MS-DOS programs on Windows 10 and 11 by double-clicking them. No configuration, no mounting,
no emulator UI to learn.

```
redos                       open the MS-DOS machine
redos run GAME.EXE          run a DOS program
redos manager               add, run and delete programs in the sandbox
```

---

## What it actually does

Windows 10/11 on x64 **cannot** execute 16-bit DOS code natively. Real-mode DOS needs the CPU's
virtual-8086 mode, which does not exist in x64 long mode — this is why Microsoft removed NTVDM and
why `foo.exe` gives you *"This app can't run on your PC"*.

ReDOS does not pretend otherwise. What it removes is the *setup*, not the emulation:

- **Detects DOS executables properly** — reads the MZ header and what follows it, so real-mode MZ,
  DOS-extended (LE/LX, DOS/4GW) and headerless `.COM` files are recognised, while normal Windows
  programs are handed straight back to Windows untouched.
- **Runs them with zero configuration** — no mounting, no `dosbox.conf`, no window that asks you
  questions. The DOS core is bundled and driven silently.
- **Uses the native path when there is one** — on 32-bit Windows 10, where NTVDM still exists,
  ReDOS lets the OS run the program for real instead of emulating it.
- **Keeps one persistent machine** — every program shares a single sandbox drive C:, so files you
  create in one program are there in the next one.
- **Brings the program's data files along** (see [Dependencies](#dependencies)).
- **Opens everything in a window**, never full-screen.

## Install

Download the latest build from the [releases page](../../releases/tag/latest):

- **`ReDOS-*-win-x64.zip`** — ReDOS plus the DOS core. Unzip anywhere, run `ReDOS.exe` once.
- **`ReDOS-*-standalone.exe`** — just ReDOS; it downloads the core itself on first use.

The first run registers ReDOS for your user account. Everything it writes lives under
`HKEY_CURRENT_USER` and `%LOCALAPPDATA%\ReDOS`, so **no administrator rights are needed** and
`redos uninstall` reverses all of it.

## The sandbox

`%LOCALAPPDATA%\ReDOS\sandbox` **is** drive C: of your DOS machine:

```
C:\
  PROGRAMS\     one folder per imported program
  DOCS\         your own files
  TEMP\         TEMP and TMP point here
  DOS\          drop DOS utilities here; it is on the PATH
  AUTOEXEC.BAT  runs before every program - yours to edit, ReDOS never overwrites it
```

It is an ordinary Windows folder. Add and delete files with Explorer (`redos open`), with the
manager window (`redos manager`), or from DOS itself — they are the same files.

When you run a DOS program from outside the sandbox, ReDOS imports it into `C:\PROGRAMS\` first, so
everything the program writes stays in one place and survives. Use `--no-import` to leave it where
it is and mount its folder as drive D: instead.

## Dependencies

DOS programs do not declare what they need; they just open `GAME.DAT` and crash if it is missing.
When ReDOS imports a single executable it works out the rest from three signals:

1. **Filenames embedded in the binary** — ReDOS scans the executable for string literals shaped like
   DOS filenames (`SOUND.CFG`, `LEVELS\L1.DAT`) and copies any that exist next to it, preserving
   the subdirectory layout.
2. **Files sharing the program's name** — `FOO.EXE` pulls in `FOO.DAT`, `FOO.CFG`, `FOO.OVL`.
3. **Sibling data files** — the usual DOS data extensions, plus companion DOS executables.

When the program already lives in its own folder, ReDOS just brings the whole folder. Re-running a
program re-checks for data files that were missed, and never overwrites anything already in the
sandbox.

## The DOS machine in a terminal

`redos` opens the machine. How it appears depends on which core is available:

| Core | Where DOS appears | Handles |
|---|---|---|
| Graphical (bundled, DOSBox-X) | Its own window | Everything, including games, graphics and sound |
| Console (optional, msdos-player) | Inside a real terminal — Windows Terminal, cmd or PowerShell | Text-mode programs only |

The graphical core cannot draw inside a terminal tab: it renders through SDL into its own window.
For text-mode work where you *want* a real terminal — Windows Terminal tabs, splits, copy/paste,
your own colour scheme — install a console core:

```
redos console-core --install <path to msdos-player.exe or its zip>
```

After that `redos` opens a Windows Terminal tab that *is* the DOS machine, and `redos run --console
FOO.EXE` runs a text-mode program in your terminal. Both cores share the same sandbox drive C:.
ReDOS never downloads the console core by itself, because it has no canonical release to fetch.

## Commands

```
redos                        Open the MS-DOS machine.
redos run [OPTS] FILE [ARGS...]
                             Run FILE. Non-DOS programs are passed straight to Windows.
                               --force-dos   run as DOS even if the header disagrees
                               --no-import   leave in place, mounted as D:, not imported
                               --console     run in this terminal instead of a window
                               --stay        stay at the DOS prompt after it exits
                               --dry-run     print the machine config instead of running
redos manager                Open the sandbox manager.
redos import FILE            Copy a program and its data files into the sandbox.
redos list                   List the programs in the sandbox.
redos remove NAME            Delete a program from the sandbox.
redos open                   Open drive C: in Explorer.
redos reset                  Erase the sandbox and start from a fresh machine.
redos detect FILE            Report what kind of executable FILE is.
redos status                 Show what is installed and where.
redos install [--intercept-exe] / redos uninstall
redos core [--update]        Show or refresh the graphical DOS core.
redos console-core --install PATH
```

### `--intercept-exe`

By default ReDOS claims `.com`, `.pif` and `.dos`, and adds a right-click **Run with ReDOS** entry
to executables and batch files. DOS programs with an `.exe` extension still need that right-click.

`redos install --intercept-exe` routes *every* double-clicked `.exe` through ReDOS instead: DOS ones
run in the sandbox, everything else is started normally via `CreateProcess`, bypassing the shell
lookup so it cannot loop back. It is a single per-user registry value and `redos uninstall` removes
it.

## Configuration

Nothing needs configuring, but if you want to:

| File | Applies to |
|---|---|
| `%LOCALAPPDATA%\ReDOS\overrides\_global.conf` | every program |
| `%LOCALAPPDATA%\ReDOS\overrides\<NAME>.conf` | one program |
| `<sandbox>\AUTOEXEC.BAT` | DOS commands run before every program |

Override files use DOSBox-X config syntax and are merged into the generated profile, last value
wins. An `[autoexec]` section in an override is ignored — use `AUTOEXEC.BAT` for that. Check the
result with `redos run --dry-run FILE`.

Environment variables: `REDOS_CORE` and `REDOS_CONSOLE_CORE` point at a core of your choosing;
`REDOS_NO_UI=1` forces text output and never opens a dialog, for scripts and CI.

The built-in DOS does not process a `CONFIG.SYS`, so creating one has no effect — use an override
file instead.

## Building

```
dotnet publish src/ReDOS/ReDOS.csproj -c Release -r win-x64 -o publish
```

Requires the .NET 8 SDK. The result is a self-contained single file with no runtime to install.
CI builds every push to `main`, bundles the DOS core, and publishes to the
[`latest`](../../releases/tag/latest) release; tagging `v*` publishes a versioned release.

## Licence

ReDOS is MIT licensed — see [LICENSE](LICENSE).

Release archives also contain [DOSBox-X](https://github.com/joncampbell123/dosbox-x), which is
licensed under the **GNU GPL v2** and ships with its own licence file. ReDOS invokes it as a
separate program; the two are merely aggregated in the download. Source for DOSBox-X is available
from its repository.
