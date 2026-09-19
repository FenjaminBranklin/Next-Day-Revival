"""Build the technical's art from a source model.

WHAT THE RUNTIME EXPECTS. RevivalTechnical.cs asks for these files and falls
back to generated geometry for every one of them that is missing, so the
vehicle works with none of them and gets better with each one:

    technical_body.ndmesh        the vehicle body. When it is there, the
                                 donor UAZ's own body meshes are hidden and
                                 this one is fitted into their measured box.
    technical_diffuse.png        its colour, plus technical_normal.png.
    technical_mg.ndmesh          the machine gun, WITHOUT the body. This is
                                 the part that swivels with the gunner, so it
                                 has to be separate from the body or it cannot
                                 move. Its own atlas is technical_mg_diffuse.png
                                 (+ _normal); two imports cannot share one UV
                                 layout, which is why the gun has its own.
    technical_mount.ndmesh       the pintle under the gun (optional).
    technical_shield.ndmesh      the gun shield (optional).

THE SHORT VERSION. With the model anywhere in the toolkit folder:

    python technical_build.py            find it, split it, build it
    python verify.py

WHERE THE MODEL MAY SIT. assets/src/technical.glb is the canonical place, but
the file does not have to be moved there first: the search also covers the
repository root and one level of subdirectories below it, and takes any .glb or
.gltf whose name SAYS technical. A path given on the command line beats the
search. Every hit is printed, so there is never a doubt about which file was
used. assets/src/*.glb is gitignored (large third-party binaries are kept
locally, see CREDITS.md), so a fresh checkout normally has no model at all -
this script then prints where it looked and exits 0, and a full make_assets.py
run is not broken by it.

WHAT "SAYS TECHNICAL" MEANS, and why it is not just the word. A downloaded
model is called whatever its author called it, and the one delivered for this
vehicle is "pick-up_truck_improvised_fighting_vehicle.glb" - which does not
contain "technical" at all, so the search walked straight past it and the
vehicle kept running on the donor UAZ. The names are therefore FLATTENED (lower
case, every non-alphanumeric character dropped) and matched against several
spellings of the same thing: technical, gun truck, pickup truck, improvised
fighting vehicle. "pick-up_truck_improvised_fighting_vehicle" then matches on
three of them at once. Adding a spelling to NAME_HINTS is how the next donation
is found; renaming the file to technical.glb still works and still wins.

HOW THE GUN IS SPLIT OFF. The gun must be its own mesh or it cannot swivel. The
materials are therefore divided into gun / mount / shield / body, and the
division is made BY MATERIAL NAME: a material whose name carries a word like
"mg", "dshk" or "browning" is the machine gun's. That is a heuristic, so the
full material table and the resulting split are always printed, and either of
the two overrides wins over it:

    python technical_build.py --gun 2,3 --mount 4      for one run
    GUN_MATERIALS = [2, 3]                             permanently, below

    python technical_build.py --list      print the material table and stop
    python technical_build.py --no-auto   no name guessing; only the lists

An empty gun set is not an error: the whole model becomes the body, its gun (if
it has one) sits welded to the truck, and a generated one swivels above it. The
runtime logs exactly that warning.

AXES AND FIT. The importer's axis map is the only orientation control, exactly
as in m7_build.py / mg42real_build.py. The runtime fits the body into the
donor's measured box by LENGTH along the game's +Z, so the model has to come
out of here with its nose along +Z and its roof along +Y. If the vehicle stands
sideways or on its roof in game, change AXES here and rebuild - never in C#.

    python make_assets.py technical      the same build, through the asset run
"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(ROOT, "assets")
SRC_DIR = os.path.join(ASSETS, "src")

# The canonical names, looked for in assets/src first and in this order, so a
# file dropped in as "technical.glb" is found before any search runs.
CANDIDATES = [
    "technical.glb",
    "technical.gltf",
    "technical_pickup.glb",
    "toyota_technical.glb",
    "pick-up_truck_improvised_fighting_vehicle.glb",
]

# The search that follows those names: any model file whose flattened name
# contains one of these, in the directories below. One level deep only - a model
# is delivered into a folder, not buried in a tree - and cheap enough to run
# every time.
#
# FLATTENED means lower case with every non-alphanumeric character removed, so
# one hint covers every spelling of the same name: "pick-up_truck", "pick up
# truck" and "PickupTruck" all flatten to "pickuptruck". The word "technical"
# alone was the whole test until 2026-09-19, and it is why the delivered
# "pick-up_truck_improvised_fighting_vehicle.glb" was never found.
MODEL_EXT = (".glb", ".gltf")
NAME_HINTS = (
    "technical",
    "guntruck",
    "pickuptruck",
    "improvisedfighting",
    "fightingvehicle",
)
SKIP_DIRS = ("build", "research", "docs", "orchestration", "backup_20260827")

# Material indices, when the names cannot be trusted. Anything listed here is
# taken as given and the name guessing below is not consulted for that part.
GUN_MATERIALS = []
MOUNT_MATERIALS = []
SHIELD_MATERIALS = []

# The name guessing. A material is claimed when one of its name TOKENS (the
# name cut at every non-alphanumeric character) equals one of these words -
# token equality and not a substring test, so "gunmetal" is not a gun and
# "bodyplate" is not a shield. Shield and mount are tested before the gun,
# because a gun shield is part of the weapon and would otherwise be swallowed
# by it.
SHIELD_WORDS = ("shield", "gunshield", "armorplate", "armourplate")
MOUNT_WORDS = ("pintle", "mount", "tripod", "swivel", "cradle", "yoke",
               "turret", "ring")
GUN_WORDS = ("mg", "hmg", "lmg", "gun", "machinegun", "weapon", "dshk",
             "dshkm", "nsv", "kord", "pk", "pkm", "pkt", "browning", "m2",
             "m2hb", "m240", "minigun", "cal50", "50cal")

# Source axis map -> game axes. "x,y,z" is the identity. The game frame used
# here is: +Z forward (the nose), +Y up. Typical fixes: a Blender export is
# usually Z-up, which is "x,z,-y"; a model facing -Z is "-x,y,-z".
AXES = "x,y,z"

# Target box in game units for each part. The runtime rescales the BODY into
# the donor's own measured box, so the body's box here only has to be sane and
# proportional; the gun, mount and shield are placed by the runtime in METRES,
# so their boxes are real metres.
FIT_BODY = (9.9, 9.9, 4.6)      # bind on length: ~4.6 m of pickup
FIT_GUN = (9.9, 9.9, 1.15)      # ~1.15 m of machine gun, muzzle at +Z
FIT_MOUNT = (9.9, 0.95, 9.9)    # pintle height
FIT_SHIELD = (0.75, 9.9, 9.9)   # shield width

# The importer also writes an icon and a metal/rough pair for every part. A
# vehicle has no inventory icon and the runtime reads neither map, and an
# unreferenced file under assets\ is a verify.py hint and an extra entry in the
# launch receipt - so they are removed again right after the import.
SPARE_SUFFIXES = ["_icon.png", "_metal.png", "_rough.png"]


# ------------------------------------------------------------ finding a model

def search_dirs():
    """assets/src, the toolkit folder itself, and one level below it."""
    dirs = [SRC_DIR, ROOT]
    try:
        names = sorted(os.listdir(ROOT))
    except OSError:
        names = []
    for name in names:
        path = os.path.join(ROOT, name)
        if not os.path.isdir(path):
            continue
        if name.startswith(".") or name.lower() in SKIP_DIRS:
            continue
        dirs.append(path)
    out = []
    seen = set()
    for d in dirs:
        key = os.path.normcase(os.path.abspath(d))
        if key in seen or not os.path.isdir(d):
            continue
        seen.add(key)
        out.append(d)
    return out


def flat(name):
    """A file name with everything but its letters and digits taken out, so
    "pick-up_truck", "pick up truck" and "PickupTruck" are one string."""
    return "".join(c for c in name.lower() if c.isalnum())


def says_technical(name):
    """Does this file name name THIS vehicle? Any of the spellings, flattened."""
    low = name.lower()
    if not low.endswith(MODEL_EXT):
        return False
    f = flat(os.path.splitext(low)[0])
    for hint in NAME_HINTS:
        if hint in f:
            return True
    return False


def found_models():
    """Every model file in the search path whose name says "technical"."""
    hits = []
    for d in search_dirs():
        try:
            names = sorted(os.listdir(d))
        except OSError:
            continue
        for name in names:
            if not says_technical(name):
                continue
            hits.append(os.path.join(d, name))
    return hits


def source():
    """The model to build from, or None. The canonical names win, then the
    search, and the first hit is used - the rest are only reported."""
    for name in CANDIDATES:
        p = os.path.join(SRC_DIR, name)
        if os.path.exists(p):
            return p
    hits = found_models()
    if not hits:
        return None
    if len(hits) > 1:
        print("technical_build: several models match, using the first:")
        for h in hits:
            print("    " + shown(h))
    return hits[0]


def no_source():
    print("technical_build: no source model found. Looked for:")
    for name in CANDIDATES:
        print("    " + os.path.join("assets", "src", name))
    print("and for %s whose name says %s, in:"
          % ("/".join(MODEL_EXT), " / ".join(NAME_HINTS)))
    for d in search_dirs():
        print("    " + (shown(d) or "."))
    print("Nothing built. The technical runs on generated geometry and the")
    print("donor UAZ body until a model is dropped in - that is a complete,")
    print("drivable vehicle, so this is not an error.")
    return 0


# -------------------------------------------------------- splitting the model

def materials(src):
    """(index, name) for every material in the source, in file order. A model
    without a material table still has one implicit material 0."""
    import gltf_read
    ext = os.path.splitext(src)[1].lower()
    js, _ = (gltf_read._load_glb(src) if ext == ".glb"
             else gltf_read._load_gltf(src))
    table = js.get("materials", [])
    if not table:
        return [(0, "")]
    out = []
    for i, m in enumerate(table):
        out.append((i, (m or {}).get("name", "") or ""))
    return out


def tokens(name):
    """The name cut at every non-alphanumeric character, lower case."""
    out = []
    cur = ""
    for ch in name.lower():
        if ch.isalnum():
            cur += ch
        elif cur:
            out.append(cur)
            cur = ""
    if cur:
        out.append(cur)
    return out


def claims(name, words):
    for t in tokens(name):
        if t in words:
            return True
    return False


def guess(table):
    """(gun, mount, shield) material indices from the material names."""
    gun, mount, shield = [], [], []
    for index, name in table:
        if claims(name, SHIELD_WORDS):
            shield.append(index)
        elif claims(name, MOUNT_WORDS):
            mount.append(index)
        elif claims(name, GUN_WORDS):
            gun.append(index)
    return gun, mount, shield


USAGE = ("usage: technical_build.py [model] [--gun 1,2] [--mount 3] "
         "[--shield 4] [--no-auto] [--list]")


def parse_list(text):
    """"1,2" -> [1, 2]. Returns None when it is not a list of indices, so the
    caller can say so instead of showing a traceback."""
    out = []
    for piece in text.replace(";", ",").split(","):
        piece = piece.strip()
        if not piece:
            continue
        try:
            out.append(int(piece))
        except ValueError:
            return None
    return out


def shown(path):
    """The path as the reader knows it. relpath throws across drives."""
    try:
        return os.path.relpath(path, ROOT)
    except ValueError:
        return path


# ------------------------------------------------------------- the asset pass

def tidy(name):
    """Delete the files the importer writes that this feature does not read."""
    for suffix in SPARE_SUFFIXES:
        p = os.path.join(ASSETS, name + suffix)
        if os.path.exists(p):
            os.remove(p)


def rename_mesh(built, wanted):
    """build_part names the mesh after the atlas; the runtime wants its own
    name. Renaming is safer than a second import: the mesh and its atlas have
    to come out of the SAME call or their UVs do not match."""
    a = os.path.join(ASSETS, built + ".ndmesh")
    b = os.path.join(ASSETS, wanted + ".ndmesh")
    if not os.path.exists(a):
        return False
    if os.path.normcase(a) == os.path.normcase(b):
        return True          # the atlas is already named after the part
    if os.path.exists(b):
        os.remove(b)
    os.rename(a, b)
    return True


def part(src, atlas, mesh_name, mats, fit):
    import textured_import as ti
    ti.build_part(src, atlas, mats, AXES, fit, 1024, (0.0, 0.0), None, 0.5,
                  None, True)
    tidy(atlas)
    if rename_mesh(atlas, mesh_name):
        print("  -> %s.ndmesh (atlas %s_diffuse.png)" % (mesh_name, atlas))
    else:
        print("  !! %s.ndmesh not written" % mesh_name)


def main(argv):
    given = None
    cli_gun = cli_mount = cli_shield = None
    auto = True
    only_list = False

    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--no-auto":
            auto = False
        elif a == "--list":
            only_list = True
        elif a in ("--gun", "--mount", "--shield"):
            if i + 1 >= len(argv):
                print("technical_build: %s needs a list of material indices" % a)
                return 2
            i += 1
            value = parse_list(argv[i])
            if value is None:
                print("technical_build: %s wants material indices like 2,3 - "
                      "got \"%s\"" % (a, argv[i]))
                return 2
            if a == "--gun":
                cli_gun = value
            elif a == "--mount":
                cli_mount = value
            else:
                cli_shield = value
        elif a.startswith("-"):
            print("technical_build: unknown option %s" % a)
            print(USAGE)
            return 2
        elif given is None:
            given = a
        else:
            print("technical_build: more than one model path given")
            return 2
        i += 1

    if given is not None and not os.path.exists(given):
        print("technical_build: %s does not exist" % given)
        return 2
    src = given if given is not None else source()
    if src is None:
        return no_source()

    print("technical_build: %s" % shown(src))
    table = materials(src)
    print("materials:")
    for index, name in table:
        print("  %3d  %s" % (index, name or "(unnamed)"))

    auto_gun, auto_mount, auto_shield = guess(table) if auto else ([], [], [])

    def pick(cli, const, guessed, label):
        if cli is not None:
            print("%s: %s (command line)" % (label, cli))
            return cli
        if const:
            print("%s: %s (%s_MATERIALS)" % (label, const, label.upper()))
            return list(const)
        if guessed:
            print("%s: %s (from the material names)" % (label, guessed))
            return guessed
        return []

    gun = sorted(set(pick(cli_gun, GUN_MATERIALS, auto_gun, "gun")))
    mount = sorted(set(pick(cli_mount, MOUNT_MATERIALS, auto_mount, "mount")))
    shield = sorted(set(pick(cli_shield, SHIELD_MATERIALS, auto_shield,
                             "shield")))
    taken = set(gun) | set(mount) | set(shield)
    every = [index for index, _ in table]
    body = [m for m in every if m not in taken]
    print("body: %s" % body)

    if only_list:
        print("technical_build: --list, nothing built.")
        return 0

    if not body:
        print("technical_build: every material was claimed by a part - there is")
        print("nothing left for the body. Check GUN/MOUNT/SHIELD_MATERIALS or")
        print("pass --gun/--mount/--shield explicitly.")
        return 1

    # The body carries the shared atlas name "technical", because the runtime's
    # body material reads technical_diffuse.png / technical_normal.png.
    part(src, "technical", "technical_body", body, FIT_BODY)

    if gun:
        part(src, "technical_mg", "technical_mg", gun, FIT_GUN)
    else:
        print("gun: no material claimed - the model's own gun (if it has one)")
        print("     stays welded to the body and the generated, swivelling gun")
        print("     is built on top of it. Pick the gun's materials from the")
        print("     table above with --gun to get the model's gun to turn.")

    if mount:
        part(src, "technical_mount", "technical_mount", mount, FIT_MOUNT)
    if shield:
        part(src, "technical_shield", "technical_shield", shield, FIT_SHIELD)

    print("technical_build: done. Run verify.py, then build.ps1.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
