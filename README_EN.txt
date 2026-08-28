NEXT DAY: SURVIVAL - PLAY AGAIN
Package @VERSION@
Russian version below / Русская версия ниже

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
The mod adds six items of its own: an MG42, a TAC-50 with its own scope, an
M72 LAW that explodes where it hits, and the matching ammunition for all
three. Plus a fix that keeps the mouse cursor from sliding out of the game in
windowed mode.

There is also a gunner seat on the BTR-80A: get in and press G.


YOUR SAVE GAME
--------------
It lives on the server, not on your machine. Your first character is created
when you first log in. Until your game reports something back, it is called
"Bean Battles World Champion" - that is the template new characters are made
from, and it goes away by itself.


================================================================================
   РУССКАЯ ВЕРСИЯ / RUSSIAN VERSION
================================================================================


NEXT DAY: SURVIVAL - СНОВА В ИГРЕ
Пакет @VERSION@

У Next Day: Survival больше нет официального мастер-сервера - студия его
отключила, поэтому кнопка "Играть" больше не пускает в игру. Этот пакет
переключает вашу игру на частный сервер-замену. После этого всё работает как
раньше, через сервер идёт только вход в игру.


КАК ЭТО СДЕЛАТЬ
---------------
1. Steam должен быть запущен, а Next Day: Survival - установлена.
   Сама игра при этом должна быть ЗАКРЫТА.

2. Перенесите эту папку в обычное место на диске, например в "Загрузки".
   НЕ работайте внутри окна архива: Windows только притворяется, что это
   папка, и скрипты не найдут друг друга.

3. Двойной щелчок по 1_EINRICHTEN.bat  (по-немецки "настроить").
   Скрипт сам найдёт игру, всё настроит и в конце скажет, отвечает ли
   сервер. Занимает несколько секунд.

4. Двойной щелчок по 2_SPIELEN.bat  ("играть").
   Это запускает игру. Steam должен быть запущен.

5. В главном меню нажмите "Играть" и выберите сервер из списка.

Готово. Дальше нужен только шаг 4.


ВАЖНО: НЕ ЗАПУСКАЙТЕ ЧЕРЕЗ STEAM
--------------------------------
Зелёная кнопка "Играть" в Steam запускает лаунчер античита, а он прерывается,
как только установлен мод ("Untrusted system file"). У вас ничего не сломано -
просто используйте 2_SPIELEN.bat.


ЕСЛИ WINDOWS РУГАЕТСЯ
---------------------
"Система Windows защитила ваш компьютер"
    Нажмите "Подробнее", затем "Выполнить в любом случае". Windows пишет это
    про любой незнакомый файл из интернета.

"Файл заблокирован" / вообще ничего не происходит
    Правый щелчок по .bat, Свойства, внизу отметьте "Разблокировать", ОК.

"Spielordner wurde nicht gefunden" (папка игры не найдена)
    Игра лежит в другом месте, например на втором диске. В Steam правый
    щелчок по игре, "Управление", "Просмотреть локальные файлы", скопируйте
    путь из адресной строки и запустите так:
        powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Game "ВАШ\ПУТЬ"

"Server nicht erreichbar" (сервер недоступен)
    Сервер сейчас не работает. Сообщите тому, кто дал вам этот пакет. Ваша
    игра тут ни при чём.


ЧТО ИМЕННО МЕНЯЕТСЯ В ИГРЕ
--------------------------
Честно и полностью, чтобы вы знали, на что нажимаете:

1. В ClientConfig.ini записывается адрес сервера-замены.
   Прежняя версия файла сохраняется в резервную копию.

2. Assembly-CSharp.dll меняется в ОДНОМ месте: проверка, включающая античит,
   теперь всегда возвращает "выключено". Без этого мод не загрузится.
   Оригинал сохраняется в резервную копию. Размер файла не меняется,
   переписываются 8 байт, больше ничего.

3. BepInEx попадает в папку игры. Это стандартный инструмент, которым игры на
   Unity загружают моды - он свободно доступен, см. DRITTANBIETER.txt.

4. Сам мод попадает в BepInEx\plugins.

Всё это находится внутри папки с игрой. Ничего в Windows, ничего в реестре,
права администратора не нужны.


КАК ВЕРНУТЬ ВСЁ НАЗАД
---------------------
    powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Restore

Это вернёт прежний адрес сервера. Чтобы убрать и сам мод, удалите
BepInEx\plugins\NextDayRevivalToolkit.dll и папку assets рядом с ним.
А если хотите полностью исходную игру, запустите в Steam "Проверить
целостность файлов игры" - это восстановит нетронутый Assembly-CSharp.dll.


ТОЛЬКО ПОСМОТРЕТЬ, НИЧЕГО НЕ МЕНЯЯ
----------------------------------
    powershell -ExecutionPolicy Bypass -File client_patch.ps1 -Check

Покажет, что уже настроено, а что нет, не трогая ни одного файла.


ЧТО ЕЩЁ ЗДЕСЬ ЕСТЬ
------------------
Мод добавляет шесть своих предметов: MG42, TAC-50 с собственным прицелом,
M72 LAW, которая взрывается в точке попадания, и подходящие боеприпасы ко всем
трём. Плюс исправление, из-за которого курсор мыши больше не уезжает из игры
в оконном режиме.

Кроме того, у БТР-80А есть место наводчика: сядьте в машину и нажмите G.


ВАШ ПЕРСОНАЖ
------------
Он хранится на сервере, а не у вас на компьютере. Первый персонаж создаётся
при первом входе. Пока ваша игра ничего не сообщила обратно, его зовут
"Bean Battles World Champion" - это шаблон, из которого создаются новые
персонажи, и это имя исчезнет само.
