using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{

    // ------------------------------------------------- the crew of a patrol

    /// <summary>
    /// The men in the vehicle, and what happens when it burns.
    ///
    /// WHILE IT DRIVES the crew is a NUMBER, not bodies. One man per seat is
    /// counted in <see cref="Patrol"/>, the seats are closed to players
    /// (Turret.FreeSeatPostfix asks Patrol.Besetzt), and that is all. Putting
    /// real NPCs into the seats was tried on paper first and dropped, for two
    /// reasons that are both read out of the game, not guessed:
    ///
    ///   1  `VehicleGameSystem::SetDamageToAllPassengers` and
    ///      `GetPassengersPlayerIds` walk `Passengers` and call
    ///      `GetComponent&lt;PlayerNetworkController&gt;().GetPhotonPlayer` on
    ///      every entry. On an NPC GameObject that GetComponent returns null
    ///      and the call throws - and SetDamageToAllPassengers runs exactly
    ///      when the vehicle is destroyed, which is the one moment that must
    ///      not fail.
    ///   2  A body parented to the seat is parented on the HOST only. The
    ///      other clients get the NPC's own position sync and would see the
    ///      crew standing in the road where the vehicle once spawned. The
    ///      hull is closed - nobody can see the crew inside it anyway.
    ///
    /// WHEN IT IS DESTROYED the number becomes bodies. That is the moment
    /// they are worth their cost: the man who killed the vehicle is standing
    /// within a few dozen metres, and now there are marauders on the ground
    /// looking for him.
    ///
    /// HOW THE BODIES ARE MADE. Not by hand. The game builds an NPC out of a
    /// **settlement** plus one **spawn point** per man, and every value an NPC
    /// needs - appearance, weapon, level, behaviour - is derived from those
    /// two by the game's own code (RE 10, `NPC_Settlement::InitSpawnNpc`). So
    /// this class builds a settlement the size of one wreck: a GameObject at
    /// the wreck, a spawn point at each of the vehicle's own GetOutPoints, a
    /// ring of walk points around it, and then `StartMainInit` - the game's
    /// own entry point - does the rest, including
    /// `PhotonNetwork.InstantiateSceneObject` so every client sees them.
    ///
    /// The settlement is what makes them hunt: its `Update` runs
    /// `PlayersDistanceControll` and the sensors that hand an NPC a target.
    /// Behaviour Aggressive, respawn switched off - they die once and stay
    /// dead. WHICH SIDE they are on comes from the route the vehicle was
    /// driving (`fraction=` in its first waypoint's flags) and is written into
    /// the settlement's `FractionOptions` by <see cref="Fraktion"/>; that one
    /// object is what `NPC_AI2::IsEnemyFraction` reads, so it is the whole
    /// answer to "who do these men shoot at" (RE 23).
    ///
    /// TWO THINGS AN AddComponent DOES NOT GIVE, and both of them stopped this
    /// class dead once each:
    ///
    ///   the lists and the little classes   `Listen`, and the reasons are at
    ///       that method. The second run died on `NPC_SpawnPoint.Quests` being
    ///       null, after the men had already been built.
    ///   the designers' numbers             `Abschreiben`. A settlement built
    ///       at runtime has SensorVisibleDist 0 - a crew that cannot see. The
    ///       values are copied off a settlement the map already has instead of
    ///       being invented here.
    ///
    /// THE PHOTONVIEW WITH ID 0 IS DELIBERATE. `InitSpawnNpc` puts
    /// `photonView.viewID` into the instantiation data as element 0, and on
    /// the other clients `NPC_AI2::FindMySpawnPointAndSet` looks that id up in
    /// `LocalSettlementsDictionary`. A settlement built at runtime is not in
    /// that dictionary on any machine, so the lookup misses and the method
    /// returns - after it has read the weapon id out of element 2, but BEFORE
    /// it applies element 1, the appearance. An unregistered PhotonView
    /// (viewID 0) gives a clean lookup miss and no id collision, while the
    /// remote Start postfix below supplies the missing visual half.
    ///
    /// UNTESTED. Every line of this is read IL. Nothing here has run.
    /// </summary>
    public static class Crew
    {
        /// <summary>Metres between the walk points the crew wanders over.
        /// Wide enough that they spread out around the wreck, tight enough
        /// that they stay a group.</summary>
        const float RingRadius = 9f;

        /// <summary>Tactical points used by the game's real alarm state. They
        /// sit between the ordinary patrol points so an alarm makes the crew
        /// spread out instead of walking the same ring a little faster.</summary>
        const float TacticalRingRadius = 12f;

        /// <summary>The name every crew settlement carries. It is how the
        /// template search tells the map's settlements from our own.</summary>
        internal const string Name = "NDR_PatrolCrew";

        /// <summary>The two custom weapons a crew carries. Most men get the
        /// MG42; a few carry the M72 LAW - see <see cref="CrewLaw"/>.</summary>
        internal const int MG42_ID = 1160;
        internal const int LAW_ID = 1162;

        static List<GameObject> _settlements = new List<GameObject>();
        // Spawn-point instance id -> seven-value NPC customization overlay.
        // -1 preserves the game's generated face/backpack; positive ids replace
        // body, hands, legs, headwear or mask before Photon instantiation data is
        // created, so owner and remote clients receive the same uniform.
        static Dictionary<int, int[]> _appearance = new Dictionary<int, int[]>();

        static FieldInfo _shotDelayCached;

        /// <summary>
        /// Make one MG42 crewman rattle. `NPC_AI2::Start` writes
        /// `_shootingTimerDelayCached = Random.Range(0.2, 0.3)` and
        /// `ShootToTarget` resets `_shootingTimerDelay` to that cache after every
        /// round, so the game fires EVERY NPC at that fixed pace no matter what
        /// the weapon's own `rateOfFire` is - which is exactly why the belt-fed
        /// gun sounded no faster than a bolt rifle. Overwriting the cache with a
        /// small value lets the MG42 fire at close to its own rate. It matters
        /// on the owner, where the AI runs; setting it on a remote puppet is
        /// harmless. CONFIRMED from the IL of Start/ShootToTarget/ShootingActions
        /// and NPC_FirearmWeaponController::FireTo (RE 21.9).
        /// </summary>
        static void SetShotCadence(Component ai)
        {
            try
            {
                if (RevivalPlugin.CfgPatrolCrewMgShotDelay == null) return;
                if (_shotDelayCached == null)
                    _shotDelayCached = AccessTools.Field(ai.GetType(),
                        "_shootingTimerDelayCached");
                if (_shotDelayCached != null)
                    _shotDelayCached.SetValue(ai,
                        Mathf.Max(0f, RevivalPlugin.CfgPatrolCrewMgShotDelay.Value));
            }
            catch { }
        }

        /// <summary>Wreck crews are permanent combatants, not settlements
        /// waiting for a nearby player. The vanilla distance shutdown calls
        /// SetActiveAI(false), which stops the legacy Animation and leaves its
        /// skinned mesh in the bind pose. Skip only that shutdown for the
        /// generated crew settlement; every map settlement keeps the vanilla
        /// distance optimization.</summary>
        public static bool AutoDisablePrefix(object __instance)
        {
            Component settlement = __instance as Component;
            return settlement == null || settlement.gameObject == null
                || settlement.gameObject.name != Name;
        }

        /// <summary>
        /// The switch that froze them, and the same switch that made them
        /// unkillable. One call, four effects (`NPC_AI2::SetPlayVisualizationValue`):
        ///
        ///     Anim.enabled = false            the legacy Animation stops
        ///                                     where it stands - the frozen
        ///                                     figure with the frozen weapon
        ///     RagdollController.SetPhysActive(false)
        ///                                     detectCollisions = false on
        ///                                     EVERY ragdoll rigidbody
        ///     _colliderMain.enabled = false
        ///     _rigidbody.Sleep()
        ///
        /// The second line is why a drone detonating beside them does nothing.
        /// `ExplosionObject::ExplosionPhysicsEffect` damages an NPC only
        /// through a collider tagged `RagdollBone` that `Physics.OverlapSphere`
        /// reports, and a rigidbody with collision detection switched off is
        /// not reported by that query. 550 damage in 7 m never reaches them,
        /// and no damage value would have.
        ///
        /// It is switched off whenever the local player BODY is not in
        /// `PlayersAround`: `CheckVisualizationForLocalPlayer` and
        /// `StartAutoDisableTimer` both do it, neither looks at the camera, and
        /// a drone is not a body. So a wreck crew that is flown to instead of
        /// walked to is frozen and invulnerable at once, from one line.
        ///
        /// E-042 found the neighbouring switch - `SetActiveAI(false)`, which
        /// calls `Animation.Stop()` - and this one was left standing. Same rule
        /// as there, and same reason: only the generated crew settlement
        /// refuses it, every settlement the map brought keeps the vanilla
        /// distance optimization. Enabling is never refused.
        /// </summary>
        public static bool PlayVisualizationPrefix(object __instance, bool __0)
        {
            if (__0) return true;
            Component settlement = __instance as Component;
            return settlement == null || settlement.gameObject == null
                || settlement.gameObject.name != Name;
        }

        internal static void Install(Harmony harmony)
        {
            Type type = RevivalPlugin.TypeByName("NPC_Settlement");
            if (type == null)
            {
                RevivalPlugin.L.LogWarning("Crew: NPC_Settlement not found - distant "
                    + "wreck crews stay frozen and cannot be hit.");
            }
            else
            {
                // Two switches, two patches, and one must not cost the other.
                Haengen(harmony, type, "AutoDisableControl",
                    typeof(Crew).GetMethod("AutoDisablePrefix"),
                    "Crew: wreck crews stay animated at any distance.",
                    "distant wreck crews may enter the T-pose");
                Haengen(harmony, type, "SetPlayVisualizationValue",
                    typeof(Crew).GetMethod("PlayVisualizationPrefix"),
                    "Crew: wreck crews keep animation, collision and ragdoll at any distance.",
                    "a wreck crew reached by drone stays frozen and cannot be hit");
                // BOTH appearance paths, because InitSpawnNpc picks ONE of
                // them per spawn point and it is not the one this hook used to
                // assume. Confirmed in IL (NPC_Settlement::InitSpawnNpc):
                //
                //     if (sp.UseCustomAppearance)
                //         items = GetCustomAppearanceItems(sp, template);
                //     else
                //         items = GetRandomAppearance(sp, db);
                //
                // Punkt() sets UseCustomAppearance from the military template it
                // found on the map, so on every map that HAS one the editor
                // uniform went down the GetCustomAppearanceItems branch, which
                // was not patched - the crew wore the preset and the admin's
                // choice was silently dropped. Both are overlaid now; whichever
                // branch runs, the editor slots win.
                int hooked = 0;
                if (Anziehen(harmony, type, "GetRandomAppearance")) hooked++;
                if (Anziehen(harmony, type, "GetCustomAppearanceItems")) hooked++;
                if (hooked == 0)
                    RevivalPlugin.L.LogWarning("Crew: neither GetRandomAppearance "
                        + "nor GetCustomAppearanceItems found - editor uniforms "
                        + "cannot be applied.");
                else
                    RevivalPlugin.L.LogInfo("Crew: editor uniforms overlay "
                        + hooked + " of 2 appearance path(s).");
            }

            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            if (npc == null)
            {
                RevivalPlugin.L.LogWarning("Crew: NPC_AI2 not found - remote crew "
                    + "appearance cannot be restored.");
                return;
            }
            try
            {
                MethodInfo start = AccessTools.Method(npc, "Start", null, null);
                MethodInfo visualization = AccessTools.Method(npc,
                    "SetPlayVisualizationValue", null, null);
                MethodInfo setActive = AccessTools.Method(npc, "SetActiveAI",
                    null, null);
                if (start != null)
                    harmony.Patch(start, null,
                        new HarmonyMethod(typeof(Crew).GetMethod("NpcStartPostfix")),
                        null, null, null);
                if (visualization != null)
                    harmony.Patch(visualization,
                        new HarmonyMethod(typeof(Crew).GetMethod(
                            "NpcVisualizationPrefix")), null, null, null, null);
                if (setActive != null)
                    harmony.Patch(setActive,
                        new HarmonyMethod(typeof(Crew).GetMethod(
                            "NpcActiveAiPrefix")), null, null, null, null);
                RevivalPlugin.L.LogInfo("Crew: remote NPC appearance and animation "
                    + "repair hooks installed.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Crew: remote NPC hooks failed - " + ex);
            }

            CrewLaw.Install(harmony);
        }

        /// <summary>Hang the uniform overlay on one of the game's two
        /// appearance builders. Returns whether the method was there.</summary>
        static bool Anziehen(Harmony harmony, Type type, string method)
        {
            MethodInfo mi = AccessTools.Method(type, method, null, null);
            if (mi == null) return false;
            try
            {
                harmony.Patch(mi, null, new HarmonyMethod(typeof(Crew).GetMethod(
                    "CustomAppearancePostfix")), null, null, null);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + method
                    + " could not be patched - " + ex.Message);
                return false;
            }
        }

        /// <summary>Overlay the editor's real equipment-slot ids onto the
        /// game's own seven-value appearance result. This runs before the result
        /// is placed in Photon instantiation data, so the existing remote repair
        /// path receives the same complete uniform.</summary>
        public static void CustomAppearancePostfix(object __0, ref int[] __result)
        {
            Component spawn = __0 as Component;
            if (spawn == null || __result == null || __result.Length < 7) return;
            int[] selected;
            int id = spawn.GetInstanceID();
            if (!_appearance.TryGetValue(id, out selected)) return;
            _appearance.Remove(id);
            for (int i = 0; i < 7; i++)
                if (selected[i] > 0) __result[i] = selected[i];
        }

        static void RegisterAppearance(Component spawn,
                                       RevivalComposition.CrewMan spec)
        {
            if (spawn == null || spec == null) return;
            // CustomizationData order, confirmed in
            // NPC_Settlement.GenerateCustomizationDefault:
            // head/face, body, hands, legs, headwear, mask, backpack.
            _appearance[spawn.GetInstanceID()] = new int[] {
                -1, spec.Body, spec.Hands, spec.Legs,
                spec.Headwear, spec.Mask, -1 };
        }

        public static void NpcStartPostfix(object __instance)
        {
            try
            {
                Component ai = __instance as Component;
                object[] data;
                bool isMine;
                if (ai == null || !SpawnData(ai, out data, out isMine)) return;
                int[] appearance = data[1] as int[];
                int weapon = Convert.ToInt32(data[2]);

                // Fire rate first, and on the owner too: this is the machine
                // that runs the AI, and the reason an MG42 crewman rattles
                // instead of plinking. The LAW keeps the vanilla pace.
                if (weapon == MG42_ID) SetShotCadence(ai);

                if (isMine) return;            // the rest only repairs a remote puppet
                if (appearance == null) return;
                CrewRemoteFix fix = ai.gameObject.GetComponent<CrewRemoteFix>();
                if (fix == null) fix = ai.gameObject.AddComponent<CrewRemoteFix>();
                fix.Begin(ai, appearance, weapon);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: remote NPC start repair - "
                    + ex.Message);
            }
        }

        public static bool NpcVisualizationPrefix(object __instance, bool __0)
        {
            if (__0) return true;
            Component ai = __instance as Component;
            object[] data;
            bool isMine;
            return ai == null || !SpawnData(ai, out data, out isMine);
        }

        /// <summary>
        /// The second animation-stop path, and the one a remote puppet has no
        /// settlement to guard. `NPC_AI2::SetActiveAI(false)` calls the legacy
        /// `Animation.Stop()` directly, dropping the skinned mesh into its bind
        /// pose - the T-pose - independently of `SetPlayVisualizationValue`.
        ///
        /// On the owner the generated `NDR_PatrolCrew` settlement is exempted
        /// from `AutoDisableControl`, so it never reaches this call. A Photon
        /// puppet on another player's machine carries no such settlement: it is
        /// reached by `NPC_Settlement::NetworkReInitAllNpc -> TryReInit ->
        /// SetActiveAI` and by any distance shutdown its own scene runs, and
        /// nothing keeps it animated after the one-shot `CrewRemoteFix` has
        /// played the animation once and destroyed itself. That is why the crew
        /// still froze on the colleague's machine.
        ///
        /// `NpcVisualizationPrefix` already keeps a marked crew NPC hittable by
        /// blocking the collider and ragdoll shutdown; this blocks the matching
        /// animation shutdown so the same NPC also stays animated. Enabling
        /// (`__0 == true`) is never refused. Keyed on the replicated
        /// instantiation marker, so it acts on every client and touches only
        /// our own crew NPCs. HYPOTHESIS until confirmed on the remote machine.
        /// </summary>
        public static bool NpcActiveAiPrefix(object __instance, bool __0)
        {
            if (__0) return true;
            Component ai = __instance as Component;
            object[] data;
            bool isMine;
            return ai == null || !SpawnData(ai, out data, out isMine);
        }

        static bool SpawnData(Component ai, out object[] data, out bool isMine)
        {
            data = null;
            isMine = true;
            try
            {
                MethodInfo pv = AccessTools.Method(ai.GetType(), "get_photonView",
                                                   null, null);
                object view = pv == null ? null : pv.Invoke(ai, null);
                if (view == null) return false;
                MethodInfo mine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                if (mine != null) isMine = (bool)mine.Invoke(view, null);
                MethodInfo inst = AccessTools.PropertyGetter(view.GetType(),
                                                              "instantiationData");
                data = inst == null ? null : inst.Invoke(view, null) as object[];
                return data != null && data.Length >= 5 && data[0] != null
                    && Convert.ToInt32(data[0]) == 0 && data[1] is int[];
            }
            catch { return false; }
        }

        internal static bool ApplyRemoteAppearance(Component ai, int[] appearance,
                                                   int weapon, out string problem)
        {
            problem = "";
            if (ai == null) { problem = "NPC component disappeared"; return false; }
            try
            {
                MethodInfo customize = AccessTools.Method(ai.GetType(),
                    "SetCustomization", new Type[] { typeof(int[]) }, null);
                MethodInfo arm = AccessTools.Method(ai.GetType(), "SetMainWeaponId",
                    new Type[] { typeof(int), typeof(bool) }, null);
                MethodInfo visualize = AccessTools.Method(ai.GetType(),
                    "SetPlayVisualizationValue", new Type[] { typeof(bool) }, null);
                if (customize == null || arm == null)
                {
                    problem = "customization or weapon method missing";
                    return false;
                }
                customize.Invoke(ai, new object[] { appearance });
                arm.Invoke(ai, new object[] { weapon, false });
                if (visualize != null) visualize.Invoke(ai, new object[] { true });

                FieldInfo animationField = AccessTools.Field(ai.GetType(), "Anim");
                Component animation = animationField == null ? null
                    : animationField.GetValue(ai) as Component;
                if (animation == null)
                {
                    problem = "animation component not ready";
                    return false;
                }
                PropertyInfo enabled = AccessTools.Property(animation.GetType(), "enabled");
                if (enabled != null) enabled.SetValue(animation, true, null);
                MethodInfo play = AccessTools.Method(animation.GetType(), "Play",
                                                      Type.EmptyTypes, null);
                if (play != null) play.Invoke(animation, null);
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.InnerException == null
                    ? ex.Message : ex.InnerException.Message;
                return false;
            }
        }

        static void Haengen(Harmony harmony, Type type, string spielMethode,
                            MethodInfo eigene, string erfolg, string folge)
        {
            try
            {
                MethodInfo method = AccessTools.Method(type, spielMethode, null, null);
                if (method == null || eigene == null)
                {
                    RevivalPlugin.L.LogWarning("Crew: NPC_Settlement." + spielMethode
                        + " not found - " + folge + ".");
                    return;
                }
                harmony.Patch(method, new HarmonyMethod(eigene), null, null, null, null);
                RevivalPlugin.L.LogInfo(erfolg);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Crew: " + spielMethode + " hook failed - " + ex);
            }
        }

        /// <summary>
        /// The crew of a destroyed vehicle climbs out.
        /// </summary>
        public static void Aussteigen(GameObject car, Component vgs, int count,
                                      bool tank, string fraktion)
        {
            Aussteigen(car, vgs, count, tank, fraktion, null);
        }

        /// <summary>Composition-aware wreck crew. The list order is the editor's
        /// role order for this vehicle; each spawn point receives that role's
        /// validated main weapon and uniform ids. A null list keeps the legacy
        /// config-driven MG42/LAW crew unchanged.</summary>
        internal static void Aussteigen(GameObject car, Component vgs, int count,
                                      bool tank, string fraktion,
                                      List<RevivalComposition.CrewMan> composition)
        {
            if (!RevivalPlugin.CfgPatrolCrew.Value || count <= 0) return;
            if (car == null) return;

            GameObject settlement = null;
            try
            {
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                Type pType = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                Type wType = RevivalPlugin.TypeByName("NPC_WP");
                if (sType == null || pType == null || wType == null)
                {
                    RevivalPlugin.L.LogWarning("Crew: NPC_Settlement, NPC_SpawnPoint "
                        + "or NPC_WP not found - the wreck stays empty.");
                    return;
                }

                Vector3[] wo = Ausstiege(car, vgs, count);

                settlement = new GameObject(Name);
                settlement.transform.position = car.transform.position;

                GameObject leute = new GameObject("People");
                leute.transform.SetParent(settlement.transform, false);
                GameObject wege = new GameObject("WalkPoints");
                wege.transform.SetParent(settlement.transform, false);
                for (int i = 0; i < 8; i++)
                {
                    GameObject wp = new GameObject("WP" + i);
                    wp.transform.SetParent(wege.transform, false);
                    float a = i * Mathf.PI * 2f / 8f;
                    wp.transform.localPosition = new Vector3(
                        Mathf.Cos(a) * RingRadius, 0f, Mathf.Sin(a) * RingRadius);

                    // StartMainInit does not collect transforms here. It asks
                    // AllWalkPointsTr for NPC_WP components, then filters them
                    // by Type. Without this component every NPC receives an
                    // empty _mainWalkPoints list and IdleStateAction indexes
                    // element zero forever. NPC_WP::.ctor sets Type to 1,
                    // which is the settlement's ordinary patrol-point type.
                    wp.AddComponent(wType);

                    // An aggressive NPC switches to TemporaryTask Tactical
                    // when the settlement alarm is raised. That task reads
                    // NPC_WP type 5, not the patrol list above. With no type 5
                    // points RepairTemporaryWalkPointsList indexes an empty
                    // list on every state change and the otherwise complete
                    // AI freezes. Real tactical points carry default values;
                    // their point type is the complete behavioural contract.
                    GameObject tactical = new GameObject("Tactical" + i);
                    tactical.transform.SetParent(wege.transform, false);
                    float ta = a + Mathf.PI / 8f;
                    tactical.transform.localPosition = new Vector3(
                        Mathf.Cos(ta) * TacticalRingRadius, 0f,
                        Mathf.Sin(ta) * TacticalRingRadius);
                    Component tacticalPoint = tactical.AddComponent(wType);
                    SetEnum(tacticalPoint, "Type", "Tactical");
                }

                // How many of this crew carry the LAW instead of the MG42. The
                // rocketeers are the first men out; the rest rattle with the
                // machine gun. Clamped so the config can never ask for more LAWs
                // than there are men.
                int lawCount = RevivalPlugin.CfgPatrolCrewLawCount == null ? 0
                    : Mathf.Clamp(RevivalPlugin.CfgPatrolCrewLawCount.Value, 0, count);

                // The spawn points, one per man. They must exist BEFORE the
                // settlement component: StartMainInit collects them with
                // GetComponentsInChildren when _npcSpawnPoints is null.
                // The editor's roles are the LOADOUT of this vehicle's men,
                // not a head count. A vehicle with more seats than listed roles
                // repeats them around the crew, so a single "crew" line dresses
                // and arms everybody instead of leaving one man in uniform and
                // the rest in the map's preset.
                for (int i = 0; i < count; i++)
                {
                    RevivalComposition.CrewMan spec =
                        composition != null && composition.Count > 0
                        ? composition[i % composition.Count] : null;
                    string role = spec == null || spec.Role.Length == 0
                        ? "crew" : spec.Role;
                    GameObject sp = new GameObject("Crew" + i + "_" + role);
                    sp.transform.SetParent(settlement.transform, true);
                    sp.transform.position = wo[i];
                    Component punkt = sp.AddComponent(pType);
                    Listen(punkt, 0);
                    Abschreiben(punkt, VorlagePunkt(pType));
                    Component military = VorlageMilitaer(pType);
                    Abschreiben(punkt, military);
                    int weapon = spec != null && spec.MainWeapon > 0
                        ? spec.MainWeapon : (i < lawCount ? LAW_ID : MG42_ID);
                    Punkt(punkt, military != null, weapon, spec);
                    RegisterAppearance(punkt, spec);
                }

                // An unregistered PhotonView - see the class comment.
                Type viewType = RevivalPlugin.TypeByName("PhotonView");
                if (viewType != null) settlement.AddComponent(viewType);

                Component sied = settlement.AddComponent(sType);
                Listen(sied, 0);
                Abschreiben(sied, VorlageSiedlung(sType, settlement));
                Siedlung(sied, leute.transform, wege.transform, fraktion);

                // The game's own entry point. On the master client it runs
                // InitSpawnNpc and InitSetupNpc, and those two build the men.
                Invoke(sied, "StartMainInit");
                _appearance.Clear();
                Set(sied, "AllInitializationDone", true);
                Absichern(sied);

                string wer = Fraktion.Sauber(fraktion);
                if (wer.Length == 0) wer = "neutral";
                _settlements.Add(settlement);
                RevivalPlugin.L.LogInfo("Crew: " + count + " " + wer
                    + " out of the "
                    + (tank ? "tank" : "BTR") + " at " + car.transform.position
                    + (composition == null ? "" : " with editor loadouts")
                    + " - " + _settlements.Count + " crew(s) on the ground.");
                Turret.Hinweis(count + " " + wer + Loc.T(" выбрались из обломков",
                                                         " out of the wreck"), 4f);
            }
            catch (Exception ex)
            {
                _appearance.Clear();
                RevivalPlugin.L.LogError("Crew: nobody climbed out - " + ex);
                if (settlement != null) UnityEngine.Object.Destroy(settlement);
            }
        }

        /// <summary>
        /// Runtime truth after the game's complete initialization. The spawn
        /// point already requests GodMode=false and InitSetupNpc normally sets
        /// IsInitialized=true. Repeat those two safety-critical facts on the
        /// resulting NPC and log them: damage returns immediately when either
        /// is wrong, so a future report no longer has to infer killability from
        /// a model on screen.
        /// </summary>
        static void Absichern(Component settlement)
        {
            FieldInfo nf = AccessTools.Field(settlement.GetType(), "NpcAI");
            Array npcs = nf == null ? null : nf.GetValue(settlement) as Array;
            if (npcs == null)
            {
                RevivalPlugin.L.LogWarning("Crew: NpcAI array missing after initialization.");
                return;
            }

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null)
                {
                    RevivalPlugin.L.LogWarning("Crew: NPC " + i
                        + " missing after initialization.");
                    continue;
                }

                MethodInfo god = AccessTools.Method(ai.GetType(), "SetGodMode",
                    new Type[] { typeof(bool) }, null);
                if (god != null) god.Invoke(ai, new object[] { false });
                if (!Bool(ai, "IsInitialized")) Set(ai, "IsInitialized", true);
            }

            // Photon-spawned NPCs run Unity Start on the next frame. Until
            // then their game data is complete but _aimIk is still null.
            // SetGeneralAlarm calls ClearKillTarget, which dereferences that
            // component without a null check. Arm the real alarm only after
            // Unity has completed every NPC component; a delayed failure must
            // never escape into Aussteigen's spawn-cleanup catch.
            CrewAlarm delayed = settlement.gameObject.AddComponent<CrewAlarm>();
            delayed.Begin(settlement, npcs);
            CrewDrone.Begin(settlement.transform, npcs);
        }

        /// <summary>Enter the game's own settlement alarm once Unity Start has
        /// populated the components SetGeneralAlarm assumes. False with an
        /// empty problem means "not ready yet"; false with text is a real
        /// reflection/runtime failure for CrewAlarm to report once.</summary>
        internal static bool TryStartAlarm(Component settlement, Array npcs,
                                           out string problem)
        {
            problem = "";
            if (settlement == null || npcs == null) return false;

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null) return false;
                FieldInfo af = AccessTools.Field(ai.GetType(), "_aimIk");
                object aim = af == null ? null : af.GetValue(ai);
                if (aim == null) return false;
                FieldInfo solver = AccessTools.Field(aim.GetType(), "solver");
                if (solver != null && solver.GetValue(aim) == null) return false;
            }

            // EnabledAI is copied as true before the NPCs exist. That field
            // alone does not play their legacy Animation: OnPlayerEnterZone
            // calls SetEnableNpcAi(true) only after a previous disable made
            // the settlement field false. Invoke the same local game method
            // once after Unity Start so every NPC enters its current
            // walk/combat animation even when the crew spawned far from the
            // player's body (for example while piloting the drone).
            MethodInfo enable = AccessTools.Method(settlement.GetType(),
                "SetEnableNpcAi", new Type[] { typeof(bool) }, null);
            if (enable == null)
            {
                problem = "NPC_Settlement.SetEnableNpcAi(bool) is missing";
                return false;
            }
            try { enable.Invoke(settlement, new object[] { true }); }
            catch (Exception ex)
            {
                Exception cause = ex.InnerException == null ? ex : ex.InnerException;
                problem = "crew animation activation failed - " + cause.Message;
                return false;
            }

            // THE SECOND SWITCH. `SetEnableNpcAi` alone leaves the crew frozen
            // and unhittable whenever the player body is not in PlayersAround
            // - the reasons are at PlayVisualizationPrefix. The prefix refuses
            // every later attempt to switch it off again; this is the one call
            // that switches it ON, and it has to be a real call because the
            // game does the work in a coroutine, not in the field.
            MethodInfo visual = AccessTools.Method(settlement.GetType(),
                "SetPlayVisualizationValue", new Type[] { typeof(bool) }, null);
            if (visual == null)
            {
                problem = "NPC_Settlement.SetPlayVisualizationValue(bool) is missing";
                return false;
            }
            try { visual.Invoke(settlement, new object[] { true }); }
            catch (Exception ex)
            {
                Exception cause = ex.InnerException == null ? ex : ex.InnerException;
                problem = "crew visualization activation failed - " + cause.Message;
                return false;
            }

            // The runtime settlement has no registered Photon view. Mark the
            // persistent local alarm before any NPC sensor can ask the
            // settlement to broadcast it through illegal view id 0.
            Set(settlement, "AlarmEnabled", true);
            SetNumber(settlement, "_alarmTimer", float.MaxValue);

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                MethodInfo alarm = AccessTools.Method(ai.GetType(),
                    "SetGeneralAlarm", new Type[] { typeof(bool) }, null);
                if (alarm == null)
                {
                    problem = "NPC_AI2.SetGeneralAlarm(bool) is missing";
                    Set(settlement, "AlarmEnabled", false);
                    return false;
                }
                try { alarm.Invoke(ai, new object[] { true }); }
                catch (Exception ex)
                {
                    Exception cause = ex.InnerException == null ? ex : ex.InnerException;
                    problem = "NPC " + i + " alarm failed - " + cause.Message;
                    Set(settlement, "AlarmEnabled", false);
                    return false;
                }
            }

            for (int i = 0; i < npcs.Length; i++)
                LogNpc(npcs.GetValue(i) as Component, i);
            return true;
        }

        static void LogNpc(Component ai, int index)
        {
            if (ai == null) return;
            FieldInfo sf = AccessTools.Field(ai.GetType(), "Specifications");
            object specs = sf == null ? null : sf.GetValue(ai);
            RevivalPlugin.L.LogInfo("Crew: NPC " + index
                + " initialized=" + Bool(ai, "IsInitialized")
                + ", enabled=" + Bool(ai, "EnabledAI")
                + ", sensor=" + Bool(ai, "SensorIsActive")
                + ", godmode=" + Bool(ai, "GodModeEnabled")
                + ", alarm=" + Bool(ai, "AlarmIsEnabled")
                + ", task=" + GetNumber(ai, "TemporaryTask").ToString("0")
                + ", tactical=" + Count(ai, "_temporaryWalkPoints")
                // The two that decide whether he moves and whether he can be
                // hit. `visual` false means Anim.enabled false and every
                // ragdoll rigidbody with detectCollisions off - frozen and
                // immune at the same time, see PlayVisualizationPrefix.
                + ", visual=" + Bool(ai, "IsPlayVisualizationEnabled")
                + ", safe=" + Bool(ai, "_isSafeSettlement")
                + ", health=" + GetNumber(specs, "Health").ToString("0.#")
                + "/" + GetNumber(specs, "HealthMax").ToString("0.#") + ".");
        }

        static bool Bool(object o, string name)
        {
            if (o == null) return false;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null || fi.FieldType != typeof(bool)) return false;
            try { return (bool)fi.GetValue(o); }
            catch { return false; }
        }

        static int Count(object o, string name)
        {
            if (o == null) return -1;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null) return -1;
            try
            {
                ICollection values = fi.GetValue(o) as ICollection;
                return values == null ? -1 : values.Count;
            }
            catch { return -1; }
        }

        /// <summary>Take every crew off the map again. Shift plus the patrol
        /// key does this, so a test run can be reset without a restart.</summary>
        public static void StopAll()
        {
            if (_settlements.Count == 0) return;
            int n = _settlements.Count;
            for (int i = 0; i < _settlements.Count; i++)
                if (_settlements[i] != null)
                    UnityEngine.Object.Destroy(_settlements[i]);
            _settlements.Clear();
            RevivalPlugin.L.LogInfo("Crew: " + n + " crew(s) removed.");
        }

        /// <summary>
        /// Where the men appear. The vehicle carries its own answer:
        /// `GetOutPoints` is a single Transform whose children Point0..Point9
        /// are the places the game itself puts a player who leaves the vehicle
        /// (RE 10). A ring around the hull is only the fallback.
        /// </summary>
        static Vector3[] Ausstiege(GameObject car, Component vgs, int count)
        {
            Vector3[] wo = new Vector3[count];
            Transform points = null;
            if (vgs != null)
            {
                FieldInfo fi = AccessTools.Field(vgs.GetType(), "GetOutPoints");
                if (fi != null) points = fi.GetValue(vgs) as Transform;
            }

            for (int i = 0; i < count; i++)
            {
                if (points != null && points.childCount > 0)
                {
                    wo[i] = points.GetChild(i % points.childCount).position;
                    // A vehicle with fewer exit points than men would put two of
                    // them in the same spot. The second man round the list steps
                    // a metre aside instead of standing inside the first.
                    int round = i / points.childCount;
                    if (round > 0)
                    {
                        float turn = i * 2.3999632f;      // golden angle
                        wo[i] += new Vector3(Mathf.Cos(turn), 0f, Mathf.Sin(turn))
                               * (1.2f * round);
                    }
                }
                else
                {
                    float a = i * Mathf.PI * 2f / Mathf.Max(1, count);
                    wo[i] = car.transform.position
                          + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 4.5f;
                }

                // Onto the ground. A man dropped at hatch height falls, and a
                // NavMeshAgent that starts in the air never finds the mesh.
                Vector3 boden;
                GameObject unter = Turret.RaycastObject(wo[i] + Vector3.up * 6f,
                                                        Vector3.down, 30f, out boden);
                if (unter != null) wo[i] = boden + Vector3.up * 0.1f;
            }
            return wo;
        }

        // ----------------------------------------------------- the template

        static Component _vorlageSiedlung, _vorlagePunkt, _vorlageMilitaer;
        static bool _vorlageGesucht;
        static bool _militaerGesucht;

        /// <summary>
        /// Fields a template must NOT hand over: they are running state, not
        /// a designer's choice, and a settlement that starts life already
        /// initialised never runs its own init.
        /// </summary>
        static readonly string[] Tabu = new string[] {
            "IsInitialized", "InitializationState", "AllInitializationDone",
            "AlarmEnabled", "IsPlayVisualizationEnabled",
            "IsMain", "SettlementType", "MyNPC", "Active",
        };

        /// <summary>
        /// The one thing an `AddComponent` cannot give us: the numbers the
        /// game's own level designers put on a settlement.
        ///
        /// A component built at runtime has whatever its constructor writes,
        /// and for a MonoBehaviour that is almost nothing - every float in the
        /// inspector is 0. `SensorVisibleDist` at 0 means a crew that cannot
        /// see; `UnitsPerEnemy` at 0, `AlarmTimeDelay` at 0, `FloorDelta*` at
        /// 0 are all the same kind of quiet wrong. Rather than invent a value
        /// for each, this copies them off a settlement THE MAP ALREADY HAS -
        /// found once per session, and then the handful that matter are
        /// overwritten by `Siedlung` on top.
        ///
        /// Three limits, each for its own reason:
        ///   value types and strings only   an object reference belongs to the
        ///       template's own scene objects and would point a wreck's crew at
        ///       some village's walk points. Lists are `Listen`'s job.
        ///   PUBLIC fields only             the designer's knobs are public and
        ///       the running state is private with an underscore -
        ///       `_aliveNPCsCount`, `_killedCount`, `_respawnQueueIndex`. A
        ///       fresh settlement that starts life believing twelve of its men
        ///       are already alive is worse than one with a zero in it.
        ///   the Tabu list                  the handful of PUBLIC fields that
        ///       are running state anyway. A settlement copied as already
        ///       initialised never initialises.
        /// </summary>
        static void Abschreiben(Component ziel, Component vorlage)
        {
            if (ziel == null || vorlage == null) return;
            if (vorlage.GetType() != ziel.GetType()) return;
            int n = 0;
            for (Type t = ziel.GetType(); t != null && t != typeof(MonoBehaviour);
                 t = t.BaseType)
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fs.Length; i++)
                {
                    Type ft = fs[i].FieldType;
                    if (!ft.IsValueType && ft != typeof(string)) continue;
                    if (Verboten(fs[i].Name)) continue;
                    try { fs[i].SetValue(ziel, fs[i].GetValue(vorlage)); n++; }
                    catch { }
                }
            }
            if (n > 0)
                RevivalPlugin.L.LogInfo("Crew: " + n + " value(s) copied off an "
                    + "existing " + ziel.GetType().Name + ".");
        }

        static bool Verboten(string name)
        {
            for (int i = 0; i < Tabu.Length; i++)
                if (Tabu[i] == name) return true;
            return false;
        }

        /// <summary>A settlement of the map's own, never one of ours. The
        /// search costs a FindObjectsOfType and happens at most once a
        /// session - a vehicle has to be destroyed first.</summary>
        static Component VorlageSiedlung(Type sType, GameObject eigen)
        {
            Suchen(sType, null);
            if (_vorlageSiedlung != null && _vorlageSiedlung.gameObject == eigen)
                return null;
            return _vorlageSiedlung;
        }

        static Component VorlagePunkt(Type pType)
        {
            Suchen(null, pType);
            return _vorlagePunkt;
        }

        static Component VorlageMilitaer(Type pType)
        {
            if (_militaerGesucht) return _vorlageMilitaer;
            _militaerGesucht = true;
            try
            {
                UnityEngine.Object asset = Resources.Load(
                    "npcspawn/npc_premade/military_1_heavy");
                GameObject go = asset as GameObject;
                Component direct = asset as Component;
                _vorlageMilitaer = go == null ? direct : go.GetComponent(pType);
                RevivalPlugin.L.LogInfo("Crew: uniform military preset "
                    + (_vorlageMilitaer == null ? "NOT found"
                       : "military_1_heavy loaded") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: military uniform preset - "
                    + ex.Message);
            }
            return _vorlageMilitaer;
        }

        static void Suchen(Type sType, Type pType)
        {
            if (_vorlageGesucht) return;
            _vorlageGesucht = true;
            try
            {
                if (sType == null) sType = RevivalPlugin.TypeByName("NPC_Settlement");
                if (pType == null) pType = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                if (sType != null)
                {
                    UnityEngine.Object[] alle = UnityEngine.Object.FindObjectsOfType(sType);
                    for (int i = 0; i < alle.Length; i++)
                    {
                        Component c = alle[i] as Component;
                        if (c == null || c.gameObject.name == Name) continue;
                        _vorlageSiedlung = c;
                        break;
                    }
                }
                if (pType != null)
                {
                    UnityEngine.Object[] alle = UnityEngine.Object.FindObjectsOfType(pType);
                    for (int i = 0; i < alle.Length; i++)
                    {
                        Component c = alle[i] as Component;
                        if (c == null) continue;
                        if (c.transform.parent != null
                            && c.transform.parent.name == Name) continue;
                        _vorlagePunkt = c;
                        break;
                    }
                }
                RevivalPlugin.L.LogInfo("Crew: template settlement "
                    + (_vorlageSiedlung == null ? "NOT found - the crew will "
                       + "run on hand written numbers"
                       : "\"" + _vorlageSiedlung.gameObject.name + "\"")
                    + ", template spawn point "
                    + (_vorlagePunkt == null ? "NOT found"
                       : "\"" + _vorlagePunkt.gameObject.name + "\"") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: looking for a template - "
                    + ex.Message);
            }
        }

        // ------------------------------------------------------- the values

        /// <summary>
        /// One spawn point. Only the fields whose meaning is CONFIRMED are
        /// written; everything else keeps the value the component ships with.
        /// The shipped military_1_heavy preset remains the fallback. An editor
        /// composition overlays its selected equipment slots on the game's
        /// generated face and backpack. GrantWeaponType 1 is the confirmed
        /// fixed-id path in `NPC_Settlement::GetWeaponId`.
        /// </summary>
        static void Punkt(Component sp, bool militaryPreset, int weaponId,
                          RevivalComposition.CrewMan spec)
        {
            Set(sp, "Active", true);
            SetNumber(sp, "Health", RevivalPlugin.CfgPatrolCrewHealth.Value);
            SetNumber(sp, "Level", RevivalPlugin.CfgPatrolCrewLevel.Value);
            SetEnum(sp, "BehaviorPattern", "Aggressive");
            SetNumber(sp, "NPCType", 0);              // -> prefab Marauder_NPC_01
            SetNumber(sp, "GrantWeaponType", 1);      // fixed WeaponId below
            SetNumber(sp, "WeaponId", weaponId);      // NDR MG42 or NDR M72 LAW
            if (!militaryPreset)
                SetNumber(sp, "AppearanceType", 0);   // safe vanilla fallback
            Set(sp, "UseCustomAppearance", militaryPreset);
            Set(sp, "UseIndividualFraction", false);  // the settlement decides
            Set(sp, "UseIndividualGodMode", true);
            Set(sp, "GodModeEnabled", false);
            Set(sp, "UseIndividualWalkPoints", false);
            Set(sp, "RandomWalkPoint", true);

            // PlayerInteractingManager always localizes this key before it
            // draws the NPC name plate. A runtime NPCQuestData has a null key,
            // and Dictionary.ContainsKey(null) aborts the whole hover path.
            // A non-localized key longer than five characters is deliberately
            // returned verbatim by LocalizationManager; the faction is drawn
            // separately from MainOptions.MyFraction.
            FieldInfo qf = AccessTools.Field(sp.GetType(), "Quests");
            object quests = qf == null ? null : qf.GetValue(sp);
            if (quests != null)
                Set(quests, "NameKey", spec == null || spec.Role.Length == 0
                    ? "Patrol Crew" : "Patrol Crew - " + spec.Role);

            // Loot on the body is phase 5 and needs SpawnCategories, which is
            // a list this class has no business inventing. At 0 the game's own
            // GenerateOtherItems returns an empty array without ever reading
            // that list - the weapon in his hands still drops.
            SetNumber(sp, "RandomItemsCount", 0);
        }

        static void Siedlung(Component s, Transform leute, Transform wege,
                             string fraktion)
        {
            Set(s, "AllPeopleTr", leute);
            Set(s, "AllWalkPointsTr", wege);
            Set(s, "IsSafeSettlement", false);
            Set(s, "SensorIsEnabled", true);
            Set(s, "EnabledAI", true);
            Set(s, "VisibleNpc", true);
            Set(s, "IsIndoors", false);
            Set(s, "UseFloorBounds", false);
            Set(s, "UseCustomFractionOptions", true);
            SetNumber(s, "SettlementType", 0);
            SetNumber(s, "CheckPlayersDistRadius", 120f);
            SetNumber(s, "DisableAiTimer", 600f);

            // What a crewman can SEE. SetupNpcSettlements hands this straight
            // to NPC_AI2::SetSensorVisibleDist, and an AddComponent leaves it
            // at 0 - men who stand around their own wreck and never look up.
            // A template settlement will already have overwritten it with the
            // map's own number; this is the floor under that.
            if (GetNumber(s, "SensorVisibleDist") < 5f)
                SetNumber(s, "SensorVisibleDist",
                          RevivalPlugin.CfgPatrolCrewSensor.Value);
            if (GetNumber(s, "UnitsPerEnemy") < 1f) SetNumber(s, "UnitsPerEnemy", 1f);
            if (GetNumber(s, "AlarmTimeDelay") < 0.1f) SetNumber(s, "AlarmTimeDelay", 20f);

            // No second wave. RespawnQueueActions would put the crew back on
            // its feet at RespawnTimeInSec, and a wreck that keeps producing
            // gunmen is a bug report, not a feature.
            SetNumber(s, "RespawnTimeInSec", 1000000f);

            // WHO THEY SHOOT AT. `SetNpcParams` copies this object straight
            // into every NPC's `MainOptions`, and `IsEnemyFraction` walks its
            // `HatedFractions` - so this one field IS the answer to "which
            // side is this patrol on" (RE 23). An AddComponent leaves it null,
            // which is worse than wrong: `MainOptions` would be null on every
            // crewman and the first sensor hit would throw.
            object opts = Fraktion.Optionen(fraktion);
            if (opts != null)
            {
                Set(s, "FractionOptions", opts);
                RevivalPlugin.L.LogInfo("Crew: the crew is " + Fraktion.Sauber(fraktion)
                    + " - " + Fraktion.Erklaerung(fraktion) + ".");
            }
            Notausgang(s);
        }

        /// <summary>
        /// Whatever went wrong above, `HatedFractions` must not be null.
        ///
        /// `IsEnemyFraction` does `ldlen` on it with no null check (RE 23), so
        /// a crew whose fraction could not be built would not merely be
        /// harmless - it would throw on its first sensor hit, once per NPC per
        /// scan. `Listen` has already put an empty `NPCMainOptions` in the
        /// field; this fills its array with nothing, and a crew that attacks
        /// nobody is a bug you can walk away from.
        /// </summary>
        static void Notausgang(Component s)
        {
            try
            {
                FieldInfo fo = AccessTools.Field(s.GetType(), "FractionOptions");
                if (fo == null) return;
                object opts = fo.GetValue(s);
                if (opts == null)
                {
                    if (fo.FieldType.GetConstructor(Type.EmptyTypes) == null) return;
                    opts = Activator.CreateInstance(fo.FieldType);
                    fo.SetValue(s, opts);
                }
                FieldInfo hf = AccessTools.Field(opts.GetType(), "HatedFractions");
                if (hf == null || !hf.FieldType.IsArray) return;
                if (hf.GetValue(opts) != null) return;
                hf.SetValue(opts, Array.CreateInstance(
                    hf.FieldType.GetElementType(), 0));
                RevivalPlugin.L.LogWarning("Crew: the fraction could not be set - "
                    + "HatedFractions is an empty array, so this crew attacks "
                    + "nobody. It would otherwise have thrown on its first "
                    + "sensor scan.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: HatedFractions - " + ex.Message);
            }
        }

        // -------------------------------------------------------- reflection

        /// <summary>
        /// Every empty list the component would have brought with it out of a
        /// prefab, and does not bring out of `AddComponent`.
        ///
        /// This is what stopped the crew on the first run (E-033). A Unity
        /// component built at runtime gets exactly the fields its CONSTRUCTOR
        /// writes; a `List&lt;T&gt;` that the game only ever fills in the
        /// inspector is `null`. `NPC_Settlement::.ctor` builds `_enemysDic`
        /// and `_enemysTargets` and NOT `_enemysTr`, so the third line of
        /// `ClearEnemys` - the third call in `StartMainInit` - threw a
        /// NullReference before a single man was built. Seven more of the
        /// settlement's lists are in the same state, `TacticalPoints` and
        /// `enemyeOwnerId` among them, and those are read from `Update` while
        /// the crew is fighting.
        ///
        /// So: every instance field that is a plain data type with a
        /// parameterless constructor and is null gets an empty instance - which
        /// is precisely what a component placed in the editor has. That means
        /// `List&lt;T&gt;` and `Dictionary&lt;K,V&gt;`, and it ALSO means the
        /// serialisable little classes: `NPC_SpawnPoint.Quests` is an
        /// `NPCQuestData`, and its being null is what stopped the crew the
        /// SECOND time (2026-08-30, run two of E-033):
        ///
        ///     QuestManager.ConfigNPC   point.Quests.StartQuest.Count
        ///     NPC_Settlement.SetupNpcSettlements
        ///     NPC_Settlement.InitSetupNpc
        ///     NPC_Settlement.StartMainInit
        ///
        /// The men were already built by then - `InitSpawnNpc` runs first and
        /// had done its work - and this one null threw them all away again.
        /// Hence the RECURSION: an `NPCQuestData` built by `Activator` has the
        /// same problem one level down, its `StartQuest` list being null, and
        /// `ConfigNPC` reads exactly that.
        ///
        /// WHAT IS LEFT ALONE, and why each one:
        ///   ARRAYS      `StartMainInit` fills `_npcSpawnPoints` itself, but
        ///               only while it is null, and a helpful empty array here
        ///               would leave the settlement without any men. The one
        ///               array that MUST be filled, `HatedFractions`, is
        ///               written by `Fraktion.Optionen` by name.
        ///   UnityEngine.Object   a null Texture is a missing icon; a NEW
        ///               Texture is a leak, and `AddComponent` on a type we
        ///               guessed is worse.
        ///   string      null and "" behave the same everywhere the game
        ///               reads these, and `Abschreiben` copies strings anyway.
        /// </summary>
        static void Listen(object o, int tiefe)
        {
            if (o == null || tiefe > 3) return;
            int n = 0;
            for (Type t = o.GetType(); t != null && t != typeof(MonoBehaviour)
                 && t != typeof(object); t = t.BaseType)
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fs.Length; i++)
                {
                    Type ft = fs[i].FieldType;
                    if (IstCache(fs[i].Name)) continue;
                    if (!Fuellbar(ft)) continue;
                    try
                    {
                        object hat = fs[i].GetValue(o);
                        if (hat != null)
                        {
                            if (!ft.IsGenericType) Listen(hat, tiefe + 1);
                            continue;
                        }
                        object neu = Activator.CreateInstance(ft);
                        fs[i].SetValue(o, neu);
                        n++;
                        if (!ft.IsGenericType) Listen(neu, tiefe + 1);
                    }
                    catch (Exception ex)
                    {
                        RevivalPlugin.L.LogWarning("Crew: field " + fs[i].Name
                            + " not filled - " + ex.Message);
                    }
                }
            }
            if (n > 0)
                RevivalPlugin.L.LogInfo("Crew: " + n + " empty value(s) put into "
                    + o.GetType().Name + ".");
        }

        /// <summary>Is this a type the Unity serialiser would have handed us an
        /// instance of? See the rules in <see cref="Listen"/>.</summary>
        static bool Fuellbar(Type ft)
        {
            if (ft.IsValueType || ft.IsArray) return false;
            if (ft == typeof(string)) return false;
            if (ft.IsAbstract || ft.IsInterface) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) return false;
            if (typeof(Delegate).IsAssignableFrom(ft)) return false;
            return ft.GetConstructor(Type.EmptyTypes) != null;
        }

        /// <summary>
        /// Null is meaningful for these three lists: their getters use it as
        /// "not built yet" and then filter the NPC_WP components collected by
        /// StartMainInit. An empty list is not equivalent - PatrolPoints in
        /// particular is returned unchanged, every NPC receives it, and
        /// IdleStateAction indexes element zero forever. These are caches, not
        /// inspector data for Listen to recreate.
        /// </summary>
        static bool IstCache(string name)
        {
            return name == "PatrolPoints" || name == "EscapePoints"
                || name == "SleepPoints";
        }

        static void Set(object o, string name, object value)
        {
            if (o == null) return;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null)
            {
                RevivalPlugin.L.LogWarning("Crew: field " + name + " not on "
                    + o.GetType().Name + ".");
                return;
            }
            try { fi.SetValue(o, value); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + name + " not set - " + ex.Message);
            }
        }

        /// <summary>Reads a numeric field as a float, 0 when it is not
        /// there. Used to tell a value a template handed over from the 0 an
        /// AddComponent leaves behind.</summary>
        static float GetNumber(object o, string name)
        {
            if (o == null) return 0f;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null) return 0f;
            try
            {
                object v = fi.GetValue(o);
                if (v == null) return 0f;
                return Convert.ToSingle(v);
            }
            catch { return 0f; }
        }

        /// <summary>Writes a number into whatever numeric type the field
        /// happens to be. Health is an int in the spawn point and a float
        /// everywhere it is used, and guessing wrong throws.</summary>
        static void SetNumber(object o, string name, float value)
        {
            if (o == null) return;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null)
            {
                RevivalPlugin.L.LogWarning("Crew: field " + name + " not on "
                    + o.GetType().Name + ".");
                return;
            }
            try
            {
                Type t = fi.FieldType;
                if (t.IsEnum) fi.SetValue(o, Enum.ToObject(t, (int)value));
                else if (t == typeof(float)) fi.SetValue(o, value);
                else if (t == typeof(int)) fi.SetValue(o, (int)value);
                else if (t == typeof(double)) fi.SetValue(o, (double)value);
                else fi.SetValue(o, Convert.ChangeType(value, t));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + name + " not set - " + ex.Message);
            }
        }

        /// <summary>An enum field by NAME. The numbers behind
        /// NPCBehaviorPattern and Fraction are known (RE 10), but a name that
        /// no longer exists says so in the log instead of quietly picking the
        /// wrong behaviour.</summary>
        static void SetEnum(object o, string name, string value)
        {
            if (o == null) return;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null || !fi.FieldType.IsEnum)
            {
                RevivalPlugin.L.LogWarning("Crew: enum field " + name + " not on "
                    + o.GetType().Name + ".");
                return;
            }
            try { fi.SetValue(o, Enum.Parse(fi.FieldType, value, true)); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + name + " = " + value
                    + " not set - " + ex.Message);
            }
        }

        static void Invoke(object o, string name)
        {
            if (o == null) return;
            MethodInfo mi = AccessTools.Method(o.GetType(), name, null, null);
            if (mi == null)
                throw new MissingMethodException(o.GetType().Name + "." + name);
            mi.Invoke(o, null);
        }
    }

    /// <summary>
    /// A crew M72 LAW that actually goes off. The player LAW gets its impact
    /// explosion from <see cref="RocketHook"/>, which reads the player camera -
    /// a field an NPC does not have, so that hook is a no-op for a crewman. This
    /// is the NPC half: a postfix on `NPC_FirearmWeaponController::FireOneShot`
    /// that, when the weapon is the LAW, raycasts from the muzzle toward the
    /// AI's aim point and lights a networked explosion at the impact.
    ///
    /// Only the master client detonates - the crew AI, and therefore every crew
    /// FireOneShot, runs there - so the rocket goes off once and is networked
    /// out to everyone. The shot's own hitscan damage is left untouched; the
    /// blast is simply added on top, which turns the LAW's puny bullet into a
    /// real rocket. The postfix returns immediately for every weapon that is not
    /// the LAW, so it costs a field read per NPC round and nothing more.
    /// </summary>
    public static class CrewLaw
    {
        static FieldInfo _weaponData;
        static FieldInfo _itemId;
        static MethodInfo _muzzlePos;
        static MethodInfo _idImplicit;
        static MethodInfo _masterGetter;
        static bool _masterLookedUp;
        static bool _detonateLogged;

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("NPC_FirearmWeaponController");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Crew LAW: NPC_FirearmWeaponController "
                        + "not found - crew rockets would be duds, so the hook is off.");
                    return;
                }
                MethodInfo fire = AccessTools.Method(t, "FireOneShot", null, null);
                if (fire == null)
                {
                    RevivalPlugin.L.LogWarning("Crew LAW: FireOneShot not found - "
                        + "crew rockets would be duds, so the hook is off.");
                    return;
                }
                _weaponData = AccessTools.Field(t, "_weaponFirearmData");
                _muzzlePos = AccessTools.Method(t, "GetMuzzlePos", null, null);
                harmony.Patch(fire, null,
                    new HarmonyMethod(typeof(CrewLaw).GetMethod("FirePostfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Crew LAW: rocket impact explosion active.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Crew LAW install: " + ex);
            }
        }

        public static void FirePostfix(object __instance, Vector3 __0)
        {
            try
            {
                if (__instance == null || _weaponData == null) return;
                // The crew AI runs on the master; detonating anywhere else would
                // double the blast on that machine's networked copy.
                if (!IsMaster()) return;

                object data = _weaponData.GetValue(__instance);
                if (data == null) return;
                if (_itemId == null)
                {
                    _itemId = AccessTools.Field(data.GetType(), "ItemID");
                    if (_itemId == null) return;
                }
                if (ItemId(_itemId.GetValue(data)) != Crew.LAW_ID) return;

                Vector3 muzzle = _muzzlePos == null ? Vector3.zero
                    : (Vector3)_muzzlePos.Invoke(__instance, null);
                Vector3 boom;
                if (muzzle == Vector3.zero)
                {
                    // No muzzle transform ready - detonate where the AI aimed.
                    boom = __0;
                }
                else
                {
                    Vector3 to = __0 - muzzle;
                    float dist = to.magnitude;
                    if (dist < 0.5f) { boom = __0; }
                    else
                    {
                        Vector3 hit;
                        GameObject struck = Turret.RaycastObject(
                            muzzle, to / dist, dist + 1f, out hit);
                        boom = struck != null ? hit : __0;
                        RocketHook.SpawnTracer(new List<Vector3> { muzzle, boom });
                    }
                }

                float dmg = RevivalPlugin.CfgPatrolCrewLawDamage == null
                    ? 600f : RevivalPlugin.CfgPatrolCrewLawDamage.Value;
                float radius = RevivalPlugin.CfgPatrolCrewLawRadius == null
                    ? 8f : RevivalPlugin.CfgPatrolCrewLawRadius.Value;
                RocketHook.Detonate(boom, dmg, radius, 3f);

                if (!_detonateLogged && RevivalPlugin.L != null)
                {
                    _detonateLogged = true;
                    RevivalPlugin.L.LogInfo("Crew LAW: first rocket detonation ("
                        + dmg + " in " + radius + " m).");
                }
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Crew LAW fire: " + ex.Message);
            }
        }

        /// <summary>Master-client check by reflection - PhotonNetwork is a game
        /// type this assembly does not reference. Resolved once. A missing
        /// property means Photon is not up, so nothing detonates.</summary>
        static bool IsMaster()
        {
            if (!_masterLookedUp)
            {
                _masterLookedUp = true;
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon != null)
                {
                    _masterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                    if (_masterGetter == null)
                        _masterGetter = AccessTools.PropertyGetter(photon, "IsMasterClient");
                }
            }
            if (_masterGetter == null) return false;
            return (bool)_masterGetter.Invoke(null, null);
        }

        /// <summary>The weapon `ItemID` is an ObscuredInt; unwrap it through its
        /// implicit int conversion, caching the method after the first look.</summary>
        static int ItemId(object value)
        {
            if (value == null) return -1;
            if (value is int) return (int)value;
            if (_idImplicit == null)
            {
                MethodInfo[] ms = value.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "op_Implicit"
                        || ms[i].ReturnType != typeof(int)) continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == value.GetType())
                    {
                        _idImplicit = ms[i];
                        break;
                    }
                }
                if (_idImplicit == null) return -1;
            }
            return (int)_idImplicit.Invoke(null, new object[] { value });
        }
    }

    /// <summary>
    /// Repairs the visual half of a runtime crew on non-master clients. The
    /// scene settlement lookup cannot succeed for view id zero, so vanilla
    /// applies the weapon but never the seven customization items. Waiting a
    /// few frames also covers the legacy Animation component created by the
    /// NPC prefab after its network spawn.
    /// </summary>
    public sealed class CrewRemoteFix : MonoBehaviour
    {
        Component _ai;
        int[] _appearance;
        int _weapon;
        float _next;
        float _deadline;
        string _problem = "";

        public void Begin(Component ai, int[] appearance, int weapon)
        {
            _ai = ai;
            _appearance = appearance;
            _weapon = weapon;
            _next = Time.time + 0.1f;
            _deadline = Time.time + 6f;
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.25f;
            if (Crew.ApplyRemoteAppearance(_ai, _appearance, _weapon,
                                           out _problem))
            {
                RevivalPlugin.L.LogInfo("Crew: remote uniform, MG42 and animation "
                    + "restored.");
                UnityEngine.Object.Destroy(this);
                return;
            }
            if (Time.time < _deadline) return;
            RevivalPlugin.L.LogWarning("Crew: remote appearance repair timed out"
                + (_problem.Length == 0 ? "." : " - " + _problem + "."));
            UnityEngine.Object.Destroy(this);
        }
    }

    /// <summary>
    /// The crew NPCs are created inside NPC_Settlement.StartMainInit. Their
    /// Unity Start methods, including FinalIK discovery, run after that call
    /// returns. This tiny one-shot waits for those components and then enters
    /// the real alarm state. It never owns or replaces NPC behaviour.
    /// </summary>
    public sealed class CrewAlarm : MonoBehaviour
    {
        Component _settlement;
        Array _npcs;
        float _next;
        float _deadline;
        string _lastProblem = "";

        public void Begin(Component settlement, Array npcs)
        {
            _settlement = settlement;
            _npcs = npcs;
            _next = Time.time + 0.25f;
            _deadline = Time.time + 10f;
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.25f;

            string problem;
            if (Crew.TryStartAlarm(_settlement, _npcs, out problem))
            {
                RevivalPlugin.L.LogInfo("Crew: aggravated settlement AI is active.");
                UnityEngine.Object.Destroy(this);
                return;
            }
            if (problem.Length > 0) _lastProblem = problem;
            if (Time.time < _deadline) return;

            RevivalPlugin.L.LogWarning("Crew: spawned normally, but aggravated "
                + "state did not start within 10 s"
                + (_lastProblem.Length == 0 ? "." : " - " + _lastProblem + "."));
            UnityEngine.Object.Destroy(this);
        }
    }

    /// <summary>
    /// One disposable FPV drone per dismounted crew. The master client uses
    /// the crew's real kill target, which preserves the game's faction rules.
    /// A fixed lateral error keeps it dangerous without making it a perfect
    /// homing missile. Other clients receive only its transform and may report
    /// a firearm hit back to the owner.
    /// </summary>
    public static class CrewDrone
    {
        class Pending
        {
            public Transform Root;
            public Array Npcs;
            public float At;
        }

        class Local
        {
            public int Id;
            public GameObject Go;
            public GameObject Target;
            public Vector3 Pos;
            public Vector3 Dir;
            public Vector3 Error;
            public float Hp;
            public float Armed;
            public float Deadline;
            public float NextSend;
        }

        class Remote
        {
            public int Owner;
            public int Id;
            public GameObject Go;
            public Vector3 From;
            public Vector3 To;
            public Vector3 Dir;
            public float T;
            public float Duration;
            public float Last;
        }

        static readonly List<Pending> _pending = new List<Pending>();
        static readonly List<Local> _local = new List<Local>();
        static readonly Dictionary<string, Remote> _remote =
            new Dictionary<string, Remote>();
        static readonly List<string> _remove = new List<string>();
        static int _nextId = 1;

        public static void Begin(Transform root, Array npcs)
        {
            if (RevivalPlugin.CfgPatrolCrewDrone == null
                || !RevivalPlugin.CfgPatrolCrewDrone.Value
                || root == null || npcs == null || npcs.Length == 0) return;
            Pending p = new Pending();
            p.Root = root;
            p.Npcs = npcs;
            p.At = Time.time + Mathf.Max(1f,
                RevivalPlugin.CfgPatrolCrewDroneDelay.Value);
            _pending.Add(p);
        }

        public static void Tick()
        {
            if (RevivalPlugin.CfgPatrolCrewDrone == null
                || !RevivalPlugin.CfgPatrolCrewDrone.Value) return;
            Net.EnsureHooked();
            Net.TickRemotes();

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (p.Root == null) { _pending.RemoveAt(i); continue; }
                if (Time.time < p.At) continue;
                GameObject target = FindTarget(p.Npcs);
                if (target == null) continue;
                Launch(p.Root, target);
                _pending.RemoveAt(i);
            }

            for (int i = _local.Count - 1; i >= 0; i--)
                Move(_local[i]);
        }

        static GameObject FindTarget(Array npcs)
        {
            for (int i = 0; i < npcs.Length; i++)
            {
                object ai = npcs.GetValue(i);
                if (ai == null) continue;
                FieldInfo targetField = AccessTools.Field(ai.GetType(), "_killTarget");
                object raw = targetField == null ? null : targetField.GetValue(ai);
                GameObject go = raw as GameObject;
                Component component = raw as Component;
                if (go == null && component != null) go = component.gameObject;
                if (go != null && go.activeInHierarchy) return go;
            }
            return null;
        }

        static void Launch(Transform root, GameObject target)
        {
            Local d = new Local();
            d.Id = _nextId++;
            d.Target = target;
            d.Pos = root.position + Vector3.up * 3.2f;
            Vector3 to = AimPoint(target) - d.Pos;
            d.Dir = to.sqrMagnitude < 0.01f ? root.forward : to.normalized;

            float miss = Mathf.Max(0f, RevivalPlugin.CfgPatrolCrewDroneMiss.Value);
            float angle = Mathf.Abs(Mathf.Sin(d.Id * 12.9898f)) * Mathf.PI * 2f;
            float radius = miss * (0.45f + 0.55f
                * Mathf.Abs(Mathf.Sin(d.Id * 78.233f)));
            d.Error = new Vector3(Mathf.Cos(angle) * radius,
                                  radius * 0.12f,
                                  Mathf.Sin(angle) * radius);
            d.Hp = Mathf.Max(1, RevivalPlugin.CfgPatrolCrewDroneHitpoints.Value);
            d.Armed = Time.time + 1.2f;
            d.Deadline = Time.time + 55f;
            d.Go = Drone.Modell.Bauen();
            d.Go.name = "NDR Crew FPV " + d.Id;
            d.Go.transform.localScale *= 1.15f;
            d.Go.transform.position = d.Pos;
            d.Go.transform.rotation = Quaternion.LookRotation(d.Dir, Vector3.up);
            Drone.Sound.Anhaengen(d.Go);
            _local.Add(d);
            Net.Send(Net.Start, d.Id, d.Pos, d.Dir, 0f, true);
            RevivalPlugin.L.LogInfo("Crew FPV " + d.Id + " launched at player "
                + target.name + " with " + radius.ToString("0.0")
                + " m deliberate miss.");
        }

        static Vector3 AimPoint(GameObject target)
        {
            return target == null ? Vector3.zero
                : target.transform.position + Vector3.up * 1.1f;
        }

        static void Move(Local d)
        {
            if (d == null || d.Go == null)
            {
                if (d != null) _local.Remove(d);
                return;
            }
            if (Time.time >= d.Deadline)
            {
                Finish(d, d.Pos, false, "flight timeout");
                return;
            }

            Vector3 aim = d.Target == null ? d.Pos + d.Dir * 20f
                : AimPoint(d.Target) + d.Error;
            Vector3 wanted = aim - d.Pos;
            if (wanted.sqrMagnitude > 0.001f) wanted.Normalize();
            else wanted = d.Dir;

            Vector3 right = Vector3.Cross(Vector3.up, wanted);
            if (right.sqrMagnitude > 0.001f) right.Normalize();
            wanted += right * Mathf.Sin(Time.time * 2.1f + d.Id) * 0.055f;
            wanted += Vector3.up * Mathf.Sin(Time.time * 1.4f + d.Id * 0.7f) * 0.025f;
            wanted.Normalize();
            d.Dir = Vector3.Slerp(d.Dir, wanted,
                Mathf.Clamp01(Time.deltaTime * 2.4f)).normalized;

            float speed = Mathf.Max(3f, RevivalPlugin.CfgPatrolCrewDroneSpeed.Value);
            Vector3 step = d.Dir * speed * Time.deltaTime;
            float length = step.magnitude;
            if (Time.time >= d.Armed && length > 0.001f)
            {
                Vector3 hit;
                GameObject struck = Turret.RaycastObject(d.Pos, d.Dir,
                                                         length + 0.25f, out hit);
                if (struck != null)
                {
                    Finish(d, hit, true, "impact");
                    return;
                }
            }

            d.Pos += step;
            d.Go.transform.position = d.Pos;
            d.Go.transform.rotation = Quaternion.LookRotation(d.Dir, Vector3.up);
            if (Time.time >= d.Armed && Vector3.Distance(d.Pos, aim) < 1.1f)
            {
                Finish(d, d.Pos, true, "target area");
                return;
            }
            if (Time.time >= d.NextSend)
            {
                d.NextSend = Time.time + 0.08f;
                Net.Send(Net.Move, d.Id, d.Pos, d.Dir, 0f, false);
            }
        }

        static void Finish(Local d, Vector3 point, bool explode, string why)
        {
            if (!_local.Remove(d)) return;
            Net.Send(Net.End, d.Id, point, d.Dir, explode ? 1f : 0f, true);
            if (d.Go != null) UnityEngine.Object.Destroy(d.Go);
            if (explode)
            {
                try
                {
                    RocketHook.Detonate(point,
                        RevivalPlugin.CfgPatrolCrewDroneDamage.Value,
                        RevivalPlugin.CfgPatrolCrewDroneRadius.Value, 3f);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Crew FPV explosion: " + ex.Message);
                }
            }
            RevivalPlugin.L.LogInfo("Crew FPV " + d.Id + " ended: " + why + ".");
        }

        public static bool Beschuss(Vector3 origin, Vector3 dir, float range,
                                    float damage)
        {
            if (dir.sqrMagnitude < 0.000001f) return false;
            dir.Normalize();
            float radius = RevivalPlugin.CfgPatrolCrewDroneHitRadius == null
                ? 2f : Mathf.Max(0.25f,
                    RevivalPlugin.CfgPatrolCrewDroneHitRadius.Value);
            float nearest = float.MaxValue;
            Local localHit = null;
            Remote remoteHit = null;

            for (int i = 0; i < _local.Count; i++)
            {
                Local d = _local[i];
                if (d.Go == null) continue;
                float t = Along(origin, dir, d.Pos, range, radius);
                if (t > 0f && t < nearest)
                { nearest = t; localHit = d; remoteHit = null; }
            }
            foreach (KeyValuePair<string, Remote> pair in _remote)
            {
                Remote d = pair.Value;
                if (d.Go == null) continue;
                float t = Along(origin, dir, d.Go.transform.position, range, radius);
                if (t > 0f && t < nearest)
                { nearest = t; localHit = null; remoteHit = d; }
            }
            if (localHit == null && remoteHit == null) return false;

            Vector3 obstruction;
            if (nearest > 2.2f && Turret.RaycastObject(origin + dir, dir,
                    nearest - 1.2f, out obstruction) != null) return false;

            if (localHit != null)
            {
                localHit.Hp -= Mathf.Max(0.1f, damage);
                if (localHit.Hp <= 0f)
                    Finish(localHit, localHit.Pos, Time.time >= localHit.Armed,
                           "shot down");
            }
            else
            {
                Net.Send(Net.Hit, remoteHit.Id, remoteHit.Go.transform.position,
                         Vector3.zero, remoteHit.Owner, true);
            }
            Turret.Hinweis(Loc.T("Дрон экипажа подбит", "Crew drone hit"), 0.6f);
            return true;
        }

        static float Along(Vector3 origin, Vector3 dir, Vector3 point,
                           float range, float radius)
        {
            Vector3 to = point - origin;
            float t = Vector3.Dot(to, dir);
            if (t < 1f || t > range) return -1f;
            return (to - dir * t).sqrMagnitude <= radius * radius ? t : -1f;
        }

        public static class Net
        {
            public const int Start = 1;
            public const int Move = 2;
            public const int End = 3;
            public const int Hit = 4;

            static bool _hooked;
            static bool _failed;
            static MethodInfo _raise;
            static Type _optionsType;
            static FieldInfo _eventField;

            public static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    int code = RevivalPlugin.CfgPatrolCrewDroneEventCode.Value;
                    int playerDrone = RevivalPlugin.CfgDroneEventCode.Value;
                    if (code < 0 || code > 199
                        || (code >= playerDrone && code <= playerDrone + 4)
                        || code == RevivalPlugin.CfgTurretEventCode.Value
                        || code == RevivalPlugin.CfgAdminEventCode.Value)
                        throw new Exception("event code overlaps another feature");
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) throw new Exception("PhotonNetwork missing");
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _eventField = AccessTools.Field(photon, "OnEventCall");
                    _optionsType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (_raise == null || _eventField == null)
                        throw new Exception("event reflection path incomplete");
                    MethodInfo own = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_eventField.FieldType, own);
                    Delegate current = _eventField.GetValue(null) as Delegate;
                    _eventField.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Crew FPV network hooked on event "
                        + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Crew FPV network hook: " + ex);
                }
            }

            public static void Send(int action, int id, Vector3 point, Vector3 dir,
                                    float extra, bool reliable)
            {
                EnsureHooked();
                if (!_hooked) return;
                try
                {
                    float[] data = new float[] { action, id,
                        point.x, point.y, point.z, dir.x, dir.y, dir.z, extra };
                    object options = _optionsType == null ? null
                        : Activator.CreateInstance(_optionsType);
                    _raise.Invoke(null, new object[] {
                        (byte)RevivalPlugin.CfgPatrolCrewDroneEventCode.Value,
                        data, reliable, options });
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Crew FPV network send: " + ex.Message);
                }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (RevivalPlugin.CfgPatrolCrewDroneEventCode == null
                    || code != (byte)RevivalPlugin.CfgPatrolCrewDroneEventCode.Value)
                    return;
                try
                {
                    float[] data = content as float[];
                    if (data == null || data.Length < 9) return;
                    int action = Mathf.RoundToInt(data[0]);
                    int id = Mathf.RoundToInt(data[1]);
                    Vector3 point = new Vector3(data[2], data[3], data[4]);
                    Vector3 dir = new Vector3(data[5], data[6], data[7]);
                    if (action == Hit)
                    {
                        if (Mathf.RoundToInt(data[8]) != Admin.Net.OwnActor()) return;
                        for (int i = 0; i < _local.Count; i++)
                        {
                            Local own = _local[i];
                            if (own.Id != id) continue;
                            own.Hp -= 1f;
                            if (own.Hp <= 0f)
                                Finish(own, own.Pos, Time.time >= own.Armed,
                                       "shot down by player #" + sender);
                            return;
                        }
                        return;
                    }
                    if (sender == Admin.Net.OwnActor()) return;
                    string key = sender + ":" + id;
                    if (action == End)
                    {
                        RemoveRemote(key);
                        return;
                    }

                    Remote remote;
                    if (!_remote.TryGetValue(key, out remote))
                    {
                        remote = new Remote();
                        remote.Owner = sender;
                        remote.Id = id;
                        remote.Go = Drone.Modell.Bauen();
                        remote.Go.name = "NDR Remote Crew FPV " + key;
                        remote.Go.transform.localScale *= 1.15f;
                        remote.Go.transform.position = point;
                        Drone.Sound.Anhaengen(remote.Go);
                        remote.From = point;
                        _remote[key] = remote;
                    }
                    else remote.From = remote.Go == null
                        ? point : remote.Go.transform.position;
                    remote.To = point;
                    remote.Dir = dir.sqrMagnitude < 0.001f
                        ? Vector3.forward : dir.normalized;
                    remote.Duration = remote.Last <= 0f ? 0.08f
                        : Mathf.Clamp(Time.time - remote.Last, 0.02f, 0.3f);
                    remote.Last = Time.time;
                    remote.T = 0f;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Crew FPV network receive: "
                        + ex.Message);
                }
            }

            public static void TickRemotes()
            {
                _remove.Clear();
                foreach (KeyValuePair<string, Remote> pair in _remote)
                {
                    Remote r = pair.Value;
                    if (r.Go == null || Time.time - r.Last > 4f)
                    { _remove.Add(pair.Key); continue; }
                    r.T = Mathf.Min(1f, r.T + Time.deltaTime
                        / Mathf.Max(0.02f, r.Duration));
                    r.Go.transform.position = Vector3.Lerp(r.From, r.To, r.T);
                    r.Go.transform.rotation = Quaternion.LookRotation(r.Dir,
                                                                       Vector3.up);
                }
                for (int i = 0; i < _remove.Count; i++) RemoveRemote(_remove[i]);
            }

            static void RemoveRemote(string key)
            {
                Remote r;
                if (!_remote.TryGetValue(key, out r)) return;
                if (r.Go != null) UnityEngine.Object.Destroy(r.Go);
                _remote.Remove(key);
            }
        }

        /// <summary>
        /// Read-only source for the proximity monitor: the world positions of
        /// every crew FPV drone currently in the air on this client, own
        /// (`_local`) and mirrored from other clients (`_remote`). Appends to the
        /// caller's list so the monitor allocates nothing per scan. Deliberately
        /// isolated so `DroneAlert` never reaches into the private lists.
        /// </summary>
        public static void CollectThreats(List<Vector3> into)
        {
            if (into == null) return;
            for (int i = 0; i < _local.Count; i++)
            {
                Local d = _local[i];
                if (d != null && d.Go != null) into.Add(d.Go.transform.position);
            }
            foreach (KeyValuePair<string, Remote> pair in _remote)
            {
                Remote d = pair.Value;
                if (d != null && d.Go != null) into.Add(d.Go.transform.position);
            }
        }
    }
}
