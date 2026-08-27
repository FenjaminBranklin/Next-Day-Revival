# Next Day: Survival - Befunde aus Assembly und Assets

Stand: 2026-08-27. Alles hier ist aus Assembly-CSharp.dll (IL) und aus
resources.assets (UnityPy + TypeTreeGenerator) gelesen, nicht geraten.
Werkzeuge liegen daneben: ilq.py, mono.py, dump_items.py, dump_prefab.py,
dump_material.py, mesh_dump.py, extract.py, scan_str.py, scan_call.py.

## Lokalisierung

Schluessel sind `$<ItemID>_Name` und **`$<ItemID>_Descr`** - nicht `_Description`.
Belegt in PlayerInventoryManager::AddWeaponItemFromValues
(ldstr "$" + id + ldstr "_Descr" -> GetLocalizationText) und im
Localization_DB-TextAsset. Der bisherige Hook im Plugin bediente `_Description`
und lieferte deshalb nie eine Beschreibung.

## Waffe in der Hand: Transform

ChangeWeaponHelper ruft WeaponTranformManager::ApplyLocalTransformData().
Das setzt **localPosition, localEulerAngles und localScale** der Prefab-Wurzel
aus den serialisierten Feldern der Komponente. Beim RPD (1023):

    localPosition (-0.00116, 0.00817, 0.00112)
    localRotation (356.584, 258.039, 169.966)   Euler-Grad
    localScale    (0.01, 0.01, 0.01)

Folge: die Wurzel-Skalierung, die das Plugin beim Bau setzt, wird beim Anlegen
ueberschrieben. CustomWeapon/Scale war damit wirkungslos, sobald die Waffe in
der Hand landet. Wer die Waffe groesser will, muss den Wert in der kopierten
Komponente aendern.

Im Charakter- und Menuebild laeuft es ueber ItemTransformManager
(PlayerMenuCustomizationManager::WeaponSpineInstanceManager), gleiche Felder.

## RPD-Geometrie als Referenz (Mesh-Einheiten, Wurzelskalierung 0.01)

    Bounds         x -0.055..0.081   y -1.314..1.321   z -0.249..0.239
    Muzzle         (0.034, -1.390, 0.096)
    MuzzleShoot    (0.032, -1.316, 0.097)
    CapsuleSpawner (0.050,  0.118, 0.143)
    Pistolengriff  y 0.50..0.74, faellt bis z -0.24
    Schaftkappe    y 0.99..1.32

LHandIKTarget, LHandIKTargetAiming, Trigger, Shell1 und Shell2 sind NULL,
usingLHandIK = 0. Die linke Hand wird also von der Animation gesetzt, nicht von
Ankern der Waffe. Wer die Handhaltung treffen will, muss das Mesh an der
RPD-Geometrie ausrichten, nicht Anker verschieben.

## Material: was die Spielwaffen wirklich benutzen

osnova (RPD, LOD0), Shader Standard:

    Keywords     _NORMALMAP
    _MainTex     osnova_texture
    _BumpMap     osnova_normal
    _Metallic    0.0
    _Glossiness  0.6
    _MetallicGlossMap  NICHT gesetzt

Ueber alle 1488 Materialien mit _Metallic in resources.assets hat **kein
einziges** tatsaechlich eine _MetallicGlossMap-Textur. 78 Prozent stehen auf
Metallic 0.0. Das Plugin setzte Metallic 0.55 plus eine eigene
Metallic/Gloss-Map und das Keyword - ein Zustand, den kein Spielmaterial hat.

## Munition und Nachladen

GetMaxItemBullets(itemId) =
ItemSpawnCategoriesDB.current.GetItemSpawnedScriptByID(itemId).Bullets.
Die Kapazitaet eines Munitionsitems steht also im Feld Bullets seines
ItemSpawned-Prefabs. 7,62-Kiste 2030: Bullets 100, Gewicht 2.0,
Kategorien 26 (MilitaryAmmunation) und 37 (SpecialAmmunation).

ReloadMagazine nimmt MaxBullets - bulletsInWeapon aus dem Rucksackslot, wartet
ReloadTime Sekunden und traegt die ClipItemID in _weaponsData ein. Welche
Munition passt, entscheidet WeaponAmmo.Clips aus ClipItemID..ClipItemID5 in
weapons_db.xml.

## Schaden und Ruestung

PlayerLifeDataManager::PlayerApplyDamage:

    d = CalculateDamageValueFromDamageData(damage, bodyPart, damageType, unarmed)
        bodyPart 0 (Kopf)  -> x3   (ausser damageType 17/18)
        bodyPart 1 (Rumpf) -> x1
        sonst              -> x0.5
    d = GetPlayerSkillResultPercent(0, d, false)      Skill 0, max 30 % Abzug
    d = DecreaseDamageFromGearRegenerate(d, bodyPart) Ruestung
    Health -= d                                       Health ist auf 0..100 geklemmt

DecreaseDamageFromGearRegenerate: k = ItemRegenerate[slot] / 100, und
**verdoppelt**, wenn das Teil zum Exoskelett gehoert UND die Energie aktiv ist.
Ergebnis = d * (1 - k). Slotzuordnung: bodyPart 0 -> Gear 0, 1 -> Gear 2,
2 und 3 -> Gear 6.

