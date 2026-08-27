NEXT DAY: SURVIVAL - PLAY AGAIN
Package @VERSION@

Next Day: Survival has no official master server any more - the studio shut it
down, which is why "Play" no longer gets you into the game. This package points
your game at a private replacement server. After that everything works like it
used to; only the login goes through that server.


HOW TO
------
1. Have Steam running and Next Day: Survival installed ONCE.
   The game itself must be CLOSED.

2. Move this folder somewhere real on your disk, for example to Downloads.
   Do NOT work inside the zip window: Windows only pretends it is a folder,
   and the scripts will not find each other in there.

3. Double-click 1_EINRICHTEN.bat  (that is German for "set up").
   It finds your game on its own, sets everything up and tells you at the end
   whether the server answers. Takes a few seconds.

4. Double-click 2_SPIELEN.bat  ("play").
   That starts the game. Steam has to be running.

5. In the main menu click Play, then pick the server from the list.

Done. From now on step 4 is all you need.


IMPORTANT: DO NOT START THROUGH STEAM
-------------------------------------
Steam's green Play button starts an anti-cheat launcher, and that one aborts
as soon as the mod is installed ("Untrusted system file"). Nothing is broken
on your machine - just use 2_SPIELEN.bat.


IF WINDOWS COMPLAINS
--------------------
"Windows protected your PC"
    Click "More info", then "Run anyway". Windows says this about every file
    from the internet it does not recognise.

"The file is blocked" / nothing happens at all
    Right-click the .bat, Properties, tick "Unblock" at the bottom, OK.

"Spielordner wurde nicht gefunden" (game folder not found)
    Your game lives somewhere else, for example on a second drive. In Steam
    right-click the game, Manage, Browse local files, copy the path from the
    address bar and run this instead:
        powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Game "YOUR\PATH"

"Server nicht erreichbar" (server unreachable)
    The server is down right now. Tell whoever gave you this package. It is
    not your game.


WHAT THIS CHANGES IN YOUR GAME
------------------------------
Honestly and completely, so you know what you are clicking:

1. ClientConfig.ini gets the address of the replacement server.
   Your old version is backed up first.

2. Assembly-CSharp.dll is changed in ONE place: a check that switches the
   anti-cheat on now returns a fixed "off". Without that the mod will not
   load. The original is backed up first. File size stays the same, 8 bytes
   are overwritten, nothing else.

3. BepInEx goes into the game folder. That is the standard tool Unity games
   use to load mods - freely available, see DRITTANBIETER.txt.

4. The mod itself goes into BepInEx\plugins.

All of it lives in your game folder. Nothing in Windows, nothing in the
registry, no administrator rights.


UNDO
----
    powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Restore

That puts the server address back. To also remove the mod, delete
BepInEx\plugins\NextDayRevivalToolkit.dll and the assets folder next to it.
And if you want the game fully stock again, run "Verify integrity of game
files" in Steam - that restores the untouched Assembly-CSharp.dll.


JUST LOOK, CHANGE NOTHING
-------------------------
    powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Check

Tells you what is set up and what is not, without touching a single file.


WHAT ELSE IS IN HERE
--------------------
The mod adds four items of its own: an MG42, a TAC-50 with its own scope, and
the matching ammunition. Plus a fix that keeps the mouse cursor from sliding
out of the game in windowed mode.


YOUR SAVE GAME
--------------
It lives on the server, not on your machine. Your first character is created
when you first log in. Until your game reports something back, it is called
"Bean Battles World Champion" - that is the template new characters are made
from, and it goes away by itself.
