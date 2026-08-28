# Project brief: the Revival Launcher

Status: proposed, not started. **This one is yours** - it is self-contained,
it cannot break anyone else's game, and at the end there is something you can
actually look at.

## The problem it solves

The master server ships its own item database to every client on login. If the
server does not know an item id that your plugin registers, the server wins and
the item turns back into its donor weapon. Nothing about that looks like a
version problem in the game - it looks like a bug in the mod. It costs an hour
every time, and with two people working it costs two.

A launcher is where that gets caught, in words a person can act on, before the
game starts.

## The insight that makes this non-trivial

There are **two** version questions, and they are not the same question:

| Question | Source | What a mismatch means |
|---|---|---|
| Is my client the newest build? | GitHub releases | "There is something new to download" |
| Does my client match the **server**? | `revival.json` | "Your weapon will turn into an RPD" |

A launcher that only answers the first one does not solve our problem. Answer
both, separately, and say which one is wrong.

## The four states

This is the actual design. Not "update yes/no":

1. **In sync** - show the version, offer Play.
2. **Client older than server** - offer Update. The normal case.
3. **Client newer than server** - *"You built item 2053. The server does not
   know it yet. Ask Kevin to deploy."* This is the case that will hit us
   constantly while two people work, and no off-the-shelf launcher has it.
4. **Server unreachable** - offer Play anyway, with a note. **The launcher must
   never be the reason somebody cannot play.** Two second timeout, then move on.

### Decide states 1 and 3 on the item ids, never on the version strings

This is the trap in this design, and it will bite you on day one if you miss
it. `contentVersion` describes the **server's content**, and it only changes
when somebody deploys a new `weapons_db.xml`. The client version changes with
every release, including releases that add nothing the server cares about.

Right now the live server reports `contentVersion` 0.4.3 while the newest
client is 0.4.5, and **that is correct and in sync** - the weapons match. A
launcher that compares the two strings would shout at every single player about
a problem that does not exist, and once it has cried wolf twice nobody reads
its warnings again.

So: compare `modWeapons` against the ids the plugin registers. Show
`contentVersion` as information, never as a verdict. `serversync.py` in the
repository root already does exactly this comparison - read it, or just call it
and use its exit code (0 in sync, 1 mismatch, 2 unreachable).

## What the server will provide

Kevin adds this; the endpoint does not exist yet. Schema:

    GET http://187.124.117.145:12080/revival.json

    {
      "contentVersion":   "0.4.3",
      "modWeapons":       [{"id": 1160, "clip": 2050},
                           {"id": 1161, "clip": 2051}],
      "minClientVersion": "0.4.0",
      "downloadUrl":      "https://github.com/FenjaminBranklin/Next-Day-Revival/releases/download/v0.4.0/NextDayRevival_Client_0.4.0.zip",
      "message":          ""
    }

`modWeapons` is every weapon from the reserved range 1160-1199 that the
server's own `weapons_db.xml` actually contains, with the magazine it expects.
The server computes it; nobody maintains it by hand.

Weapons, not items: ammunition ids have no entry of their own in that file,
they only appear as a weapon's `ClipItemID`. `serversync.py` in the repository
root already implements the comparison - call it rather than re-deriving the
rule, and note the `--url` switch, which is how you point it at a mock.

**Do not wait for it.** Write a `revival.sample.json` next to the launcher and
read that when the server does not answer. You can build and finish the whole
thing against the mock, and the day the endpoint appears you change one URL.

## Where the client's own version comes from

`VERSION` in the repository root - one line, currently `0.4.0`. It is shipped
next to the plugin and it is the single number the launcher reads from disk.
**Trim it.** The repository normalises line endings, so what you read may carry
a trailing `\r`, and `"0.4.0" -ne "0.4.0\r"` will have you comparing versions
that look identical on screen.
`RevivalPlugin.VERSION` in the source must match it, and `verify.py` check [10]
fails if they drift.

For "is there a newer release", GitHub answers without authentication:

    https://api.github.com/repos/FenjaminBranklin/Next-Day-Revival/releases/latest

Take `tag_name` and the `browser_download_url` of the zip asset. Rate limit is
60 requests an hour per IP unauthenticated - fine for a launcher, not fine for
a polling loop.

## Technology: PowerShell with WinForms

Not for frugality - because you win three fights for free:

- **The client logic already exists and is debugged.** Finding the Steam
  library across drives, patching EAC out of the game code, installing BepInEx,
  writing `ClientConfig.ini` - that is ~490 lines in `client_patch.ps1` that
  took real work to get right.
- **No build step, no code signing, no SmartScreen.** An unsigned `.exe` that
  downloads DLLs into a Steam folder is exactly the shape antivirus software
  reacts to. A script is not.
- **Double-click already works** via a `.bat` shim, the same way
  `1_EINRICHTEN.bat` does today.

If you want a real `.exe` later: PS2EXE, or rewrite it in C#. Do that *after*
the logic works in the field - then you are porting something proven instead of
inventing and debugging at the same time.

## Rules

- **Do not modify `client_patch.ps1` or `start_game.ps1`.** Call them as child
  processes and stream their output into your log pane. They work today and
  they are what everyone else depends on; your blast radius stays zero.
- **Never ship game code.** `client_patch.ps1` computes the EAC patch on the
  player's own installation and never distributes `Assembly-CSharp.dll`. An
  updater that copies game files around would be distributing someone else's
  code. Whatever you download must contain only our own files.
- **No self-update.** A running program cannot replace itself, and the
  workarounds are a rabbit hole. The launcher stays dumb and stable; only the
  payload - plugin and assets - gets versioned and replaced.
- **Never block Play.** Every check is advisory. Timeout, log, continue.
- Verify a download before unpacking it: expected size, and it must actually be
  a zip. A truncated download that overwrites a working install is worse than
  no update.

## What it replaces

`1_EINRICHTEN.bat` and `2_SPIELEN.bat`. You are not adding a layer, you are
turning two batch files into one window. Four things on screen:

    Status      "Client 0.4.0 - Server 0.4.0 - in sync", or the mismatch
    Update      fetch, verify, replace plugin + assets, re-apply the patch
    Repair      re-run client_patch.ps1. This is the most-pressed button:
                after any Steam file verification the EAC patch is gone and
                the game hangs on connect.
    Play        start_game.ps1. Never Steam's Play button.

Plus a log pane, because when something goes wrong the log is the only thing
worth sending back.

## Milestones

Each one is useful on its own - stop anywhere and it was still worth it.

    M1  Read VERSION from disk and revival.json from the mock. Print the four
        states to the console. No window yet. This is the whole brain.
    M2  A window: status line, Play button, log pane. Play shells out to
        start_game.ps1.
    M3  Repair button - shells out to client_patch.ps1, streams its output.
    M4  Update button - GitHub releases API, download to a temp folder,
        verify, replace plugin + assets, re-run the patch.

## Acceptance

    1  With the mock reporting the same version: status says in sync, Play
       starts the game.
    2  Mock reporting a higher version: Update offered, and the wording says
       what will happen.
    3  Mock reporting a LOWER version than the client: it says the server has
       not been deployed yet and names the ids the server is missing.
    4  Mock unreachable: two second wait at most, then Play still works.
    5  After a Steam file verification, Repair fixes the game without anyone
       reading a README.
    6  Killing the launcher mid-download leaves a working installation.

## Not in scope

- No account system, no login, no news feed, no mod browser.
- No self-update.
- No changes to the plugin, the server, or the patch script.
- No installer. It ships in the same zip as everything else.