UKB-1-Set: 4017 Helm (Regenerate 48.5), 4316 Oberteil (48.5), 4509 Unterteil
(40), 4603 Handschuhe, 6019 Exoskelett (Rucksackslot, liefert die Energie).
Mit aktiver Energie also bis zu 97 Prozent Schadensreduktion.

Reichweite: DistanceDamageModifier = 1 unterhalb EffectiveRange, 0 oberhalb
MaximumRange, dazwischen cos(pi*t)*0.5+0.5.

## Zielfernrohr

xmlItemsDataManager::DeserealizeWeaponsDB laedt das Attribut Scope per
**Resources.Load(string)** (nicht generisch) und castet auf Texture2D. Der
bestehende Resources.Load-Prefix des Plugins kann also ein eigenes Fadenkreuz
ausliefern.

Gezeichnet wird es in ScopeCameraEffect::OnGUI:
GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height), tex, ScaleMode 1),
also **ScaleAndCrop** ueber den ganzen Bildschirm. Aktiviert wird der Effekt in
CameraAimingSystem::ScopeAimingMode; Voraussetzung laut
CameraSwitch::CantRenderScope ist _weaponFirearmData.Scope != null. ScopeFOV
ist das FOV im Zielmodus (Spiel-Sniper: 10).

Vorlagen im Spiel: SniperScope2k 1920x1920 RGBA, PSO_Scope_ND 1920x1920,
Mosin_Scope 1920x1440. Aussen deckend schwarz, Linse teiltransparent.

## Waffenwerte (weapons_db.xml, vom Masterserver ausgeliefert)

    ShootModes   0 singleOnly, 1 singleAndAuto, 2 autoOnly
    WeaponType   0 normal, 1 shootgun
    TracersMode  0 Missing, 1 Every, 2 EveryThird

Repetierer = ShootModes 0 plus grosse rateOfFire (Mosin 1152: 1.8).
SVD 1010: Damage 100, MaxBullets 10, Spread 0.0015, Eff/Max 1800/3600.

## Regionen

GameRegionsManager::SetupGameRegionsData laedt
Resources.Load<GameRegionsData>("ScriptableObjects/GameRegions").
Der Datensatz GameRegions (Release) enthaelt **eine** Region:

    region 0 (Severoufimsk), startScene 5, scenes [5, 6, 9, 7, 13, 14]

Daneben liegt GameRegions_DEV mit vier Regionen (0, 1, 4, 3).
Enum GameRegion: 0 Severoufimsk, 1 DEV, 2 Uralsk, 3 DEV_Test_Vasya,
4 DEV_Test_Mitya.

Szenenliste aus den BuildSettings (Index = level<N>-Datei):

     0 SplashScene       1 GL_Scene        2 LoadingScene
     3 Bunker_A65        4 GW_Scene_2      5 GW_Scene_3
     6 Catacombs         7 GW_Scene_1      8..17 GW_Scene_1_Chunk_0..9
    18 Underground_Lab

Benutzt werden 5, 6, 7, 9, 13, 14. **Nicht benutzt, aber vollstaendig im Build:
3 Bunker_A65 (190 MB), 4 GW_Scene_2 (846 MB), 18 Underground_Lab (69 MB) sowie
die Chunks 0, 2, 3, 4, 7, 8, 9.**

## Mobs und Loot

NPC_SpawnPoint (MonoBehaviour, in den Szenen): Active, Health, Level, NPCType,
BehaviorPattern, WeaponId, RandomWeaponsGroupId, SpawnCategories,
ItemsInInventoryMaxCount, Appearance-IDs, GuardPoint und WalkPoints, Fraction,
GodMode.

ItemSpawnPoint: PointID, spawnType, SpawnCategories, ItemIDList,
MIN/MAX_SpawnChance, MIN/MAX_RespawnTime, OrderGroup, AmmoSpawnPoints.

ItemSpawnCategoriesDB haelt fuenf Woerterbuecher: SpawnCategoriesDictionary,
DeserealizedSpawnDictionary, ItemSpawnedDictionary,
LootSpawnGlobalModifersNormal (11 Eintraege), LootSpawnGlobalModifersLastSurvivor.

Kategorie-Enum ItemSpawnCategory (Empty = -1, danach fortlaufend):
26 MilitaryAmmunation, 27 MilitaryWeaponFirearm, 37 SpecialAmmunation,
38 SpecialWeaponFirearm, 52 RareItem, 53 RareWeapon, 54 RareClothe.

## Fahrzeuge

Sieben Fahrzeuge in resources.assets, je mit VehicleGameSystem,
RCCCarControllerV2 (Realistic Car Controller), VehicleInventoryManager,
VehicleCharacterController, VehicleNetworkController, VehicleTriggersManager:

    zaz-968            Durability  150   FuelMax  400
    vaz_1111           Durability  150   FuelMax  300
    uaz-3151_police    Durability  200   FuelMax  390
    uaz-3151_military  Durability  200   FuelMax  390
    PAZ-672 (Bus)      Durability  900   FuelMax 1050
    ural-375 (mod)     Durability 1000   FuelMax 3600
    BTR-80A            Durability 2000   FuelMax 4200, supportFuelType 3

RCC-Kennwerte am BTR: engineTorque 11000, maxspeed 100, brake 10000,
steerAngle 40, totalGears 3, antiRoll 2500, downForce 125, automaticGear 1.
