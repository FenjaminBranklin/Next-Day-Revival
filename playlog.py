r"""Wertet aus, was der letzte Spielstart im Log hinterlassen hat.

Die Abnahme im Spiel ist der Engpass dieses Projekts: sie kostet einen Start,
Augen und Zeit. Ein grosser Teil davon steht aber ohnehin im Log - welche
Kamera uebernommen wurde, aus welcher Quelle die Testflaeche ihr Material hat,
ob Munition gezogen wurde, ob ein Treffer Schaden gemacht hat. Dieses Skript
liest das heraus und beantwortet, was ohne Augen zu beantworten ist.

    python playlog.py             letzte Sitzung, Urteile und Fehler
    python playlog.py --alles     alle Sitzungen im Log
    python playlog.py --roh       zusaetzlich jede Revival-Zeile im Wortlaut
    python playlog.py --unity     zusaetzlich die Ausnahmen aus output_log.txt

Was es NICHT kann: alles, was man sehen muss. Ob die Flaeche schwebt, ob der
Turm in die richtige Richtung dreht, ob eine Textur gut aussieht. Diese Punkte
bleiben in TASKS.md unter "Abnahme im Spiel" - das Skript kuerzt die Liste, es
ersetzt sie nicht.
"""

import io
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

MARKE = "Next Day Revival Toolkit]"
SITZUNGSSTART = "BepInEx 5."


