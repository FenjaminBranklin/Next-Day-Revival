using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    // ---------------------------------------------------- NPC-versus-NPC combat
    //
    // WHAT THIS ADDS
    //
    //   The vanilla AI only ever fights players: sensing reads
    //   NetworkGameServer.localPlayer, target allocation resolves Photon PLAYER
    //   ids, and NPC_FirearmWeaponController.FireOneShot has no branch that
    //   damages NPC infantry (audit NPC_AI_COMBAT_ARCHITECTURE_AUDIT.txt,
    //   sections 2 and 13). Two NPCs next to each other never fight.
    //
    //   NpcWar runs OPERATIONS. An operation is one squad a heli troop landing
    //   (RevivalTroopInsertion) has put on the ground, plus the combat vector
    //   the admin drew in the editor:
    //
    //     ToStart   the squad walks from the landing zone to the arrow tail
    //     Advance   it walks as one body along the arrow toward the head and
    //               attacks everything it sees that its faction hates
    //     Patrol    the survivors walk the arrow up and down until the patrol
    //               time (at most two hours) is over
    //     removed   every man of the squad, alive or dead, leaves the map
    //
    //   The men stay ordinary game NPCs. Players are the game's business: a man
    //   whose own AI has a player kill target (NPC_AI2._killTarget) gets no
    //   order from here at all, so the vanilla aim, fire, cover and reload run
    //   untouched. NpcWar only adds what the game cannot do - walking the arrow
    //   and fighting other NPCs - and an NPC the squad fires on becomes a
    //   DEFENDER that fires back, so a fight is two-sided.
    //
    //   An NPC-vs-NPC shot uses the game's own weapon: the man is put in the
    //   native aim state (MainState 0, AdditionalState 3 - the state
    //   NPC_AI2.ShootingActions fires from) and NPC_FirearmWeaponController.
    //   FireTo(point, true) fires it, which brings the weapon's rate of fire,
    //   magazine, muzzle flash and sound on every client (RPC
    //   NetworkWeaponState). FireOneShot has no NPC damage branch, so the hit is
    //   decided by our own raycast and applied through NPC_AI2.ApplyDamage - the
    //   road the patrol guns already use (Revival.Patrol.cs, Gun.Schaden). An
    //   empty magazine starts the native reload (NPC_AI2.OnBulletsEnded). Only
    //   when a weapon controller cannot fire does the old tracer-and-sound shot
    //   stand in.
    //
    // 6.16.0 FIELD BUG. InitSpawnNpc parents every NPC under the settlement's
    //   AllPeopleTr (IL_015F-016A), and 6.16.0 set the settlement's position to
    //   the men's centre every frame: each frame moved every man by the offset
    //   between his settlement and the squad centre, so the squad shot across
    //   the map in circles and never stood on the NavMesh long enough to walk
    //   or fire. Only the walk-point root follows the squad now, every two
    //   seconds, and the men move by NavMesh orders alone.
    //
    // SCALE. The game world is modelled about 2.8 times real size: a human NPC
    //   capsule is 5.0 units tall (CapsuleCollider on every *_NPC prefab), so
    //   heights and spacings below are in those units.
    //
    // AUTHORITY
    //
    //   Only the Photon master client decides anything, and only on NPCs whose
    //   photonView.isMine is true - the two conditions ApplyDamage checks.
    //   Nothing here runs without an operation, so a map without troop landings
    //   is unchanged.
    //
    public static class NpcWar
    {
        // -------------------------------------------------------------- config

        internal static ConfigEntry<float> CfgEngageRange;
        internal static ConfigEntry<float> CfgSightRange;
        internal static ConfigEntry<float> CfgFireInterval;
        internal static ConfigEntry<float> CfgReactionMax;
        internal static ConfigEntry<int>   CfgBurst;
        internal static ConfigEntry<float> CfgDamage;
        internal static ConfigEntry<float> CfgAccuracy;
        internal static ConfigEntry<float> CfgSpread;
        internal static ConfigEntry<float> CfgDetour;
        internal static ConfigEntry<int>   CfgMaxCombatants;
        internal static ConfigEntry<bool>  CfgDebug;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEngageRange = cfg.Bind("NpcWar", "EngageRange", 60f,
                "Ab dieser Entfernung (Meter) bleibt ein Truppmitglied stehen und "
                + "schiesst, statt weiter vorzuruecken.");
            CfgSightRange = cfg.Bind("NpcWar", "SightRange", 110f,
                "Groesste Entfernung (Meter), auf die ein NPC einen feindlichen NPC "
                + "als Ziel annimmt. Sichtlinie wird vor jedem Schuss per Strahl "
                + "geprueft.");
            CfgFireInterval = cfg.Bind("NpcWar", "FireInterval", 1.0f,
                "Grundabstand in Sekunden zwischen zwei Feuerstoessen eines NPC. Der "
                + "echte Abstand schwankt zwischen diesem Wert und dem Doppelten.");
            CfgReactionMax = cfg.Bind("NpcWar", "ReactionMax", 0.8f,
                "Groesste zufaellige Reaktionszeit in Sekunden, wenn ein NPC ein "
                + "neues Ziel erfasst.");
            CfgBurst = cfg.Bind("NpcWar", "BurstMax", 3,
                "Hoechstzahl schneller Schuesse in Folge. 1 = kein Feuerstoss.");
            CfgDamage = cfg.Bind("NpcWar", "DamagePerShot", 18f,
                "Schaden je Treffer an einem NPC.");
            CfgAccuracy = cfg.Bind("NpcWar", "Accuracy", 0.6f,
                "Trefferwahrscheinlichkeit auf kurze Entfernung (0..1); sie faellt "
                + "zur Sichtweite hin um 40 Prozent ab.");
            CfgSpread = cfg.Bind("NpcWar", "MissSpread", 1.6f,
                "Wie weit (Meter) ein verfehlter Schuss neben dem Ziel einschlaegt.");
            CfgDetour = cfg.Bind("NpcWar", "MaxDetour", 80f,
                "Wie weit (Meter) sich der Trupp fuer einen gesehenen Gegner von "
                + "seinem Pfeil entfernen darf.");
            CfgMaxCombatants = cfg.Bind("NpcWar", "MaxDefenders", 32,
                "Sicherheitsgrenze: so viele angegriffene NPCs duerfen gleichzeitig "
                + "zurueckschiessen (alle Einsaetze zusammen).");
            CfgDebug = cfg.Bind("NpcWar", "Debug", false,
                "Ausfuehrliche Log-Zeilen und eine Statuszeile oben links.");
        }

        // -------------------------------------------------------- world scale

        const float ChestHeight = 3.3f;     // of a 5.0 unit NPC capsule
        const float EyeHeight = 4.2f;
        const float FileGap = 6f;           // two files abreast
        const float RowDepth = 7f;
        const float Bound = 45f;            // one bound of the body
        const float Arrive = 25f;           // the body counts as arrived

        // NPC_AI2 states, from ShootingActions/OnBulletsEnded/IdleStateAction.
        const int MainIdle = 0, MainWalk = 1, MainRun = 2;
        const int AddNone = 0, AddAim = 3;

        // ------------------------------------------------------- runtime state

        enum Phase { ToStart, Advance, Patrol }

        /// <summary>One NPC in a fight: a squad man or a defender.</summary>
        class Fighter
        {
            public Component Ai;          // NPC_AI2
            public Transform Tr;
            public Squad Squad;           // null = defender
            public object Faction;        // Fraction enum value
            public Array Hated;           // Fraction[] this NPC hates
            public Transform Target;
            public float NextScan, NextShot, ReactUntil, NextMove, NextAim, NextLos, LastSeen;
            public int Burst, Slot;
            public bool Aiming, HasOrder, Sees;
            public Vector3 Ordered;
            public GameObject Point;      // the walk point currently issued
        }

        class Squad
        {
            public string Tag;
            public GameObject Settlement;
            public Transform WalkRoot;       // AllWalkPointsTr: follows the body
            public readonly List<Fighter> Men = new List<Fighter>();
            public Vector3 Tail, Head;
            public Phase Phase;
            public bool TowardHead = true;   // patrol leg
            public float PatrolSeconds, PatrolEnds, HardEnd, ContactUntil, NextRing;
            public Vector3 Body;             // where the body is heading now
        }

        static readonly List<Squad> _squads = new List<Squad>();
        static readonly List<Fighter> _defenders = new List<Fighter>();
        static List<Component> _scene = new List<Component>();
        static float _nextSceneScan;
        static Transform _pointsRoot;
        static string _status = "";

        // ------------------------------------------------------- reflection cache

        static bool _looked, _ok;
        static Type _npcType, _optType, _wpType;
        static FieldInfo _fMainOptions, _fMyFraction, _fHated, _fTempPoints, _fTempIndex, _fWpType;
        static FieldInfo _fKillTarget, _fReloading, _fMainState, _fAddState, _fPoseState;
        static FieldInfo _fUseTemp, _fTempTaskField, _fWeapon, _fAimingPoint, _fRofDelay;
        static MethodInfo _mIsAlive, _mTempTask, _mTargetWp, _mStateSync, _mAlarm;
        static MethodInfo _mPhotonView, _mIsMine, _mMasterGetter, _mDestroy;
        static MethodInfo _mBulletsEnded, _mStartRotation;
        static MethodInfo _mFireTo, _mHasBullets, _mMuzzle, _mCantWork;
        static object _wpTacticalValue;

        static bool LookUp()
        {
            if (_looked) return _ok;
            _looked = true;

            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            _optType = RevivalPlugin.TypeByName("NPCMainOptions");
            _wpType = RevivalPlugin.TypeByName("NPC_WP");
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_npcType == null || _optType == null || _wpType == null)
            {
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2, NPCMainOptions or NPC_WP "
                    + "not found - troop squads will not fight NPCs.");
                return false;
            }

            _fMainOptions = AccessTools.Field(_npcType, "MainOptions");
            _fTempPoints = AccessTools.Field(_npcType, "_temporaryWalkPoints");
            _fTempIndex = AccessTools.Field(_npcType, "_temporaryWalkPointIndex");
            _fMyFraction = AccessTools.Field(_optType, "MyFraction");
            _fHated = AccessTools.Field(_optType, "HatedFractions");
            _fWpType = AccessTools.Field(_wpType, "Type");
            _fKillTarget = AccessTools.Field(_npcType, "_killTarget");
            _fReloading = AccessTools.Field(_npcType, "_reloading");
            _fMainState = AccessTools.Field(_npcType, "MainState");
            _fAddState = AccessTools.Field(_npcType, "AdditionalState");
            _fPoseState = AccessTools.Field(_npcType, "PoseState");
            _fUseTemp = AccessTools.Field(_npcType, "_useTemporaryWalkPoints");
            _fTempTaskField = AccessTools.Field(_npcType, "TemporaryTask");
            _fWeapon = AccessTools.Field(_npcType, "_firearmWeaponController");
            _fAimingPoint = AccessTools.Field(_npcType, "_aimingPoint");

            _mIsAlive = AccessTools.Method(_npcType, "IsAlive", null, null);
            _mTempTask = AccessTools.Method(_npcType, "SetTemporaryTask", null, null);
            _mTargetWp = AccessTools.Method(_npcType, "SetTargetWalkPoint", null, null);
            _mStateSync = AccessTools.Method(_npcType, "SetStateWithAnimAndSync", null, null);
            _mAlarm = AccessTools.Method(_npcType, "SetGeneralAlarm", null, null);
            _mPhotonView = AccessTools.Method(_npcType, "get_photonView", null, null);
            _mBulletsEnded = AccessTools.Method(_npcType, "OnBulletsEnded", Type.EmptyTypes, null);
            _mStartRotation = AccessTools.Method(_npcType, "StartRotation",
                new Type[] { typeof(Vector3) }, null);

            Type weapon = _fWeapon == null ? null : _fWeapon.FieldType;
            if (weapon != null)
            {
                _mFireTo = AccessTools.Method(weapon, "FireTo",
                    new Type[] { typeof(Vector3), typeof(bool) }, null);
                _mHasBullets = AccessTools.Method(weapon, "HasBullets", Type.EmptyTypes, null);
                _mMuzzle = AccessTools.Method(weapon, "GetMuzzlePos", Type.EmptyTypes, null);
                _mCantWork = AccessTools.Method(weapon, "CantWorkWeapon", Type.EmptyTypes, null);
                _fRofDelay = AccessTools.Field(weapon, "CurrentRateOfFireDelay");
                if (_fRofDelay != null && _fRofDelay.FieldType != typeof(float)) _fRofDelay = null;
            }

            if (photon != null)
            {
                _mMasterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                _mDestroy = AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
            }

            if (_fWpType != null && _fWpType.FieldType.IsEnum)
            {
                try { _wpTacticalValue = Enum.Parse(_fWpType.FieldType, "Tactical", true); }
                catch { _wpTacticalValue = null; }
            }

            _ok = _fMainOptions != null && _fMyFraction != null && _fHated != null
                  && _mIsAlive != null;
            if (!_ok)
                RevivalPlugin.L.LogWarning("NpcWar: a required NPC_AI2 member is missing "
                    + "(MainOptions " + (_fMainOptions != null) + ", MyFraction "
                    + (_fMyFraction != null) + ", HatedFractions " + (_fHated != null)
                    + ", IsAlive " + (_mIsAlive != null) + ") - squads will not fight NPCs.");
            if (_mTempTask == null || _mTargetWp == null || _mStateSync == null
                || _fTempPoints == null || _wpTacticalValue == null)
                RevivalPlugin.L.LogWarning("NpcWar: native movement entry points missing - "
                    + "squads stay where they land.");
            if (_mFireTo == null || _mHasBullets == null || _fRofDelay == null
                || _fMainState == null || _fAddState == null)
                RevivalPlugin.L.LogWarning("NpcWar: native weapon or aim members missing - "
                    + "NPC-vs-NPC shots fall back to tracer and sound.");
            return _ok;
        }

        static bool IsMaster()
        {
            if (_mMasterGetter == null) return true;
            try { return (bool)_mMasterGetter.Invoke(null, null); }
            catch { return true; }
        }

        // ------------------------------------------------------------ operations

        /// <summary>Hand a freshly landed squad its combat vector. The patrol
        /// clock starts when the body reaches the arrow head; a squad that never
        /// gets there is still removed after the walking allowance plus the
        /// patrol time, so no landing can pile men up on the map.</summary>
        internal static bool StartOperation(string tag, GameObject settlement, Array npcs,
                                            Vector3 tail, Vector3 head, float patrolSeconds)
        {
            if (!LookUp() || settlement == null || npcs == null) return false;
            Squad s = new Squad();
            s.Tag = tag;
            s.Settlement = settlement;
            s.WalkRoot = WalkRootOf(settlement);
            s.Tail = tail;
            s.Head = head;
            s.Phase = Phase.ToStart;
            s.PatrolSeconds = Mathf.Clamp(patrolSeconds, 60f, 7200f);
            float walk = Vector3.Distance(settlement.transform.position, tail)
                       + Vector3.Distance(tail, head);
            // One unit per second is a slow, fighting pace; plus half an hour.
            s.HardEnd = Time.time + walk + 1800f + s.PatrolSeconds;
            s.Body = tail;

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null) continue;
                // A squad over eight men got transport firing sectors around the
                // landing zone (Crew.AssignSectors). They would pull every alarm
                // move back to the helicopter; the arrow is this squad's sector.
                CrewSector sector = ai.GetComponent<CrewSector>();
                if (sector != null) UnityEngine.Object.Destroy(sector);
                Fighter f = NewFighter(ai, s);
                f.Slot = s.Men.Count;
                f.NextMove = Time.time + 0.5f + 0.15f * f.Slot;
                s.Men.Add(f);
            }
            if (s.Men.Count == 0) return false;
            _squads.Add(s);
            EnsurePointsRoot();
            RevivalPlugin.L.LogInfo("NpcWar: operation " + tag + " - " + s.Men.Count
                + " men, arrow " + tail.ToString("0") + " -> " + head.ToString("0")
                + ", patrol " + (s.PatrolSeconds / 60f).ToString("0") + " min"
                + (s.WalkRoot == null ? ", no walk-point root found" : "") + ".");
            return true;
        }

        /// <summary>Is an operation with this tag still on the map?</summary>
        internal static bool IsActive(string tag)
        {
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i].Tag == tag) return true;
            return false;
        }

        internal static int ActiveCount { get { return _squads.Count; } }

        static Fighter NewFighter(Component ai, Squad squad)
        {
            Fighter f = new Fighter();
            f.Ai = ai;
            f.Tr = ai.transform;
            f.Squad = squad;
            f.Faction = FactionOf(ai);
            f.Hated = GetHated(ai);
            f.NextScan = Time.time + UnityEngine.Random.value;
            f.NextShot = Time.time + UnityEngine.Random.value * CfgFireInterval.Value;
            return f;
        }

        static Transform WalkRootOf(GameObject settlement)
        {
            try
            {
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                Component sied = sType == null ? null : settlement.GetComponent(sType);
                FieldInfo f = sied == null ? null : AccessTools.Field(sType, "AllWalkPointsTr");
                return f == null ? null : f.GetValue(sied) as Transform;
            }
            catch { return null; }
        }

        // ----------------------------------------------------------- per frame

        public static void Tick()
        {
            if (_squads.Count == 0 && _defenders.Count == 0) { _status = ""; return; }
            if (!LookUp()) return;
            if (!IsMaster()) { _status = "NpcWar: not master client"; return; }

            float now = Time.time;
            if (now >= _nextSceneScan)
            {
                _nextSceneScan = now + 2f;
                _scene = LiveNpcs();
            }

            for (int q = _squads.Count - 1; q >= 0; q--)
            {
                Squad s = _squads[q];
                try { RunSquad(s, now); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("NpcWar: operation " + s.Tag + " - " + ex);
                    Remove(s, "error");
                }
            }

            for (int i = _defenders.Count - 1; i >= 0; i--)
            {
                Fighter d = _defenders[i];
                if (d.Ai == null || d.Tr == null || !Alive(d.Ai) || NearestSquadMan(d, 2f) == null)
                {
                    if (d.Ai != null && d.Aiming && Alive(d.Ai)) StandDown(d);
                    _defenders.RemoveAt(i);
                    continue;
                }
                if (HasKillTarget(d)) { d.Aiming = false; continue; }   // a player: the game's fight
                if (!Fight(d, now) && d.Aiming) StandDown(d);
            }

            _status = "NpcWar: " + _squads.Count + " operation(s), "
                + _defenders.Count + " defender(s)";
        }

        static void RunSquad(Squad s, float now)
        {
            int alive = 0;
            Vector3 centre = Vector3.zero;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                alive++;
                centre += f.Tr.position;
            }

            if (s.Settlement == null) { Remove(s, "settlement gone"); return; }
            if (alive == 0) { Remove(s, "wiped out"); return; }
            if (now >= s.HardEnd) { Remove(s, "time is up"); return; }
            if (s.Phase == Phase.Patrol && now >= s.PatrolEnds) { Remove(s, "patrol over"); return; }
            centre /= alive;

            // The ring of walk and tactical points follows the body, so whatever
            // the vanilla alarm picks between two orders is next to the squad,
            // not back at the landing zone. Never the settlement itself: the
            // men are its children (InitSpawnNpc) and would be dragged along.
            if (s.WalkRoot != null && now >= s.NextRing)
            {
                s.NextRing = now + 2f;
                s.WalkRoot.position = centre;
            }

            // Phase changes are decided by the body, not by one fast runner.
            Vector3 goal = Goal(s);
            if (Flat(centre - goal) < Arrive)
            {
                if (s.Phase == Phase.ToStart) s.Phase = Phase.Advance;
                else if (s.Phase == Phase.Advance)
                {
                    s.Phase = Phase.Patrol;
                    s.PatrolEnds = now + s.PatrolSeconds;
                    s.TowardHead = false;
                    RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " reached the "
                        + "arrow head with " + alive + " men - patrolling for "
                        + (s.PatrolSeconds / 60f).ToString("0") + " min.");
                }
                else s.TowardHead = !s.TowardHead;
                goal = Goal(s);
            }

            // The body moves in bounds along the line toward the goal, so the
            // men arrive together instead of strung out over the map. A hostile
            // seen near the arrow pulls the body toward it, to just inside
            // firing range rather than on top of it.
            Transform enemy = NearestEnemyOfSquad(s, centre);
            if (enemy != null) s.ContactUntil = now + 6f;
            if (enemy != null && DistanceToSegment(enemy.position, s.Tail, s.Head) <= CfgDetour.Value)
            {
                Vector3 back = centre - enemy.position;
                back.y = 0f;
                float keep = Mathf.Min(back.magnitude, CfgEngageRange.Value * 0.6f);
                s.Body = back.sqrMagnitude < 0.01f ? enemy.position
                       : enemy.position + back.normalized * keep;
            }
            else
            {
                Vector3 to = goal - centre;
                to.y = 0f;
                float bound = Mathf.Min(to.magnitude, Bound);
                s.Body = to.sqrMagnitude < 0.01f ? goal : centre + to.normalized * bound;
            }

            Vector3 along = s.Head - s.Tail;
            along.y = 0f;
            if (along.sqrMagnitude < 0.01f) along = Vector3.forward;
            along.Normalize();
            if (s.Phase == Phase.Patrol && !s.TowardHead) along = -along;
            bool contact = now < s.ContactUntil;

            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;

                // A player in his sights: the game's own combat runs him.
                if (HasKillTarget(f)) { f.Aiming = false; f.HasOrder = false; continue; }

                if (Fight(f, now)) continue;          // aiming and firing at an NPC
                if (f.Aiming) { StandDown(f); f.NextMove = now; }
                if (Reloading(f) || now < f.NextMove) continue;
                f.NextMove = now + 1.2f + UnityEngine.Random.value * 0.6f;

                Vector3 spot = s.Body + Formation(f.Slot, along);
                float away = Flat(f.Tr.position - spot);
                if (away < 4f) { f.HasOrder = false; continue; }   // already there
                // A straggler or a squad in contact runs; otherwise they walk.
                int state = contact || away > 40f ? MainRun : MainWalk;
                // Walking there already: re-issuing only restarts the animation.
                if (f.HasOrder && Flat(spot - f.Ordered) < 6f
                    && IntField(f.Ai, _fMainState, -1) == state && StillOurPoint(f)) continue;
                OrderMove(f, spot, state);
            }
        }

        /// <summary>Does the man still walk to the point we gave him? The alarm's
        /// tactical intentions replace the temporary list with the settlement's
        /// tactical points (IntentionsActions IL_044F).</summary>
        static bool StillOurPoint(Fighter f)
        {
            if (f.Point == null || _fTempPoints == null) return false;
            try
            {
                IList list = _fTempPoints.GetValue(f.Ai) as IList;
                return list != null && list.Count == 1
                    && list[0] as Component == f.Point.GetComponent(_wpType);
            }
            catch { return false; }
        }

        static Vector3 Goal(Squad s)
        {
            switch (s.Phase)
            {
                case Phase.ToStart: return s.Tail;
                case Phase.Advance: return s.Head;
                default: return s.TowardHead ? s.Head : s.Tail;
            }
        }

        /// <summary>Two files abreast, rows behind each other, laid out in the
        /// direction of travel.</summary>
        static Vector3 Formation(int slot, Vector3 along)
        {
            Vector3 side = new Vector3(along.z, 0f, -along.x);
            float lateral = (slot % 2 == 0 ? -0.5f : 0.5f) * FileGap;
            float back = (slot / 2) * RowDepth;
            return side * lateral - along * back;
        }

        // ------------------------------------------------------------- combat

        /// <summary>Scan, aim and fire at an NPC target. True while the man is
        /// engaged - he then takes no movement order.</summary>
        static bool Fight(Fighter f, float now)
        {
            if (now >= f.NextScan)
            {
                f.NextScan = now + 0.75f + UnityEngine.Random.value * 0.25f;
                Transform had = f.Target;
                f.Target = f.Squad != null ? PickTargetForMan(f) : PickTargetForDefender(f);
                if (f.Target != null && f.Target != had)
                {
                    f.ReactUntil = now + UnityEngine.Random.Range(0.2f, Mathf.Max(0.2f, CfgReactionMax.Value));
                    f.LastSeen = now;
                    f.NextLos = now;
                }
            }
            if (f.Target == null) return false;
            if (Vector3.Distance(f.Tr.position, f.Target.position) > CfgEngageRange.Value) return false;
            if (Reloading(f)) return true;

            if (now >= f.NextLos)
            {
                f.NextLos = now + 0.5f;
                f.Sees = LineOfSight(f.Tr, f.Target);
                if (f.Sees) f.LastSeen = now;
            }
            // Three seconds without a line of fire: move up with the squad
            // instead of aiming at a wall.
            if (!f.Sees && now - f.LastSeen > 3f) return false;

            HoldAim(f, now);
            if (!f.Sees || now < f.ReactUntil || now < f.NextShot) return true;
            if (Shoot(f)) ScheduleNextShot(f, now);
            return true;
        }

        static void ScheduleNextShot(Fighter f, float now)
        {
            float baseDelay = Mathf.Max(0.1f, CfgFireInterval.Value);
            int burstMax = Mathf.Max(1, CfgBurst.Value);
            f.Burst++;
            if (f.Burst < burstMax)
                f.NextShot = now + baseDelay * UnityEngine.Random.Range(0.15f, 0.3f);
            else
            {
                f.Burst = 0;
                f.NextShot = now + UnityEngine.Random.Range(baseDelay, baseDelay * 2f);
            }
        }

        /// <summary>A squad man: keep a living target in sight, otherwise the
        /// nearest NPC he hates - a scene NPC, a defender, or a man of another
        /// squad. Never his own squad, never a god-mode or safe-zone NPC.</summary>
        static Transform PickTargetForMan(Fighter f)
        {
            float sight = CfgSightRange.Value;
            if (f.Target != null)
            {
                Component cur = f.Target.GetComponent(_npcType);
                if (cur != null && Alive(cur)
                    && Vector3.Distance(f.Tr.position, f.Target.position) <= sight * 1.2f)
                    return f.Target;
            }

            Component best = null;
            float bestSqr = sight * sight;
            Vector3 p = f.Tr.position;
            for (int i = 0; i < _scene.Count; i++)
            {
                Component c = _scene[i];
                if (c == null || c == f.Ai) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d >= bestSqr) continue;
                Fighter other = FighterOf(c);
                if (other != null && other.Squad == f.Squad) continue;
                if (!Hostile(f.Hated, FactionOf(c))) continue;
                if (other == null && !Targetable(c)) continue;
                if (!Alive(c)) continue;
                best = c;
                bestSqr = d;
            }
            if (best == null) return null;
            if (FighterOf(best) == null) Enlist(best);
            return best.transform;
        }

        /// <summary>A defender fires back at the nearest squad man in sight.</summary>
        static Transform PickTargetForDefender(Fighter f)
        {
            Fighter man = NearestSquadMan(f, 1f);
            return man == null ? null : man.Tr;
        }

        static Fighter NearestSquadMan(Fighter f, float rangeFactor)
        {
            Fighter best = null;
            float bestSqr = CfgSightRange.Value * rangeFactor;
            bestSqr *= bestSqr;
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                {
                    Fighter m = _squads[q].Men[i];
                    if (m.Ai == null || m.Tr == null || !Alive(m.Ai)) continue;
                    float d = (m.Tr.position - f.Tr.position).sqrMagnitude;
                    if (d < bestSqr) { best = m; bestSqr = d; }
                }
            return best;
        }

        /// <summary>The attacked NPC fights back - and so do comrades of its
        /// faction standing close to it, which is what a real ambush on a camp
        /// looks like. Alarmed through the vanilla call so it also engages a
        /// hostile player the normal way.</summary>
        static void Enlist(Component struck)
        {
            object faction = FactionOf(struck);
            Vector3 at = struck.transform.position;
            for (int i = 0; i < _scene.Count && _defenders.Count < CfgMaxCombatants.Value; i++)
            {
                Component c = _scene[i];
                if (c == null || FighterOf(c) != null || !Targetable(c) || !Alive(c)) continue;
                if (c != struck)
                {
                    if ((c.transform.position - at).sqrMagnitude > 60f * 60f) continue;
                    object other = FactionOf(c);
                    if (other == null || !other.Equals(faction)) continue;
                }
                Fighter d = NewFighter(c, null);
                d.ReactUntil = Time.time + UnityEngine.Random.Range(0.4f, 1.5f);
                _defenders.Add(d);
                TryAlarm(c);
            }
        }

        /// <summary>The nearest enemy any man has: his NPC target or the player
        /// his own AI is fighting.</summary>
        static Transform NearestEnemyOfSquad(Squad s, Vector3 centre)
        {
            Transform best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                Transform t = f.Target;
                Component player = KillTarget(f);
                if (player != null) t = player.transform;
                if (t == null) continue;
                float d = (t.position - centre).sqrMagnitude;
                if (d < bestSqr) { best = t; bestSqr = d; }
            }
            return best;
        }

        // ------------------------------------------------------------- firing

        /// <summary>One shot at the man's target. False when the weapon did not
        /// fire this frame (rate of fire, reload), so the burst waits for it.</summary>
        static bool Shoot(Fighter f)
        {
            Vector3 aimAt = f.Target.position + Vector3.up * ChestHeight;
            Component weapon = WeaponOf(f);
            Vector3 from = Muzzle(weapon, f);
            float dist = Vector3.Distance(from, aimAt);
            Vector3 aim = aimAt + MissOffset(from, aimAt, dist);

            int fired = VanillaShot(f, weapon, aim);
            if (fired == 0) return false;
            bool native = fired > 0;

            Vector3 dir = aim - from;
            if (dir.sqrMagnitude < 0.0001f) return true;
            dir.Normalize();

            float range = Mathf.Max(dist + 5f, CfgSightRange.Value + 20f);
            Vector3 impact;
            // Past the muzzle, or past the shooter's own 0.75 unit capsule when
            // the fallback shoots from eye height.
            GameObject struck = Turret.RaycastObject(from + dir * 1.0f, dir, range, out impact);
            Vector3 end = struck == null ? from + dir * range : impact;
            ShotEffect(from, end, !native);
            RevivalTroopInsertion.Net.SendShot(from, end, !native);

            if (struck == null) return true;
            Component hitAi = struck.GetComponentInParent(_npcType);
            if (hitAi == null || !Alive(hitAi)) return true;
            Fighter victim = FighterOf(hitAi);
            if (victim != null && victim.Squad != null && victim.Squad == f.Squad) return true;
            bool enemy = f.Squad == null
                ? victim != null && victim.Squad != null
                : Hostile(f.Hated, FactionOf(hitAi)) && (victim != null || Targetable(hitAi));
            if (!enemy) return true;
            if (victim == null && f.Squad != null) Enlist(hitAi);

            if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", CfgDamage.Value)
                && CfgDebug.Value)
                RevivalPlugin.L.LogInfo("NpcWar: hit at " + dist.ToString("0") + " units.");
            return true;
        }

        /// <summary>Fire the NPC's own weapon at a point. 1 = a round went out,
        /// 0 = not this frame (rate of fire, empty and reloading), -1 = no usable
        /// native weapon, the caller shoots the mod's substitute instead.</summary>
        static int VanillaShot(Fighter f, Component weapon, Vector3 aim)
        {
            if (weapon == null || _mFireTo == null || _mHasBullets == null || _fRofDelay == null)
                return -1;
            try
            {
                if (_mCantWork != null && (bool)_mCantWork.Invoke(weapon, null)) return -1;
                if (!(bool)_mHasBullets.Invoke(weapon, null))
                {
                    StartReload(f);
                    return 0;
                }
                float before = (float)_fRofDelay.GetValue(weapon);
                if (before >= Time.time) return 0;
                if (_fAimingPoint != null && _fAimingPoint.FieldType == typeof(Vector3))
                    _fAimingPoint.SetValue(f.Ai, aim);
                _mFireTo.Invoke(weapon, new object[] { aim, true });
                float after = (float)_fRofDelay.GetValue(weapon);
                return after != before ? 1 : 0;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: native shot failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return -1;
            }
        }

        /// <summary>An empty magazine: the game's own reload, exactly what
        /// ShootingActions does on the master (reload state, animation, RPC).</summary>
        static void StartReload(Fighter f)
        {
            if (_mBulletsEnded == null || Reloading(f)) return;
            try
            {
                _mBulletsEnded.Invoke(f.Ai, null);
                f.Aiming = false;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: reload failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        /// <summary>Tracer, and the mod's report only when the game's weapon did
        /// not fire (its own RPC already plays flash and sound everywhere).
        /// Identical on the master and on every client that receives the
        /// broadcast.</summary>
        internal static void ShotEffect(Vector3 from, Vector3 end, bool sound)
        {
            try
            {
                if (sound) VehicleShotSound.Play(from, false);
                List<Vector3> path = new List<Vector3>();
                path.Add(from + (end - from).normalized * 1.2f);
                path.Add(end);
                RocketHook.SpawnTracer(path, 0.06f, 0.03f,
                    new Color(1f, 0.9f, 0.6f, 1f), new Color(1f, 0.6f, 0.2f, 0.6f), 0.08f);
            }
            catch (Exception ex)
            {
                if (CfgDebug != null && CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: shot effect - " + ex.Message);
            }
        }

        static Vector3 MissOffset(Vector3 from, Vector3 to, float dist)
        {
            float acc = Mathf.Clamp01(CfgAccuracy.Value);
            float far = Mathf.Clamp01(dist / Mathf.Max(1f, CfgSightRange.Value));
            if (UnityEngine.Random.value <= acc * (1f - 0.4f * far)) return Vector3.zero;

            Vector3 axis = (to - from).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, axis);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(axis, side).normalized;
            float ang = UnityEngine.Random.value * Mathf.PI * 2f;
            float amt = Mathf.Max(0.4f, CfgSpread.Value) * (0.5f + UnityEngine.Random.value);
            return (side * Mathf.Cos(ang) + up * Mathf.Sin(ang)) * amt;
        }

        static bool LineOfSight(Transform shooter, Transform target)
        {
            Vector3 from = shooter.position + Vector3.up * EyeHeight;
            Vector3 to = target.position + Vector3.up * ChestHeight;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 1f) return true;
            dir /= dist;
            Vector3 point;
            // Start past the shooter's own 0.75 unit capsule and ragdoll.
            GameObject hit = Turret.RaycastObject(from + dir * 1.2f, dir, dist, out point);
            if (hit == null) return true;
            if (hit.transform.IsChildOf(target)) return true;
            return (to - point).sqrMagnitude < 6.25f;
        }

        // ------------------------------------------------------------- aiming

        /// <summary>Stand and aim at the target in the native aim state. The
        /// idle action generates no new intentions while AdditionalState is set
        /// (IdleStateAction IL_060D), so the pose holds between shots.</summary>
        static void HoldAim(Fighter f, float now)
        {
            Vector3 flat = f.Target.position - f.Tr.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.01f)
                f.Tr.rotation = Quaternion.RotateTowards(f.Tr.rotation,
                    Quaternion.LookRotation(flat), 360f * Time.deltaTime);

            if (_mStateSync == null || _fMainState == null || _fAddState == null) return;
            bool aiming = IntField(f.Ai, _fMainState, -1) == MainIdle
                       && IntField(f.Ai, _fAddState, -1) == AddAim;
            if (aiming) { f.Aiming = true; return; }
            if (now < f.NextAim) return;
            f.NextAim = now + 0.8f;
            float rotY = flat.sqrMagnitude > 0.01f
                ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : f.Tr.eulerAngles.y;
            if (SetState(f, MainIdle, AddAim, Mathf.Max(0, IntField(f.Ai, _fPoseState, 0)), -1, rotY))
            {
                f.Aiming = true;
                f.HasOrder = false;
                if (_mStartRotation != null)
                {
                    try { _mStartRotation.Invoke(f.Ai, new object[] { f.Target.position }); }
                    catch { }
                }
            }
        }

        /// <summary>Out of the aim pose: a defender goes back to idle, a squad
        /// man gets his next move order right after this.</summary>
        static void StandDown(Fighter f)
        {
            f.Aiming = false;
            if (IntField(f.Ai, _fAddState, -1) != AddAim) return;
            SetState(f, MainIdle, AddNone, Mathf.Max(0, IntField(f.Ai, _fPoseState, 0)), -1,
                f.Tr.eulerAngles.y);
        }

        // ----------------------------------------------------------- movement

        /// <summary>Send one man to a world point on the NavMesh: a tactical
        /// task, one tactical walk point there, and SetStateWithAnimAndSync,
        /// which drives the NavMeshAgent, the animation and the Photon sync.
        /// State 1 walks (IdleStateAction uses it for patrol points), 2 runs.</summary>
        static void OrderMove(Fighter f, Vector3 dest, int state)
        {
            if (_mTempTask == null || _mTargetWp == null || _mStateSync == null
                || _fTempPoints == null || _wpTacticalValue == null) return;
            try
            {
                Vector3 target = Ground(dest);
                if (f.Point == null)
                {
                    f.Point = new GameObject("NpcWarPoint");
                    f.Point.transform.SetParent(_pointsRoot, false);
                    Component made = f.Point.AddComponent(_wpType);
                    _fWpType.SetValue(made, _wpTacticalValue);
                }
                f.Point.transform.position = target;
                Component wp = f.Point.GetComponent(_wpType);

                IList list = Activator.CreateInstance(_fTempPoints.FieldType) as IList;
                if (list == null) return;
                list.Add(wp);

                _mTempTask.Invoke(f.Ai, new object[] { Arg(_mTempTask, 0, 2) });   // Tactical
                _fTempPoints.SetValue(f.Ai, list);
                if (_fTempIndex != null) _fTempIndex.SetValue(f.Ai, 0);
                _mTargetWp.Invoke(f.Ai, new object[] { wp });
                if (!SetState(f, state, AddNone, 0, 0, f.Tr.eulerAngles.y)) return;
                f.HasOrder = true;
                f.Ordered = dest;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: move order failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        /// <summary>NPC_AI2.SetStateWithAnimAndSync(position, main, additional,
        /// pose, walk point index, use temporary points, temporary task, rotY)
        /// with the game's own enum types built from the numbers.</summary>
        static bool SetState(Fighter f, int main, int additional, int pose, int index, float rotY)
        {
            if (_mStateSync == null) return false;
            try
            {
                bool useTemp = _fUseTemp == null || !(_fUseTemp.GetValue(f.Ai) is bool)
                    || (bool)_fUseTemp.GetValue(f.Ai);
                int task = IntField(f.Ai, _fTempTaskField, 2);
                if (main != MainIdle) { useTemp = true; task = 2; }
                _mStateSync.Invoke(f.Ai, new object[] {
                    f.Tr.position, Arg(_mStateSync, 1, main), Arg(_mStateSync, 2, additional),
                    Arg(_mStateSync, 3, pose), Arg(_mStateSync, 4, index), useTemp,
                    Arg(_mStateSync, 6, task), rotY });
                return true;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: state change failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return false;
            }
        }

        static object Arg(MethodInfo m, int index, int value)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (index >= ps.Length) return value;
            Type t = ps[index].ParameterType;
            return t.IsEnum ? Enum.ToObject(t, value) : (object)value;
        }

        static Vector3 Ground(Vector3 p)
        {
            Vector3 hit;
            GameObject g = Turret.RaycastObject(p + Vector3.up * 30f, Vector3.down, 80f, out hit);
            Vector3 at = g == null ? p : hit + Vector3.up * 0.1f;
            NavMeshHit nav;
            if (NavMesh.SamplePosition(at, out nav, 12f, NavMesh.AllAreas)) return nav.position;
            return at;
        }

        static void EnsurePointsRoot()
        {
            if (_pointsRoot != null) return;
            GameObject root = new GameObject("NpcWarPoints");
            UnityEngine.Object.DontDestroyOnLoad(root);
            _pointsRoot = root.transform;
        }

        static void TryAlarm(Component ai)
        {
            if (_mAlarm == null) return;
            try { _mAlarm.Invoke(ai, new object[] { true }); }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: alarm failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        // ------------------------------------------------------------- removal

        /// <summary>The squad leaves the map: every man, alive or dead, through
        /// PhotonNetwork.Destroy on the master, so every client loses the same
        /// objects, then the local settlement and its walk points.</summary>
        static void Remove(Squad s, string why)
        {
            int n = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Point != null) UnityEngine.Object.Destroy(f.Point);
                if (f.Ai == null) continue;
                if (NetDestroy(f.Ai.gameObject)) n++;
            }
            if (s.Settlement != null)
            {
                Crew.Forget(s.Settlement);
                UnityEngine.Object.Destroy(s.Settlement);
            }
            _squads.Remove(s);
            RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " ended (" + why
                + ") - " + n + " men removed from the map.");
        }

        static bool NetDestroy(GameObject go)
        {
            if (go == null) return false;
            try
            {
                if (_mDestroy != null && IsMine(go.GetComponent(_npcType)))
                {
                    _mDestroy.Invoke(null, new object[] { go });
                    return true;
                }
                UnityEngine.Object.Destroy(go);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("NpcWar: removing a squad man failed - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return false;
            }
        }

        // ----------------------------------------------------- faction helpers

        static object FactionOf(Component ai)
        {
            object opt = MainOptions(ai);
            if (opt == null) return null;
            try { return _fMyFraction.GetValue(opt); }
            catch { return null; }
        }

        static Array GetHated(Component ai)
        {
            object opt = MainOptions(ai);
            if (opt == null) return null;
            try { return _fHated.GetValue(opt) as Array; }
            catch { return null; }
        }

        static object MainOptions(Component ai)
        {
            if (ai == null || _fMainOptions == null) return null;
            try { return _fMainOptions.GetValue(ai); }
            catch { return null; }
        }

        /// <summary>Is B's faction in A's HatedFractions? The same test as
        /// NPC_AI2.IsEnemyFraction, between two NPCs.</summary>
        static bool Hostile(Array aHated, object bFrac)
        {
            if (aHated == null || bFrac == null) return false;
            for (int i = 0; i < aHated.Length; i++)
            {
                object h = aHated.GetValue(i);
                if (h != null && h.Equals(bFrac)) return true;
            }
            return false;
        }

        // -------------------------------------------------------- npc helpers

        static List<Component> LiveNpcs()
        {
            List<Component> list = new List<Component>();
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_npcType);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c != null && Alive(c)) list.Add(c);
            }
            return list;
        }

        static bool Alive(Component ai)
        {
            if (ai == null) return false;
            try
            {
                object r = _mIsAlive.Invoke(ai, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>The player this NPC's own AI is fighting, or null.</summary>
        static Component KillTarget(Fighter f)
        {
            if (_fKillTarget == null || f.Ai == null) return null;
            try
            {
                Component c = _fKillTarget.GetValue(f.Ai) as Component;
                return c == null ? null : c;   // Unity null for a destroyed player
            }
            catch { return null; }
        }

        static bool HasKillTarget(Fighter f) { return KillTarget(f) != null; }

        static bool Reloading(Fighter f)
        {
            if (_fReloading == null || f.Ai == null) return false;
            try { object v = _fReloading.GetValue(f.Ai); return v is bool && (bool)v; }
            catch { return false; }
        }

        static Component WeaponOf(Fighter f)
        {
            if (_fWeapon == null || f.Ai == null) return null;
            try
            {
                Component c = _fWeapon.GetValue(f.Ai) as Component;
                return c == null ? null : c;
            }
            catch { return null; }
        }

        static Vector3 Muzzle(Component weapon, Fighter f)
        {
            if (weapon != null && _mMuzzle != null)
            {
                try
                {
                    object v = _mMuzzle.Invoke(weapon, null);
                    if (v is Vector3 && (Vector3)v != Vector3.zero) return (Vector3)v;
                }
                catch { }
            }
            return f.Tr.position + Vector3.up * EyeHeight;
        }

        static int IntField(Component c, FieldInfo f, int fallback)
        {
            if (c == null || f == null) return fallback;
            try
            {
                object v = f.GetValue(c);
                return v == null ? fallback : Convert.ToInt32(v);
            }
            catch { return fallback; }
        }

        /// <summary>May a squad shoot this scene NPC? Owned here, not god-moded,
        /// not in a safe settlement (traders) and not in a conversation.</summary>
        static bool Targetable(Component ai)
        {
            if (!IsMine(ai)) return false;
            if (Bool(ai, "GodModeEnabled")) return false;
            if (Bool(ai, "_isSafeSettlement")) return false;
            if (Bool(ai, "IsTalkActive")) return false;
            return FactionOf(ai) != null;
        }

        static bool IsMine(Component ai)
        {
            if (ai == null) return false;
            if (_mPhotonView == null) return true;
            try
            {
                object view = _mPhotonView.Invoke(ai, null);
                if (view == null) return false;
                if (_mIsMine == null)
                    _mIsMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                if (_mIsMine == null) return true;
                object r = _mIsMine.Invoke(view, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        static Fighter FighterOf(Component ai)
        {
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                    if (_squads[q].Men[i].Ai == ai) return _squads[q].Men[i];
            for (int i = 0; i < _defenders.Count; i++)
                if (_defenders[i].Ai == ai) return _defenders[i];
            return null;
        }

        static readonly Dictionary<string, FieldInfo> _boolFields = new Dictionary<string, FieldInfo>();

        static bool Bool(Component c, string field)
        {
            if (c == null) return false;
            FieldInfo fi;
            if (!_boolFields.TryGetValue(field, out fi))
            {
                fi = AccessTools.Field(c.GetType(), field);
                if (fi != null && fi.FieldType != typeof(bool)) fi = null;
                _boolFields[field] = fi;
            }
            if (fi == null) return false;
            try { return (bool)fi.GetValue(c); }
            catch { return false; }
        }

        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a, ap = p - a;
            ab.y = 0f; ap.y = 0f;
            float len = ab.sqrMagnitude;
            float t = len < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(ap, ab) / len);
            return Flat(p - (a + ab * t));
        }

        // -------------------------------------------------------------- status

        public static void Draw()
        {
            if (CfgDebug == null || !CfgDebug.Value || _status.Length == 0) return;
            GUI.Label(new Rect(8f, 8f, 480f, 22f), _status);
        }
    }
}
