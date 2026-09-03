// Next Day: Survival - Revival Toolkit
//
// Convoy vehicle repair - the whole system in one file.
//
// The player can bring a DESTROYED patrol/convoy vehicle (APC or tank) back to
// life on the road with two new carried items, in two steps:
//
//   1. Fire extinguisher (2063). Aim at the burning wreck and press the repair
//      key. The body freezes, a progress bar runs (a short "spraying" action),
//      and the wreck fire goes out - the vehicle is still broken ("kaputt"),
//      but the flames stop and cannot come back until it is either repaired or
//      destroyed anew.
//   2. Heavy tool kit (2064). With the fire out, aim at the same wreck and press
//      the key again. A longer progress bar runs - the same idea as the game's
//      own repair kit ("vehicle_repair_01"), which repairs a vehicle to
//      DurabilityMax - and the vehicle is whole again. It is handed back to the
//      game as an ordinary vehicle: Patrol stops managing it, so it never
//      despawns, and if there is fuel in the tank the player can drive off.
//
// WHY A SEPARATE FILE. RevivalPlugin.cs is one ~19k-line file. Keeping this
// feature in its own file (its own top-level classes, no partial of the giant
// class) lets several agents work in parallel and lets the integration agent
// merge by concatenation instead of untangling one file - see AGENTS.md, the
// same-file exception, and the drone rework / vehicle modules that did the same.
// The ONLY edits this feature makes to RevivalPlugin.cs are a handful of clearly
// marked one-line seams (BindConfig, AddItems, Install, Tick, Draw) plus one
// small additive internal method on Patrol (ReleaseRepaired). Everything else -
// the fire suppression, the body freeze, the targeting and the two-step state
// machine - lives here and reaches the rest of the plugin only through the
// existing internal helpers (Turret.HasItem/TakeItem/Hinweis, MapTools.
// LocalPlayer, FireEffect.WreckName, Tank.IstPanzer, VehicleWreck).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs. Player-facing strings go through Loc.T, whose
// Russian half is real Cyrillic - this file is therefore UTF-8 (no BOM) and is
// compiled with /codepage:65001 like the main file.
//
// KNOWN LIMITATIONS (documented for the acceptance run, not blockers):
//   - Networking. VehicleGameSystem.SetDurabilityValue only RPCs the repair to
//     the other clients when the local player OWNS the vehicle's PhotonView
//     (the master client owns patrol vehicles). A non-owner's repair is
//     local-visual and may resync; a proper ownership handover is a later piece
//     of work, like the surveillance drone's networking.
//   - Interaction trigger. The game exposes no item-use / right-click hook (see
//     the drone rework note), so both steps are driven by one config key
//     (default F) that only acts when on foot, aiming at a wreck, and carrying
//     the matching item. F is the game's own interact key; if it clashes in
//     game, rebind ConvoyRepair/Key.
//   - The player animation is best-effort (the game's repair animation is a
//     large state-machine method); the guaranteed feedback is the progress bar
//     and the frozen body.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Marker put on a wreck whose fire has been extinguished. Two jobs: it tells
    /// <see cref="ConvoyFireHook"/> to keep the wreck fire from respawning while
    /// the vehicle is still destroyed, and it lets the repair step recognise a
    /// wreck that is ready for the tool kit. Removed again on repair, so a
    /// vehicle destroyed a second time burns normally.
    /// </summary>
    public sealed class NdrExtinguished : MonoBehaviour { }

    /// <summary>
    /// Config, the two new items, the targeting, the two-step state machine and
    /// the on-screen prompt/progress bar for convoy vehicle repair.
    /// </summary>
    public static class ConvoyRepair
    {
        // Fresh ids. items.tsv (the game's items) has nothing at 2063/2064, and
        // the plugin's own items occupy 1160-1163, 2050-2057 and 2060-2062, so
        // this block is free. Verified at build time by verify.py.
        public const int DEF_EXTINGUISHER = 2063;
        public const int DEF_TOOLKIT      = 2064;
        const int DEF_DONOR = 2030;           // the generic carryable other gear clones

        public static ConfigEntry<bool>    CfgEnabled;
        public static ConfigEntry<KeyCode> CfgKey;
        public static ConfigEntry<float>   CfgRange;
        public static ConfigEntry<float>   CfgAim;
        public static ConfigEntry<float>   CfgExtinguishSeconds;
        public static ConfigEntry<float>   CfgRepairSeconds;
        public static ConfigEntry<bool>    CfgConsumeExtinguisher;
        public static ConfigEntry<bool>    CfgConsumeToolKit;
        public static ConfigEntry<bool>    CfgNoDespawn;
        public static ConfigEntry<int>     CfgExtinguisherId;
        public static ConfigEntry<int>     CfgToolKitId;

        static int ExtId  { get { return CfgExtinguisherId != null ? CfgExtinguisherId.Value : DEF_EXTINGUISHER; } }
        static int KitId  { get { return CfgToolKitId != null ? CfgToolKitId.Value : DEF_TOOLKIT; } }
        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        // ------------------------------------------------------------- state

        enum Phase { Idle, Extinguishing, Repairing }

        static Phase _phase = Phase.Idle;
        static float _start;
        static float _len;
        static Component _vgs;          // VehicleGameSystem being worked on
        static GameObject _car;         // its GameObject
        static string _prompt;          // the "[F] ..." hint, set every idle Tick

        // Cheap cache so the per-frame target scan does not call
        // FindObjectsOfType every single frame.
        static float _scanUntil;
        static Component _scanVgs;
        static GameObject _scanCar;
        static bool _scanBurning;

        // Same throttle for the "am I seated?" check: InVehicle runs a full
        // FindObjectsOfType(VehicleGameSystem) and TickIdle calls it EVERY frame
        // on foot, before the (already cached) wreck scan. Without this cache it
        // was one whole-scene scan per frame while walking around - the 6.1 frame
        // drop, the same class of bug the 6.0 HasItem cache fixed. Cached like
        // DroneGear.InVehicle (0.5 s); seating changes are never that fast.
        static float _inVehUntil;
        static bool _inVehResult;

        /// <summary>True while a spray/repair action runs; the body is frozen
        /// then (see <see cref="ConvoyFreezeHook"/>).</summary>
        public static bool Busy { get { return _phase != Phase.Idle; } }

        // ------------------------------------------------------------- Config

        public static void BindConfig(ConfigFile cfg)
        {
            // .cfg descriptions stay German by project convention - only the
            // admin who opens the file reads them.
            CfgEnabled = cfg.Bind("ConvoyRepair", "Enabled", true,
                "Zerstoerte Patrouillen-/Konvoifahrzeuge mit Feuerloescher und "
                + "schwerem Werkzeugkasten wieder instand setzen.");
            CfgKey = cfg.Bind("ConvoyRepair", "Key", KeyCode.F,
                "Taste, die - zu Fuss, mit Blick auf ein Wrack und mit dem passenden "
                + "Item im Rucksack - Loeschen bzw. Reparieren startet. F ist die "
                + "Interaktionstaste des Spiels; bei Konflikt hier umlegen.");
            CfgRange = cfg.Bind("ConvoyRepair", "Range", 6f,
                "Reichweite in Metern: so nah muss man am Wrack stehen.");
            CfgAim = cfg.Bind("ConvoyRepair", "AimDot", 0.4f,
                "Wie genau man das Wrack anschauen muss (Skalarprodukt Blick/Richtung, "
                + "0 = egal, 1 = exakt). Sehr nahe Wracks (<3 m) gelten immer.");
            CfgExtinguishSeconds = cfg.Bind("ConvoyRepair", "ExtinguishSeconds", 4f,
                "Dauer der Loesch-Aktion in Sekunden (Ladebalken, Koerper steht still).");
            CfgRepairSeconds = cfg.Bind("ConvoyRepair", "RepairSeconds", 9f,
                "Dauer der Reparatur mit dem schweren Werkzeugkasten in Sekunden.");
            CfgConsumeExtinguisher = cfg.Bind("ConvoyRepair", "ConsumeExtinguisher", true,
                "Den Feuerloescher beim Loeschen verbrauchen.");
            CfgConsumeToolKit = cfg.Bind("ConvoyRepair", "ConsumeToolKit", true,
                "Den schweren Werkzeugkasten bei der Reparatur verbrauchen (wie der "
                + "normale Reparatursatz des Spiels).");
            CfgNoDespawn = cfg.Bind("ConvoyRepair", "RepairedNeverDespawns", true,
                "Ein repariertes Patrouillenfahrzeug aus der Patrouillenverwaltung "
                + "nehmen, damit es nicht mehr nach WreckSeconds verschwindet.");
            CfgExtinguisherId = cfg.Bind("ConvoyRepair", "ExtinguisherItemId", DEF_EXTINGUISHER,
                "Item-Id des Feuerloeschers.");
            CfgToolKitId = cfg.Bind("ConvoyRepair", "ToolKitItemId", DEF_TOOLKIT,
                "Item-Id des schweren Werkzeugkastens.");
        }

        // --------------------------------------------------------- Item table

        /// <summary>
        /// Appends the two new items to the shared item table. Called once from
        /// BuildItemTable after DroneGear.AddItems. Placeholder art: both reuse
        /// the ammo50 (extinguisher-bottle-ish tin) and jammer meshes/icons so
        /// the build and the runtime self-test are green; a Codex asset job can
        /// give them dedicated meshes/icons later without touching this table.
        /// </summary>
        public static void AddItems(List<ItemDef> items)
        {
            // Fire extinguisher (2063) - placeholder art: the .50 tin box.
            items.Add(new ItemDef(
                ExtId, DEF_DONOR, false,
                "Огнетушитель", "Fire extinguisher",
                "Большой углекислотный огнетушитель. Погасите им пламя на "
                + "подбитой бронемашине или танке: подойдите к горящему остову, "
                + "прицельтесь и нажмите клавишу - несколько секунд работы, и огонь "
                + "потухнет. Машина при этом остаётся разбитой; починить её потом "
                + "можно тяжёлым набором инструментов.",
                "A large CO2 fire extinguisher. Use it to put out the flames on a "
                + "knocked-out APC or tank: walk up to the burning wreck, aim, and "
                + "hold the key for a few seconds until the fire dies. The vehicle "
                + "stays wrecked; a heavy tool kit repairs it afterwards.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png",
                "ammo50_icon.png", null,
                1, 0, 6.0f));

            // Heavy tool kit (2064) - placeholder art: the jammer crate.
            items.Add(new ItemDef(
                KitId, DEF_DONOR, false,
                "Тяжёлый набор инструментов", "Heavy tool kit",
                "Полевой ремонтный комплект для бронетехники: домкраты, сварка, "
                + "запасные узлы. В отличие от обычного набора он поднимает даже "
                + "полностью уничтоженную машину - но лишь после того, как сбито "
                + "пламя огнетушителем. Прицельтесь в потушенный остов и держите "
                + "клавишу; когда полоса заполнится, машина снова на ходу. Если в "
                + "баке есть топливо, можно уезжать.",
                "A field repair kit for armour: jacks, welding gear, spare "
                + "assemblies. Unlike the ordinary kit it can raise even a fully "
                + "destroyed vehicle - but only once the fire has been put out with "
                + "an extinguisher. Aim at the extinguished wreck and hold the key; "
                + "when the bar fills the vehicle runs again. With fuel in the tank "
                + "you can drive off.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 12.0f));
        }

        // ------------------------------------------------------------- Install

        public static void Install(Harmony harmony)
        {
            if (!Enabled) return;
            try
            {
                ConvoyFireHook.Install(harmony);
                ConvoyFreezeHook.Install(harmony);
                RevivalPlugin.L.LogInfo("Convoy repair: extinguisher (" + ExtId
                    + ") and heavy tool kit (" + KitId + ") active.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Convoy repair install: " + ex);
            }
        }

        // ------------------------------------------------------------- Runtime

        /// <summary>Called every frame from RevivalPlugin.Update.</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                if (_phase != Phase.Idle) { TickActive(); return; }
                TickIdle();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("ConvoyRepair.Tick: " + ex);
                _phase = Phase.Idle;
            }
        }

        static void TickActive()
        {
            // Abort if the wreck vanished under us (despawned, destroyed anew,
            // scene change) or - for the repair step - the fire came back.
            if (_car == null || _vgs == null)
            { Cancel(Loc.T("Цель потеряна", "Target lost")); return; }
            if (_phase == Phase.Repairing && _car.transform.Find(FireEffect.WreckName) != null
                && _car.GetComponent<NdrExtinguished>() == null)
            { Cancel(Loc.T("Снова горит", "Burning again")); return; }

            if (Time.time - _start < _len) return;   // still working; bar in Draw()

            if (_phase == Phase.Extinguishing) FinishExtinguish();
            else FinishRepair();
        }

        static void TickIdle()
        {
            _prompt = null;

            // Only on foot, never while seated in a vehicle.
            if (InVehicle()) return;

            Component vgs; GameObject car; bool burning;
            if (!FindWreck(out vgs, out car, out burning)) return;

            bool haveExt = Turret.HasItem(ExtId);
            bool haveKit = Turret.HasItem(KitId);
            bool pressed = Input.GetKeyDown(CfgKey.Value);
            string keyName = CfgKey.Value.ToString();

            if (burning)
            {
                if (haveExt)
                {
                    _prompt = "[" + keyName + "] " + Loc.T("Потушить пожар", "Extinguish fire");
                    if (pressed) Begin(Phase.Extinguishing, vgs, car);
                }
                else
                {
                    _prompt = Loc.T("Нужен огнетушитель", "Fire extinguisher needed");
                }
            }
            else // extinguished wreck, still destroyed - ready for the tool kit
            {
                if (haveKit)
                {
                    _prompt = "[" + keyName + "] " + Loc.T("Починить машину", "Repair vehicle");
                    if (pressed) Begin(Phase.Repairing, vgs, car);
                }
                else
                {
                    _prompt = Loc.T("Нужен тяжёлый набор инструментов", "Heavy tool kit needed");
                }
            }
        }

        static void Begin(Phase phase, Component vgs, GameObject car)
        {
            _phase = phase;
            _vgs = vgs;
            _car = car;
            _start = Time.time;
            _len = phase == Phase.Extinguishing
                ? Mathf.Max(0.5f, CfgExtinguishSeconds.Value)
                : Mathf.Max(0.5f, CfgRepairSeconds.Value);
            _prompt = null;

            TryPlayAnimation();
            Turret.Hinweis(phase == Phase.Extinguishing
                ? Loc.T("Тушим...", "Extinguishing...")
                : Loc.T("Ремонт...", "Repairing..."), 1.5f);
        }

        static void Cancel(string why)
        {
            RevivalPlugin.L.LogInfo("Convoy repair: action cancelled - " + why + ".");
            Turret.Hinweis(why, 1.5f);
            _phase = Phase.Idle;
            _vgs = null; _car = null;
        }

        // ------------------------------------------------------------- Steps

        static void FinishExtinguish()
        {
            GameObject car = _car;
            _phase = Phase.Idle;
            _vgs = null; _car = null;

            // Kill the wreck fire and stop it from coming back while destroyed.
            Transform fire = car.transform.Find(FireEffect.WreckName);
            if (fire != null) UnityEngine.Object.Destroy(fire.gameObject);
            if (car.GetComponent<NdrExtinguished>() == null)
                car.AddComponent<NdrExtinguished>();

            if (CfgConsumeExtinguisher.Value)
                Turret.TakeItem(ExtId, "Feuerloescher");

            RevivalPlugin.L.LogInfo("Convoy repair: fire extinguished on a wreck.");
            Turret.Hinweis(Loc.T("Пожар потушен. Почините тяжёлым набором инструментов.",
                                 "Fire out. Repair it with the heavy tool kit."), 3f);
        }

        static void FinishRepair()
        {
            Component vgs = _vgs;
            GameObject car = _car;
            _phase = Phase.Idle;
            _vgs = null; _car = null;

            float max = GetFloat(vgs, "DurabilityMax", 2000f);
            if (max <= 0f) max = 2000f;

            // The game's own repair kit repairs to DurabilityMax through
            // SetDurabilityValue, which sets the field, clears the damage smoke,
            // and RPCs the new value when the vehicle's PhotonView is ours.
            bool ok = SetDurability(vgs, max);
            if (!ok)
            {
                Turret.Hinweis(Loc.T("Не удалось починить", "Repair failed"), 2.5f);
                return;
            }

            // The vehicle is no longer a wreck: drop the suppression marker (so a
            // future destruction burns normally) and remove any lingering fire.
            NdrExtinguished mark = car.GetComponent<NdrExtinguished>();
            if (mark != null) UnityEngine.Object.Destroy(mark);
            Transform fire = car.transform.Find(FireEffect.WreckName);
            if (fire != null) UnityEngine.Object.Destroy(fire.gameObject);

            // Hand it back to the game as an ordinary vehicle: Patrol must stop
            // managing it, or it despawns after WreckSeconds regardless of its
            // restored durability.
            bool wasPatrol = false;
            if (CfgNoDespawn.Value) wasPatrol = Patrol.ReleaseRepaired(car);

            if (CfgConsumeToolKit.Value)
                Turret.TakeItem(KitId, "Werkzeugkasten");

            RevivalPlugin.L.LogInfo("Convoy repair: vehicle repaired to " + max.ToString("0")
                + " durability" + (wasPatrol ? " and released from patrol control" : "")
                + ".");
            Turret.Hinweis(Loc.T("Машина отремонтирована. Есть топливо - можно ехать.",
                                 "Vehicle repaired. Drive off if it has fuel."), 3f);
        }

        // ------------------------------------------------------------- Draw

        /// <summary>Called every frame from RevivalPlugin.OnGUI.</summary>
        public static void Draw()
        {
            if (!Enabled) return;
            try
            {
                if (_phase != Phase.Idle) { DrawBar(); return; }
                if (!string.IsNullOrEmpty(_prompt)) DrawPrompt(_prompt);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("ConvoyRepair.Draw: " + ex); }
        }

        static void DrawBar()
        {
            float t = Mathf.Clamp01((Time.time - _start) / _len);
            float rest = Mathf.Max(0f, _len - (Time.time - _start));
            string label = _phase == Phase.Extinguishing
                ? Loc.T("Тушение пожара", "Extinguishing fire")
                : Loc.T("Ремонт машины", "Repairing vehicle");

            float w = 320f, h = 22f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.66f;

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 2f, y - 2f, w + 4f, h + 4f), Px());
            GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
            GUI.DrawTexture(new Rect(x, y, w, h), Px());
            GUI.color = _phase == Phase.Extinguishing
                ? new Color(0.30f, 0.62f, 0.95f, 0.95f)
                : new Color(0.95f, 0.72f, 0.20f, 0.95f);
            GUI.DrawTexture(new Rect(x, y, w * t, h), Px());
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y - 22f, w, 20f),
                label + "  " + Mathf.CeilToInt(rest) + " s");
            GUI.color = old;
        }

        static void DrawPrompt(string text)
        {
            // Roughly centre the text without TextAnchor (which would pull in
            // UnityEngine.TextRenderingModule, unreferenced by this build): size
            // the plate to the label and place the label with a little padding.
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
            float w = size.x + 24f, h = Mathf.Max(24f, size.y + 8f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.62f;

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(x, y, w, h), Px());
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 12f, y + 4f, size.x + 4f, size.y + 2f), text);
            GUI.color = old;
        }

        static Texture2D _px;
        static Texture2D Px()
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
            }
            return _px;
        }

        // ------------------------------------------------------------- Target

        /// <summary>
        /// The best destroyed vehicle the player is standing near and looking at.
        /// A "wreck" is a VehicleGameSystem with Durability &lt;= 0. `burning` is
        /// true while it still has the NDR wreck fire and has not been
        /// extinguished. Cached for a fraction of a second so the scan does not
        /// run FindObjectsOfType every frame.
        /// </summary>
        static bool FindWreck(out Component vgs, out GameObject car, out bool burning)
        {
            if (Time.time < _scanUntil)
            {
                vgs = _scanVgs; car = _scanCar; burning = _scanBurning;
                return vgs != null && car != null;
            }

            vgs = null; car = null; burning = false;
            _scanUntil = Time.time + 0.25f;
            _scanVgs = null; _scanCar = null; _scanBurning = false;

            GameObject player = MapTools.LocalPlayer();
            if (player == null) return false;
            Vector3 me = player.transform.position;

            Camera cam = Camera.main;
            Vector3 look = cam != null ? cam.transform.forward : player.transform.forward;

            Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (t == null) return false;

            float range = Mathf.Max(1f, CfgRange.Value);
            float aim = Mathf.Clamp(CfgAim.Value, -1f, 1f);
            float best = float.MaxValue;
            Component bestVgs = null;

            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c == null) continue;
                if (GetFloat(c, "Durability", 1f) > 0f) continue;   // not a wreck

                Vector3 to = c.transform.position - me;
                float dist = to.magnitude;
                if (dist > range) continue;
                // Looking at it - but a wreck you are almost touching counts
                // regardless of exact aim.
                if (dist > 3f && Vector3.Dot(to.normalized, look) < aim) continue;

                if (dist < best) { best = dist; bestVgs = c; }
            }

            if (bestVgs == null) return false;

            vgs = bestVgs;
            car = bestVgs.gameObject;
            burning = car.GetComponent<NdrExtinguished>() == null
                      && car.transform.Find(FireEffect.WreckName) != null;

            _scanVgs = vgs; _scanCar = car; _scanBurning = burning;
            return true;
        }

        static bool InVehicle()
        {
            if (Time.time < _inVehUntil) return _inVehResult;
            bool inv = false;
            try
            {
                Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (t != null)
                {
                    UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                    for (int i = 0; i < all.Length; i++)
                    {
                        FieldInfo f = AccessTools.Field(all[i].GetType(), "_localPlayerPassengerId");
                        if (f == null) continue;
                        object v = f.GetValue(all[i]);
                        if (v is int && (int)v >= 0) { inv = true; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("ConvoyRepair: vehicle check: " + ex.Message);
            }
            _inVehResult = inv;
            _inVehUntil = Time.time + 0.5f;
            return inv;
        }

        // ------------------------------------------------------------- Reflection

        static bool SetDurability(Component vgs, float value)
        {
            try
            {
                MethodInfo set = AccessTools.Method(vgs.GetType(), "SetDurabilityValue",
                    new Type[] { typeof(float) }, null);
                if (set != null)
                {
                    set.Invoke(vgs, new object[] { value });
                    return true;
                }
                // Fallback: write the field directly (local-visual only, no smoke
                // handling and no RPC) if the method is missing on some build.
                FieldInfo fi = AccessTools.Field(vgs.GetType(), "Durability");
                if (fi != null && fi.FieldType == typeof(float))
                {
                    fi.SetValue(vgs, value);
                    RevivalPlugin.L.LogWarning("Convoy repair: SetDurabilityValue missing - "
                        + "wrote Durability field directly (no network sync).");
                    return true;
                }
                RevivalPlugin.L.LogWarning("Convoy repair: no way to set durability found.");
                return false;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Convoy repair: SetDurabilityValue: " + ex);
                return false;
            }
        }

        static float GetFloat(Component c, string field, float fallback)
        {
            try
            {
                FieldInfo fi = AccessTools.Field(c.GetType(), field);
                if (fi != null && fi.FieldType == typeof(float))
                    return (float)fi.GetValue(c);
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// Best-effort: play the game's own repair animation on the local player
        /// so the action looks like work, not a stare. The animation entry point
        /// is a large state-machine method; if no simple string overload exists
        /// this is skipped and the progress bar remains the feedback.
        /// </summary>
        static void TryPlayAnimation()
        {
            try
            {
                GameObject player = MapTools.LocalPlayer();
                if (player == null) return;
                Type psc = RevivalPlugin.TypeByName("PlayerStatesController");
                if (psc == null) return;
                Component ctrl = player.GetComponentInChildren(psc);
                if (ctrl == null) return;
                MethodInfo m = AccessTools.Method(psc, "PlayerPlayAnimationState",
                    new Type[] { typeof(string) }, null);
                if (m == null) return;
                m.Invoke(ctrl, new object[] { "vehicle_repair_01" });
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Convoy repair: repair animation skipped: "
                    + ex.Message);
            }
        }
    }

    /// <summary>
    /// Keeps a wreck's fire from respawning once it has been extinguished. A
    /// Harmony prefix on <c>FireEffect.SpawnWreck</c> (which VehicleWreck.Update
    /// calls every frame while a vehicle's durability is &lt;= 0) that skips the
    /// spawn for any vehicle carrying an <see cref="NdrExtinguished"/> marker.
    /// Own file, own patch - the fire suppression touches no existing line.
    /// </summary>
    public static class ConvoyFireHook
    {
        public static void Install(Harmony harmony)
        {
            Type fx = RevivalPlugin.TypeByName("NextDayRevival.FireEffect");
            if (fx == null) fx = typeof(FireEffect);
            MethodInfo spawn = AccessTools.Method(fx, "SpawnWreck",
                new Type[] { typeof(GameObject), typeof(bool) }, null);
            if (spawn == null)
            {
                RevivalPlugin.L.LogWarning("Convoy repair: FireEffect.SpawnWreck not found - "
                    + "extinguished wrecks may re-ignite.");
                return;
            }
            harmony.Patch(spawn,
                new HarmonyMethod(typeof(ConvoyFireHook).GetMethod("Prefix")),
                null, null, null, null);
        }

        /// <summary>Return false to skip the original for extinguished wrecks.</summary>
        public static bool Prefix(GameObject vehicle)
        {
            if (vehicle == null) return true;
            return vehicle.GetComponent<NdrExtinguished>() == null;
        }
    }

    /// <summary>
    /// Freezes the player while a spray/repair action runs, the same way the
    /// drone rework freezes the pilot: a postfix on the game's "cannot do X"
    /// predicates that forces them true while <see cref="ConvoyRepair.Busy"/>.
    /// Its own patch, stacked on the same predicates DroneInputHook uses, so this
    /// file never edits DroneInputHook.
    /// </summary>
    public static class ConvoyFreezeHook
    {
        static readonly string[] Sperren = {
            "PlayerMovementController::PlayerCantMovement",
            "PlayerMovementController::PlayerCantRotate",
            "PlayerMovementController::PlayerCantRotateAxisX",
            "PlayerMovementController::PlayerCantJump",
            "PlayerMovementController::PlayerCantRun",
            "PlayerFirearmWeaponController::CantShoot",
            "PlayerMeleeWeaponController::MeleeCantAttack",
            "PlayerGrenadeWeaponController::CantThrowGrenade",
        };

        public static void Postfix(ref bool __result)
        {
            if (ConvoyRepair.Busy) __result = true;
        }

        public static void Install(Harmony harmony)
        {
            HarmonyMethod post = new HarmonyMethod(
                typeof(ConvoyFreezeHook).GetMethod("Postfix"));
            int patched = 0;
            for (int i = 0; i < Sperren.Length; i++)
            {
                string[] parts = Sperren[i].Split(new string[] { "::" },
                                                  StringSplitOptions.None);
                try
                {
                    Type t = RevivalPlugin.TypeByName(parts[0]);
                    MethodInfo m = t == null ? null
                                 : AccessTools.Method(t, parts[1], null, null);
                    if (m == null || m.ReturnType != typeof(bool)) continue;
                    harmony.Patch(m, null, post, null, null, null);
                    patched++;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Convoy repair freeze: " + Sperren[i]
                        + ": " + ex.Message);
                }
            }
            RevivalPlugin.L.LogInfo("Convoy repair: body-freeze on " + patched
                + " predicate(s).");
        }
    }
}
