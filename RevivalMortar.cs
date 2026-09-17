// Next Day: Survival - Revival Toolkit
//
// The settlement gun - a truck-mounted self-propelled howitzer standing in every
// settlement, manned by its own crew, loaded by hand and aimed on the game's
// own map screen. Design notes and the seam survey:
// docs/ai/tasks/stationary-artillery.md, and for the crew, the recon drone and
// the automatic fire missions docs/ai/tasks/arty-vehicle-drone.md.
//
// This file is the GUN: the emplacement, the loading, the map fire control,
// the flight of a shell and the impact. Everything standing AROUND it - the
// gunner, the drone operator, the recon drone and the fire missions the drone
// buys - is RevivalArtyBattery.cs.
//
// WHAT IT IS, IN THE ORDER THE PLAYER MEETS IT
//
//   1. A gun vehicle stands in the middle of every settlement, on a free, flat
//      patch of ground. It is a LOCAL object built from the imported Bohdana meshes - no network object, no collider.
//      Every client builds the same vehicle in the same place from the same
//      rule. Its turret turns and its barrel elevates.
//   2. TWO MEN belong to it, and by default they are at it: while a crew that
//      is hostile to the player is alive around the gun, the sight is theirs
//      and [F] is refused. Kill them and the gun is his.
//   3. Walking up to a free gun shows "[R] Load" / "[F] Aim". R moves shells
//      (item 2066) out of the backpack into the gun, one press fills it.
//   4. F opens the map and enters AIM MODE: the mouse pointer becomes a
//      crosshair and a highlighted disc around the gun shows how far it
//      reaches, with a second, small ring for the dead zone under it.
//   5. THE CROSSHAIR IS THE GUN, AND A GUN TURNS SLOWLY. The mouse only asks
//      for a point; the crosshair walks towards it at the turret's own
//      traverse rate and the barrel's own elevation rate, and the turret
//      follows the crosshair. Swinging the mouse across the map does not swing
//      the gun across the map - that was the order, and it is what makes a
//      mortar out of a rifle.
//   6. A left click inside the reach fires there. No confirmation button -
//      the click IS the fire order, which is what was asked for.
//   7. A few seconds later the shells land, one after another, inside a
//      dispersion circle around the aim point. They are heard leaving the
//      barrel, heard coming in, and seen going off.
//
// THE FACTION RULE, WHICH IS THE ONE HARD REQUIREMENT
//
// A howitzer is an area weapon, so it WILL kill people from the firing
// player's own faction. That must never turn him into a traitor. Three
// separate things make sure of it, in order of how much they are relied on:
//
//   1. EVERY NPC CASUALTY IS AN ANONYMOUS ONE. The kill is applied through
//      NPC_AI2.ApplyDamage with damageOwnerId 0 (Turret.TryDamage fills every
//      non-damage argument with its type default). Owner 0 is not a player:
//      Revival.NpcCombat.cs:4260 states it plainly - "A real player id is NOT
//      an option: kill credit, counter-attack and settlement hostility would
//      all go to that player." Nobody is credited, so nothing can be held
//      against the shooter. This is the guarantee; the other two are nets.
//   2. THE BLAST ITSELF CARRIES NO DAMAGE. The visible explosion is the
//      game's own networked ExplosionObject (RocketHook.Detonate) and its
//      damage is 0 by default (Mortar/BlastDamage). It is there to be seen
//      and heard. Were it lethal, the kill would belong to whoever spawned
//      the prefab - the firing player - and it would also only ever work
//      where somebody is standing, because a distant settlement's NPCs have
//      their ragdoll collision switched off (Revival.Crew.cs:222-252) and
//      Physics.OverlapSphere does not report them at all.
//   3. A SHIELD ON THE PLAYER'S OWN FACTION FIELD. From the moment a fire
//      mission leaves the tube until a few seconds after the last impact,
//      PlayerStatisticsManager.GetPlayerInfo(localPlayer).fraction is watched;
//      if it flips to Traitor (6) in that window it is written back and the
//      event is logged. HYPOTHESIS, not confirmed IL: it exists because the
//      requirement is absolute and a cheap net costs nothing. Switched off
//      with Mortar/ProtectFaction.
//
// WHERE THE DAMAGE ACTUALLY COMES FROM
//
// All of it is applied by this file, never by the blast:
//   NPCs      NPC_AI2.ApplyDamage (owner 0), falling off to zero at the rim.
//   Vehicles  VehicleGameSystem.ApplyDamage(dmg, 14), MASTER CLIENT ONLY so
//             two clients never bill the same tank twice.
//   Players   the PlayerApplyDamage RPC the patrol gun already uses, sent by
//             the shooter only. A player of the SHOOTER'S OWN faction is
//             skipped outright where both factions can be read.
// A joined client that fires raises one Photon event (Mortar/NetworkEventCode,
// 190 by default) so the master applies the NPC and vehicle sweep for the
// settlements it owns. The SAME event with a fourth float carries a shell the
// NPC crew fired the other way round: the master sends it, and every client
// applies that blast to its OWN player only - which is why an NPC battery can
// wound a player without any client being credited with hurting another.
//
// C# 3.0. Comments, logs and identifiers are ASCII; the player-facing strings
// are bilingual through Loc.T and carry real Cyrillic, so this file is UTF-8
// without a BOM and build.ps1 compiles it with /codepage:65001 (CLAUDE.md).
//
// SEAMS OUTSIDE THIS FILE (four one-liners, marked "NDR settlement mortar"):
//   RevivalPlugin.cs BindConfig     -> Mortar.BindConfig(Config)
//   RevivalPlugin.cs BuildItemTable -> Mortar.AddItems(Items)
//   RevivalPlugin.cs Update         -> Mortar.Tick()
//   RevivalPlugin.cs OnGUI          -> Mortar.Draw()
// and towards the battery around the gun (RevivalArtyBattery.cs):
//   Raise/Place  -> ArtyBattery.GunRaised / ArtyBattery.GunLost
//   Ground       -> ArtyBattery.CrewHoldsGun
//   ArtyBattery  -> Mortar.Lay / Laid / NpcFire / PlayerAiming / PlayerList

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Config, emplacements, loading, the turret, the map fire control and the
    /// impact. The imported vehicle is <see cref="ArtyModel"/>, its crew and
    /// recon drone are <see cref="ArtyBattery"/>, the Photon channel is
    /// <see cref="Mortar.Net"/>, and the faction net is <see cref="FactionShield"/>.
    ///
    /// The class is still called Mortar. The gun grew a hull, a turret and a
    /// crew, but every seam, config section and static check in the repository
    /// is anchored on this name, and a rename would buy nothing but a day of
    /// finding the places it was missed.
    /// </summary>
    public static class Mortar
    {
        // THE SHELL'S ITEM ID. 2001..3000 is the AMMUNITION band, and an item's
        // inventory category comes from its id band alone (the range switch in
        // ItemDataManager::GetItemCatData - see the long note at the head of
        // RevivalAntiTankMine.cs). A howitzer shell is ammunition and is never
        // equipped into a weapon slot, so the ammunition band is the right one
        // and no weapon slot has to light up for it.
        // 2066 is free: research/items.tsv stops at 2034, and the plugin's own
        // ids are 1160-1164, 1490 and 2050-2064. 2065 is deliberately skipped -
        // the mine shipped as 2065 once and an old config file may still name it.
        public const int DEF_SHELL = 2066;
        const int DEF_DONOR = 2030;        // the 7.62 box every ammo item clones

        // ------------------------------------------------------------- config
        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<string> _cfgUseKey;
        static ConfigEntry<string> _cfgLoadKey;
        static ConfigEntry<float> _cfgUseDistance;
        static ConfigEntry<float> _cfgPlaceRange;
        static ConfigEntry<float> _cfgScale;
        static ConfigEntry<bool> _cfgSkipSafe;
        static ConfigEntry<bool> _cfgOpenMap;

        static ConfigEntry<float> _cfgMaxRange;
        static ConfigEntry<float> _cfgMinRange;
        static ConfigEntry<int> _cfgMagazine;
        static ConfigEntry<int> _cfgRounds;
        static ConfigEntry<float> _cfgRoundInterval;
        static ConfigEntry<float> _cfgCooldown;
        static ConfigEntry<float> _cfgDispersion;
        static ConfigEntry<float> _cfgDispersionPercent;
        static ConfigEntry<float> _cfgFlightBase;
        static ConfigEntry<float> _cfgFlightPer100;

        static ConfigEntry<float> _cfgTraverse;
        static ConfigEntry<float> _cfgElevate;
        static ConfigEntry<float> _cfgClearance;

        static ConfigEntry<float> _cfgRadius;
        static ConfigEntry<float> _cfgDamage;
        static ConfigEntry<float> _cfgPlayerDamage;
        static ConfigEntry<float> _cfgVehicleDamage;
        static ConfigEntry<float> _cfgBlastDamage;
        static ConfigEntry<bool> _cfgProtectFaction;
        static ConfigEntry<int> _cfgEventCode;

        static bool Enabled { get { return _cfgEnabled == null || _cfgEnabled.Value; } }
        static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }
        static int I(ConfigEntry<int> c, int fallback) { return c == null ? fallback : c.Value; }

        internal static float MaxRange { get { return Mathf.Max(50f, F(_cfgMaxRange, 1200f)); } }
        internal static float MinRange
        {
            get { return Mathf.Clamp(F(_cfgMinRange, 80f), 0f, MaxRange - 10f); }
        }
        internal static float Radius { get { return Mathf.Max(1f, F(_cfgRadius, 16f)); } }

        /// <summary>Degrees per second the turret turns - and, because the
        /// crosshair may never outrun the gun, the rate the aim point walks
        /// around the gun in aim mode as well.</summary>
        internal static float Traverse { get { return Mathf.Clamp(F(_cfgTraverse, 9f), 0.5f, 90f); } }

        /// <summary>Degrees per second the barrel elevates. It is what limits
        /// how fast the aim point may move TOWARDS or AWAY from the gun, because
        /// range is elevation on a howitzer.</summary>
        internal static float Elevate { get { return Mathf.Clamp(F(_cfgElevate, 5f), 0.5f, 60f); } }

        // The barrel's working arc. The gun is laid by range: MaxRange sits at
        // the bottom of it and MinRange at the top, the way a high-angle weapon
        // is actually fired.
        internal const float ElevLow = 18f;
        internal const float ElevHigh = 65f;

        /// <summary>Metres of range one degree of elevation is worth. Everything
        /// that limits the crosshair radially is derived from this, so the two
        /// rates cannot drift apart.</summary>
        internal static float MetresPerDegree
        {
            get { return (MaxRange - MinRange) / (ElevHigh - ElevLow); }
        }

        // -------------------------------------------------------------- state

        /// <summary>One emplacement. Plain data - the gun is driven from
        /// <see cref="Tick"/>, so it needs no MonoBehaviour of its own and no
        /// per-frame Update per settlement.
        ///
        /// Still called Tube, like the class around it: the type is private to
        /// this file, every method here reads "Tube t", and renaming it would be
        /// a diff with no behaviour in it.</summary>
        class Tube
        {
            public GameObject Go;
            public Transform Turret;   // turns about its local Y
            public Transform Barrel;   // elevates about its local X
            public float Yaw;          // where the turret is now, world degrees
            public float Pitch;        // and where the barrel is now
            public float WantYaw;      // where it is being laid
            public float WantPitch;
            public string Name;
            public int Rounds;         // the PLAYER's shells, loaded by hand
            public float ReadyAt;      // Time.time the next mission may start
            public int SettlementId;
            public Vector3 Centre;     // the settlement centre it belongs to
        }

        /// <summary>A shell between the gun and the ground. Nothing is modelled
        /// in flight: a howitzer round is invisible on the way up, and a tracer
        /// would be both wrong and a networked object per round.</summary>
        class Shell
        {
            public float LeaveAt;
            public float ImpactAt;
            public Vector3 Point;
            public Vector3 From;
            public bool Left;
            public bool Whistled;
            /// <summary>Fired by the NPC crew, not by the player. It decides who
            /// applies the impact: a player's round is his own business, the
            /// crew's round is the master's and goes out to every client so each
            /// of them can hurt its OWN player with it.</summary>
            public bool Npc;
        }

        // How often a settlement's free-ground search may come up empty before
        // it is written off. Eight tries at one second apart is the walk from
        // PlaceRange to the middle of the place; if nothing has been found by
        // then, there really is nowhere to stand a tube.
        const int MaxTries = 8;

        static readonly List<Tube> _tubes = new List<Tube>();
        static readonly List<Shell> _inFlight = new List<Shell>();
        static readonly Dictionary<int, bool> _placed = new Dictionary<int, bool>();
        static readonly Dictionary<int, int> _tries = new Dictionary<int, int>();

        static Tube _aiming;
        static bool _cursorHidden;
        static bool _cursorWas = true;   // the pointer state aim mode took over
        static bool _weOpenedMap;
        static float _aimSince;
        static Vector3 _aimPoint;        // WHERE THE GUN IS LAID - the crosshair
        static bool _aimHave;            // ... once it has been initialised
        static Vector3 _aimWanted;       // the point the mouse is asking for
        static bool _aimWantHave;
        static float _aimStepAt;         // Time.time of the last crosshair step
        static Vector3 _fireTarget;      // the last aim point, drawn on the map
        static float _firedAt;           // Time.time of the last fire order
        static float _nextPlaceScan;
        static string _prompt;
        static string _status;
        static float _statusUntil;

        static KeyCode _useKey = KeyCode.F;
        static KeyCode _loadKey = KeyCode.R;
        static bool _keysParsed;

        static Type _settlementType;
        static Type _npcType;
        static MethodInfo _npcAlive;
        static bool _typesLooked;

        static Texture2D _px;
        static Texture2D _disc;
        static Texture2D _ring;

        // --------------------------------------------------------------- item

        /// <summary>Adds the 122 mm shell to the shared item table. Placeholder
        /// art on purpose: it reuses the 125 mm tank shell's mesh, textures and
        /// icon (shell125.*), which is a fat finned projectile in a box and
        /// reads correctly for a howitzer round. Three shipped items already do
        /// exactly this (the two vehicle modules and the drone battery); a
        /// dedicated arty_* generator is a separate asset job.</summary>
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_SHELL, DEF_DONOR, false,
                "122-мм снаряд (1)", "122 mm howitzer shell (1)",
                "Снаряд калибра 122 мм "
                + "для самоходной гаубицы "
                + "в поселениях. "
                + "Заряжается вручную.",
                "A 122 mm shell for the self-propelled howitzer that stands in the "
                + "settlements. Walk up to a free gun, load it, then aim on the map.",
                "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
                "shell125_icon.png", null,
                1, 0, 15.4f));
        }

        // ------------------------------------------------------------- config

        public static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Mortar", "Enabled", true,
                "The settlement gun: one self-propelled howitzer per settlement, "
                + "loaded with 122 mm shells (item 2066) and aimed on the map "
                + "screen. Its crew and their recon drone are [Artillery].");
            _cfgUseKey = cfg.Bind("Mortar", "UseKey", "F",
                "Pressed at a gun, opens the map and enters aim mode. Pressed "
                + "again (or Escape) leaves it. Refused while the gun's own crew "
                + "is alive - see Artillery/CrewHoldsTheGun.");
            _cfgLoadKey = cfg.Bind("Mortar", "LoadKey", "R",
                "Pressed at a gun, moves 122 mm shells from the backpack into "
                + "it until it is full or the pack is empty.");
            _cfgUseDistance = cfg.Bind("Mortar", "UseDistance", 3.5f,
                "Metres from the gun at which the prompt appears and the keys "
                + "work.");
            _cfgPlaceRange = cfg.Bind("Mortar", "PlaceRange", 250f,
                "A settlement gets its gun the first time a player comes this "
                + "close. Not tidiness: away from every player the whole-map "
                + "TerrainColliders are off (E-059), so a free-ground search out "
                + "there would hit nothing and could not tell a clear patch from "
                + "the inside of a house.");
            _cfgSkipSafe = cfg.Bind("Mortar", "SkipSafeSettlements", false,
                "true leaves the trader camps (IsSafeSettlement) without a gun. "
                + "They never get a crew, a drone or a fire mission either way - "
                + "this only decides whether the vehicle stands there at all.");
            _cfgScale = cfg.Bind("Mortar", "Scale", 1f,
                "Size of the vehicle, 1 = a 6.2 m hull with a 4 m barrel.");
            _cfgOpenMap = cfg.Bind("Mortar", "OpenMapOnAim", true,
                "Let the use key open the map itself through "
                + "UIController.ShowMap(true). false leaves opening the map to "
                + "the player; aim mode then waits for it.");

            _cfgMaxRange = cfg.Bind("Mortar", "MaxRange", 1200f,
                "Reach in metres. A real 122 mm howitzer throws 15 km, but the "
                + "world is 5000 x 5000, so a true-to-life gun would cover the "
                + "whole map from anywhere. 1200 m is the honest compromise: far "
                + "enough to shell the next settlement, short enough that the map "
                + "still has distance in it.");
            _cfgMinRange = cfg.Bind("Mortar", "MinRange", 80f,
                "Dead zone under the gun. A howitzer cannot shoot straight down; "
                + "this also keeps the gunner from killing himself.");
            _cfgMagazine = cfg.Bind("Mortar", "Magazine", 6,
                "How many shells the player may load into a gun. The NPC crew "
                + "has its own supply, Artillery/CrewRounds.");
            _cfgRounds = cfg.Bind("Mortar", "RoundsPerMission", 3,
                "Shells sent per fire order, by the player and by the crew. A "
                + "salvo, not a sniper shot.");
            _cfgRoundInterval = cfg.Bind("Mortar", "RoundInterval", 1.4f,
                "Seconds between two shells leaving the barrel.");
            _cfgCooldown = cfg.Bind("Mortar", "CooldownSeconds", 20f,
                "Seconds after a fire order before the gun accepts the next one.");
            _cfgDispersion = cfg.Bind("Mortar", "Dispersion", 8f,
                "Metres of scatter at any distance. Every shell lands somewhere "
                + "inside Dispersion + DispersionPercent of the range. An NPC "
                + "crew's mission carries Artillery/AimErrorMetres on top.");
            _cfgDispersionPercent = cfg.Bind("Mortar", "DispersionPercent", 2f,
                "Extra scatter as a percentage of the firing distance. 2 percent "
                + "at 1200 m is another 24 m - this is an area weapon.");
            _cfgFlightBase = cfg.Bind("Mortar", "FlightSecondsBase", 4f,
                "Seconds of flight time before distance is counted.");
            _cfgFlightPer100 = cfg.Bind("Mortar", "FlightSecondsPer100m", 0.85f,
                "Extra seconds of flight per 100 m. With the base, 1200 m takes "
                + "about 14 s - long enough for a walking target to leave the "
                + "beaten zone, which is what makes a spotter worth having.");

            _cfgTraverse = cfg.Bind("Mortar", "TraverseDegreesPerSecond", 9f,
                "How fast the turret turns - and with it, how fast the crosshair "
                + "may be swung around the gun in aim mode. The crosshair IS the "
                + "gun's lay: the mouse only asks for a point and the crosshair "
                + "walks there at this rate, so a flick across the map does not "
                + "move the muzzle across the map.");
            _cfgElevate = cfg.Bind("Mortar", "ElevationDegreesPerSecond", 5f,
                "How fast the barrel rises and falls. Range is elevation on a "
                + "howitzer, so this is also how fast the crosshair may be pushed "
                + "away from or pulled towards the gun.");
            _cfgClearance = cfg.Bind("Mortar", "VehicleClearance", 5.6f,
                "Metres of free ground the gun VEHICLE needs around its centre "
                + "before a spot is accepted. The old tube needed 1.6 m; a hull "
                + "six metres long parked inside a shed is the mistake this "
                + "number exists to prevent.");

            _cfgRadius = cfg.Bind("Mortar", "ExplosionRadius", 16f,
                "Metres. The damage falls off to zero at the rim. The patrol "
                + "tank gun already calls 16 m artillery.");
            _cfgDamage = cfg.Bind("Mortar", "Damage", 450f,
                "Damage to an NPC at the centre of the impact, falling off to "
                + "zero at ExplosionRadius. Applied through NPC_AI2.ApplyDamage "
                + "with damage owner 0, so no kill is ever credited to the "
                + "firing player - that is what keeps a friendly casualty from "
                + "turning him into a traitor.");
            _cfgPlayerDamage = cfg.Bind("Mortar", "PlayerDamage", 260f,
                "Damage to a player at the centre, same falloff. A player of the "
                + "shooter's OWN faction is never hit at all.");
            _cfgVehicleDamage = cfg.Bind("Mortar", "VehicleDamage", 700f,
                "Damage to a vehicle at the centre, same falloff, applied on the "
                + "master client only. Deliberately not a guaranteed tank kill: "
                + "it goes through VehicleArmor like any other explosion.");
            _cfgBlastDamage = cfg.Bind("Mortar", "BlastDamage", 0f,
                "Damage of the VISIBLE explosion. 0 on purpose. The blast is the "
                + "game's own ExplosionObject and belongs to whoever spawned it - "
                + "the firing player - so a lethal blast would credit him with "
                + "every casualty. It would also do nothing at all in a "
                + "settlement nobody is standing in, because distant NPCs have "
                + "their ragdoll collision switched off. Raise it only if you "
                + "WANT the shooter blamed for his own shelling.");
            _cfgProtectFaction = cfg.Bind("Mortar", "ProtectFaction", true,
                "Watch the firing player's own faction around every impact and "
                + "write it back if it flips to Traitor. A safety net on top of "
                + "the anonymous damage above, not the main protection.");
            _cfgEventCode = cfg.Bind("Mortar", "NetworkEventCode", 190,
                "Photon event code that asks the master client to apply the "
                + "impact to the NPCs and vehicles it owns. Every other channel "
                + "of the toolkit is below it: troops 160-162, FPV drone "
                + "176-180, turret 181, admin 182, surveillance drone 182-185, "
                + "crew drone 183. Codes from 200 up belong to Photon itself.");
        }

        // --------------------------------------------------------------- tick

        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                Flight();
                FactionShield.Tick();
                Slew();
                GameObject player = MapTools.LocalPlayer();
                if (player == null) { LeaveAim("no player"); _prompt = null; return; }
                Place(player);
                Aim(player);
                if (_aiming == null) Ground(player);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Mortar.Tick: " + ex);
            }
        }

        // ------------------------------------------------------- emplacements

        /// <summary>Gives every settlement near the player its gun. The scan is
        /// a full FindObjectsOfType, so it runs every second while a settlement
        /// in range is still without one and every five otherwise.
        ///
        /// A FAILED attempt is retried, and that is not politeness. Away from
        /// every player the whole-map TerrainColliders are off (E-059), so the
        /// free-ground search finds nothing at all out there and cannot tell a
        /// clear patch from the inside of a house. The first attempt happens at
        /// PlaceRange, which may still be too far; the player is usually walking
        /// closer, so the next attempt is a better one. Only after
        /// <see cref="MaxTries"/> failures is a settlement written off, with one
        /// line in the log saying so.</summary>
        static void Place(GameObject player)
        {
            if (Time.time < _nextPlaceScan) return;

            for (int i = _tubes.Count - 1; i >= 0; i--)
            {
                if (_tubes[i].Go != null) continue;
                // The scene changed under us. Forget the settlement too, so the
                // next scene's own settlements are served again - and drop the
                // shells that were still in the air, or a mission fired outside
                // goes off at those coordinates inside the next scene.
                ArtyBattery.GunLost(_tubes[i].SettlementId);   // NDR settlement artillery
                _placed.Remove(_tubes[i].SettlementId);
                _tries.Remove(_tubes[i].SettlementId);
                _tubes.RemoveAt(i);
                _inFlight.Clear();
            }

            if (!LookUp() || _settlementType == null)
            {
                _nextPlaceScan = Time.time + 5f;
                return;
            }
            float reach = Mathf.Max(30f, F(_cfgPlaceRange, 250f));
            Vector3 me = player.transform.position;
            bool pending = false;

            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_settlementType);
            for (int i = 0; i < all.Length; i++)
            {
                Component s = all[i] as Component;
                if (s == null || s.gameObject == null) continue;
                // Runtime NPC containers are not villages. DropSquad creates
                // Crew.Name for every gun, convoy and insertion; accepting it
                // here recursively creates another gun and another crew.
                if (s.gameObject.name.StartsWith("NDR_", StringComparison.Ordinal)) continue;
                int id = s.gameObject.GetInstanceID();
                if (_placed.ContainsKey(id)) continue;
                Vector3 centre = s.transform.position;
                if (Vector3.Distance(new Vector3(centre.x, 0f, centre.z),
                                     new Vector3(me.x, 0f, me.z)) > reach) continue;
                if (_cfgSkipSafe != null && _cfgSkipSafe.Value && SafeSettlement(s))
                {
                    _placed[id] = true;
                    continue;
                }
                if (Raise(s, centre, id))
                {
                    _placed[id] = true;
                    continue;
                }
                int tries = 0;
                _tries.TryGetValue(id, out tries);
                tries++;
                _tries[id] = tries;
                if (tries >= MaxTries)
                {
                    _placed[id] = true;
                    RevivalPlugin.L.LogWarning("Mortar: no free ground in settlement "
                        + s.gameObject.name + " at " + centre.ToString("0") + " after "
                        + tries + " tries - that settlement keeps no gun.");
                }
                else pending = true;
            }
            _nextPlaceScan = Time.time + (pending ? 1f : 5f);
        }

        static bool SafeSettlement(Component settlement)
        {
            try
            {
                FieldInfo f = AccessTools.Field(settlement.GetType(), "IsSafeSettlement");
                if (f == null || f.FieldType != typeof(bool)) return false;
                return (bool)f.GetValue(settlement);
            }
            catch { return false; }
        }

        /// <summary>Builds one emplacement at the first free, flat patch at or
        /// around the settlement centre. False when there was no ground to
        /// measure - the caller tries again from closer up.</summary>
        static bool Raise(Component settlement, Vector3 centre, int id)
        {
            // Keep the allocation idempotent even if battery setup or logging
            // threw after the model was registered on the previous scan.
            for (int i = 0; i < _tubes.Count; i++)
                if (_tubes[i].SettlementId == id && _tubes[i].Go != null) return true;
            Vector3 spot, normal;
            if (!FreeGround(centre, out spot, out normal)) return false;

            // Park the vehicle facing away from the settlement centre, so it
            // looks like it was driven into position facing outwards rather than
            // into the houses. The turret turns anyway; this is the hull.
            Vector3 out3 = spot - centre;
            out3.y = 0f;
            if (out3.sqrMagnitude < 0.01f) out3 = Vector3.forward;

            Transform turret, barrel;
            GameObject go = ArtyModel.Build(out turret, out barrel);
            if (go == null) return false;
            go.transform.position = spot + normal * 0.02f;
            go.transform.rotation = Quaternion.LookRotation(out3.normalized, Vector3.up);
            // Stand it on the surface rather than through it, exactly as the
            // anti-tank mine does (MineObject.Place).
            go.transform.up = normal;
            float sc = Mathf.Clamp(F(_cfgScale, 1f), 0.2f, 4f);
            go.transform.localScale = new Vector3(sc, sc, sc);

            Tube t = new Tube();
            t.Go = go;
            t.Turret = turret;
            t.Barrel = barrel;
            t.Name = settlement.gameObject.name;
            t.SettlementId = id;
            t.Centre = centre;
            t.Rounds = 0;
            t.ReadyAt = 0f;
            // Laid straight ahead at half reach to begin with, so a gun that is
            // taken over does not start by swinging out of a random direction.
            t.Yaw = go.transform.eulerAngles.y;
            t.WantYaw = t.Yaw;
            t.Pitch = PitchFor((MinRange + MaxRange) * 0.5f);
            t.WantPitch = t.Pitch;
            Point(t);
            _tubes.Add(t);
            _placed[id] = true;

            // NDR settlement artillery. A TRADER CAMP gets the vehicle and
            // nothing else: no crew, no drone, no fire missions. The camps are
            // where the game puts a player in front of a shopkeeper, and an
            // armed crew standing in one would shoot at him over the counter.
            ArtyBattery.GunRaised(id, go, centre, t.Name, SafeSettlement(settlement));

            RevivalPlugin.L.LogInfo("Mortar: gun raised for settlement \"" + t.Name
                + "\" at " + spot.ToString("0") + " (centre " + centre.ToString("0")
                + ", " + Vector3.Distance(centre, spot).ToString("0.0") + " m off).");
            return true;
        }

        /// <summary>The centre first, then rings outwards. A spot qualifies when
        /// the ground under it is flat, nothing hangs over it (so the gun is not
        /// inside a house), no wall stands inside the hull's clearance and the
        /// ground the hull would rest on is level with its centre.
        ///
        /// The offsets are fixed and walked in a fixed order, so two clients
        /// that search the same loaded terrain agree. They may still differ by a
        /// few metres when one of them searched while less of the world was
        /// streamed in, and that is harmless: the gun is a local object, only
        /// the impact point travels over the wire, and nothing about the fire
        /// mission is derived from where the other client drew it.</summary>
        static bool FreeGround(Vector3 centre, out Vector3 spot, out Vector3 normal)
        {
            // TWO passes over the same points, and the second one is the reason
            // every settlement ends up with a gun. The first asks for a tidy
            // emplacement: flat, nothing overhead, no wall inside the hull's own
            // clearance. In a tight village every one of those points can fail
            // on the wall test alone, and "no gun at all" is a worse answer than
            // "a gun close to a wall" - the order was one in EVERY settlement.
            // What the second pass does NOT drop is the roof test: a gun inside
            // a house would fire into the ceiling, which is not a cosmetic
            // problem.
            for (int pass = 0; pass < 2; pass++)
            {
                bool strict = pass == 0;
                if (Clear(centre, strict, out spot, out normal)) return true;
                for (float r = 4f; r <= 24f; r += 4f)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        float a = i * Mathf.PI * 2f / 12f;
                        Vector3 probe = centre + new Vector3(Mathf.Cos(a) * r, 0f,
                                                             Mathf.Sin(a) * r);
                        if (Clear(probe, strict, out spot, out normal)) return true;
                    }
                }
            }
            spot = Vector3.zero;
            normal = Vector3.up;
            return false;
        }

        static bool Clear(Vector3 xz, bool strict, out Vector3 spot, out Vector3 normal)
        {
            spot = Vector3.zero;
            normal = Vector3.up;

            Vector3 point, n;
            GameObject hit = Turret.RaycastObject(new Vector3(xz.x, xz.y + 60f, xz.z),
                                                  Vector3.down, 200f, out point, out n);
            if (hit == null) return false;          // no ground we can measure
            if (n.y < 0.80f) return false;          // a slope, not an emplacement

            // Roof, bridge, container: anything straight above means the gun
            // would fire into a ceiling.
            Vector3 dummy;
            if (Turret.RaycastObject(point + Vector3.up * 0.4f, Vector3.up,
                                     7f, out dummy) != null) return false;

            // A wall inside the hull's clearance at chest height. EIGHT rays,
            // not four: the vehicle is over ten metres long, and four rays can walk a
            // hull straight through the corner between two of them.
            if (strict)
            {
                // Old configs contain the placeholder's 3.6 m clearance.
                float reach = Mathf.Clamp(F(_cfgClearance, 5.6f), 5.6f, 8f);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI * 0.25f;
                    Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    if (Turret.RaycastObject(point + Vector3.up * 0.9f, dir,
                                             reach, out dummy) != null) return false;
                }
                // ... and the ground the hull would rest on has to be as flat as
                // the patch under its centre. A gun standing half over a ditch
                // hangs in the air.
                for (int i = 0; i < 4; i++)
                {
                    float a = i * Mathf.PI * 0.5f;
                    Vector3 probe = point + new Vector3(Mathf.Cos(a) * reach, 0f,
                                                        Mathf.Sin(a) * reach);
                    Vector3 corner;
                    if (Turret.RaycastObject(probe + Vector3.up * 8f, Vector3.down,
                                             30f, out corner) == null) continue;
                    if (Mathf.Abs(corner.y - point.y) > 0.9f) return false;
                }
            }

            spot = point;
            normal = n;
            return true;
        }

        // --------------------------------------------------- standing at a gun

        /// <summary>The prompt and the two keys, while the player is on foot at
        /// a gun and not already aiming.
        ///
        /// The gun is NOT his by default. While a crew that is hostile to him is
        /// alive around it, the sight belongs to the gunner standing at it; the
        /// prompt says so and both keys are refused. That is the whole rule -
        /// kill the crew and the gun is yours.</summary>
        static void Ground(GameObject player)
        {
            _prompt = null;
            Tube t = Nearest(player.transform.position);
            if (t == null) return;

            int cap = Mathf.Clamp(I(_cfgMagazine, 6), 1, 30);
            if (ArtyBattery.CrewHoldsGun(t.SettlementId))   // NDR settlement artillery
            {
                _prompt = Name() + "   " + TextCrewAtGun();
                if (Input.GetKeyDown(Key(true)) || Input.GetKeyDown(Key(false)))
                    Say(TextCrewAtGun());
                return;
            }

            string keys = "[" + Key(true) + "] " + Loc.T("Наводка", "Aim")
                + "   [" + Key(false) + "] " + Loc.T("Зарядить", "Load");
            _prompt = Name() + " " + t.Rounds + "/" + cap + "   " + keys;

            if (Input.GetKeyDown(Key(false))) Load(t, cap);
            else if (Input.GetKeyDown(Key(true))) EnterAim(t);
        }

        // ----------------------------------------------------- the two strings
        //
        // The battery around the gun (RevivalArtyBattery.cs) is written ASCII
        // only, because it is machine-written and a stray BOM would break the
        // BOM-less rule build.ps1 relies on. Its two player-facing lines
        // therefore live here, with the rest of this feature's Russian.

        /// <summary>What the gun is called on screen.</summary>
        internal static string Name()
        {
            return Loc.T("Гаубица", "Howitzer");
        }

        /// <summary>The sight is taken: a living crew is standing at it.</summary>
        internal static string TextCrewAtGun()
        {
            return Loc.T("Расчёт у орудия",
                         "Its crew is at the gun");
        }

        /// <summary>A drone is overhead and this player is under it.</summary>
        internal static string TextSpotted()
        {
            return Loc.T("Разведдрон над вами - уходите",
                         "A recon drone is above you - move");
        }

        static void Load(Tube t, int cap)
        {
            if (t.Rounds >= cap)
            {
                Say(Loc.T("Орудие заряжено полностью",
                          "The gun is full"));
                return;
            }
            int taken = 0;
            while (t.Rounds < cap && Turret.TakeItem(DEF_SHELL, "Mortar"))
            {
                t.Rounds++;
                taken++;
            }
            if (taken == 0)
            {
                Say(Loc.T("Нужны снаряды",
                          "No 122 mm shells in the pack"));
                return;
            }
            RevivalPlugin.L.LogInfo("Mortar: \"" + t.Name + "\" loaded with " + taken
                + " shell(s), " + t.Rounds + " ready.");
            Say(Loc.T("Орудие заряжено",
                      "Gun loaded") + ": " + t.Rounds);
        }

        static Tube Nearest(Vector3 from)
        {
            float best = Mathf.Max(1f, F(_cfgUseDistance, 3.5f));
            best *= best;
            Tube found = null;
            for (int i = 0; i < _tubes.Count; i++)
            {
                Tube t = _tubes[i];
                if (t.Go == null) continue;
                float d = (t.Go.transform.position - from).sqrMagnitude;
                if (d > best) continue;
                best = d;
                found = t;
            }
            return found;
        }

        // ------------------------------------------------------------ aim mode

        static void EnterAim(Tube t)
        {
            _aiming = t;
            _aimSince = Time.time;
            _aimStepAt = Time.time;
            _weOpenedMap = false;
            // The crosshair STARTS where the gun is already pointing. Anything
            // else would mean the first frame of aim mode swings the turret
            // somewhere nobody asked for, and the whole point of the rate limit
            // is that the crosshair and the muzzle are the same thing.
            _aimPoint = LayPoint(t);
            _aimHave = true;
            _aimWantHave = false;
            if (_cfgOpenMap == null || _cfgOpenMap.Value) _weOpenedMap = ShowMap(true);
            if (!_weOpenedMap)
                Say(Loc.T("Откройте карту",
                          "Open the map to aim"));
            RevivalPlugin.L.LogInfo("Mortar: aim mode at \"" + t.Name + "\", "
                + t.Rounds + " ready, reach " + MaxRange.ToString("0") + " m, traverse "
                + Traverse.ToString("0.0") + " deg/s"
                + (_weOpenedMap ? ", map opened by us." : ", waiting for the map."));
        }

        /// <summary>The world point the gun is laid on right now: its own yaw and
        /// the range its elevation stands for.</summary>
        static Vector3 LayPoint(Tube t)
        {
            float range = RangeFor(t.Pitch);
            float rad = t.Yaw * Mathf.Deg2Rad;
            Vector3 gun = t.Go == null ? Vector3.zero : t.Go.transform.position;
            Vector3 flat = new Vector3(gun.x + Mathf.Sin(rad) * range, 0f,
                                       gun.z + Mathf.Cos(rad) * range);
            float y;
            if (!RevivalTroopInsertion.GroundY(flat, out y)) y = gun.y;
            return new Vector3(flat.x, y, flat.z);
        }

        /// <summary>Barrel elevation for a firing range. MaxRange sits at the
        /// bottom of the arc and MinRange at the top: on a high-angle weapon the
        /// nearer target is the steeper shot.</summary>
        internal static float PitchFor(float range)
        {
            float span = Mathf.Max(1f, MaxRange - MinRange);
            float t = Mathf.Clamp01((range - MinRange) / span);
            return Mathf.Lerp(ElevHigh, ElevLow, t);
        }

        /// <summary>The inverse of <see cref="PitchFor"/>.</summary>
        internal static float RangeFor(float pitch)
        {
            float t = Mathf.Clamp01((ElevHigh - pitch) / (ElevHigh - ElevLow));
            return Mathf.Lerp(MinRange, MaxRange, t);
        }

        static void LeaveAim(string why)
        {
            if (_aiming == null) return;
            _aiming = null;
            _aimHave = false;
            _aimWantHave = false;
            ShowCursor();
            if (_weOpenedMap) ShowMap(false);
            _weOpenedMap = false;
            RevivalPlugin.L.LogInfo("Mortar: aim mode left (" + why + ").");
        }

        /// <summary>Aim mode: the crosshair, the exits, and the click that
        /// fires. The click is read here in Update, exactly where MapTeleport
        /// reads its own - OnGUI would see it too, but only on the frames the
        /// GUI is repainted.</summary>
        static void Aim(GameObject player)
        {
            if (_aiming == null) return;
            if (_aiming.Go == null) { LeaveAim("tube gone"); return; }

            float leash = Mathf.Max(1f, F(_cfgUseDistance, 3.5f)) * 2.5f;
            if (Vector3.Distance(player.transform.position,
                                 _aiming.Go.transform.position) > leash)
            { LeaveAim("walked away"); return; }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(Key(true)))
            { LeaveAim("key"); return; }

            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            bool open = MapTools.Context(out manager, out texture, out cam, out world, out map);
            if (!open)
            {
                ShowCursor();
                // We opened it ourselves: it is there within a frame or two, so a
                // short grace is enough and giving up is the honest answer.
                // We could NOT open it (ShowMap is the one untried seam of this
                // feature): then the player has to press the map key himself, and
                // cutting him off after two seconds would look like a bug in the
                // mortar rather than the invitation it is.
                float grace = _weOpenedMap ? 2.5f : 15f;
                if (Time.time - _aimSince > grace) LeaveAim("map not open");
                return;
            }

            HideCursor();

            // THE CROSSHAIR IS THE GUN. The mouse only asks; the lay walks
            // towards what it asks for at the turret's own rate, and the click
            // fires where the gun is actually pointing - never where the pointer
            // happens to be.
            Vector3 wanted;
            _aimWantHave = MapPoint(texture, cam, world, out wanted);
            if (_aimWantHave) _aimWanted = wanted;
            StepLay(_aiming, _aimWantHave ? _aimWanted : _aimPoint);

            if (!Input.GetMouseButtonDown(0)) return;
            if (!_aimHave) return;
            Fire(_aiming, _aimPoint);
        }

        /// <summary>
        /// One frame of laying the gun.
        ///
        /// The aim point is kept in the gun's own polar coordinates - bearing and
        /// range - because those are the two things the vehicle can actually
        /// change and each has its own speed: the turret traverses at
        /// <see cref="Traverse"/> degrees a second, the barrel elevates at
        /// <see cref="Elevate"/>, and range IS elevation (MetresPerDegree). The
        /// mouse may be anywhere; the crosshair closes the gap at those two
        /// rates and no faster. Near the gun a degree is a metre or two, so the
        /// crosshair crawls; at full reach the same degree is twenty metres and
        /// it sweeps - which is exactly how a real gun behaves and why the
        /// limit is expressed in degrees rather than in pixels.
        /// </summary>
        static void StepLay(Tube t, Vector3 wanted)
        {
            float now = Time.time;
            float dt = Mathf.Clamp(now - _aimStepAt, 0f, 0.25f);
            _aimStepAt = now;
            if (t == null || t.Go == null) return;
            if (!_aimHave) { _aimPoint = LayPoint(t); _aimHave = true; }

            Vector3 gun = t.Go.transform.position;
            float haveBear = Bearing(_aimPoint - gun);
            float haveRange = Flat(_aimPoint - gun);
            float wantBear = Bearing(wanted - gun);
            float wantRange = Mathf.Clamp(Flat(wanted - gun), MinRange, MaxRange);

            float bear = Mathf.MoveTowardsAngle(haveBear, wantBear, Traverse * dt);
            float range = Mathf.MoveTowards(haveRange, wantRange,
                                            Elevate * MetresPerDegree * dt);
            range = Mathf.Clamp(range, MinRange, MaxRange);

            float rad = bear * Mathf.Deg2Rad;
            Vector3 flat = new Vector3(gun.x + Mathf.Sin(rad) * range, 0f,
                                       gun.z + Mathf.Cos(rad) * range);
            float y;
            if (!RevivalTroopInsertion.GroundY(flat, out y)) y = _aimPoint.y;
            _aimPoint = new Vector3(flat.x, y, flat.z);

            // The turret is told to follow the crosshair, not the mouse. Both
            // move at the same rate, so it arrives in the same frame - the slew
            // is what makes it visible from outside.
            t.WantYaw = bear;
            t.WantPitch = PitchFor(range);
        }

        /// <summary>Compass bearing of a flat direction, in the same degrees the
        /// turret's world yaw is measured in (0 = +Z, 90 = +X).</summary>
        static float Bearing(Vector3 v)
        {
            v.y = 0f;
            if (v.sqrMagnitude < 1e-6f) return 0f;
            return Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        }

        /// <summary>Hides the system pointer so the crosshair is the only thing
        /// the player sees. Written every frame on purpose: the game's UI sets
        /// Cursor.visible itself while the map is up, and CursorGuard pulls the
        /// pointer back to the last value it saw (CursorTracker.DesiredVisible).
        /// Going through the property means the tracker records OUR wish, so the
        /// guard stops fighting us - and it is also why the state the game
        /// wanted is remembered here before the first frame overwrites it.</summary>
        static void HideCursor()
        {
            if (!_cursorHidden)
            {
                _cursorWas = CursorTracker.DesiredVisible;
                _cursorHidden = true;
            }
            try { Cursor.visible = false; }
            catch { }
        }

        /// <summary>Gives the pointer back exactly as it was when aim mode took
        /// it, rather than simply making it visible: leaving aim mode can happen
        /// in the same frame the map closes, and "visible" would then be a
        /// pointer sitting in the middle of the 3D scene.</summary>
        static void ShowCursor()
        {
            if (!_cursorHidden) return;
            _cursorHidden = false;
            try { Cursor.visible = _cursorWas; }
            catch { }
        }

        /// <summary>UIController.ShowMap(bool) writes _UI_General 8 for open and
        /// 0 for closed (CONFIRMED, REVERSE_ENGINEERING 28.4) - the same field
        /// MapTools.Context gates on. Calling it from here is the one piece of
        /// this feature that has never been tried; it fails softly, and the
        /// player can always open the map himself.</summary>
        static bool ShowMap(bool open)
        {
            try
            {
                Type ui = RevivalPlugin.TypeByName("UIController");
                if (ui == null) return false;
                MethodInfo get = AccessTools.PropertyGetter(ui, "Instance");
                object inst = get == null ? null : get.Invoke(null, null);
                MethodInfo show = AccessTools.Method(ui, "ShowMap",
                    new Type[] { typeof(bool) }, null);
                if (inst == null || show == null)
                {
                    RevivalPlugin.L.LogWarning("Mortar: UIController.ShowMap(bool) not "
                        + "found - open the map by hand before aiming.");
                    return false;
                }
                show.Invoke(inst, new object[] { open });
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: ShowMap(" + open + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>World point under the cursor on the open map.
        ///
        /// This is MapTools.MouseWorld's projection - the same widget bounds and
        /// the same WORLD_SIZE scale - with ONE deliberate difference: the height
        /// comes from RevivalTroopInsertion.GroundY, which falls back to
        /// Terrain.SampleHeight when the downward ray finds nothing. MouseWorld
        /// needs a ray hit and returns false without one, and far from the player
        /// there is no collider to hit (E-059) - which is precisely where a
        /// mortar is aimed. Returns false only when the cursor is off the map
        /// picture.</summary>
        static bool MapPoint(Component texture, Camera cam, Vector2 world, out Vector3 point)
        {
            point = Vector3.zero;
            try
            {
                Type math = RevivalPlugin.TypeByName("NGUIMath");
                MethodInfo bounds = math == null ? null
                    : AccessTools.Method(math, "CalculateAbsoluteWidgetBounds",
                        new Type[] { typeof(Transform) }, null);
                if (bounds == null) return false;
                Bounds b = (Bounds)bounds.Invoke(null, new object[] { texture.transform });
                if (b.size == Vector3.zero) return false;

                Vector3 mouse = Input.mousePosition;
                mouse.z = 10f;
                Vector3 inUi = cam.ScreenToWorldPoint(mouse);
                float nx = (inUi.x - b.min.x) / b.size.x;
                float ny = (inUi.y - b.min.y) / b.size.y;
                if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return false;

                Vector3 flat = new Vector3((nx - 0.5f) * world.x, 0f, (ny - 0.5f) * world.y);
                float y;
                if (!RevivalTroopInsertion.GroundY(flat, out y)) y = 0f;
                point = new Vector3(flat.x, y, flat.z);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: map click: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------- turret

        /// <summary>Every gun turns towards where it is being laid, a little
        /// each frame. This is the only place a turret or a barrel moves, so
        /// there is exactly one rate in the game whatever asked for the lay -
        /// the player's crosshair, the NPC gunner, or nothing at all.</summary>
        static void Slew()
        {
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.25f);
            for (int i = 0; i < _tubes.Count; i++)
            {
                Tube t = _tubes[i];
                if (t.Go == null) continue;
                t.Yaw = Mathf.MoveTowardsAngle(t.Yaw, t.WantYaw, Traverse * dt);
                t.Pitch = Mathf.MoveTowards(t.Pitch, t.WantPitch, Elevate * dt);
                Point(t);
            }
        }

        /// <summary>Write the current lay onto the model. The turret's angle is
        /// stored in WORLD degrees and applied as a local one, so a hull parked
        /// across a slope still points its gun where the map says it does.</summary>
        static void Point(Tube t)
        {
            if (t.Go == null) return;
            if (t.Turret != null)
            {
                float hull = t.Go.transform.eulerAngles.y;
                t.Turret.localRotation =
                    Quaternion.Euler(0f, Mathf.DeltaAngle(hull, t.Yaw), 0f);
            }
            // Positive pitch is UP, and a positive rotation about local X points
            // the barrel's +Z down - hence the sign.
            if (t.Barrel != null)
                t.Barrel.localRotation = Quaternion.Euler(-t.Pitch, 0f, 0f);
        }

        /// <summary>Where the shell leaves, for the report and the smoke.</summary>
        static Vector3 Muzzle(Tube t)
        {
            if (t.Go == null) return Vector3.zero;
            if (t.Barrel != null) return t.Barrel.TransformPoint(ArtyModel.MuzzleLocal);
            return t.Go.transform.position + Vector3.up * 2f;
        }

        static Tube ById(int settlementId)
        {
            for (int i = 0; i < _tubes.Count; i++)
                if (_tubes[i].SettlementId == settlementId) return _tubes[i];
            return null;
        }

        // ------------------------------------------- what the NPC crew may ask

        /// <summary>Lay this gun on a point. Refused while the player has the
        /// sight: two people cannot turn the same turret.</summary>
        internal static void Lay(int settlementId, Vector3 point)
        {
            if (!Enabled) return;
            Tube t = ById(settlementId);
            if (t == null || t.Go == null || _aiming == t) return;
            Vector3 gun = t.Go.transform.position;
            t.WantYaw = Bearing(point - gun);
            t.WantPitch = PitchFor(Mathf.Clamp(Flat(point - gun), MinRange, MaxRange));
        }

        /// <summary>Is the gun actually on that point yet? The NPC gunner waits
        /// for this, which is why a target behind the vehicle costs him the
        /// seconds the turret needs to come round.</summary>
        internal static bool Laid(int settlementId, Vector3 point)
        {
            Tube t = ById(settlementId);
            if (t == null || t.Go == null) return false;
            Vector3 gun = t.Go.transform.position;
            if (Mathf.Abs(Mathf.DeltaAngle(t.Yaw, Bearing(point - gun))) > 1.5f) return false;
            float pitch = PitchFor(Mathf.Clamp(Flat(point - gun), MinRange, MaxRange));
            return Mathf.Abs(t.Pitch - pitch) <= 1.5f;
        }

        /// <summary>
        /// The gun nearest to an impact, laid on it.
        ///
        /// A joined client never runs an NPC crew's mission - that is the
        /// master's - so its turrets would stand still while shells came out of
        /// them. The impact event is the one thing it does hear, so the gun that
        /// could have fired it is turned onto the point. It is cosmetic by
        /// design: nothing is decided here, the turret simply ends up standing
        /// where the shells are going.
        /// </summary>
        static void LayNearest(Vector3 point)
        {
            Tube best = null;
            float bestD = MaxRange;
            for (int i = 0; i < _tubes.Count; i++)
            {
                Tube t = _tubes[i];
                if (t.Go == null || _aiming == t) continue;
                float d = Flat(point - t.Go.transform.position);
                if (d > bestD) continue;
                bestD = d;
                best = t;
            }
            if (best != null) Lay(best.SettlementId, point);
        }

        /// <summary>Has the player taken this gun's sight?</summary>
        internal static bool PlayerAiming(int settlementId)
        {
            return _aiming != null && _aiming.SettlementId == settlementId;
        }

        /// <summary>
        /// The NPC crew's fire mission. Runs on the MASTER only (the battery
        /// never calls it anywhere else), and differs from the player's order in
        /// exactly three ways: the shells come out of the crew's own supply and
        /// not out of a backpack, the mission is not drawn on anybody's map, and
        /// every impact goes out over the wire so each client can apply it to its
        /// own player. Returns how many shells left the barrel.
        /// </summary>
        internal static int NpcFire(int settlementId, Vector3 target, int available)
        {
            if (!Enabled || available <= 0) return 0;
            Tube t = ById(settlementId);
            if (t == null || t.Go == null || _aiming == t) return 0;
            if (Time.time < t.ReadyAt) return 0;

            Vector3 gun = t.Go.transform.position;
            float dist = Flat(target - gun);
            if (dist > MaxRange || dist < MinRange) return 0;

            int want = Mathf.Clamp(I(_cfgRounds, 3), 1, 12);
            int rounds = Mathf.Min(want, available);
            t.ReadyAt = Time.time + Mathf.Max(0f, F(_cfgCooldown, 20f));

            // The same net the player's own mission gets. The crew's damage is
            // credited to owner 0 exactly as his is, so this should never have
            // anything to do - and it costs nothing.
            FactionShield.Arm();

            float tof = Salvo(t, target, rounds, true);
            RevivalPlugin.L.LogInfo("Mortar: \"" + t.Name + "\" NPC crew fires " + rounds
                + " round(s) -> " + target.ToString("0") + " d=" + dist.ToString("0")
                + " tof=" + tof.ToString("0.0") + " s.");
            return rounds;
        }

        /// <summary>The game's own player list, shared with the battery so it
        /// does not have to walk the scene for what two field reads
        /// answer.</summary>
        internal static List<GameObject> PlayerList()
        {
            return Players();
        }

        // ------------------------------------------------------- fire mission

        static void Fire(Tube t, Vector3 target)
        {
            Vector3 tube = t.Go.transform.position;
            float dist = Flat(target - tube);

            if (Time.time < t.ReadyAt)
            {
                Say(Loc.T("Перезарядка",
                          "Reloading") + " " + Mathf.CeilToInt(t.ReadyAt - Time.time) + " s");
                return;
            }
            if (t.Rounds <= 0)
            {
                Say(Loc.T("Орудие пусто",
                          "The gun is empty"));
                return;
            }
            // A click outside the reach is REFUSED with a reason, never clamped
            // onto the rim: clamping would fire at a place nobody chose.
            if (dist > MaxRange)
            {
                Say(Loc.T("Слишком далеко",
                          "Too far") + ": " + dist.ToString("0") + " m > "
                    + MaxRange.ToString("0") + " m");
                return;
            }
            if (dist < MinRange)
            {
                Say(Loc.T("Слишком близко",
                          "Too close") + ": " + dist.ToString("0") + " m < "
                    + MinRange.ToString("0") + " m");
                return;
            }

            int want = Mathf.Clamp(I(_cfgRounds, 3), 1, 12);
            int rounds = Mathf.Min(want, t.Rounds);
            t.Rounds -= rounds;
            t.ReadyAt = Time.time + Mathf.Max(0f, F(_cfgCooldown, 20f));
            _fireTarget = target;
            _firedAt = Time.time;

            FactionShield.Arm();

            float tof = Salvo(t, target, rounds, false);

            RevivalPlugin.L.LogInfo("Mortar: \"" + t.Name + "\" " + rounds
                + " round(s) -> " + target.ToString("0") + " d=" + dist.ToString("0")
                + " tof=" + tof.ToString("0.0") + " s spread=" + Spread(dist).ToString("0")
                + " m, " + t.Rounds + " left.");
            Say(Loc.T("Огонь!", "Firing!") + " " + rounds
                + " x " + dist.ToString("0") + " m, "
                + Loc.T("Полёт", "flight") + " "
                + tof.ToString("0") + " s");
        }

        /// <summary>The shells themselves, one salvo's worth. Shared by the
        /// player's fire order and the NPC crew's so there is exactly one place
        /// that decides where a round lands. Returns the flight time.</summary>
        static float Salvo(Tube t, Vector3 target, int rounds, bool npc)
        {
            Vector3 gun = t.Go.transform.position;
            float dist = Flat(target - gun);
            float spread = Spread(dist);
            float tof = Flight(dist);
            float gap = Mathf.Max(0.2f, F(_cfgRoundInterval, 1.4f));
            Vector3 muzzle = Muzzle(t);
            for (int i = 0; i < rounds; i++)
            {
                // Uniform over the AREA of the dispersion circle, not over its
                // radius: r = R * sqrt(rand), or every shell crowds the centre.
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                float r = spread * Mathf.Sqrt(UnityEngine.Random.value);
                Vector3 flat = new Vector3(target.x + Mathf.Cos(a) * r, 0f,
                                           target.z + Mathf.Sin(a) * r);
                float y;
                if (!RevivalTroopInsertion.GroundY(flat, out y)) y = target.y;

                Shell s = new Shell();
                s.From = muzzle;
                s.Point = new Vector3(flat.x, y, flat.z);
                s.LeaveAt = Time.time + i * gap;
                s.ImpactAt = s.LeaveAt + tof;
                s.Npc = npc;
                _inFlight.Add(s);
            }
            return tof;
        }

        internal static float Spread(float dist)
        {
            return Mathf.Max(1f, F(_cfgDispersion, 8f)
                + dist * F(_cfgDispersionPercent, 2f) * 0.01f);
        }

        internal static float Flight(float dist)
        {
            return Mathf.Max(1f, F(_cfgFlightBase, 4f)
                + dist * 0.01f * F(_cfgFlightPer100, 0.85f));
        }

        /// <summary>Shells on the way: the muzzle report when one leaves, the
        /// incoming whistle shortly before it lands, and the impact - which is
        /// a different one for the player's rounds and the crew's.</summary>
        static void Flight()
        {
            if (_inFlight.Count == 0) return;
            float now = Time.time;
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                Shell s = _inFlight[i];
                if (!s.Left && now >= s.LeaveAt)
                {
                    s.Left = true;
                    Sound.Thump(s.From);
                }
                if (!s.Whistled && now >= s.ImpactAt - 1.4f)
                {
                    s.Whistled = true;
                    Sound.Whistle(s.Point);
                }
                if (now < s.ImpactAt) continue;
                _inFlight.RemoveAt(i);
                try
                {
                    if (s.Npc) NpcImpact(s.Point);
                    else Impact(s.Point);
                }
                catch (Exception ex) { RevivalPlugin.L.LogError("Mortar impact: " + ex); }
            }
        }

        // ------------------------------------------------------------- impact

        static void Impact(Vector3 point)
        {
            // 1) The picture. Networked through the game's own ExplosionObject so
            //    every client sees one blast and hears one bang. Its damage is 0
            //    by default - see the head of this file for why that matters.
            try
            {
                RocketHook.Detonate(point + Vector3.up * 0.2f,
                                    Mathf.Max(0f, F(_cfgBlastDamage, 0f)), Radius, 3f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: no visible explosion at "
                    + point.ToString("0") + " - " + ex.Message);
            }

            // 2) The damage, applied by us: NPCs and (on the master) vehicles
            //    here, players over the wire.
            int npc, veh, plr;
            Sweep(point, true, out npc, out veh, out plr);

            // 3) The master owns the settlement NPCs. If we are not it, ask it
            //    to run the same sweep for the ones it owns.
            if (!Master()) Net.SendImpact(point);

            RevivalPlugin.L.LogInfo("Mortar: impact at " + point.ToString("0")
                + " r=" + Radius.ToString("0") + " - " + npc + " NPC, " + veh
                + " vehicle, " + plr + " player hit.");
        }

        /// <summary>
        /// A shell the NPC crew fired. It lands on the master client - that is
        /// the only client that ran the mission - and from there:
        ///
        ///   the picture  is the game's own networked explosion, spawned once
        ///                here, so every client sees and hears one blast;
        ///   NPCs and     are the master's own, swept exactly as a player's
        ///   vehicles     shell sweeps them, with owner 0 on the damage;
        ///   players      are NOT touched from here. The point goes out on the
        ///                battery channel and every client - the master
        ///                included, by calling Self directly - applies the blast
        ///                to ITS OWN player only.
        ///
        /// That last rule is the whole reason this method exists beside
        /// <see cref="Impact"/>. A master client that damaged other players
        /// would be a player shooting players: kill credit, counter-attack and a
        /// traitor flag, all for a shell an NPC fired. Damaging only yourself
        /// cannot be held against anybody.
        /// </summary>
        static void NpcImpact(Vector3 point)
        {
            try
            {
                RocketHook.Detonate(point + Vector3.up * 0.2f,
                                    Mathf.Max(0f, F(_cfgBlastDamage, 0f)), Radius, 3f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: no visible explosion at "
                    + point.ToString("0") + " - " + ex.Message);
            }

            int npc, veh, plr;
            Sweep(point, false, out npc, out veh, out plr);
            Self(point);
            Net.SendNpcImpact(point);

            RevivalPlugin.L.LogInfo("Mortar: NPC crew impact at " + point.ToString("0")
                + " r=" + Radius.ToString("0") + " - " + npc + " NPC, " + veh
                + " vehicle.");
        }

        /// <summary>The blast on our OWN player, and on nobody else's. Called on
        /// every client that hears about an NPC crew's shell, including the one
        /// that fired it.</summary>
        internal static void Self(Vector3 point)
        {
            try
            {
                GameObject me = MapTools.LocalPlayer();
                if (me == null) return;
                float d = Vector3.Distance(me.transform.position, point);
                if (d > Radius) return;
                float dmg = Mathf.Max(0f, F(_cfgPlayerDamage, 260f)) * Falloff(d, Radius);
                if (dmg < 1f) return;
                if (PlayerDamage(me, dmg, point))
                    RevivalPlugin.L.LogInfo("Mortar: NPC crew shell caught us at "
                        + d.ToString("0") + " m for " + dmg.ToString("0") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: own blast damage: " + ex.Message);
            }
        }

        /// <summary>The impact sweep. <paramref name="shooter"/> is true on the
        /// client that fired: only it sends player damage, so a player is never
        /// hit twice for one bomb. Vehicles are the master's business for the
        /// same reason - VehicleGameSystem.ApplyDamage is applied once and
        /// replicated, so running it on two clients would bill the same tank
        /// twice.</summary>
        internal static void Sweep(Vector3 point, bool shooter,
                                   out int npcHits, out int vehicleHits, out int playerHits)
        {
            npcHits = 0;
            vehicleHits = 0;
            playerHits = 0;
            float radius = Radius;

            // ---- NPCs. Turret.TryDamage fills every argument but the damage
            //      with its type default, so damageOwnerId goes in as 0 - the
            //      anonymous owner that is credited to nobody.
            if (LookUp() && _npcType != null)
            {
                float peak = Mathf.Max(0f, F(_cfgDamage, 450f));
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_npcType);
                for (int i = 0; i < all.Length; i++)
                {
                    Component ai = all[i] as Component;
                    if (ai == null || ai.gameObject == null) continue;
                    float d = Vector3.Distance(ai.transform.position, point);
                    if (d > radius) continue;
                    if (!Alive(ai) || !Hurtable(ai)) continue;
                    float dmg = peak * Falloff(d, radius);
                    if (dmg < 1f) continue;
                    BreakKillStreak(ai);
                    try
                    {
                        if (Turret.TryDamage(ai.gameObject, "NPC_AI2", "ApplyDamage", dmg))
                            npcHits++;
                    }
                    catch (Exception ex)
                    {
                        // The man is dead either way - DecreaseHealth runs before
                        // the statistics do. Never let one casualty end the sweep,
                        // or the rest of the beaten zone survives the bomb.
                        if (!_streakWarned)
                        {
                            _streakWarned = true;
                            RevivalPlugin.L.LogWarning("Mortar: NPC_AI2.ApplyDamage threw - "
                                + ex.Message);
                        }
                    }
                }
            }

            // ---- Vehicles, master only. Part type 14 is the explosion part, the
            //      same one the anti-tank mine uses - so VehicleArmor re-balances
            //      a tank hit exactly as it does for every other blast.
            if (Master())
            {
                float peak = Mathf.Max(0f, F(_cfgVehicleDamage, 700f));
                Type vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                MethodInfo apply = vgs == null ? null : AccessTools.Method(vgs, "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
                if (apply != null)
                {
                    Component[] cars = VehicleScan.All();
                    for (int i = 0; i < cars.Length; i++)
                    {
                        Component c = cars[i];
                        if (c == null) continue;
                        float d = Vector3.Distance(c.transform.position, point);
                        if (d > radius) continue;
                        float dmg = peak * Falloff(d, radius);
                        if (dmg < 1f) continue;
                        try { apply.Invoke(c, new object[] { dmg, 14 }); vehicleHits++; }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("Mortar: vehicle damage: " + ex.Message);
                        }
                    }
                }
            }

            // ---- Players, shooter only, and never one of our own faction.
            if (shooter)
            {
                float peak = Mathf.Max(0f, F(_cfgPlayerDamage, 260f));
                List<GameObject> players = Players();
                for (int i = 0; i < players.Count; i++)
                {
                    GameObject go = players[i];
                    if (go == null) continue;
                    float d = Vector3.Distance(go.transform.position, point);
                    if (d > radius) continue;
                    // THE FACTION RULE for players. A man of our own faction is
                    // not hit at all - the only way to be certain his death can
                    // never be booked against us. Where a faction cannot be read
                    // on either side the shot goes through: "unknown" is not
                    // "ours", and refusing every unreadable player would quietly
                    // turn the mortar into a blank.
                    if (FactionShield.SameFactionAsLocal(go)) continue;
                    float dmg = peak * Falloff(d, radius);
                    if (dmg < 1f) continue;
                    if (PlayerDamage(go, dmg, point)) playerHits++;
                }
            }
        }

        /// <summary>Linear to zero at the rim. Simple on purpose: a curve nobody
        /// can feel is a curve nobody can balance.</summary>
        static float Falloff(float d, float radius)
        {
            return Mathf.Clamp01(1f - d / Mathf.Max(0.001f, radius));
        }

        static Type _pncType;
        static PropertyInfo _photonPlayer;
        static MethodInfo _getView, _rpc;
        static bool _damageLooked;

        /// <summary>Damage on a player the way the game does it: health lives on
        /// the victim's own client, so this is an RPC, not a call on our copy.
        /// The argument list is the one FireOneShot uses (Revival.Patrol.cs:4459):
        /// damage, body part 0, damage kind 4, hit point, shooter position.</summary>
        static bool PlayerDamage(GameObject victimGo, float damage, Vector3 point)
        {
            if (!DamageLookUp()) return false;
            try
            {
                Component pnc = victimGo.GetComponentInParent(_pncType);
                if (pnc == null) return false;
                object victim = _photonPlayer.GetValue(pnc, null);
                if (victim == null) return false;
                object view = _getView.Invoke(null, new object[] { pnc.gameObject });
                if (view == null) return false;
                _rpc.Invoke(view, new object[] {
                    "PlayerApplyDamage", victim,
                    new object[] { damage, 0, 4, point, point + Vector3.up * 50f } });
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: player damage: " + ex.Message);
                return false;
            }
        }

        static bool DamageLookUp()
        {
            if (_damageLooked) return _rpc != null;
            _damageLooked = true;
            _pncType = RevivalPlugin.TypeByName("PlayerNetworkController");
            Type ext = RevivalPlugin.TypeByName("Extensions");
            Type viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_pncType == null || ext == null || viewType == null)
            {
                RevivalPlugin.L.LogWarning("Mortar: PlayerNetworkController, Extensions "
                    + "or PhotonView not found - the mortar cannot hurt a player.");
                return false;
            }
            _photonPlayer = _pncType.GetProperty("GetPhotonPlayer",
                BindingFlags.Public | BindingFlags.Instance);
            _getView = AccessTools.Method(ext, "GetPhotonView",
                new Type[] { typeof(GameObject) }, null);
            MethodInfo[] ms = viewType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "RPC") continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length != 3) continue;
                if (ps[0].ParameterType != typeof(string)) continue;
                if (ps[2].ParameterType != typeof(object[])) continue;
                // PhotonView has TWO three-argument RPC overloads that differ
                // only in the middle: RPC(string, PhotonTargets, object[]) and
                // RPC(string, PhotonPlayer, object[]). We send to one player,
                // so the second is the only one that takes our argument - the
                // other throws on Invoke. Same test as the patrol gun
                // (Revival.Patrol.cs:4508).
                if (ps[1].ParameterType.Name != "PhotonPlayer") continue;
                _rpc = ms[i];
                break;
            }
            if (_photonPlayer == null || _getView == null || _rpc == null)
            {
                RevivalPlugin.L.LogWarning("Mortar: the PlayerApplyDamage RPC path is "
                    + "incomplete - the mortar cannot hurt a player.");
                _rpc = null;
                return false;
            }
            return true;
        }

        /// <summary>The game's own list of player GameObjects - the same one
        /// NPC_Settlement::PlayersDistanceControll walks. Two field reads
        /// instead of a FindObjectsOfType over the whole scene.</summary>
        static List<GameObject> Players()
        {
            List<GameObject> list = new List<GameObject>();
            try
            {
                Type ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                if (ngs == null) return list;
                MethodInfo inst = AccessTools.PropertyGetter(ngs, "Instance");
                object server = inst == null ? null : inst.Invoke(null, null);
                FieldInfo f = AccessTools.Field(ngs, "NetworkPlayers");
                IList raw = server == null || f == null ? null : f.GetValue(server) as IList;
                if (raw == null) return list;
                for (int i = 0; i < raw.Count; i++)
                {
                    GameObject go = raw[i] as GameObject;
                    if (go != null) list.Add(go);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar: player list: " + ex.Message);
            }
            return list;
        }

        // ------------------------------------------------- NPC target filters

        static FieldInfo _mySettlement;
        static FieldInfo _lastKillerId;
        static bool _streakLooked;
        static bool _killerLooked;
        static bool _streakWarned;

        /// <summary>The price of an anonymous kill, and the reason this is not
        /// optional.
        ///
        /// Our damage carries damageOwnerId 0 (see the head of this file), and
        /// NPC_Settlement.StatsOnNpcKilled counts consecutive kills by one id.
        /// On the kill that leaves a six-or-more-spawn-point settlement with
        /// nobody ready, after spawnPoints-1 kills in a row, it looks the killer
        /// up with PhotonPlayer.Find(_lastKillerId).ID - and id 0 is not a
        /// player, so that throws (CONFIRMED IL, Revival.NpcCombat.cs:4260).
        /// A salvo into a small settlement is exactly the case that reaches it.
        /// Resetting an id-0 streak to -1 makes every one of our bombs a "new
        /// killer"; a real player's streak is never touched.
        ///
        /// NpcWar solves it the same way for the same reason. It is repeated
        /// here rather than shared because that method is private to another
        /// feature's file, and every source file has one writer.</summary>
        static void BreakKillStreak(Component ai)
        {
            if (ai == null) return;
            try
            {
                if (!_streakLooked)
                {
                    _streakLooked = true;
                    _mySettlement = AccessTools.Field(ai.GetType(), "MySettlement");
                }
                if (_mySettlement == null) return;
                object home = _mySettlement.GetValue(ai);
                if (home == null) return;
                if (!_killerLooked)
                {
                    _killerLooked = true;
                    _lastKillerId = AccessTools.Field(home.GetType(), "_lastKillerId");
                    if (_lastKillerId != null && _lastKillerId.FieldType != typeof(int))
                        _lastKillerId = null;
                    if (_lastKillerId == null)
                        RevivalPlugin.L.LogWarning("Mortar: NPC_Settlement._lastKillerId "
                            + "missing - a settlement-clearing bomb may throw (caught).");
                }
                if (_lastKillerId == null) return;
                if ((int)_lastKillerId.GetValue(home) == 0)
                    _lastKillerId.SetValue(home, -1);
            }
            catch { }
        }

        static bool Alive(Component ai)
        {
            if (_npcAlive == null) return true;
            try
            {
                object r = _npcAlive.Invoke(ai, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>Would NPC_AI2.ApplyDamage take health off this one? The same
        /// four gates NpcWar.Targetable/Hurtable use: a trader, a StoreKeeper,
        /// an NPC in a safe settlement or one in a conversation swallows every
        /// round, so shelling them is a waste and a settlement full of angry
        /// shopkeepers nobody asked for.</summary>
        static bool Hurtable(Component ai)
        {
            try
            {
                if (Bool(ai, "GodModeEnabled")) return false;
                if (Bool(ai, "IsTalkActive")) return false;
                if (Bool(ai, "_isSafeSettlement")) return false;
                FieldInfo init = AccessTools.Field(ai.GetType(), "IsInitialized");
                if (init != null && init.FieldType == typeof(bool)
                    && !(bool)init.GetValue(ai)) return false;
                FieldInfo beh = AccessTools.Field(ai.GetType(), "BehaviorPattern");
                if (beh != null && Convert.ToInt32(beh.GetValue(ai)) == 1) return false;  // StoreKeeper
                FieldInfo home = AccessTools.Field(ai.GetType(), "MySettlement");
                object s = home == null ? null : home.GetValue(ai);
                if (s != null)
                {
                    FieldInfo safe = AccessTools.Field(s.GetType(), "IsSafeSettlement");
                    if (safe != null && safe.FieldType == typeof(bool)
                        && (bool)safe.GetValue(s)) return false;
                }
            }
            catch { }
            return true;
        }

        static bool Bool(Component c, string field)
        {
            try
            {
                FieldInfo f = AccessTools.Field(c.GetType(), field);
                if (f == null || f.FieldType != typeof(bool)) return false;
                return (bool)f.GetValue(c);
            }
            catch { return false; }
        }

        // ----------------------------------------------------------- plumbing

        static bool LookUp()
        {
            if (_typesLooked) return _npcType != null || _settlementType != null;
            _typesLooked = true;
            _settlementType = RevivalPlugin.TypeByName("NPC_Settlement");
            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (_npcType != null) _npcAlive = AccessTools.Method(_npcType, "IsAlive", null, null);
            if (_settlementType == null)
                RevivalPlugin.L.LogWarning("Mortar: NPC_Settlement not found - no guns "
                    + "will be raised.");
            return _npcType != null || _settlementType != null;
        }

        internal static bool Master()
        {
            return RevivalTroopInsertion.MasterClient();
        }

        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        static KeyCode Key(bool use)
        {
            if (!_keysParsed)
            {
                _keysParsed = true;
                _useKey = Parse(_cfgUseKey == null ? "F" : _cfgUseKey.Value, KeyCode.F);
                _loadKey = Parse(_cfgLoadKey == null ? "R" : _cfgLoadKey.Value, KeyCode.R);
            }
            return use ? _useKey : _loadKey;
        }

        static KeyCode Parse(string s, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("Mortar: key \"" + s + "\" unknown, using "
                    + fallback + ".");
                return fallback;
            }
        }

        static void Say(string text)
        {
            _status = text;
            _statusUntil = Time.time + 4f;
            Turret.Hinweis(Name() + ": " + text, 3f);
        }

        // ----------------------------------------------------------- drawing

        public static void Draw()
        {
            if (!Enabled) return;
            try
            {
                if (_aiming != null) { DrawMap(); return; }
                if (!string.IsNullOrEmpty(_prompt)) DrawPlate(_prompt, 0.62f);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Mortar.Draw: " + ex); }
        }

        /// <summary>The fire control: the reach as a highlighted disc on the
        /// game's own map, the dead zone as a small ring, the gun as a cross
        /// with a short line along its bearing, and the crosshair on the lay.
        ///
        /// Two rules the route overlay already learned the hard way. Everything
        /// is HARD-clipped with GUI.BeginClip to the visible map window - the
        /// map texture scrolls inside a clipping UIPanel, and a disc drawn past
        /// its edge paints over the 3D scene. And nothing is drawn on a scene
        /// whose map is not the overworld, because a world-sized ring on a
        /// bunker's small map smears across the whole picture.
        ///
        /// Deliberately NOT corrected by Patrol.MapArt. That correction nudges
        /// a drawn line onto the hand-painted picture's own roads; the CLICK
        /// path has no such correction, so a corrected ring and an uncorrected
        /// click would disagree by a few pixels at the rim. Ring and click are
        /// kept in the same, world-true system.</summary>
        static void DrawMap()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_aiming == null || _aiming.Go == null) return;

            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out cam, out world, out map)) return;

            Rect clip;
            if (!MapTools.MapScreenRect(texture, cam, out clip)) return;
            Rect view;
            if (MapTools.MapViewportRect(texture, cam, out view)) clip = Intersect(clip, view);
            if (clip.width < 2f || clip.height < 2f) return;

            Vector3 tube = _aiming.Go.transform.position;
            // Scene gate: the tube belongs to the overworld terrain, so on a
            // smaller interior map its coordinates fall outside by whole terrain
            // widths. Draw nothing rather than somewhere wrong.
            if (Mathf.Abs(tube.x) > world.x * 0.75f || Mathf.Abs(tube.z) > world.y * 0.75f)
                return;

            Vector2 centre, rimX, rimZ;
            if (!MapTools.WorldToGui(tube, texture, cam, world, map, out centre)) return;
            if (!MapTools.WorldToGui(tube + new Vector3(MaxRange, 0f, 0f), texture, cam,
                                     world, map, out rimX)) return;
            if (!MapTools.WorldToGui(tube + new Vector3(0f, 0f, MaxRange), texture, cam,
                                     world, map, out rimZ)) return;
            float hw = Mathf.Abs(rimX.x - centre.x);
            float hh = Mathf.Abs(rimZ.y - centre.y);
            if (hw < 2f || hh < 2f) return;
            float inner = MinRange / MaxRange;

            // WHERE THE GUN IS LAID. Not where the pointer is: the crosshair is
            // the lay, it is stepped towards the pointer in Update at the
            // turret's own rate, and every number on this overlay - the beaten
            // zone, the range, the flight time - belongs to it and not to a
            // mouse that may be half a map ahead of the barrel.
            Vector3 under = _aimHave ? _aimPoint : tube;
            bool onMap = _aimHave;
            float distUnder = onMap ? Flat(under - tube) : -1f;

            Color old = GUI.color;
            GUI.BeginClip(clip);
            try
            {
                Vector2 c = centre - clip.position;

                // The reach: a soft fill with a bright rim. One draw call, and
                // it is the "highlighted area" rather than a bare outline.
                GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.95f);
                GUI.DrawTexture(new Rect(c.x - hw, c.y - hh, hw * 2f, hh * 2f), Disc());

                // The dead zone under the gun. Hiding it produces "I clicked
                // and nothing happened".
                if (inner > 0.02f)
                {
                    GUI.color = new Color(0.95f, 0.35f, 0.25f, 0.95f);
                    GUI.DrawTexture(new Rect(c.x - hw * inner, c.y - hh * inner,
                                             hw * inner * 2f, hh * inner * 2f), Ring());
                }

                // The gun itself, and the line from it to where it points - the
                // muzzle's own bearing, so a player can see the turret swing
                // round on the map while he waits for it.
                GUI.color = new Color(1f, 0.85f, 0.25f, 1f);
                GUI.DrawTexture(new Rect(c.x - 5f, c.y - 1f, 10f, 2f), Px());
                GUI.DrawTexture(new Rect(c.x - 1f, c.y - 5f, 2f, 10f), Px());
                float lay = _aiming.Yaw * Mathf.Deg2Rad;
                for (int i = 1; i <= 8; i++)
                {
                    float f = i / 8f * 0.16f;
                    GUI.DrawTexture(new Rect(c.x + Mathf.Sin(lay) * hw * f - 1f,
                                             c.y - Mathf.Cos(lay) * hh * f - 1f,
                                             2f, 2f), Px());
                }

                // WHERE THE SHELLS WOULD GO. A howitzer is an area weapon, and a
                // player who is only shown a point believes he has a rifle. The
                // circle under the crosshair is the beaten zone a click would
                // buy, drawn to scale from the same Spread() the shells use.
                float pixelsPerMetre = hw / MaxRange;
                if (onMap && distUnder >= MinRange && distUnder <= MaxRange)
                {
                    GUI.color = new Color(0.35f, 1f, 0.45f, 0.55f);
                    Spot(under, Spread(distUnder) * pixelsPerMetre, hh / hw,
                         texture, cam, world, map, clip, Ring());
                }

                // Where the MOUSE is asking the gun to go, as a faint dot. It is
                // not a second crosshair: it is the only way to see that the gun
                // is still on its way there.
                if (_aimWantHave)
                {
                    Vector2 wp;
                    if (MapTools.WorldToGui(_aimWanted, texture, cam, world, map, out wp))
                    {
                        Vector2 w = wp - clip.position;
                        GUI.color = new Color(0.85f, 0.95f, 0.85f, 0.35f);
                        GUI.DrawTexture(new Rect(w.x - 2f, w.y - 2f, 4f, 4f), Px());
                    }
                }

                // The mission that is on its way: the aim point as a cross with
                // its dispersion circle, and every shell still in the air as a
                // dot where it will land. This is what makes a correction
                // possible - a second salvo is aimed off the first one.
                if (_firedAt > 0f && (_inFlight.Count > 0 || Time.time - _firedAt < 12f))
                {
                    GUI.color = new Color(1f, 0.55f, 0.20f, 0.90f);
                    Spot(_fireTarget,
                         Spread(Flat(_fireTarget - tube)) * pixelsPerMetre,
                         hh / hw, texture, cam, world, map, clip, Ring());
                    Vector2 fp;
                    if (MapTools.WorldToGui(_fireTarget, texture, cam, world, map, out fp))
                    {
                        Vector2 f = fp - clip.position;
                        GUI.DrawTexture(new Rect(f.x - 6f, f.y - 1f, 13f, 2f), Px());
                        GUI.DrawTexture(new Rect(f.x - 1f, f.y - 6f, 2f, 13f), Px());
                    }
                    GUI.color = new Color(1f, 0.90f, 0.35f, 0.95f);
                    for (int i = 0; i < _inFlight.Count; i++)
                    {
                        Vector2 sp;
                        if (!MapTools.WorldToGui(_inFlight[i].Point, texture, cam,
                                                 world, map, out sp)) continue;
                        Vector2 s = sp - clip.position;
                        GUI.DrawTexture(new Rect(s.x - 2f, s.y - 2f, 4f, 4f), Px());
                    }
                }
            }
            finally
            {
                GUI.EndClip();
                GUI.color = old;
            }

            // The crosshair sits on the LAY, and is drawn after the clip is
            // closed so it stays whole at the map's edge. Off the picture (the
            // map is panned away from the gun) there is nothing to draw it on,
            // and the status line alone has to do.
            Vector2 cross = Vector2.zero;
            bool haveCross = onMap
                && MapTools.WorldToGui(under, texture, cam, world, map, out cross);
            DrawCrosshair(haveCross, cross, onMap, distUnder);
        }

        /// <summary>One circle of <paramref name="radius"/> pixels around a world
        /// point, in the clipped map's own coordinates. <paramref name="squash"/>
        /// is the map's y-to-x scale, so the circle stays a circle on a map whose
        /// two axes are not drawn at the same number of pixels per metre.</summary>
        static void Spot(Vector3 at, float radius, float squash,
                         Component texture, Camera cam, Vector2 world, Vector2 map,
                         Rect clip, Texture2D art)
        {
            Vector2 gui;
            if (!MapTools.WorldToGui(at, texture, cam, world, map, out gui)) return;
            float rx = Mathf.Max(2f, radius);
            float ry = Mathf.Max(2f, radius * squash);
            Vector2 p = gui - clip.position;
            GUI.DrawTexture(new Rect(p.x - rx, p.y - ry, rx * 2f, ry * 2f), art);
        }

        /// <summary>The crosshair that replaced the mouse pointer, plus one line
        /// telling the player what a click here would do. Both are drawn in
        /// ABSOLUTE coordinates after the map clip is closed, so the crosshair
        /// stays whole at the map's edge.
        ///
        /// It is drawn where the GUN points, not where the pointer is. That is
        /// the visible half of the rate limit: swing the mouse and the cross
        /// follows it at the turret's pace, with the faint dot on the map
        /// showing what it is chasing.</summary>
        static void DrawCrosshair(bool have, Vector2 m, bool onMap, float dist)
        {
            Color old = GUI.color;
            bool good = onMap && dist >= MinRange && dist <= MaxRange;

            if (have)
            {
                GUI.color = good ? new Color(0.35f, 1f, 0.45f, 0.95f)
                                 : new Color(1f, 0.45f, 0.35f, 0.95f);
                // A cross with a gap in the middle, so the pixel being aimed at
                // is never covered by the crosshair itself.
                GUI.DrawTexture(new Rect(m.x - 13f, m.y - 1f, 9f, 2f), Px());
                GUI.DrawTexture(new Rect(m.x + 4f, m.y - 1f, 9f, 2f), Px());
                GUI.DrawTexture(new Rect(m.x - 1f, m.y - 13f, 2f, 9f), Px());
                GUI.DrawTexture(new Rect(m.x - 1f, m.y + 4f, 2f, 9f), Px());
                GUI.DrawTexture(new Rect(m.x - 1f, m.y - 1f, 2f, 2f), Px());
                GUI.color = old;
            }

            int cap = Mathf.Clamp(I(_cfgMagazine, 6), 1, 30);
            string line = Name()
                + " " + _aiming.Rounds + "/" + cap + "   "
                + Loc.T("Дальность", "Reach")
                + " " + MinRange.ToString("0") + "-" + MaxRange.ToString("0") + " m";
            if (onMap)
                line += "   " + dist.ToString("0") + " m   "
                    + Loc.T("Полёт", "flight") + " "
                    + Flight(dist).ToString("0") + " s   +/-"
                    + Spread(dist).ToString("0") + " m";
            if (Time.time < _statusUntil && !string.IsNullOrEmpty(_status))
                line += "   -   " + _status;
            DrawPlate(line, 0.93f);
        }

        static void DrawPlate(string text, float screenY)
        {
            // No TextAnchor anywhere: it would pull in
            // UnityEngine.TextRenderingModule, which this build does not
            // reference. The plate is sized to the label instead.
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
            float w = size.x + 24f, h = Mathf.Max(24f, size.y + 8f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * screenY;
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, w, h), Px());
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 12f, y + 4f, size.x + 4f, size.y + 2f), text);
            GUI.color = old;
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.x, b.x), y0 = Mathf.Max(a.y, b.y);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }

        static Texture2D Px()
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
                _px.hideFlags = HideFlags.HideAndDontSave;
            }
            return _px;
        }

        /// <summary>A white disc: faint inside, solid in the outermost pixels.
        /// Stretched to the projected reach it is both the highlighted area and
        /// its circle, in one draw. White RGB, so GUI.color tints it.</summary>
        static Texture2D Disc()
        {
            if (_disc != null) return _disc;
            _disc = Circle(0.10f, true);
            return _disc;
        }

        /// <summary>The same circle with an empty middle - the dead zone.</summary>
        static Texture2D Ring()
        {
            if (_ring != null) return _ring;
            _ring = Circle(0f, false);
            return _ring;
        }

        /// <summary>The white pixel and the empty circle, for the battery's own
        /// map markers. Two textures for the whole feature rather than two per
        /// file - they are tinted by GUI.color at every draw anyway.</summary>
        internal static Texture2D PxTexture() { return Px(); }

        internal static Texture2D RingTexture() { return Ring(); }

        static Texture2D Circle(float fillAlpha, bool fill)
        {
            const int n = 128;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags = HideFlags.HideAndDontSave;
            Color[] px = new Color[n * n];
            float half = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a;
                    if (r > 1f) a = 0f;
                    else if (r > 0.955f) a = 0.95f;                 // the rim
                    else if (!fill) a = 0f;
                    else a = fillAlpha * (0.45f + 0.55f * r);       // soft wash
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // ---------------------------------------------------------- the sound

        /// <summary>Two procedural spatial clips, built once and generated the
        /// same way on every client - the pattern VehicleShotSound established,
        /// for the same reason: there is no donor AudioClip to borrow.</summary>
        static class Sound
        {
            static AudioClip _thump;
            static AudioClip _whistle;

            public static void Thump(Vector3 at) { Play(at, Clip(true), 40f, 900f); }
            public static void Whistle(Vector3 at) { Play(at, Clip(false), 25f, 500f); }

            static void Play(Vector3 at, AudioClip clip, float min, float max)
            {
                if (clip == null) return;
                try
                {
                    GameObject go = new GameObject("NDR Mortar Sound");
                    go.transform.position = at;
                    AudioSource s = go.AddComponent<AudioSource>();
                    s.clip = clip;
                    s.loop = false;
                    s.playOnAwake = false;
                    s.spatialBlend = 1f;
                    s.rolloffMode = AudioRolloffMode.Logarithmic;
                    s.dopplerLevel = 0f;
                    s.minDistance = min;
                    s.maxDistance = max;
                    s.volume = 1f;
                    s.Play();
                    UnityEngine.Object.Destroy(go, clip.length + 1f);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Mortar sound: " + ex.Message);
                }
            }

            static AudioClip Clip(bool thump)
            {
                if (thump && _thump != null) return _thump;
                if (!thump && _whistle != null) return _whistle;
                try
                {
                    const int rate = 44100;
                    float seconds = thump ? 1.5f : 1.6f;
                    float[] data = new float[Mathf.RoundToInt(rate * seconds)];
                    int seed = thump ? 411 : 907;
                    float filtered = 0f;
                    for (int i = 0; i < data.Length; i++)
                    {
                        float t = (float)i / rate;
                        seed = seed * 1103515245 + 12345;
                        float noise = (((seed >> 16) & 0x7fff) / 16383.5f) - 1f;
                        filtered = filtered * 0.972f + noise * 0.028f;
                        if (thump)
                        {
                            // A deep, hollow WHUMP out of a barrel, not a crack.
                            float body = (Mathf.Sin(2f * Mathf.PI * 41f * t) * 1.00f
                                + Mathf.Sin(2f * Mathf.PI * 63f * t) * 0.45f)
                                * Mathf.Exp(-t * 3.1f);
                            float blast = filtered * 2.6f * Mathf.Exp(-t * 4.5f);
                            data[i] = Mathf.Clamp(body + blast, -1f, 1f);
                        }
                        else
                        {
                            // The incoming: a descending whistle that grows.
                            float f = 1500f - 1050f * (t / seconds);
                            float swell = Mathf.Clamp01(t / seconds);
                            data[i] = Mathf.Clamp(
                                Mathf.Sin(2f * Mathf.PI * f * t) * 0.55f * swell
                                + filtered * 0.9f * swell, -1f, 1f);
                        }
                    }
                    AudioClip clip = AudioClip.Create(thump ? "NDR_MortarThump"
                        : "NDR_MortarIncoming", data.Length, 1, rate, false);
                    clip.SetData(data, 0);
                    if (thump) _thump = clip; else _whistle = clip;
                    return clip;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Mortar sound build: " + ex.Message);
                    return null;
                }
            }
        }

        // -------------------------------------------------------- the network

        /// <summary>One event, two directions: "a shell went off here, apply it
        /// to what you own" from a joined client to the master, and the same
        /// event with a fourth float from the master to everybody when the NPC
        /// crew fired it. Built exactly like Admin.Net, including its refusal to
        /// hook when the configured code collides with another channel.</summary>
        internal static class Net
        {
            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optionsType;
            static FieldInfo _onEvent;

            static void EnsureHooked()
            {
                if (_hooked || _failed || _cfgEventCode == null) return;
                try
                {
                    int code = _cfgEventCode.Value;
                    int drone = RevivalPlugin.CfgDroneEventCode == null
                        ? 176 : RevivalPlugin.CfgDroneEventCode.Value;
                    // The surveillance drone takes FOUR codes from its base and
                    // the troop landings THREE from theirs. Both were missing
                    // from the first version of this guard, and the first
                    // default picked (184) sat inside the drone's block: its
                    // receiver only ignored our event because it demands seven
                    // floats. A length check is not a channel.
                    int surv = DroneGear.CfgSurvEventCode == null
                        ? 182 : DroneGear.CfgSurvEventCode.Value;
                    int troops = RevivalTroopInsertion.CfgEventCode == null
                        ? 160 : RevivalTroopInsertion.CfgEventCode.Value;
                    if (code < 0 || code > 199
                        || (code >= drone && code <= drone + 4)
                        || (code >= surv && code <= surv + 3)
                        || (code >= troops && code <= troops + 2)
                        || (RevivalPlugin.CfgTurretEventCode != null
                            && code == RevivalPlugin.CfgTurretEventCode.Value)
                        || (RevivalPlugin.CfgAdminEventCode != null
                            && code == RevivalPlugin.CfgAdminEventCode.Value)
                        || (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                            && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value))
                        throw new Exception("event code " + code + " overlaps another channel");

                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) throw new Exception("PhotonNetwork missing");
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _onEvent = AccessTools.Field(photon, "OnEventCall");
                    _optionsType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (_raise == null || _onEvent == null)
                        throw new Exception("Photon event reflection path incomplete");
                    MethodInfo own = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_onEvent.FieldType, own);
                    Delegate current = _onEvent.GetValue(null) as Delegate;
                    _onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Mortar network attached: event " + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Mortar network not attached: " + ex);
                }
            }

            public static void SendImpact(Vector3 point)
            {
                Send(point, 0f);
            }

            /// <summary>A shell the NPC crew fired, going the OTHER way: the
            /// master tells every client where it landed so each of them can
            /// apply it to its own player. The fourth float is what tells the
            /// two apart; an old three-float payload still reads as a player's
            /// shell, which is what it was.</summary>
            public static void SendNpcImpact(Vector3 point)
            {
                Send(point, 1f);
            }

            static void Send(Vector3 point, float kind)
            {
                try
                {
                    EnsureHooked();
                    if (!_hooked) return;
                    object options = _optionsType == null ? null
                        : Activator.CreateInstance(_optionsType);
                    _raise.Invoke(null, new object[] {
                        (byte)_cfgEventCode.Value,
                        new float[] { point.x, point.y, point.z, kind },
                        true, options });
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Mortar: impact event not sent: " + ex.Message);
                }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (_cfgEventCode == null || code != (byte)_cfgEventCode.Value) return;
                try
                {
                    float[] d = content as float[];
                    if (d == null || d.Length < 3) return;
                    Vector3 point = new Vector3(d[0], d[1], d[2]);

                    // An NPC crew's shell. It has already gone off on the master
                    // for the NPCs and vehicles it owns; here it is our own
                    // player's problem and nobody else's.
                    if (d.Length > 3 && d[3] > 0.5f)
                    {
                        Self(point);
                        LayNearest(point);
                        return;
                    }

                    // A player's shell. Only the owner of the NPCs and vehicles
                    // has anything to do with it, and players are the shooter's
                    // business.
                    if (!Master()) return;
                    int npc, veh, plr;
                    Sweep(point, false, out npc, out veh, out plr);
                    RevivalPlugin.L.LogInfo("Mortar: impact from player #" + sender
                        + " applied - " + npc + " NPC, " + veh + " vehicle.");
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Mortar: impact event: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------ faction shield

        /// <summary>
        /// The last line of defence for the one hard requirement: a casualty
        /// from the firing player's own faction must never turn him into a
        /// traitor.
        ///
        /// It does not need to know WHICH code would change the faction, which
        /// is the point - it watches the value itself. The player's faction is
        /// PlayerStatisticsManager.GetPlayerInfo(player).fraction (CONFIRMED,
        /// REVERSE_ENGINEERING 23), and Traitor is 6 in the Fraction enum. From
        /// the moment a fire order leaves the gun until a few seconds after the
        /// last shell lands, a flip to Traitor is written straight back and
        /// logged.
        ///
        /// HYPOTHESIS, deliberately labelled: that any game code writes that
        /// field on a friendly kill has NOT been read out of the IL, and if
        /// PlayerInfo turns out to be a struct the write cannot stick - in which
        /// case this says so once and gives up instead of pretending. The real
        /// protection is that our damage carries owner 0 and is credited to
        /// nobody; this only catches what that might have missed.
        /// </summary>
        internal static class FactionShield
        {
            const int Traitor = 6;

            static bool _looked;
            static MethodInfo _getInfo;
            static bool _infoStatic;
            static object _manager;
            static FieldInfo _fraction;
            static PropertyInfo _fractionProp;
            static bool _valueType;
            static bool _warned;

            static int _mine = -1;          // the faction we had when we fired
            static float _until;            // guard window end
            static float _nextCheck;

            /// <summary>Remember the faction and open the guard window.</summary>
            public static void Arm()
            {
                if (_cfgProtectFaction != null && !_cfgProtectFaction.Value) return;
                GameObject me = MapTools.LocalPlayer();
                if (me == null) return;
                int f = FactionOf(me);
                if (f < 0) return;
                if (_mine < 0 || Time.time > _until) _mine = f;
                // Long enough to cover a whole salvo plus its flight time and
                // the death animations after it.
                _until = Time.time + 25f + Flight(MaxRange);
            }

            public static void Tick()
            {
                if (_cfgProtectFaction != null && !_cfgProtectFaction.Value) return;
                if (_mine < 0 || Time.time > _until) return;
                if (Time.time < _nextCheck) return;
                _nextCheck = Time.time + 0.5f;

                GameObject me = MapTools.LocalPlayer();
                if (me == null) return;
                int now = FactionOf(me);
                if (now < 0 || now == _mine) return;
                if (now != Traitor) { _mine = now; return; }   // changed by choice, not by us

                if (!Restore(me, _mine))
                {
                    if (!_warned)
                    {
                        _warned = true;
                        RevivalPlugin.L.LogWarning("Mortar: the faction flipped to Traitor "
                            + "after a fire mission and could NOT be written back. Set "
                            + "Mortar/BlastDamage to 0 (it already is by default) and "
                            + "report this - the damage path itself is credited to owner 0 "
                            + "and should never have caused it.");
                    }
                    return;
                }
                RevivalPlugin.L.LogWarning("Mortar: faction had flipped to Traitor after a "
                    + "fire mission - written back to " + _mine + ".");
                Turret.Hinweis(Loc.T(
                    "Миномёт: фракция "
                    + "сохранена",
                    "Mortar: your faction was kept"), 4f);
            }

            /// <summary>Are these two on the same side? False whenever either
            /// faction cannot be read - see the note at the call site.</summary>
            public static bool SameFactionAsLocal(GameObject other)
            {
                GameObject me = MapTools.LocalPlayer();
                if (me == null || other == null) return false;
                if (ReferenceEquals(me, other)) return false;   // the shooter is fair game
                int a = FactionOf(me);
                int b = FactionOf(other);
                return a >= 0 && b >= 0 && a == b;
            }

            static int FactionOf(GameObject player)
            {
                object info = Info(player);
                if (info == null) return -1;
                try
                {
                    if (_fraction != null) return Convert.ToInt32(_fraction.GetValue(info));
                    if (_fractionProp != null) return Convert.ToInt32(_fractionProp.GetValue(info, null));
                }
                catch { }
                return -1;
            }

            static bool Restore(GameObject player, int value)
            {
                if (_valueType || _fraction == null) return false;
                object info = Info(player);
                if (info == null) return false;
                try
                {
                    // fraction is the Fraction enum in the game (Traitor 6), but
                    // the field is read through reflection and an int field is
                    // just as plausible. Enum.ToObject on a non-enum type throws,
                    // so the type decides, not a value test.
                    object boxed = _fraction.FieldType.IsEnum
                        ? Enum.ToObject(_fraction.FieldType, value)
                        : (object)value;
                    _fraction.SetValue(info, boxed);
                    return FactionOf(player) == value;
                }
                catch { return false; }
            }

            static object Info(GameObject player)
            {
                if (!Look()) return null;
                try
                {
                    return _infoStatic
                        ? _getInfo.Invoke(null, new object[] { player })
                        : _getInfo.Invoke(_manager, new object[] { player });
                }
                catch { return null; }
            }

            static bool Look()
            {
                if (_looked) return _getInfo != null
                    && (_fraction != null || _fractionProp != null);
                _looked = true;
                try
                {
                    Type t = RevivalPlugin.TypeByName("PlayerStatisticsManager");
                    if (t == null) { Miss("PlayerStatisticsManager not found"); return false; }

                    MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Static | BindingFlags.Instance);
                    for (int i = 0; i < ms.Length; i++)
                    {
                        if (ms[i].Name != "GetPlayerInfo") continue;
                        ParameterInfo[] ps = ms[i].GetParameters();
                        if (ps.Length != 1) continue;
                        if (!ps[0].ParameterType.IsAssignableFrom(typeof(GameObject))) continue;
                        _getInfo = ms[i];
                        _infoStatic = ms[i].IsStatic;
                        break;
                    }
                    if (_getInfo == null) { Miss("GetPlayerInfo(GameObject) not found"); return false; }
                    if (!_infoStatic)
                    {
                        UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                        if (all.Length > 0) _manager = all[0];
                        if (_manager == null) { Miss("no PlayerStatisticsManager instance"); return false; }
                    }

                    Type ret = _getInfo.ReturnType;
                    _valueType = ret.IsValueType;
                    _fraction = AccessTools.Field(ret, "fraction");
                    if (_fraction == null) _fraction = AccessTools.Field(ret, "Fraction");
                    if (_fraction == null)
                    {
                        _fractionProp = ret.GetProperty("fraction",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (_fractionProp == null)
                            _fractionProp = ret.GetProperty("Fraction",
                                BindingFlags.Public | BindingFlags.Instance);
                    }
                    if (_fraction == null && _fractionProp == null)
                    { Miss("PlayerInfo has no fraction field"); return false; }
                    if (_valueType)
                        RevivalPlugin.L.LogInfo("Mortar: PlayerInfo is a value type - the "
                            + "faction can be READ but not written back. The owner-0 damage "
                            + "path is what protects the faction; this net is read-only.");
                    return true;
                }
                catch (Exception ex) { Miss(ex.Message); return false; }
            }

            static void Miss(string why)
            {
                RevivalPlugin.L.LogWarning("Mortar: the faction shield is not available ("
                    + why + "). The mortar's own damage is still credited to owner 0, "
                    + "which is what keeps a friendly casualty off the shooter's record.");
            }
        }
    }

    // The M1943 tube's geometry (MortarModel) is gone with the tube: the
    // settlement gun is the vehicle in RevivalArtyBattery.cs (ArtyModel), which
    // carries its own copy of the cylinder builder this class used to hold.
}
