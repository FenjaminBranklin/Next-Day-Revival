# Next Day: Survival - Revival Toolkit

A BepInEx mod for **Next Day: Survival**. The official master server was shut
down; we run our own, and this repository is everything that runs on the
player's machine: the plugin, the asset generators, and the patch script that
points a Steam installation at our server.

You are talking to a developer on this project. Kevin owns the master server
and wrote most of this; he works from a private repository next door and
mirrors code here. **This repository is the shared one.**

The source comments are in German. That is not going to change - translating
92 KB of comments would be a huge diff over code that works. Read them, or ask
and translate them in place for the person you are helping.

## First run on a new machine

If the person you are helping has just cloned this and nothing is set up yet,
work through this list in order and report what you find. Do not skip ahead -
step 3 fails in a confusing way if step 2 has not happened.

**1. Look before touching anything.** This is read-only and safe:

    powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Check

It prints seven checks: game folder, server address, `ClientConfig.ini`, the
EAC patch, BepInEx, the plugin, and whether the server answers. Show the output
and say which lines are not OK. If the game folder is not found, Next Day:
Survival is not installed through Steam and nothing else will work.

**2. Patch the installation.** Ask the person what character name they want
first - it goes into `ClientConfig.ini` and it must not be left as the default,
because two players with the same name on the server is confusing for everyone.
**The game must be closed.**

    powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Name "<their name>"

This writes the server address, patches EAC out of the game code, installs
BepInEx from `bepinex\`, and copies the plugin and assets. It is safe to run
again at any time, and `-Restore` puts `ClientConfig.ini` back.

**3. Check the toolchain.** Only needed if they will actually change code.

    powershell -File build.ps1 -NoInstall

Needs `csc.exe` from .NET Framework 3.5 at
`C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe`. If it is missing, turn on
"**.NET Framework 3.5**" in Windows Features (`OptionalFeatures.exe`) and try
again. This step compiles against the DLLs inside the game folder, which is why
it comes after step 2.

    python verify.py

Needs Python 3. If it complains about `numpy` or `PIL`, run
`pip install numpy pillow` - those are only needed for the asset generators.
Expect **0 errors**. Two or three hints are normal on a fresh clone:
`ildasm.py` and `eacpatch.py` are deliberately not in this repository, and the
installed DLL may be older than the built one until step 4.

**4. Install the freshly built plugin.** Game closed:

    powershell -File build.ps1

**5. Play.** Steam must be running; never use its Play button.

    powershell -File start_game.ps1

Then report: what worked, what did not, and what the person still has to do
themselves (installing Python, turning on .NET 3.5, buying the game).

## Read this first, then stop

Everything else is in `KNOWLEDGE.md`. It starts with an index. **Read the
index, then only the section you need** - do not read it end to end, and never
read `RevivalPlugin.cs` whole. It is 213 KB. `KNOWLEDGE.md` section 2 maps
every class to a line range; go straight there.

## Build

    powershell -File build.ps1 -NoInstall    compile only (fast syntax check)
    powershell -File build.ps1               compile and install into the game
    python make_assets.py                    regenerate meshes/textures/icons
    python verify.py                         static checks - always before
                                             reporting anything as done

`build.ps1` finds the Steam library on its own and compiles against the DLLs in
your own game folder, so **BepInEx must already be installed there** - that is
what `client_patch.ps1` does. Patch first, build second.

C# 3.0, compiled with `csc.exe` from .NET Framework 3.5, because BepInEx 5.4
and HarmonyX are CLR v2.0.50727. That means: **no optional arguments, no named
arguments, no `var` in some positions, no LINQ expression trees, no `async`.**
The plugin does not reference `Assembly-CSharp.dll`; every game type is
resolved by name through `AccessTools`.

## Four traps that each cost hours

1. **Close the game before `build.ps1`.** A running game holds the DLL open and
   `csc` fails with `CS0016`. It reads like a compiler bug. It is a locked file.

2. **When cloning fields from a donor weapon, copy only value types and
   `string`.** Never `UnityEngine.Object` references - the new weapon would
   point at the donor's child objects and show up as an RPD in the inventory
   again. See the warning in `KNOWLEDGE.md` section 2 at `CopyDonorComponents`.
   This has cost several rounds. Do not "simplify" it.

3. **Start the game with `powershell -File start_game.ps1`**, never the Steam
   Play button. Play launches the anti-cheat launcher, which fails with
   "Untrusted system file" since the EAC module update of 2026-08-27. Steam
   itself must be running.

4. **EAC must stay off.** If the plugin suddenly stops loading and nothing
   changed, Steam overwrote the patch - re-run `client_patch.ps1`. If instead
   an EAC *window* appears, the patch is not the problem, the launcher is; see
   trap 3.

## The rule that is not in the code

**The server ships its own item database to every client on login.** If the
master server does not know an item id that this plugin registers, the server
wins: the item reverts to its donor weapon. Adding an item is therefore always
two changes - the plugin here, and `staticdata/weapons_db.xml` on the server,
which only Kevin can deploy.

When something you added shows up as the wrong weapon, check that first, not
last. Reserved id ranges for this mod: **1160-1199** (weapons) and **2050-2099**
(ammunition).

## Fact or hypothesis

Anything you cannot back with IL, asset data, or an observation in the running
game is a **hypothesis**, and it gets labelled as one - in what you say, in the
commit message, in the docs. Confirmed findings go to `KNOWLEDGE.md` section 4
with their evidence; things that were tried and failed go to section 5, because
a failed attempt rules out a path and is worth as much as a success.

## Working here

- Branch and open a pull request. Nobody pushes to `main` directly.
- `python verify.py` must pass before a pull request.
- Do not touch the game installation except through `build.ps1` and
  `client_patch.ps1`.
- No refactoring without being asked. What works stays, even when it is ugly.
- Before anything risky: `git add -A; git commit -m "checkpoint before ..."`.
