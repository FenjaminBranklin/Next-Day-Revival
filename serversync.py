r"""Prueft, ob dieser Client zum laufenden Masterserver passt.

Die Regel, um die es geht: **jede Waffe, die das Plugin registriert, muss in
der weapons_db.xml des Servers stehen - mit demselben Magazin.** Steht sie
nicht dort, gewinnt der Server: die Waffe wird im Inventar zu ihrem
Spende-Gegenstand. Im Spiel sieht das nach einem kaputten Mod aus, nicht nach
einem Versionsproblem.

Deshalb ist das hier die Torwaechterin vor jedem oeffentlichen Release. Sie
laeuft an zwei Stellen mit demselben Ergebnis:

    python serversync.py          von Hand, bevor du einen Tag setzt
    .github/workflows/release.yml automatisch, bevor ein Zip entsteht

Rueckgabewerte:

    0   der Server kennt jede Waffe des Plugins, mit passendem Magazin
    1   es fehlt etwas oder ein Magazin weicht ab  -> NICHT veroeffentlichen
    2   der Server war nicht erreichbar            -> NICHT veroeffentlichen

**2 ist mit Absicht ein Fehlschlag und kein Achselzucken.** Ein Release, von
dem niemand weiss, ob es zum Server passt, ist genau das, was hier verhindert
werden soll.

## Was NICHT geprueft wird, und warum

Munitions-Ids (2050, 2051, 2052) haben in `weapons_db.xml` **keinen eigenen
Eintrag**. Sie tauchen dort nur als `ClipItemID` einer Waffe auf. Die Items
selbst traegt das Plugin clientseitig in die Item-Datenbanken ein.

Das ist wichtig, weil es leicht falsch gemessen wird: die Zeichenfolge
`ClipItemID="2051"` **enthaelt** `ItemID="2051"`. Wer mit einem zu einfachen
Muster sucht, findet Munitions-Ids als vermeintliche Eintraege und zaehlt
Gegenstaende, die es dort gar nicht gibt. Genau dieser Fehler ist am
2026-08-28 passiert und hat einen halben Abend lang eine falsche Luecke
behauptet. Deshalb steht hier ueberall ein `(?<![A-Za-z])` vor `ItemID`.

    --url <adresse>   anderen Server pruefen (Vorgabe: der VPS)
    --quelle <datei>  andere Quelle fuer die Plugin-Daten
"""

import io
import json
import os
import re
import sys

VORGABE_URL = "http://187.124.117.145:12080/revival.json"
HIER = os.path.dirname(os.path.abspath(__file__))

# Reservierte Bereiche des Toolkits. Alles ausserhalb gehoert dem Spiel.
WAFFEN_VON, WAFFEN_BIS = 1160, 1199


def plugin_waffen(quelle):
    """Aus BuildItemTable: {WaffenId: MagazinId} fuer jede eigene Waffe.

    Ein Aufruf sieht so aus, ueber mehrere Zeilen:

        new ItemDef(
            1160, 1023, true,
            "MG42", "...", "mg42.ndmesh", ...,
            200, 2050, 12.0f)

    Die ganzzahligen Literale in dieser Reihenfolge sind Id, Spende-Id,
    Schusszahl und Magazin-Id. `true` an dritter Stelle heisst Waffe.
    """
    src = io.open(quelle, encoding="utf-8", errors="replace").read()
    waffen = {}
    for block in re.findall(r"new ItemDef\((.*?)\)\s*\)", src, re.S):
        ist_waffe = re.search(r"^\s*\d+\s*,\s*\d+\s*,\s*true\s*,", block) is not None
        # Die Beschreibungstexte stecken voller Zahlen - "Maschinengewehr 42",
        # "7,62 mm", ".50 BMG". Ohne dieses Wegschneiden landen die in der
        # Liste und die Magazin-Id ist Unsinn.
        block = re.sub(r'"[^"]*"', '""', block)
        ints = [int(z) for z in re.findall(r"(?<![\w.])(\d+)(?![\w.])", block)]
        if len(ints) < 4:
            continue
        if not ist_waffe:
            continue
        if not (WAFFEN_VON <= ints[0] <= WAFFEN_BIS):
            continue
        waffen[ints[0]] = ints[3]
    return waffen


def server_daten(url, sekunden):
    try:
        try:
            from urllib.request import urlopen
        except ImportError:
            from urllib2 import urlopen
        roh = urlopen(url, timeout=sekunden).read()
        if not isinstance(roh, str):
            roh = roh.decode("utf-8", "replace")
        return json.loads(roh), None
    except Exception as ex:
        return None, str(ex)


def server_waffen(daten):
    """modWeapons ist eine Liste von {"id": 1160, "clip": 2050}."""
    aus = {}
    for e in daten.get("modWeapons", []):
        try:
            aus[int(e["id"])] = int(e.get("clip", 0))
        except (KeyError, TypeError, ValueError):
            continue
    return aus


def main():
    url = VORGABE_URL
    if "--url" in sys.argv:
        url = sys.argv[sys.argv.index("--url") + 1]
    quelle = os.path.join(HIER, "RevivalPlugin.cs")
    if "--quelle" in sys.argv:
        quelle = sys.argv[sys.argv.index("--quelle") + 1]

    print("=" * 74)
    print("Abgleich Client gegen Masterserver")
    print("=" * 74)

    meine = plugin_waffen(quelle)
    print("  Plugin registriert : %s"
          % ", ".join("%d (Magazin %d)" % (k, meine[k]) for k in sorted(meine)))

    daten, fehler = server_daten(url, 10)
    if daten is None:
        print("  Server             : NICHT ERREICHBAR")
        print("                       %s" % url)
        print("                       %s" % fehler)
        print("")
        print("Kein Release. Ein Paket, von dem niemand weiss, ob es zum Server")
        print("passt, gehoert nicht zu den Leuten da draussen.")
        print("")
        print("Fehlt die Route noch, steht der Bauplan in")
        print("docs/ai/tasks/revival-json.md.")
        return 2

    seine = server_waffen(daten)
    print("  Server kennt       : %s"
          % (", ".join("%d (Magazin %d)" % (k, seine[k]) for k in sorted(seine))
             or "nichts im Modbereich"))
    print("  Serverstand        : %s" % daten.get("contentVersion", "?"))
    print("")

    fehlend = [i for i in sorted(meine) if i not in seine]
    abweichend = [(i, meine[i], seine[i]) for i in sorted(meine)
                  if i in seine and seine[i] != meine[i]]
    ueber = [i for i in sorted(seine) if i not in meine]

    if ueber:
        # Kein Fehler: der Server darf voraus sein.
        print("  HINWEIS  Der Server kennt Waffen, die dieser Client nicht "
              "hat: %s" % ueber)

    if not fehlend and not abweichend:
        print("  OK       Der Server kennt jede Waffe des Plugins.")
        print("")
        print("Freigegeben.")
        return 0

    for i in fehlend:
        print("  FEHLER   Waffe %d fehlt in der weapons_db.xml des Servers." % i)
    for i, meins, seins in abweichend:
        print("  FEHLER   Waffe %d: Plugin erwartet Magazin %d, Server sagt %d."
              % (i, meins, seins))
    print("")
    print("Eine fehlende Waffe wird bei JEDEM Spieler zu ihrem Spende-Gegenstand.")
    print("Ein falsches Magazin laesst sie nicht nachladen.")
    print("")
    print("Vor dem Release: Eintraege in staticdata/weapons_db.xml des Servers")
    print("ergaenzen und deployen. Erst danach taggen.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
