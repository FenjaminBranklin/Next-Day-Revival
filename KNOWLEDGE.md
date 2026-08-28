# KNOWLEDGE - what is known about this game and this mod

Companion to `CLAUDE.md`. **Read the index, then only the section you need.**
Nothing here needs to be read end to end.

    1  Repository layout
    2  Map of RevivalPlugin.cs - class, line, purpose
    3  How an item gets added, start to finish
    4  Confirmed facts about the game
    5  What has already been tried, including the failures
    6  The server, seen from the client side
    7  Running the game and reading the logs
    8  What is deliberately not in this repository

A note on provenance: the full evidence - IL dumps, asset indices, measured
numbers - lives in Kevin's private working repository. What is written here is
the conclusion, not the derivation. If a conclusion looks wrong, say so and ask
rather than re-deriving it from scratch; the derivation cost real hours.

## 1 Repository layout

    RevivalPlugin.cs        the entire plugin, one file, 213 KB. Never read
                            whole - use section 2.
    build.ps1               compile with csc 3.5, install into the game
    verify.py               static checks. Run before reporting anything done.
    make_assets.py          runs every generator below, writes assets\
    client_patch.ps1        turns a Steam install into a client of our server:
                            ClientConfig.ini, EAC patch, BepInEx, plugin
    start_game.ps1          the only correct way to launch the game
    make_package.ps1        builds the zip handed to a new player

    *_mesh.py               procedural geometry  -> .ndmesh
    *_texture.py            procedural textures  -> _diffuse.png, _normal.png
    *_icon.py               renders an icon from mesh + texture
    ndmesh.py               the .ndmesh container format
    texlib.py, iconlib.py   shared helpers for the generators
    mesh_preview.py         renders a mesh to a PNG without starting the game
    scope50.py              the TAC-50 scope overlay

    assets\                 generated output, shipped next to the DLL
    bepinex\                BepInEx 5.4 for people who do not have it (LGPL)
    docs\FINDINGS.md        long-form measurements, mostly numbers

The generators need Python 3 with `numpy` and `Pillow`. Nothing else.

## 2 Map of RevivalPlugin.cs

Line numbers move; class names do not. If a number is off, grep the name.

    48    ItemDef           one record per item: id, donorId, isWeapon, name,
                            description, mesh, textures, icons, capacity,
                            ammo id, weight
    82    RevivalPlugin     BepInPlugin entry point, config binding, Awake,
                            and BuildItemTable() with the item table itself
    695   CursorTracker     remembers the last requested cursor state
    741   CursorGuard       ClipCursor from user32.dll - keeps the mouse in the
                            window while focused, releases in menus and on
                            focus loss
    875   LocalizationHook  postfix that serves $<id>_Name and $<id>_Descr
    894   RocketHook        attaches the game's existing grenade explosion to
                            the LAW's hitscan impact, all via reflection
    1191  LawDropHook       without a replacement, DropWeaponFromHand throws a
                            NullReferenceException, which also aborts
                            PlayerDeath before the respawn screen
    1329  ResourceHook      prefix on Resources.Load - serves our own paths,
                            including the TAC-50 scope texture
    1395  Assets            loads meshes and textures from assets\, with cache
    1503  ItemFactory       builds the runtime prefab for one ItemDef
                            - GetModelPrefab      model held in the hand
                            - LoadDonorWeapon     loads the donor weapon
                            - MakeMaterial        shader taken from the donor
                            - CopyDonorComponents SEE THE WARNING BELOW
                            - GetSpawnPrefab      model on the ground/inventory
                            - SetIcon             ItemIcon and WeaponIcon
    2121  Registry          writes the items into the game's databases. Three
                            of them - fill only one and you get
                            "ItemSpawned is null!" and the item vanishes.
                            AddToLootTables decides whether anything spawns.
    2316  WeaponData        reads back what the game parsed from weapons_db.xml
    2419  Diag              logging around ReloadWeapon and backpack access
    2539  Research          scene jumping and other inspection tools. All off
                            by default. A tool for looking, not a feature.
    2775  Turret            the BTR-80A turret: seat, aiming, camera takeover,
                            firing. Several open questions, all needing eyes
                            in the running game.
    4108  Arena             a flat test surface spawned at runtime (F10)
    4310  CameraHook        postfix that runs after the game's own camera
                            update - a prefix was not enough, the view snapped
                            back behind the turret
    4360  ColdHook          body temperature and illness. Setting the value is
                            not enough; it comes back after the next night, so
                            the prefix sets it and skips the original.
    4509  CarSpawn          vehicle spawning. Note SetPartSpawn: in mode 3 it
                            rolls 50/50, and a car without a spark plug does
                            not drive.

**Warning at `CopyDonorComponents`.** Only value-type fields and `string` are
copied. `UnityEngine.Object` references are deliberately excluded - otherwise
the new weapon points at the donor's child objects and shows up as an RPD in
the inventory again. The component itself must still exist: without
`ItemTransformManager`, `WeaponSpineInstanceManager` throws a NullReference and
the character screen hangs. This is the single most expensive mistake in the
project's history. Do not simplify it.

## 3 How an item gets added, start to finish

1. **Pick ids** from the reserved ranges: 1160-1199 weapons, 2050-2099 ammo.
2. **Pick a donor** - an existing weapon of a similar kind. Current table:
   1160 MG42 from 1023 (RPD), 1161 TAC-50 from 1010 (SVD), 1162 M72 LAW from
   1010, and the ammo items 2050/2051/2052 all from 2030 (the 7.62 crate).
3. **Write the generators**: `<name>_mesh.py`, `<name>_texture.py`,
   `<name>_icon.py`. Run `python make_assets.py`.
