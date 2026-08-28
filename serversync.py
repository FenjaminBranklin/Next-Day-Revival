r"""Prueft, ob dieser Client zum laufenden Masterserver passt.

Die Regel, um die es geht: **jede Item-Id, die das Plugin registriert, muss der
Server kennen.** Kennt er sie nicht, gewinnt der Server - das Item wird im
Inventar zu seinem Spende-Gegenstand, und im Spiel sieht das nach einem Fehler
im Mod aus, nicht nach einem Versionsproblem.

Deshalb ist das hier die Torwaechterin vor jedem oeffentlichen Release. Sie
laeuft an zwei Stellen mit demselben Ergebnis:

    python serversync.py          von Hand, bevor du einen Tag setzt
    .github/workflows/release.yml automatisch, bevor ein Zip entsteht

Rueckgabewerte, damit ein Skript sie auswerten kann:

    0   der Server kennt alles, was das Plugin registriert
    1   dem Server fehlt mindestens eine Id  -> NICHT veroeffentlichen
    2   der Server war nicht erreichbar      -> NICHT veroeffentlichen

**2 ist mit Absicht ein Fehlschlag und kein Achselzucken.** Ein Release, von
dem niemand weiss, ob es zum Server passt, ist genau das, was hier verhindert
werden soll.

    --url <adresse>   anderen Server pruefen (Vorgabe: der VPS)
    --quelle <datei>  andere Quelle fuer die Plugin-Ids
"""

import io
import json
import os
import re
import sys

VORGABE_URL = "http://187.124.117.145:12080/revival.json"
HIER = os.path.dirname(os.path.abspath(__file__))


def plugin_ids(quelle):
    """Die Ids aus BuildItemTable - erstes Argument jedes new ItemDef(."""
    src = io.open(quelle, encoding="utf-8", errors="replace").read()
    return sorted(set(int(m) for m in re.findall(r"new ItemDef\(\s*(\d+)", src)))


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

    meine = plugin_ids(quelle)
    print("  Plugin registriert : %s" % meine)

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

    seine = sorted(set(int(i) for i in daten.get("modItems", [])))
    print("  Server kennt       : %s" % seine)
    version = daten.get("contentVersion", "?")
    print("  Serverstand        : %s" % version)

    fehlend = [i for i in meine if i not in seine]
    ueber = [i for i in seine if i not in meine]

    print("")
    if ueber:
        # Kein Fehler: der Server darf voraus sein. Der Client zeigt die
        # zusaetzlichen Gegenstaende dann nur nicht an.
        print("  HINWEIS  Der Server kennt Ids, die dieser Client nicht hat: %s"
              % ueber)

    if not fehlend:
        print("  OK       Der Server kennt jede Id des Plugins.")
        print("")
        print("Freigegeben.")
        return 0

    print("  FEHLER   Dem Server fehlen: %s" % fehlend)
    print("")
    print("Diese Gegenstaende wuerden bei jedem Spieler im Inventar zu ihrem")
    print("Spende-Gegenstand - im Spiel sieht das nach einem kaputten Mod aus.")
    print("")
    print("Vor dem Release: die Ids in staticdata/weapons_db.xml des Servers")
    print("eintragen und deployen. Erst danach taggen.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
