// Next Day: Survival - Revival Toolkit
//
// The settlement mortar - an M1943 (PM-43) 120 mm tube standing in every
// settlement, loaded by hand and aimed on the game's own map screen.
// Design notes and the seam survey: docs/ai/tasks/stationary-artillery.md.
//
// WHAT IT IS, IN THE ORDER THE PLAYER MEETS IT
//
//   1. A tube stands in the middle of every settlement, on a free, flat patch
//      of ground. It is a LOCAL object built from a mesh this file generates
//      at runtime - no asset file, no network object, no collider. Every
//      client builds the same tube in the same place from the same rule.
//   2. Walking up to it shows "[R] Load" / "[F] Aim". R moves 120 mm bombs
//      (item 2066) out of the backpack into the tube, one press fills it.
//   3. F opens the map and enters AIM MODE: the mouse pointer becomes a
//      crosshair and a highlighted disc around the tube shows how far it
//      reaches, with a second, small ring for the dead zone under the tube.
//   4. A left click inside the reach fires there. No confirmation button -
//      the click IS the fire order, which is what was asked for.
//   5. A few seconds later the bombs land, one after another, inside a
//      dispersion circle around the aim point. They are heard leaving the
//      tube, heard coming in, and seen going off.
//
// THE FACTION RULE, WHICH IS THE ONE HARD REQUIREMENT
//
// A mortar is an area weapon, so it WILL kill people from the firing player's
// own faction. That must never turn him into a traitor. Three separate things
// make sure of it, in order of how much they are relied on:
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
// 184 by default) so the master applies the NPC and vehicle sweep for the
// settlements it owns.
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
    /// Config, emplacements, loading, the map fire control and the impact.
    /// The generated model is <see cref="MortarModel"/>, the Photon channel is
    /// <see cref="Mortar.Net"/>, and the faction net is <see cref="FactionShield"/>.
    /// </summary>
    public static class Mortar
    {
        // THE BOMB'S ITEM ID. 2001..3000 is the AMMUNITION band, and an item's
        // inventory category comes from its id band alone (the range switch in
        // ItemDataManager::GetItemCatData - see the long note at the head of
        // RevivalAntiTankMine.cs). A mortar bomb is ammunition and is never
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

        // -------------------------------------------------------------- state

        /// <summary>One emplacement. Plain data - the tube is driven from
        /// <see cref="Tick"/>, so it needs no MonoBehaviour of its own and no
        /// per-frame Update per settlement.</summary>
        class Tube
        {
            public GameObject Go;
            public Vector3 Muzzle;     // where the bomb leaves, for the report
            public string Name;
            public int Rounds;
            public float ReadyAt;      // Time.time the next mission may start
            public int SettlementId;
        }

        /// <summary>A bomb between the tube and the ground. Nothing is modelled
        /// in flight: a mortar bomb is invisible on the way up, and a tracer
        /// would be both wrong and a networked object per round.</summary>
        class Shell
        {
            public float LeaveAt;
            public float ImpactAt;
            public Vector3 Point;
            public Vector3 From;
            public bool Left;
            public bool Whistled;
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

        static Material _material;
        static Mesh _mesh;
        static Texture2D _px;
        static Texture2D _disc;
        static Texture2D _ring;

        // --------------------------------------------------------------- item

        /// <summary>Adds the 120 mm bomb to the shared item table. Placeholder
        /// art on purpose: it reuses the 125 mm tank shell's mesh, textures and
        /// icon (shell125.*), which is a fat finned projectile in a box and
        /// reads correctly for a mortar bomb. Three shipped items already do
        /// exactly this (the two vehicle modules and the drone battery); a
        /// dedicated mortar_* generator is a separate asset job.</summary>
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_SHELL, DEF_DONOR, false,
                "120-мм мина (1)", "120 mm mortar bomb (1)",
                "Мина калибра 120 мм "
                + "для миномёта М-1943. "
                + "Заряжается с "
                + "дула.",
                "A 120 mm bomb for the M1943 mortar that stands in the settlements. "
                + "Walk up to a tube, load it, then aim on the map.",
                "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
                "shell125_icon.png", null,
                1, 0, 15.4f));
        }

        // ------------------------------------------------------------- config

        public static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Mortar", "Enabled", true,
                "The settlement mortar: one M1943 tube per settlement, loaded "
                + "with 120 mm bombs (item 2066) and aimed on the map screen.");
            _cfgUseKey = cfg.Bind("Mortar", "UseKey", "F",
                "Pressed at a tube, opens the map and enters aim mode. Pressed "
                + "again (or Escape) leaves it.");
            _cfgLoadKey = cfg.Bind("Mortar", "LoadKey", "R",
                "Pressed at a tube, moves 120 mm bombs from the backpack into "
                + "the tube until it is full or the pack is empty.");
            _cfgUseDistance = cfg.Bind("Mortar", "UseDistance", 3.5f,
                "Metres from the tube at which the prompt appears and the keys "
                + "work.");
            _cfgPlaceRange = cfg.Bind("Mortar", "PlaceRange", 250f,
                "A settlement gets its tube the first time a player comes this "
                + "close. Not tidiness: away from every player the whole-map "
                + "TerrainColliders are off (E-059), so a free-ground search out "
                + "there would hit nothing and could not tell a clear patch from "
                + "the inside of a house.");
            _cfgSkipSafe = cfg.Bind("Mortar", "SkipSafeSettlements", false,
                "true leaves the trader camps (IsSafeSettlement) without a tube.");
            _cfgScale = cfg.Bind("Mortar", "Scale", 1f,
                "Size of the emplacement, 1 = the real M1943 (1.86 m tube).");
            _cfgOpenMap = cfg.Bind("Mortar", "OpenMapOnAim", true,
                "Let the use key open the map itself through "
                + "UIController.ShowMap(true). false leaves opening the map to "
                + "the player; aim mode then waits for it.");

            _cfgMaxRange = cfg.Bind("Mortar", "MaxRange", 1200f,
                "Reach in metres. The real PM-43 throws 5700 m, but the world is "
                + "5000 x 5000, so a true-to-life tube would cover the whole map "
                + "from anywhere. 1200 m is the honest compromise: far enough to "
                + "shell the next settlement, short enough that the map still "
                + "has distance in it.");
            _cfgMinRange = cfg.Bind("Mortar", "MinRange", 80f,
                "Dead zone under the tube. A mortar cannot shoot straight down; "
                + "this also keeps the operator from killing himself.");
            _cfgMagazine = cfg.Bind("Mortar", "Magazine", 6,
                "How many bombs a tube holds.");
            _cfgRounds = cfg.Bind("Mortar", "RoundsPerMission", 3,
                "Bombs sent per fire order. A salvo, not a sniper shot.");
            _cfgRoundInterval = cfg.Bind("Mortar", "RoundInterval", 1.4f,
                "Seconds between two bombs leaving the tube.");
            _cfgCooldown = cfg.Bind("Mortar", "CooldownSeconds", 20f,
                "Seconds after a fire order before the tube accepts the next one.");
            _cfgDispersion = cfg.Bind("Mortar", "Dispersion", 8f,
                "Metres of scatter at any distance. Every bomb lands somewhere "
                + "inside Dispersion + DispersionPercent of the range.");
            _cfgDispersionPercent = cfg.Bind("Mortar", "DispersionPercent", 2f,
                "Extra scatter as a percentage of the firing distance. 2 percent "
                + "at 1200 m is another 24 m - this is an area weapon.");
            _cfgFlightBase = cfg.Bind("Mortar", "FlightSecondsBase", 4f,
                "Seconds of flight time before distance is counted.");
            _cfgFlightPer100 = cfg.Bind("Mortar", "FlightSecondsPer100m", 0.85f,
                "Extra seconds of flight per 100 m. With the base, 1200 m takes "
                + "about 14 s - long enough for a walking target to leave the "
                + "beaten zone, which is what makes a spotter worth having.");

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

        /// <summary>Gives every settlement near the player its tube. The scan is
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
                // bombs that were still in the air, or a mission fired outside
                // goes off at those coordinates inside the next scene.
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
                        + tries + " tries - that settlement keeps no tube.");
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
            Vector3 spot, normal;
            if (!FreeGround(centre, out spot, out normal)) return false;

            // Point the tube away from the settlement centre, so it looks like
            // it was dug in facing outwards rather than into the houses.
            Vector3 out3 = spot - centre;
            out3.y = 0f;
            if (out3.sqrMagnitude < 0.01f) out3 = Vector3.forward;

            GameObject go = new GameObject("NDR Mortar M1943");
            go.transform.position = spot + normal * 0.02f;
            go.transform.rotation = Quaternion.LookRotation(out3.normalized, Vector3.up);
            // Stand it on the surface rather than through it, exactly as the
            // anti-tank mine does (MineObject.Place).
            go.transform.up = normal;
            float sc = Mathf.Clamp(F(_cfgScale, 1f), 0.2f, 4f);
            go.transform.localScale = new Vector3(sc, sc, sc);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = Model();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            Material m = MortarMaterial();
            if (m != null) mr.material = m;

            Tube t = new Tube();
            t.Go = go;
            t.Name = settlement.gameObject.name;
            t.SettlementId = id;
            t.Rounds = 0;
            t.ReadyAt = 0f;
            // The bomb leaves at the muzzle, roughly 1.8 m up the inclined tube.
            t.Muzzle = go.transform.TransformPoint(MortarModel.MuzzleLocal);
            _tubes.Add(t);

            RevivalPlugin.L.LogInfo("Mortar: tube raised for settlement \"" + t.Name
                + "\" at " + spot.ToString("0") + " (centre " + centre.ToString("0")
                + ", " + Vector3.Distance(centre, spot).ToString("0.0") + " m off).");
            return true;
        }

        /// <summary>The centre first, then rings outwards. A spot qualifies when
        /// the ground under it is flat, nothing hangs over it (so the tube is
        /// not inside a house) and no wall stands within arm's length of it.
        ///
        /// The offsets are fixed and walked in a fixed order, so two clients
        /// that search the same loaded terrain agree. They may still differ by a
        /// few metres when one of them searched while less of the world was
        /// streamed in, and that is harmless: the tube is a local object, only
        /// the impact point travels over the wire, and nothing about the fire
        /// mission is derived from where the other client drew the tube.</summary>
        static bool FreeGround(Vector3 centre, out Vector3 spot, out Vector3 normal)
        {
            // TWO passes over the same points, and the second one is the reason
            // every settlement ends up with a tube. The first asks for a tidy
            // emplacement: flat, nothing overhead, no wall within arm's length.
            // In a tight village every one of those points can fail on the wall
            // test alone, and "no mortar at all" is a worse answer than "a
            // mortar close to a wall" - the order was one in EVERY settlement.
            // What the second pass does NOT drop is the roof test: a tube inside
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

            // Roof, bridge, container: anything straight above means the tube
            // would fire into a ceiling.
            Vector3 dummy;
            if (Turret.RaycastObject(point + Vector3.up * 0.4f, Vector3.up,
                                     7f, out dummy) != null) return false;

            // A wall within 1.6 m at chest height. Four rays are enough to find
            // a corner or a container the search would otherwise stand in.
            if (strict)
            {
                for (int i = 0; i < 4; i++)
                {
                    float a = i * Mathf.PI * 0.5f;
                    Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    if (Turret.RaycastObject(point + Vector3.up * 0.9f, dir,
                                             1.6f, out dummy) != null) return false;
                }
            }

            spot = point;
            normal = n;
            return true;
        }

        // -------------------------------------------------- standing at a tube

        /// <summary>The prompt and the two keys, while the player is on foot at
        /// a tube and not already aiming.</summary>
        static void Ground(GameObject player)
        {
            _prompt = null;
            Tube t = Nearest(player.transform.position);
            if (t == null) return;

            int cap = Mathf.Clamp(I(_cfgMagazine, 6), 1, 30);
            string keys = "[" + Key(true) + "] " + Loc.T("Наводка", "Aim")
                + "   [" + Key(false) + "] " + Loc.T("Зарядить", "Load");
            _prompt = Loc.T("Миномёт", "Mortar")
                + " " + t.Rounds + "/" + cap + "   " + keys;

            if (Input.GetKeyDown(Key(false))) Load(t, cap);
            else if (Input.GetKeyDown(Key(true))) EnterAim(t);
        }

        static void Load(Tube t, int cap)
        {
            if (t.Rounds >= cap)
            {
                Say(Loc.T("Миномёт полон",
                          "The tube is full"));
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
                Say(Loc.T("Нужны мины",
                          "No 120 mm bombs in the pack"));
                return;
            }
            RevivalPlugin.L.LogInfo("Mortar: \"" + t.Name + "\" loaded with " + taken
                + " bomb(s), " + t.Rounds + " ready.");
            Say(Loc.T("Миномёт заряжен",
                      "Mortar loaded") + ": " + t.Rounds);
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
            _weOpenedMap = false;
            if (_cfgOpenMap == null || _cfgOpenMap.Value) _weOpenedMap = ShowMap(true);
            if (!_weOpenedMap)
                Say(Loc.T("Откройте карту",
                          "Open the map to aim"));
            RevivalPlugin.L.LogInfo("Mortar: aim mode at \"" + t.Name + "\", "
                + t.Rounds + " ready, reach " + MaxRange.ToString("0") + " m"
                + (_weOpenedMap ? ", map opened by us." : ", waiting for the map."));
        }

        static void LeaveAim(string why)
        {
            if (_aiming == null) return;
            _aiming = null;
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
            if (!Input.GetMouseButtonDown(0)) return;

            Vector3 target;
            if (!MapPoint(texture, cam, world, out target)) return;   // clicked off the map
            Fire(_aiming, target);
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
                Say(Loc.T("Миномёт пуст",
                          "The tube is empty"));
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

            float spread = Spread(dist);
            float tof = Flight(dist);
            float gap = Mathf.Max(0.2f, F(_cfgRoundInterval, 1.4f));
            for (int i = 0; i < rounds; i++)
            {
                // Uniform over the AREA of the dispersion circle, not over its
                // radius: r = R * sqrt(rand), or every bomb crowds the centre.
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                float r = spread * Mathf.Sqrt(UnityEngine.Random.value);
                Vector3 flat = new Vector3(target.x + Mathf.Cos(a) * r, 0f,
                                           target.z + Mathf.Sin(a) * r);
                float y;
                if (!RevivalTroopInsertion.GroundY(flat, out y)) y = target.y;

                Shell s = new Shell();
                s.From = t.Muzzle;
                s.Point = new Vector3(flat.x, y, flat.z);
                s.LeaveAt = Time.time + i * gap;
                s.ImpactAt = s.LeaveAt + tof;
                _inFlight.Add(s);
            }

            RevivalPlugin.L.LogInfo("Mortar: \"" + t.Name + "\" " + rounds
                + " round(s) -> " + target.ToString("0") + " d=" + dist.ToString("0")
                + " tof=" + tof.ToString("0.0") + " s spread=" + spread.ToString("0")
                + " m, " + t.Rounds + " left.");
            Say(Loc.T("Огонь!", "Firing!") + " " + rounds
                + " x " + dist.ToString("0") + " m, "
                + Loc.T("Полёт", "flight") + " "
                + tof.ToString("0") + " s");
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

        /// <summary>Bombs on the way: the muzzle report when one leaves, the
        /// incoming whistle shortly before it lands, and the impact.</summary>
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
                try { Impact(s.Point); }
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
                RevivalPlugin.L.LogWarning("Mortar: NPC_Settlement not found - no tubes "
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
            Turret.Hinweis(Loc.T("Миномёт", "Mortar")
                + ": " + text, 3f);
        }

        static Mesh Model()
        {
            if (_mesh == null) _mesh = MortarModel.Build();
            return _mesh;
        }

        static Material MortarMaterial()
        {
            if (_material != null) return _material;
            try
            {
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                Material m = new Material(sh);
                m.name = "NDR_Mortar_Material";
                // Dark olive-grey gun finish. No texture file: the emplacement
                // is generated geometry, so there is nothing to unwrap and
                // nothing that has to survive the asset receipt.
                Color olive = new Color(0.20f, 0.22f, 0.17f, 1f);
                if (m.HasProperty("_Color")) m.SetColor("_Color", olive);
                m.color = olive;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.25f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.35f);
                _material = m;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mortar material: " + ex.Message);
            }
            return _material;
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
        /// game's own map, the dead zone as a small ring, the tube as a cross,
        /// and a crosshair where the pointer used to be.
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

            // The world point under the crosshair, resolved ONCE per repaint:
            // the ring under the cursor, the status line and the crosshair's
            // colour must all agree, and the projection is reflection work.
            Vector3 under;
            bool onMap = MapPoint(texture, cam, world, out under);
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

                // The dead zone under the tube. Hiding it produces "I clicked
                // and nothing happened".
                if (inner > 0.02f)
                {
                    GUI.color = new Color(0.95f, 0.35f, 0.25f, 0.95f);
                    GUI.DrawTexture(new Rect(c.x - hw * inner, c.y - hh * inner,
                                             hw * inner * 2f, hh * inner * 2f), Ring());
                }

                // The tube itself.
                GUI.color = new Color(1f, 0.85f, 0.25f, 1f);
                GUI.DrawTexture(new Rect(c.x - 5f, c.y - 1f, 10f, 2f), Px());
                GUI.DrawTexture(new Rect(c.x - 1f, c.y - 5f, 2f, 10f), Px());

                // WHERE THE BOMBS WOULD GO. A mortar is an area weapon, and a
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

                // The mission that is on its way: the aim point as a cross with
                // its dispersion circle, and every bomb still in the air as a
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

            DrawCrosshair(onMap, distUnder);
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
        /// stays whole at the map's edge.</summary>
        static void DrawCrosshair(bool onMap, float dist)
        {
            Vector2 m = Event.current.mousePosition;
            Color old = GUI.color;
            bool good = onMap && dist >= MinRange && dist <= MaxRange;

            GUI.color = good ? new Color(0.35f, 1f, 0.45f, 0.95f)
                             : new Color(1f, 0.45f, 0.35f, 0.95f);
            // A cross with a gap in the middle, so the pixel being aimed at is
            // never covered by the crosshair itself.
            GUI.DrawTexture(new Rect(m.x - 13f, m.y - 1f, 9f, 2f), Px());
            GUI.DrawTexture(new Rect(m.x + 4f, m.y - 1f, 9f, 2f), Px());
            GUI.DrawTexture(new Rect(m.x - 1f, m.y - 13f, 2f, 9f), Px());
            GUI.DrawTexture(new Rect(m.x - 1f, m.y + 4f, 2f, 9f), Px());
            GUI.DrawTexture(new Rect(m.x - 1f, m.y - 1f, 2f, 2f), Px());
            GUI.color = old;

            int cap = Mathf.Clamp(I(_cfgMagazine, 6), 1, 30);
            string line = Loc.T("Миномёт", "Mortar")
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
                            // A deep, hollow WHUMP out of a tube, not a crack.
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

        /// <summary>One event, one direction: "a bomb went off here, apply it to
        /// what you own". Built exactly like Admin.Net, including its refusal to
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
                try
                {
                    EnsureHooked();
                    if (!_hooked) return;
                    object options = _optionsType == null ? null
                        : Activator.CreateInstance(_optionsType);
                    _raise.Invoke(null, new object[] {
                        (byte)_cfgEventCode.Value,
                        new float[] { point.x, point.y, point.z },
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
                    // Only the owner of the NPCs and vehicles has anything to do
                    // here, and players are the shooter's business.
                    if (!Master()) return;
                    int npc, veh, plr;
                    Sweep(new Vector3(d[0], d[1], d[2]), false, out npc, out veh, out plr);
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
        /// the moment a fire order leaves the tube until a few seconds after the
        /// last bomb lands, a flip to Traitor is written straight back and
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

    /// <summary>
    /// The M1943 (PM-43) emplacement as generated geometry - baseplate, breech
    /// ball, smooth-bore tube, muzzle ring, bipod and elevating screw.
    ///
    /// Built in code rather than shipped as an .ndmesh on purpose. A tube per
    /// settlement is decoration with no inventory icon, no hand pose and no UV
    /// work, and a generated mesh costs no asset file, no entry in the launch
    /// receipt (ClientIntegrity rejects any file under plugins\assets that the
    /// receipt does not know) and no make_assets.py run to install. The shape
    /// follows the real weapon's dimensions: 1.86 m tube, 120 mm bore, a
    /// circular baseplate just under a metre across, and about 70 degrees of
    /// elevation.
    ///
    /// Winding follows the same rule verify.py enforces on the shipped meshes:
    /// the right-hand normal of each triangle's winding points the same way as
    /// its stored normal, so nothing is culled while it is lit.
    /// </summary>
    internal static class MortarModel
    {
        const float Elevation = 70f;       // degrees above horizontal
        const float TubeLength = 1.86f;
        const float TubeRadius = 0.075f;

        /// <summary>Where a bomb leaves, in the object's local space - the
        /// muzzle at the top of the inclined tube.</summary>
        internal static Vector3 MuzzleLocal
        {
            get
            {
                Vector3 axis = Axis();
                return Breech() + axis * (TubeLength + 0.08f);
            }
        }

        static Vector3 Axis()
        {
            float rad = Elevation * Mathf.Deg2Rad;
            // Local +Z is "forward" (away from the settlement centre), +Y is up.
            return new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad)).normalized;
        }

        static Vector3 Breech() { return new Vector3(0f, 0.10f, -0.22f); }

        internal static Mesh Build()
        {
            List<Vector3> v = new List<Vector3>();
            List<Vector3> n = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> tri = new List<int>();

            Vector3 axis = Axis();
            Vector3 breech = Breech();
            Vector3 muzzle = breech + axis * TubeLength;

            // Baseplate: a shallow round plate the whole thing sits on.
            Cyl(v, n, uv, tri, new Vector3(0f, 0f, -0.22f), new Vector3(0f, 0.09f, -0.22f),
                0.50f, 0.46f, 24);
            // Breech ball: the socket the tube sits in.
            Cyl(v, n, uv, tri, breech - axis * 0.12f, breech + axis * 0.06f,
                0.12f, 0.10f, 14);
            // The tube.
            Cyl(v, n, uv, tri, breech, muzzle, TubeRadius, TubeRadius * 0.94f, 16);
            // Muzzle ring: slightly proud of the tube, so the mouth reads.
            Cyl(v, n, uv, tri, muzzle - axis * 0.02f, muzzle + axis * 0.08f,
                0.095f, 0.090f, 16);

            // Bipod: two legs from two thirds up the tube down to the ground,
            // splayed sideways and forward, plus the elevating screw between
            // them. A real M1943 has exactly this silhouette from the side.
            Vector3 clamp = breech + axis * (TubeLength * 0.62f);
            Cyl(v, n, uv, tri, clamp - new Vector3(0f, 0f, 0.05f),
                clamp + new Vector3(0f, 0f, 0.07f), 0.085f, 0.085f, 10);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 foot = new Vector3(s * 0.62f, 0f, 0.52f);
                Cyl(v, n, uv, tri, clamp, foot, 0.035f, 0.045f, 8);
                // A small shoe so the leg does not end in a point.
                Cyl(v, n, uv, tri, foot, foot + new Vector3(0f, 0.05f, 0f),
                    0.09f, 0.07f, 10);
            }
            // The elevating screw, straight down from the clamp.
            Vector3 screwTop = clamp - new Vector3(0f, 0.02f, 0f);
            Cyl(v, n, uv, tri, new Vector3(0f, 0.06f, screwTop.z), screwTop,
                0.030f, 0.030f, 8);

            Mesh mesh = new Mesh();
            mesh.name = "NDR_Mortar_M1943";
            mesh.vertices = v.ToArray();
            mesh.normals = n.ToArray();
            mesh.uv = uv.ToArray();
            mesh.triangles = tri.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>One closed, capped, possibly tapered cylinder from
        /// <paramref name="a"/> to <paramref name="b"/>. Side and cap vertices
        /// are separate so the caps stay flat and the sides stay round.</summary>
        static void Cyl(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
                        List<int> tri, Vector3 a, Vector3 b,
                        float ra, float rb, int sides)
        {
            Vector3 w = b - a;
            float h = w.magnitude;
            if (h < 1e-4f || sides < 3) return;
            w /= h;

            // A right-handed basis U, V, W with U x V = W, so that the outward
            // direction of ring point i is cos(t)U + sin(t)V.
            Vector3 helper = Mathf.Abs(w.y) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 u = Vector3.Cross(helper, w).normalized;
            Vector3 vv = Vector3.Cross(w, u);

            int sideBase = v.Count;
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 dir = u * Mathf.Cos(t) + vv * Mathf.Sin(t);
                // The side normal of a frustum leans by the taper.
                Vector3 sn = (dir * h + w * (ra - rb)).normalized;
                float uu = (float)i / sides;
                v.Add(a + dir * ra); n.Add(sn); uv.Add(new Vector2(uu, 0f));
                v.Add(b + dir * rb); n.Add(sn); uv.Add(new Vector2(uu, 1f));
            }
            for (int i = 0; i < sides; i++)
            {
                int b0 = sideBase + i * 2;         // bottom i
                int t0 = b0 + 1;                   // top i
                int b1 = b0 + 2;                   // bottom i+1
                int t1 = b0 + 3;                   // top i+1
                // (B_i, T_i+1, T_i) and (B_i, B_i+1, T_i+1) wind outwards - the
                // opposite order winds into the body and would be culled.
                tri.Add(b0); tri.Add(t1); tri.Add(t0);
                tri.Add(b0); tri.Add(b1); tri.Add(t1);
            }

            // Cap at b, outward normal +W.
            int capTop = v.Count;
            v.Add(b); n.Add(w); uv.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 dir = u * Mathf.Cos(t) + vv * Mathf.Sin(t);
                v.Add(b + dir * rb); n.Add(w);
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(t), 0.5f + 0.5f * Mathf.Sin(t)));
            }
            for (int i = 0; i < sides; i++)
            {
                tri.Add(capTop);
                tri.Add(capTop + 1 + i);
                tri.Add(capTop + 2 + i);
            }

            // Cap at a, outward normal -W, wound the other way round.
            int capBottom = v.Count;
            v.Add(a); n.Add(-w); uv.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 dir = u * Mathf.Cos(t) + vv * Mathf.Sin(t);
                v.Add(a + dir * ra); n.Add(-w);
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(t), 0.5f + 0.5f * Mathf.Sin(t)));
            }
            for (int i = 0; i < sides; i++)
            {
                tri.Add(capBottom);
                tri.Add(capBottom + 2 + i);
                tri.Add(capBottom + 1 + i);
            }
        }
    }
}