def spielpfad():
    """Gleiche Suche wie verify.py: Registry, dann die Steam-Bibliotheken."""
    kandidaten = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = winreg.QueryValueEx(k, "SteamPath")[0]
    except Exception:
        steam = ""
    for basis in [steam, r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"]:
        if not basis:
            continue
        kandidaten.append(os.path.join(basis, "steamapps", "common", "Next Day Survival"))
        vdf = os.path.join(basis, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            txt = io.open(vdf, encoding="utf-8", errors="replace").read()
            for m in re.finditer(r'"path"\s+"([^"]+)"', txt):
                kandidaten.append(os.path.join(m.group(1).replace("\\\\", "\\"),
                                               "steamapps", "common", "Next Day Survival"))
    for k in kandidaten:
        if os.path.exists(os.path.join(k, "nextday_game.exe")):
            return k
    return ""


def unity_log():
    """Unity 2018 schreibt output_log.txt, NICHT Player.log."""
    p = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow",
                     "SOFF Games", "Next Day Survival", "output_log.txt")
    return p if os.path.exists(p) else ""


def lies(pfad):
    return io.open(pfad, encoding="utf-8", errors="replace").read().splitlines()


def sitzungen(zeilen):
    """Zerlegt das Log an den BepInEx-Startbannern in Sitzungen."""
    grenzen = [i for i, z in enumerate(zeilen) if SITZUNGSSTART in z]
    if not grenzen:
        return [zeilen]
    grenzen.append(len(zeilen))
    return [zeilen[grenzen[i]:grenzen[i + 1]] for i in range(len(grenzen) - 1)]


# ------------------------------------------------------------------ Urteile
#
# Je Eintrag: Titel, das Muster, und was ein Treffer bedeutet. Wer eine neue
# Funktion ins Plugin baut, haengt hier eine Zeile an - dann taucht sie beim
# naechsten Spielstart von selbst im Bericht auf.

URTEILE = [
    ("Plugin geladen",
     r"NEXT DAY REVIVAL TOOLKIT ([0-9.]+)",
     "Version {0}"),
    ("Item-Tabelle",
     r"Item-Tabelle: (\d+) Eintraege",
     "{0} Items registriert"),
    ("Assets",
     r"(Mesh|Textur) geladen",
     "{0} geladen"),
    ("Loot-Tabellen",
     r"Loot[^\n]*",
     "{0}"),
    ("Waffendaten vom Server",
     r"WAFFENDATEN (\d+)[^\n]*",
     "Id {0} in der Datenbank des Servers gefunden"),
    ("Geschuetzsitz",
     r"Geschuetzsitz an BTR[^\n]*",
     "{0}"),
    ("Geschuetzkamera",
     r"Geschuetzkamera uebernommen: ([^\n]+)",
     "{0}"),
    ("Geschuetz: Munition",
     r"Geschuetz: Munition ([^\n]+)",
     "{0}"),
    ("Geschuetz: Treffer",
     r"Geschuetztreffer: ([^\n]+)",
     "{0}"),
    ("Testflaeche",
     r"Testflaeche gebaut: ([^\n]+)",
     "{0}"),
    ("Fahrzeugspawn",
     r"Fahrzeugspawn: ([^\n]+)",
     "{0}"),
    ("Szenensprung",
     r"(Szene[^\n]*gewechselt[^\n]*|Sprung[^\n]*)",
     "{0}"),
    ("Erkaeltung",
     r"(Cold[^\n]*|Erkaeltung[^\n]*)",
     "{0}"),
]


def urteile(revival):
    text = "\n".join(revival)
    print("Urteile aus dem Log")
    print("-" * 74)
    gefunden = 0
    for titel, muster, form in URTEILE:
        treffer = re.findall(muster, text)
        if not treffer:
            print("  offen   %-24s keine Zeile dazu im Log" % titel)
            continue
        gefunden += 1
        # Mehrfachtreffer zusammenfassen: der letzte zaehlt, die Zahl daneben.
        letzter = treffer[-1]
        if isinstance(letzter, tuple):
            letzter = " ".join([t for t in letzter if t])
        text_aus = form.format(letzter if not letzter.isdigit() else letzter)
        zusatz = ("  (%dx)" % len(treffer)) if len(treffer) > 1 else ""
        print("  OK      %-24s %s%s" % (titel, text_aus[:200], zusatz))
    print()
    return gefunden


def fehler(alle, revival):
    print("Fehler und Warnungen")
    print("-" * 74)
    n = 0
    for z in alle:
        if "[Error" in z or "[Fatal" in z:
            print("  FEHLER  " + z.strip()[:200])
            n += 1
    for z in revival:
        if "[Warning" in z or "LogWarning" in z:
            print("  WARNUNG " + z.strip()[:200])
            n += 1
    if n == 0:
        print("  keine")
    print()
    return n


def unity_ausnahmen():
    p = unity_log()
    print("Unity-Log (output_log.txt)")
    print("-" * 74)
    if not p:
        print("  nicht gefunden - Unity 2018 schreibt output_log.txt unter")
        print("  %LOCALAPPDATA%\\..\\LocalLow\\SOFF Games\\Next Day Survival\\")
        print()
        return
    zeilen = lies(p)
    treffer = [z for z in zeilen
               if "Exception" in z or "NullReference" in z or "Error" in z]
    if not treffer:
        print("  keine Ausnahmen")
    else:
        print("  %d Zeilen mit Exception/Error, die letzten zehn:" % len(treffer))
        for z in treffer[-10:]:
            print("    " + z.strip()[:200])
    print()


def main():
    game = spielpfad()
    if not game:
        print("Spielordner nicht gefunden.")
        return 1
    log = os.path.join(game, "BepInEx", "LogOutput.log")
    if not os.path.exists(log):
        print("LogOutput.log fehlt - lief das Spiel schon einmal mit BepInEx?")
        return 1

    zeilen = lies(log)
    teile = sitzungen(zeilen)
    if "--alles" not in sys.argv:
        zeilen = teile[-1]

    revival = [z for z in zeilen if MARKE in z]

    print("=" * 74)
    print("Spiellog-Auswertung   %d Sitzung(en) im Log, ausgewertet: %s"
          % (len(teile), "alle" if "--alles" in sys.argv else "die letzte"))
    print("%s" % log)
    print("=" * 74)
    print("  %d Zeilen, davon %d vom Plugin" % (len(zeilen), len(revival)))
    print()

    if not revival:
        print("Keine einzige Zeile vom Plugin. Das heisst fast immer: BepInEx hat")
        print("es nicht geladen. Erst pruefen, ob die DLL in BepInEx\\plugins liegt,")
        print("dann den EAC-Patch.")
        return 1

    urteile(revival)
    n = fehler(zeilen, revival)

    if "--unity" in sys.argv:
        unity_ausnahmen()

    if "--roh" in sys.argv:
        print("Alle Plugin-Zeilen im Wortlaut")
        print("-" * 74)
        for z in revival:
            print("  " + z.strip()[:200])
        print()

    print("=" * 74)
    print("Was hier NICHT steht, muss weiter mit Augen abgenommen werden -")
    print("Liste in docs/ai/TASKS.md unter 'Abnahme im Spiel'.")
    return 1 if n else 0


if __name__ == "__main__":
    sys.exit(main())