4. **Add an `ItemDef`** to `BuildItemTable()` in `RevivalPlugin.cs`.
5. **`python verify.py`** - it checks that every asset named in the source
   actually exists, that meshes have no null normals, and that the reflection
   targets resolve.
6. **Build and test**: `powershell -File build.ps1`, then `start_game.ps1`.
7. **The server must learn the id too.** Only Kevin can do this. Until then
   the item reverts to its donor in the inventory - see `CLAUDE.md`.

Localization is automatic: `LocalizationHook` answers `$<id>_Name` and
`$<id>_Descr` from the `ItemDef`, so a missing translation shows up as the raw
`$1160_Name` string rather than a crash.

## 4 Confirmed facts about the game

Short form. Each of these is backed by IL, asset data, or an observation in the
running game.

- Unity 2018.1.0f2, Mono, release build. BepInEx 5.4 with HarmonyX; the CLR is
  v2.0.50727, which is why the compiler is csc 3.5.
- `weapons_db.xml` uses the attribute `ItemID`, not `id`. Weapon rows carry
  damage, spread, rate of fire, reload time, clip item ids, and sound and
  particle resource paths.
- The `.nd` save format is **not** understood. Treat save files as opaque.
- Items live in three separate databases inside the game. Registering in one is
  not enough.
- Weapon scaling and hand placement come from the donor's transform data.
- A mesh with a null normal becomes NaN in the shader and eats the screen as a
  white blob - hence the check in `verify.py`.
- Scopes work through `Resources.Load` plus `ScopeCameraEffect`; no asset
  bundle is needed.
- A region is a `GameRegionData` with `region`, `startScene` and `scenes`, and
  `scenes` are **build indices**. A genuinely new region therefore needs a new
  scene in the build, which is impossible without rebuilding the game. Ten
  unused build scenes exist and can be entered.
- Body temperature, illness and healing are driven by a nightly update that
  overwrites whatever you set - see `ColdHook`.
- Writing back `resources.assets` is **UNKNOWN**. Nobody has managed it. New
  objects and new regions depend on it, so both are blocked.

## 5 What has already been tried

Including the failures - a ruled-out path is worth as much as a success. Do not
retry these without a reason.

    Description text on new items                worked
    Blinding white light in the main menu        cause found and fixed
    Material of our own weapon                   taken from the donor
    Making a weapon bigger                       worked, via transform
    Hand posture                                 worked
    Icons                                        worked, rendered offline
    Own scope without an asset bundle            worked
    Entering unused scenes                       worked
    New weapon turns back into an RPD            cause: Object fields copied
                                                 from the donor. See section 2.
    Turning off Easy Anti-Cheat                  worked, byte pattern patch
    Making the EAC patch survive Steam           partly - Steam overwrites it,
                                                 re-run client_patch.ps1
    "Untrusted system file" after the EAC        the Steam Play button is
    module update of 2026-08-27                  dead. Use start_game.ps1.
    Master server without Unity or PowerShell    worked, runs on Linux/Mono
    Two simultaneous players on the server       worked
    Server ran but saved nothing                 a JSON assembly failed to
                                                 load; the service looked
                                                 healthy the whole time
    First real client login against the VPS      worked, 2026-08-27

## 6 The server, seen from the client side

You do not have access to it and do not need it.

    Address     187.124.117.145
    Port 12080  server list, plain HTTP
    Port 12081  the master protocol itself

What matters for your work: on login the server sends the client its own
`weapons_db.xml` and `skills_db.xml`. Those win over anything the plugin
thinks. That is the mismatch problem described in `CLAUDE.md`, and there is a
plan to make the client detect it and say so in plain English instead of
silently showing the wrong weapon.

`client_patch.ps1` writes the address into `ClientConfig.ini`. If the server
ever moves, every client has to be patched once - that is why the address lives
in exactly one place in the script.

## 7 Running the game and reading the logs

    powershell -File start_game.ps1     the only correct way. Steam must be
                                        running. Never the Play button.

Logs, in the order you usually want them:

    <game>\BepInEx\LogOutput.log        our plugin. Every line the mod writes
                                        is here, prefixed with the plugin name.
    %USERPROFILE%\AppData\LocalLow\SOFF Games\Next Day Survival\output_log.txt
                                        the game's own log - Unity exceptions,
                                        connection failures, missing resources.
                                        Unity 2018 calls it output_log.txt,
                                        NOT Player.log - that name came in
                                        with a later Unity version and will
                                        send you looking for a file that does
                                        not exist.

Do not read either one by hand first. Run:

    python playlog.py            verdicts from the last session, plus errors
    python playlog.py --roh      every line the plugin wrote, verbatim
    python playlog.py --unity    also the exceptions from output_log.txt

It answers what can be answered without eyes: which camera was taken over,
where the test surface got its material, whether the turret drew ammunition,
whether a hit did damage. What it cannot answer - does it look right, does it
point the right way - still needs a person watching.

If the plugin does not appear in `LogOutput.log` at all, BepInEx did not load
it: check that the DLL is in `BepInEx\plugins\`, then check the EAC patch.

## 8 What is deliberately not in this repository

- The master server itself, its deploy scripts, and any access to it.
- The research tooling that reads the game's IL and asset files, and the
  extracted game data it produces. That data belongs to SOFF Games.
- `Assembly-CSharp.dll` and anything else from the game. `client_patch.ps1`
  computes the patch on the player's own installation and ships no game code.
  **Any future updater must keep this property.**

If you find yourself needing one of these, that is a question for Kevin, not a
gap to fill.
