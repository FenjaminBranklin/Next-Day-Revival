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
    //     Advance   it works its way along the arrow toward the head and
    //               attacks everything it sees that its faction hates
    //     Patrol    the survivors walk the arrow up and down until the patrol
    //               time (at most two hours) is over
    //     removed   every man of the squad, alive or dead, leaves the map
    //
    //   The men stay ordinary game NPCs. Players are the game's business: a man
    //   whose own AI has a player kill target (NPC_AI2._killTarget) gets no
    //   order from here at all, so the vanilla aim, fire, cover and reload run
    //   untouched. NpcWar only adds what the game cannot do - working the arrow
    //   and fighting other NPCs - and an NPC the squad fires on becomes a
    //   DEFENDER that fires back, so a fight is two-sided.
    //
    //   An NPC-vs-NPC shot uses the game's own weapon: the man is put in the
    //   native shooting state (MainState 0, AdditionalState 3) and
    //   NPC_FirearmWeaponController.FireTo(point, true) fires it, which brings
    //   the weapon's rate of fire, magazine, muzzle flash and sound on every
    //   client (RPC NetworkWeaponState). FireOneShot has no NPC damage branch,
    //   so the hit is decided by our own raycast and applied through
    //   NPC_AI2.ApplyDamage - the road the patrol guns already use
    //   (Revival.Patrol.cs, Gun.Schaden). An empty magazine starts the native
    //   reload (NPC_AI2.OnBulletsEnded). Only when a weapon controller cannot
    //   fire does the old tracer-and-sound shot stand in.
    //
    // WHY 6.16.1 LOOKED LIKE MUZZLE FLASHES FROM STATUES IN A HUDDLE.
    //   Four causes, all read out of the installed assembly, not guessed:
    //
    //   1 THE WEAPON NEVER POINTED AT ANYTHING. NPC_AI2.LookAtIkController
    //     (called from Update) runs the aim IK only while AdditionalState is
    //     1 or 3 AND _killTarget is not null; otherwise it lerps
    //     _aimIk.solver.IKPositionWeight to 0 and finally deactivates the IK
    //     object. _killTarget is a PLAYER reference (SetKillTarget feeds
    //     _targetVehicleManager from it), so a squad fighting NPCs can never
    //     have one - the IK stayed off and the rifle pointed wherever the
    //     animation put it. The mod now drives the IK itself: it activates the
    //     object, holds IKPositionWeight at 1 and moves LookAtIKTarget, which
    //     SetupLookAtIk wires as the solver target with the chest-to-hand
    //     chain. That is also what makes a man on a tower reachable at all,
    //     because the IK is the only thing that aims in ELEVATION.
    //   2 THE MEN WERE STUCK IN THE FIRING CLIP. GetAnimationNameNormalPose
    //     maps AdditionalState 1 to "idle_aiming" / "walk_aiming" and 3 to
    //     "shoot_auto"; the crouch table has "crouch_idle_aiming" and
    //     "crouch_shoot". 6.16.1 held state 3 permanently, so every man played
    //     the shooting loop and never the aim. State 3 is now only the short
    //     window around a burst; between bursts the man holds state 1.
    //   3 THE VANILLA KEPT TAKING THEM BACK. On the tactical task with no
    //     kill target, IdleStateAction queues its own intentions every idle
    //     pass (IL_0629-06DC) and IntentionsActions then moves and re-poses
    //     the man. IdleStateAction returns early while GetCalculatedPauseTime
    //     is positive, so a man we are driving gets ClearIntentions plus a
    //     refreshed SetPauseTime and stays where he is put.
    //   4 THEY WALKED INTO THE ENEMY AND STOOD IN THE OPEN. The body closed to
    //     EngageRange * 0.6 - 36 units, about 13 m - and a man engaged only
    //     inside EngageRange. Now the squad fights from Standoff, fires at
    //     anything it can see inside SightRange, and a man who cannot see his
    //     target moves round it instead of walking at it.
    //
    // HOW THE SQUAD FIGHTS NOW
    //
    //   Every man has his own numbers, drawn once when he lands: marksmanship,
    //   nerve, pace, and a persistent place in the formation. Nothing in a
    //   contact is computed from the slot index alone any more, so no two men
    //   move the same way twice.
    //
    //   Out of contact the squad moves as a loose, staggered body along the
    //   arrow. On contact it stops closing at Standoff and forms a base of
    //   fire; men take cover NEARBY when they feel like it (CoverChance, and
    //   much more readily while suppressed) rather than always or never. A man
    //   who loses his line of fire for a few seconds - the enemy on the
    //   Locator tower is the case that named this work - does not keep aiming
    //   at a wall: he looks for a spot that HAS a line to the target, sideways
    //   rather than forward, and runs to it. After FlankSeconds of fruitless
    //   contact a third of the squad is told to work round the flank on a long
    //   leash while the rest keeps firing. If the squad still wants ground it
    //   takes it in bounds: half the men move while the other half watches.
    //
    //   Fire is not free any more. A near miss suppresses: a suppressed man
    //   goes to ground, shoots slower and worse, and looks for cover. Cover
    //   and a crouch cut the chance of being hit. Defenders spread their fire
    //   instead of all emptying magazines into whoever happens to be nearest,
    //   which is what killed a whole squad in seconds.
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
    //   heights and spacings below are in those units. 18 units of spacing is
    //   about six and a half metres between neighbours.
    //
    // AUTHORITY
    //
    //   Only the Photon master client decides anything, and only on NPCs whose
    //   photonView.isMine is true - the two conditions ApplyDamage checks.
    //   Nothing here runs without an operation, so a map without troop landings
    //   is unchanged. The aim IK is visual and is driven where the fight is
    //   computed; a remote client sees the synchronised body yaw and the
    //   weapon's own muzzle flash, not the elevation of the master's IK.
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
        // New in 6.17.0. New keys on purpose: Config.Bind keeps the value an
        // installed nextday.revival.toolkit.cfg already has, so a changed
        // DEFAULT would never reach a player (CLAUDE.md, point 4).
        internal static ConfigEntry<float> CfgSpacing;
        internal static ConfigEntry<float> CfgStandoff;
        internal static ConfigEntry<float> CfgCoverChance;
        internal static ConfigEntry<float> CfgFlankSeconds;
        internal static ConfigEntry<bool>  CfgSuppression;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEngageRange = cfg.Bind("NpcWar", "EngageRange", 60f,
                "Ab dieser Entfernung (Meter) hoert ein Truppmitglied auf, weiter "
                + "auf den Gegner zuzugehen. Geschossen wird trotzdem, solange der "
                + "Gegner innerhalb SightRange zu sehen ist.");
            CfgSightRange = cfg.Bind("NpcWar", "SightRange", 110f,
                "Groesste Entfernung (Meter), auf die ein NPC einen feindlichen NPC "
                + "als Ziel annimmt und beschiesst. Sichtlinie wird vor jedem "
                + "Schuss per Strahl geprueft.");
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
                + "zur Sichtweite hin um 40 Prozent ab. Deckung, Hinknien, "
                + "Unterdrueckung und das persoenliche Koennen des Schuetzen "
                + "veraendern sie zusaetzlich.");
            CfgSpread = cfg.Bind("NpcWar", "MissSpread", 1.6f,
                "Wie weit (Meter) ein verfehlter Schuss neben dem Ziel einschlaegt.");
            CfgDetour = cfg.Bind("NpcWar", "MaxDetour", 80f,
                "Wie weit (Meter) sich der Trupp fuer einen gesehenen Gegner von "
                + "seinem Pfeil entfernen darf. Umfassende Bewegungen duerfen das "
                + "Anderthalbfache nutzen.");
            CfgMaxCombatants = cfg.Bind("NpcWar", "MaxDefenders", 32,
                "Sicherheitsgrenze: so viele angegriffene NPCs duerfen gleichzeitig "
                + "zurueckschiessen (alle Einsaetze zusammen).");
            CfgDebug = cfg.Bind("NpcWar", "Debug", false,
                "Ausfuehrliche Log-Zeilen und eine Statuszeile oben links.");

            CfgSpacing = cfg.Bind("NpcWar", "Spacing", 18f,
                "Abstand (Meter) zwischen zwei benachbarten Maennern im Trupp. "
                + "Groesser = weiter ausgeschwaermt. Jeder Mann streut zusaetzlich "
                + "persoenlich um diesen Wert.");
            CfgStandoff = cfg.Bind("NpcWar", "Standoff", 75f,
                "Entfernung (Meter), auf die der Trupp einen erkannten Gegner "
                + "bekaempft, statt weiter auf ihn zuzulaufen.");
            CfgCoverChance = cfg.Bind("NpcWar", "CoverChance", 0.6f,
                "Wie bereitwillig ein Mann im Feuerkampf Deckung sucht (0 = nie, "
                + "1 = bei jeder Gelegenheit). Unter Beschuss steigt der Wert von "
                + "selbst.");
            CfgFlankSeconds = cfg.Bind("NpcWar", "FlankSeconds", 14f,
                "Nach so vielen Sekunden erfolglosem Feuerkampf geht ein Drittel "
                + "des Trupps auf den Gegner zu umfassen. 0 = nie umfassen.");
            CfgSuppression = cfg.Bind("NpcWar", "Suppression", true,
                "Nahe Einschlaege druecken einen NPC nieder: er geht in Deckung, "
                + "schiesst langsamer und schlechter.");
        }

        // -------------------------------------------------------- world scale

        const float ChestHeight = 3.3f;     // of a 5.0 unit NPC capsule
        const float HeadHeight = 4.6f;
        const float WaistHeight = 2.2f;
        const float EyeHeight = 4.2f;
        const float CrouchEye = 2.7f;
        const float Bound = 45f;            // one bound of the body
        const float Arrive = 25f;           // the body counts as arrived

        // NPC_AI2 states. NPCMainState: Idle 0, Walk 1, Run 2.
        // NPCAdditionalState: Empty 0, Aiming 1, Reloading 2, Shooting 3.
        // NPCPoseState: Normal 0, Crouch 1, Crawl 2 (Crawl has no aiming clip).
        const int MainIdle = 0, MainWalk = 1, MainRun = 2;
        const int AddNone = 0, AddAim = 1, AddFire = 3;
        const int PoseStand = 0, PoseCrouch = 1;

        // ------------------------------------------------------- runtime state

        enum Phase { ToStart, Advance, Patrol }

        /// <summary>What a man is doing this second. Only for reading the
        /// debug line and for deciding what to order once.</summary>
        enum Stance { March, Advance, Fire, Reposition, Down }

        /// <summary>One NPC in a fight: a squad man or a defender.</summary>
        class Fighter
        {
            public Component Ai;          // NPC_AI2
            public Transform Tr;
            public Squad Squad;           // null = defender
            public object Faction;        // Fraction enum value
            public Array Hated;           // Fraction[] this NPC hates
            public Transform Target;
            public float NextScan, NextShot, ReactUntil, NextMove, NextLos, LastSeen;
            public int Burst, Slot;
            public bool HasOrder, Sees;
            public Vector3 Ordered;
            public GameObject Point;      // the walk point currently issued

            // What he personally is like. Drawn once, never recomputed, so the
            // same man is always the steady one and always walks on the same
            // side of the formation.
            public float Skill = 1f;      // marksmanship multiplier
            public float Nerve = 1f;      // resistance to suppression
            public float Pace = 1f;       // how long he waits between orders
            public float Lateral, Depth;  // his place in the body, in units
            public float Jitter;          // when his place is drawn again
            public int Team;              // 0/1, for bounding overwatch

            // Posture this second.
            public Stance Stance = Stance.March;
            public float Suppression;     // 0..1, decays
            public float BlindSince;      // when the line of fire was lost
            public bool InCover, Crouched, Flanker;
            public Vector3 Cover;
            public float NextCover, NextThink, DuckUntil, FlankUntil;
            public float Hurt;            // 0 = untouched, 1 = nearly dead
            public float NextHurt;

            // What we last told the game, so a state is only re-sent when it
            // really changes - every SetStateWithAnimAndSync is an RPC and
            // restarts the animation.
            public int WantMain = -1, WantAdd = -1, WantPose = -1;
            public float NextState;
            public float BurstUntil;      // hold the shooting clip this long
            public float PauseUntil;      // when the vanilla idle pause expires

            // Aim IK, resolved per man on first use.
            public Component Ik;
            public Transform Look;
            public bool IkMissing, IkDriven;
            public float AimHeight = ChestHeight;  // the part of the target he can see
            public float MoveDeadline;             // give up on an order that hangs
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

            // The plan, recomputed every two seconds while in contact.
            public Transform Threat;
            public float ContactSince, NextPlan, BoundUntil;
            public int MovingTeam;
            public bool Closing;
            public Vector3 Centre;
        }

        static readonly List<Squad> _squads = new List<Squad>();
        static readonly List<Fighter> _defenders = new List<Fighter>();
        static List<Component> _scene = new List<Component>();
        static float _nextSceneScan;
        static Transform _pointsRoot;
        static string _status = "";
        // One ground search per frame across all fights: each one costs a
        // handful of raycasts and a NavMesh sample per candidate.
        static int _searchBudget;

        // ------------------------------------------------------- reflection cache

        static bool _looked, _ok;
        static Type _npcType, _optType, _wpType;
        static FieldInfo _fMainOptions, _fMyFraction, _fHated, _fTempPoints, _fTempIndex, _fWpType;
        static FieldInfo _fKillTarget, _fReloading, _fMainState, _fAddState, _fPoseState;
        static FieldInfo _fUseTemp, _fTempTaskField, _fWeapon, _fAimingPoint, _fRofDelay;
        static FieldInfo _fAimIk, _fLookTarget, _fSpecs, _fSolver, _fIkWeight;
        static FieldInfo _fHealth, _fHealthMax;
        static MethodInfo _mIsAlive, _mTempTask, _mTargetWp, _mStateSync, _mAlarm;
        static MethodInfo _mPhotonView, _mIsMine, _mMasterGetter, _mDestroy;
        static MethodInfo _mBulletsEnded, _mStartRotation, _mClearIntentions, _mPauseTime;
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
            _fAimIk = AccessTools.Field(_npcType, "_aimIk");
            _fLookTarget = AccessTools.Field(_npcType, "LookAtIKTarget");
            _fSpecs = AccessTools.Field(_npcType, "Specifications");

            _mIsAlive = AccessTools.Method(_npcType, "IsAlive", null, null);
            _mTempTask = AccessTools.Method(_npcType, "SetTemporaryTask", null, null);
            _mTargetWp = AccessTools.Method(_npcType, "SetTargetWalkPoint", null, null);
            _mStateSync = AccessTools.Method(_npcType, "SetStateWithAnimAndSync", null, null);
            _mAlarm = AccessTools.Method(_npcType, "SetGeneralAlarm", null, null);
            _mPhotonView = AccessTools.Method(_npcType, "get_photonView", null, null);
            _mBulletsEnded = AccessTools.Method(_npcType, "OnBulletsEnded", Type.EmptyTypes, null);
            _mStartRotation = AccessTools.Method(_npcType, "StartRotation",
                new Type[] { typeof(Vector3) }, null);
            _mClearIntentions = AccessTools.Method(_npcType, "ClearIntentions",
                Type.EmptyTypes, null);
            _mPauseTime = AccessTools.Method(_npcType, "SetPauseTime",
                new Type[] { typeof(float) }, null);

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
            if (_fAimIk == null || _fLookTarget == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2._aimIk or LookAtIKTarget missing - "
                    + "the men will fire without pointing the weapon at the target.");
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
            s.Centre = settlement.transform.position;

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
                f.Team = f.Slot % 2;
                DrawPlace(f);
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
            // One draw per man, never repeated: a squad of identical soldiers
            // that all shoot equally well is the thing that looked mechanical.
            f.Skill = UnityEngine.Random.Range(0.72f, 1.28f);
            f.Nerve = UnityEngine.Random.Range(0.65f, 1.45f);
            f.Pace = UnityEngine.Random.Range(0.82f, 1.25f);
            f.NextCover = Time.time + UnityEngine.Random.value * 3f;
            f.NextThink = Time.time + UnityEngine.Random.value * 0.5f;
            return f;
        }

        /// <summary>His standing place in the body: which side, how far out,
        /// how far back. Redrawn every twenty-odd seconds by a fifth, so a
        /// long walk never freezes into a parade formation.</summary>
        static void DrawPlace(Fighter f)
        {
            float spacing = Mathf.Clamp(CfgSpacing.Value, 4f, 60f);
            // Five men out to each side, then a second line behind them: a big
            // squad gets deeper, not endlessly wider.
            int file = f.Slot / 2;
            int rank = 1 + file % 5;
            float side = (f.Slot % 2 == 0) ? -1f : 1f;
            f.Lateral = side * rank * spacing * UnityEngine.Random.Range(0.72f, 1.3f);
            f.Depth = -(file / 5) * spacing * 1.6f - (file % 5) * spacing * 0.35f
                      - UnityEngine.Random.value * spacing * 0.8f;
            f.Jitter = Time.time + UnityEngine.Random.Range(16f, 34f);
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
            _searchBudget = 1;
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
                    if (d.Ai != null && Alive(d.Ai)
                        && (d.WantAdd == AddAim || d.WantAdd == AddFire)) StandDown(d);
                    _defenders.RemoveAt(i);
                    continue;
                }
                if (HasKillTarget(d)) { d.IkDriven = false; continue; }   // a player: the game's fight
                try { RunDefender(d, now); }
                catch (Exception ex)
                {
                    if (CfgDebug.Value)
                        RevivalPlugin.L.LogWarning("NpcWar: defender - " + ex.Message);
                    _defenders.RemoveAt(i);
                }
            }

            _status = "NpcWar: " + _squads.Count + " operation(s), "
                + _defenders.Count + " defender(s)" + DebugTail();
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
            s.Centre = centre;

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

            Plan(s, centre, goal, now);

            Vector3 along = s.Head - s.Tail;
            along.y = 0f;
            if (along.sqrMagnitude < 0.01f) along = Vector3.forward;
            along.Normalize();
            if (s.Phase == Phase.Patrol && !s.TowardHead) along = -along;
            // In contact the body faces the enemy, so the line forms across the
            // threat instead of across the arrow.
            if (s.Threat != null)
            {
                Vector3 toThreat = s.Threat.position - centre;
                toThreat.y = 0f;
                if (toThreat.sqrMagnitude > 1f) along = toThreat.normalized;
            }

            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                // A player in his sights: the game's own combat runs him.
                if (HasKillTarget(f)) { f.HasOrder = false; f.IkDriven = false; continue; }
                ManStep(f, s, along, now);
            }
        }

        /// <summary>What the squad as a whole intends. Recomputed every two
        /// seconds while something is happening, so one man's target switch
        /// does not swing the whole body.</summary>
        static void Plan(Squad s, Vector3 centre, Vector3 goal, float now)
        {
            if (now >= s.NextPlan)
            {
                s.NextPlan = now + 2f;
                Transform enemy = NearestEnemyOfSquad(s, centre);
                if (enemy != null)
                {
                    if (s.Threat == null) s.ContactSince = now;
                    s.Threat = enemy;
                    s.ContactUntil = now + 8f;
                }
                else if (now >= s.ContactUntil) { s.Threat = null; s.ContactSince = 0f; }
            }
            if (s.Threat == null || !s.Threat)
            {
                s.Threat = null;
                s.Closing = false;
                Vector3 to = goal - centre;
                to.y = 0f;
                float bound = Mathf.Min(to.magnitude, Bound);
                s.Body = to.sqrMagnitude < 0.01f ? goal : centre + to.normalized * bound;
                ClearFlankers(s);
                return;
            }

            float standoff = Mathf.Clamp(CfgStandoff.Value, 20f, 400f);
            Vector3 back = centre - s.Threat.position;
            back.y = 0f;
            float have = back.magnitude;
            bool onArrow = DistanceToSegment(s.Threat.position, s.Tail, s.Head) <= CfgDetour.Value;

            // Close only if the enemy is far away AND worth walking to; never
            // walk into him. Standing still at contact distance is what a
            // section does, and what kept the old squad alive for eight
            // seconds instead of forty.
            s.Closing = onArrow && have > standoff * 1.25f;
            if (s.Closing)
            {
                float keep = Mathf.Max(standoff, Mathf.Min(have - Bound, have));
                s.Body = have < 0.01f ? s.Threat.position
                       : s.Threat.position + back.normalized * keep;
                if (now >= s.BoundUntil)
                {
                    s.BoundUntil = now + UnityEngine.Random.Range(4f, 7.5f);
                    s.MovingTeam = 1 - s.MovingTeam;
                }
            }
            else
            {
                s.Body = centre;
                s.MovingTeam = -1;              // nobody advances, everybody fires
            }

            // Fruitless contact: send a third of the squad round the side. That
            // is the answer to a man on a tower whom nobody can see from the
            // front - not more men walking into the same firing lane.
            float flankAfter = Mathf.Max(0f, CfgFlankSeconds.Value);
            if (flankAfter <= 0f || s.ContactSince <= 0f
                || now - s.ContactSince < flankAfter) return;
            int want = Mathf.Max(1, s.Men.Count / 3);
            int have2 = 0;
            for (int i = 0; i < s.Men.Count; i++)
                if (s.Men[i].Flanker && Alive(s.Men[i].Ai)) have2++;
            for (int i = 0; i < s.Men.Count && have2 < want; i++)
            {
                Fighter f = s.Men[i];
                if (f.Flanker || f.Ai == null || !Alive(f.Ai)) continue;
                f.Flanker = true;
                f.FlankUntil = now + 45f;
                f.HasOrder = false;
                have2++;
            }
        }

        static void ClearFlankers(Squad s)
        {
            for (int i = 0; i < s.Men.Count; i++) s.Men[i].Flanker = false;
        }

        // -------------------------------------------------------- one man's turn

        /// <summary>One squad man: acquire, decide a stance, then either fight
        /// from where he stands or go somewhere better.</summary>
        static void ManStep(Fighter f, Squad s, Vector3 along, float now)
        {
            Decay(f, now);
            if (now >= f.Jitter) DrawPlace(f);

            Acquire(f, now);
            bool engaged = f.Target != null && now - f.LastSeen < 6f;
            Vector3 threat = engaged ? f.Target.position
                           : (s.Threat != null ? s.Threat.position : Vector3.zero);
            bool contact = engaged || s.Threat != null;

            if (!contact)
            {
                f.InCover = false;
                f.Flanker = false;
                f.Cover = Vector3.zero;
                March(f, s, along, now);
                return;
            }

            // Losing the line of fire is a decision point, not a reason to keep
            // aiming at a wall.
            if (engaged && !f.Sees)
            {
                if (f.BlindSince <= 0f) f.BlindSince = now;
            }
            else f.BlindSince = 0f;

            // 1  Still running to the spot he was sent to: finish the move. A
            //    path that cannot be finished - a locked door, a ledge - must
            //    not park him there, hence the deadline.
            if (f.Stance == Stance.Reposition && f.HasOrder)
            {
                if (now < f.MoveDeadline
                    && Flat(f.Tr.position - f.Ordered) > Mathf.Max(6f, CfgSpacing.Value * 0.4f))
                {
                    Drive(f, MainRun, AddNone, PoseStand, now, false);
                    return;
                }
                Arrived(f);
            }

            bool wantsCover = false;
            if (!f.InCover && now >= f.NextCover && CfgCoverChance.Value > 0f)
            {
                // Not a fixed habit: how badly he wants cover depends on how
                // much fire is coming his way and how hurt he already is.
                float appetite = Mathf.Clamp01(CfgCoverChance.Value)
                               * (0.45f + 0.75f * f.Suppression + 0.8f * f.Hurt);
                wantsCover = UnityEngine.Random.value < appetite;
                f.NextCover = now + UnityEngine.Random.Range(3.5f, 9f) * f.Pace;
            }

            // 2  No line of fire for a few seconds: work round, not forward.
            if (engaged && f.BlindSince > 0f && now - f.BlindSince > 3.5f
                && now >= f.NextThink)
            {
                f.NextThink = now + 1.5f;
                Vector3 spot;
                if (Search() && FindFiringSpot(f, s, f.Target.position, out spot))
                {
                    f.BlindSince = now;      // give the move time before retrying
                    Send(f, spot, now);
                    return;
                }
            }

            // 3  Told to go round the flank.
            if (f.Flanker && s.Threat != null && now >= f.NextThink)
            {
                f.NextThink = now + 2f;
                Vector3 spot;
                if (now < f.FlankUntil && Search()
                    && FindFlankSpot(f, s, s.Threat.position, out spot))
                {
                    f.Flanker = false;       // one wide move, then fight again
                    Send(f, spot, now);
                    return;
                }
                if (now >= f.FlankUntil) f.Flanker = false;
            }

            // 4  Take cover near where he is.
            if (wantsCover && threat != Vector3.zero && now >= f.NextThink)
            {
                f.NextThink = now + 1f;
                Vector3 spot;
                if (Search() && FindCover(f, s, threat, out spot))
                {
                    Send(f, spot, now);
                    f.Cover = spot;          // InCover becomes true on arrival
                    return;
                }
            }

            // 5  The moving half of a bound that is closing on the enemy - that
            //    one does leave its cover - and anyone with no target of his own
            //    who has fallen behind the body.
            bool bounding = s.Closing && s.MovingTeam == f.Team;
            if (bounding || (!engaged && !f.InCover))
            {
                float slack = Mathf.Max(8f, CfgSpacing.Value * (bounding ? 0.45f : 0.9f));
                Vector3 spot = Place(f, s, along);
                if (Flat(f.Tr.position - spot) > slack) { Advance(f, spot, now); return; }
            }

            // 6  Otherwise: fight from here.
            HoldAndFire(f, s, now);
        }

        /// <summary>Suppression wears off, and the man notices how badly he is
        /// hurt about twice a second.</summary>
        static void Decay(Fighter f, float now)
        {
            if (CfgSuppression.Value)
                f.Suppression = Mathf.Max(0f, f.Suppression - Time.deltaTime * 0.28f);
            else f.Suppression = 0f;
            if (now < f.NextHurt) return;
            f.NextHurt = now + 0.5f;
            f.Hurt = 1f - Mathf.Clamp01(HealthFraction(f));
        }

        /// <summary>Run to a spot and forget the old one.</summary>
        static void Send(Fighter f, Vector3 spot, float now)
        {
            f.InCover = false;
            f.Cover = Vector3.zero;
            Go(f, spot, MainRun, PoseStand, now, Stance.Reposition);
        }

        /// <summary>He is where he was sent. If that was cover, he is in it.</summary>
        static void Arrived(Fighter f)
        {
            f.HasOrder = false;
            f.DuckUntil = 0f;
            f.InCover = f.Cover != Vector3.zero && Flat(f.Tr.position - f.Cover) < 8f;
        }

        /// <summary>A defender does not have an arrow or a formation; he fights
        /// where he was attacked and may go to ground for it.</summary>
        static void RunDefender(Fighter d, float now)
        {
            Decay(d, now);
            Acquire(d, now);
            if (d.Target == null || now - d.LastSeen > 8f)
            {
                if (d.WantAdd == AddAim || d.WantAdd == AddFire) StandDown(d);
                d.InCover = false;
                d.Cover = Vector3.zero;
                return;
            }
            if (!d.InCover && now >= d.NextCover && CfgCoverChance.Value > 0f)
            {
                float appetite = Mathf.Clamp01(CfgCoverChance.Value)
                               * (0.35f + 0.8f * d.Suppression + 0.8f * d.Hurt);
                d.NextCover = now + UnityEngine.Random.Range(4f, 10f) * d.Pace;
                Vector3 spot;
                if (UnityEngine.Random.value < appetite && Search()
                    && FindCover(d, null, d.Target.position, out spot))
                {
                    Send(d, spot, now);
                    d.Cover = spot;
                    return;
                }
            }
            if (d.Stance == Stance.Reposition && d.HasOrder)
            {
                if (now < d.MoveDeadline && Flat(d.Tr.position - d.Ordered) > 6f)
                {
                    Drive(d, MainRun, AddNone, PoseStand, now, false);
                    return;
                }
                Arrived(d);
            }
            HoldAndFire(d, null, now);
        }

        // -------------------------------------------------------------- moving

        /// <summary>Out of contact: keep the loose body together along the
        /// arrow. Each man has his own place and his own clock.</summary>
        static void March(Fighter f, Squad s, Vector3 along, float now)
        {
            if (f.WantAdd == AddAim || f.WantAdd == AddFire) StandDown(f);
            if (Reloading(f) || now < f.NextMove) return;
            f.NextMove = now + (1.3f + UnityEngine.Random.value * 0.8f) * f.Pace;

            Vector3 spot = Place(f, s, along);
            float away = Flat(f.Tr.position - spot);
            float slack = Mathf.Max(5f, CfgSpacing.Value * 0.35f);
            if (away < slack) { f.HasOrder = false; f.Stance = Stance.March; return; }
            int state = away > 40f ? MainRun : MainWalk;
            if (f.HasOrder && f.Stance == Stance.March && Flat(spot - f.Ordered) < slack * 1.5f
                && IntField(f.Ai, _fMainState, -1) == state && StillOurPoint(f)) return;
            Go(f, spot, state, PoseStand, now, Stance.March);
        }

        /// <summary>A bound forward with the weapon up: MainState Walk plus
        /// AdditionalState Aiming is the game's own "walk_aiming" clip.</summary>
        static void Advance(Fighter f, Vector3 spot, float now)
        {
            f.Stance = Stance.Advance;
            f.InCover = false;
            f.Crouched = false;
            f.IkDriven = false;
            float away = Flat(f.Tr.position - spot);
            int state = away > 55f ? MainRun : MainWalk;
            if (now < f.NextMove || Reloading(f))
            {
                // Keep the weapon up between orders; a run has no aiming clip.
                if (state == MainWalk) Drive(f, MainWalk, AddAim, PoseStand, now, false);
                return;
            }
            f.NextMove = now + (1.1f + UnityEngine.Random.value * 0.7f) * f.Pace;
            float slack = Mathf.Max(6f, CfgSpacing.Value * 0.4f);
            if (f.HasOrder && f.Stance == Stance.Advance && Flat(spot - f.Ordered) < slack
                && IntField(f.Ai, _fMainState, -1) == state && StillOurPoint(f)) return;
            OrderMove(f, spot, state, state == MainWalk ? AddAim : AddNone, PoseStand);
        }

        /// <summary>His place in the body right now.</summary>
        static Vector3 Place(Fighter f, Squad s, Vector3 along)
        {
            if (s == null) return f.Tr.position;
            Vector3 side = new Vector3(along.z, 0f, -along.x);
            float spread = s.Threat == null ? 0.6f : 1f;   // looser only in contact
            return s.Body + side * (f.Lateral * spread) + along * (f.Depth * spread);
        }

        // ------------------------------------------------------- fight in place

        /// <summary>Hold this position, aim, and fire when there is something to
        /// fire at. Cover and suppression decide the pose and the pauses.</summary>
        static void HoldAndFire(Fighter f, Squad s, float now)
        {
            if (f.Target == null || !f.Target)
            {
                f.Target = null;
                if (f.WantAdd == AddAim || f.WantAdd == AddFire) StandDown(f);
                f.Stance = s == null ? Stance.Fire : Stance.March;
                return;
            }

            // Reloading is the game's own animation and its own state; do not
            // stand the man back up in the middle of it and do not hold the
            // rifle on the target while he is changing the magazine.
            if (Reloading(f))
            {
                f.Stance = Stance.Down;
                f.IkDriven = false;
                Face(f);
                return;
            }

            bool crouch = f.InCover || f.Suppression > 0.45f || f.Hurt > 0.6f;
            // Going to ground for a moment: still aiming, not shooting. A man
            // with poor nerve does it more often and for longer.
            if (CfgSuppression.Value && f.Suppression > 0.5f && now >= f.DuckUntil
                && UnityEngine.Random.value < 0.35f * f.Suppression / f.Nerve)
                f.DuckUntil = now + UnityEngine.Random.Range(0.8f, 2.4f) / f.Nerve;
            bool ducked = now < f.DuckUntil;
            f.Stance = ducked ? Stance.Down : Stance.Fire;
            f.Crouched = crouch;

            Aim(f, now);
            int add = now < f.BurstUntil ? AddFire : AddAim;
            Drive(f, MainIdle, add, crouch ? PoseCrouch : PoseStand, now, true);

            if (ducked) return;
            if (!f.Sees || now < f.ReactUntil || now < f.NextShot) return;
            if (Vector3.Distance(f.Tr.position, f.Target.position) > CfgSightRange.Value) return;
            if (Shoot(f)) ScheduleNextShot(f, now);
        }

        static void ScheduleNextShot(Fighter f, float now)
        {
            float baseDelay = Mathf.Max(0.1f, CfgFireInterval.Value)
                            * (1f + 0.7f * f.Suppression);
            int burstMax = Mathf.Max(1, CfgBurst.Value);
            f.BurstUntil = now + 0.45f;
            f.Burst++;
            if (f.Burst < burstMax)
                f.NextShot = now + baseDelay * UnityEngine.Random.Range(0.15f, 0.3f);
            else
            {
                f.Burst = 0;
                f.NextShot = now + UnityEngine.Random.Range(baseDelay, baseDelay * 2f);
            }
        }

        // ------------------------------------------------------------- sensing

        /// <summary>Pick a target, then keep the line of fire up to date. Both
        /// are rate limited: the scan every three quarters of a second, the
        /// line of fire twice a second.</summary>
        static void Acquire(Fighter f, float now)
        {
            if (now >= f.NextScan)
            {
                f.NextScan = now + 0.6f + UnityEngine.Random.value * 0.4f;
                Transform had = f.Target;
                f.Target = f.Squad != null ? PickTargetForMan(f) : PickTargetForDefender(f);
                if (f.Target != null && f.Target != had)
                {
                    f.ReactUntil = now + UnityEngine.Random.Range(0.2f,
                        Mathf.Max(0.2f, CfgReactionMax.Value)) * (2f - f.Skill);
                    f.LastSeen = now;
                    f.NextLos = now;
                    f.BlindSince = 0f;
                }
            }
            if (f.Target == null || !f.Target) { f.Target = null; f.Sees = false; return; }
            if (now < f.NextLos) return;
            f.NextLos = now + 0.4f + UnityEngine.Random.value * 0.2f;
            float height;
            f.Sees = AimPoint(f, f.Target, out height);
            f.AimHeight = height;
            if (f.Sees) f.LastSeen = now;
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

        /// <summary>A defender fires back at a squad man - but not always at the
        /// nearest one. Three men emptying their magazines into whoever is in
        /// front is what wiped a squad in seconds; real fire is distributed.</summary>
        static Transform PickTargetForDefender(Fighter f)
        {
            if (f.Target != null)
            {
                Component cur = f.Target.GetComponent(_npcType);
                if (cur != null && Alive(cur)
                    && Vector3.Distance(f.Tr.position, f.Target.position) <= CfgSightRange.Value * 1.2f
                    && UnityEngine.Random.value < 0.8f)
                    return f.Target;
            }
            Fighter best = null;
            float bestScore = float.MaxValue;
            float sight = CfgSightRange.Value;
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                {
                    Fighter m = _squads[q].Men[i];
                    if (m.Ai == null || m.Tr == null || !Alive(m.Ai)) continue;
                    float d = Vector3.Distance(m.Tr.position, f.Tr.position);
                    if (d > sight) continue;
                    // Every defender already shooting at him counts as half the
                    // sight range of extra distance.
                    float score = d + Focus(m) * sight * 0.5f
                                + UnityEngine.Random.value * sight * 0.15f;
                    if (score < bestScore) { best = m; bestScore = score; }
                }
            return best == null ? null : best.Tr;
        }

        static int Focus(Fighter man)
        {
            int n = 0;
            for (int i = 0; i < _defenders.Count; i++)
                if (_defenders[i].Target == man.Tr) n++;
            return n;
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
        /// hostile player the normal way. They do not all react at once.</summary>
        static void Enlist(Component struck)
        {
            object faction = FactionOf(struck);
            Vector3 at = struck.transform.position;
            for (int i = 0; i < _scene.Count && _defenders.Count < CfgMaxCombatants.Value; i++)
            {
                Component c = _scene[i];
                if (c == null || FighterOf(c) != null || !Targetable(c) || !Alive(c)) continue;
                float away = 0f;
                if (c != struck)
                {
                    away = (c.transform.position - at).magnitude;
                    if (away > 60f) continue;
                    object other = FactionOf(c);
                    if (other == null || !other.Equals(faction)) continue;
                }
                Fighter d = NewFighter(c, null);
                // The man who was hit turns first; the camp behind him needs
                // time to find out what happened.
                d.ReactUntil = Time.time + UnityEngine.Random.Range(0.4f, 1.5f)
                             + away * 0.03f;
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
                if (t == null || !t) continue;
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
            Vector3 aimAt = AimWorld(f);
            Component weapon = WeaponOf(f);
            Vector3 from = Muzzle(weapon, f);
            float dist = Vector3.Distance(from, aimAt);
            Component targetAi = f.Target.GetComponent(_npcType);
            Fighter victim = targetAi == null ? null : FighterOf(targetAi);
            Vector3 aim = aimAt + MissOffset(f, victim, from, aimAt, dist);

            int fired = VanillaShot(f, weapon, aim);
            if (fired == 0) return false;
            bool native = fired > 0;
            // Being shot at is felt whether or not the round connects.
            if (victim != null && CfgSuppression.Value)
                victim.Suppression = Mathf.Min(1f,
                    victim.Suppression + 0.34f / Mathf.Max(0.4f, victim.Nerve));

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
            Fighter hurt = FighterOf(hitAi);
            if (hurt != null && hurt.Squad != null && hurt.Squad == f.Squad) return true;
            bool enemy = f.Squad == null
                ? hurt != null && hurt.Squad != null
                : Hostile(f.Hated, FactionOf(hitAi)) && (hurt != null || Targetable(hitAi));
            if (!enemy) return true;
            if (hurt == null && f.Squad != null) Enlist(hitAi);

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
                f.WantAdd = -1;
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

        /// <summary>Where the round actually goes. Marksmanship, suppression,
        /// range, and what the man being shot at is doing about it.</summary>
        static Vector3 MissOffset(Fighter shooter, Fighter victim, Vector3 from,
                                  Vector3 to, float dist)
        {
            float acc = Mathf.Clamp01(CfgAccuracy.Value) * shooter.Skill;
            acc *= 1f - 0.45f * shooter.Suppression;
            if (victim != null)
            {
                if (victim.InCover) acc *= 0.55f;
                else if (victim.Crouched) acc *= 0.75f;
                if (victim.Stance == Stance.Reposition) acc *= 0.8f;
            }
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

        // ------------------------------------------------------------- aiming

        /// <summary>The highest part of the target this man actually has a line
        /// to: chest first, then the head, then the waist. A rifleman on a tower
        /// behind a parapet only ever shows his head and chest - aiming at the
        /// centre of a body he cannot see is why the old squad never hit
        /// anything up there. Returns false when nothing is visible; the aim
        /// point is then the chest, for the pose. The HEIGHT comes back, not the
        /// point, so the weapon keeps following a target that is moving between
        /// two line-of-fire checks.</summary>
        static bool AimPoint(Fighter f, Transform target, out float height)
        {
            Vector3 eye = f.Tr.position + Vector3.up * (f.Crouched ? CrouchEye : EyeHeight);
            height = ChestHeight;
            if (Clear(eye, target.position + Vector3.up * ChestHeight, target)) return true;
            if (Clear(eye, target.position + Vector3.up * HeadHeight, target))
            { height = HeadHeight; return true; }
            if (Clear(eye, target.position + Vector3.up * WaistHeight, target))
            { height = WaistHeight; return true; }
            return false;
        }

        /// <summary>The world point this man is holding his weapon on.</summary>
        static Vector3 AimWorld(Fighter f)
        {
            return f.Target.position + Vector3.up * f.AimHeight;
        }

        /// <summary>Is the way from a to b open? A hit on the target itself
        /// counts as open, and so does one within 1.2 units of b - a shade more
        /// than a man's own 0.75 unit capsule, for a collider that does not hang
        /// under his transform. NOT more: 6.16.1 allowed two and a half units,
        /// and a railing struck 0.9 m from a man's head passed as a line of
        /// fire, so the squad emptied its magazines into the railing.</summary>
        static bool Clear(Vector3 from, Vector3 to, Transform target)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 1f) return true;
            dir /= dist;
            Vector3 point;
            // Start past the shooter's own 0.75 unit capsule and ragdoll.
            GameObject hit = Turret.RaycastObject(from + dir * 1.2f, dir, dist, out point);
            if (hit == null) return true;
            if (target != null && hit.transform.IsChildOf(target)) return true;
            return (to - point).sqrMagnitude < 1.44f;
        }

        /// <summary>Turn the body toward the target and point the WEAPON at it.
        /// The second half is the part the game cannot do for us: NPC_AI2's
        /// LookAtIkController only runs the aim IK while _killTarget is set,
        /// and _killTarget is a player.</summary>
        static void Aim(Fighter f, float now)
        {
            Face(f);
            Vector3 look = AimWorld(f);
            // A little wander, so eight men do not all hold the same statue
            // pose. Roughly a hand's width at fifty units.
            float t = now * (0.7f + f.Slot * 0.13f);
            look += new Vector3(Mathf.Sin(t * 1.3f), Mathf.Sin(t * 0.9f + 1.1f),
                                Mathf.Cos(t * 1.1f)) * (0.35f + 0.5f * f.Suppression);
            DriveAim(f, look);
            if (_fAimingPoint != null && _fAimingPoint.FieldType == typeof(Vector3))
            {
                try { _fAimingPoint.SetValue(f.Ai, look); }
                catch { }
            }
        }

        /// <summary>Turn the body toward the target. Each man turns at his own
        /// speed, so a squad that acquires together does not snap together.</summary>
        static void Face(Fighter f)
        {
            if (f.Target == null || !f.Target) return;
            Vector3 flat = f.Target.position - f.Tr.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) return;
            f.Tr.rotation = Quaternion.RotateTowards(f.Tr.rotation,
                Quaternion.LookRotation(flat), (150f + 120f * f.Skill) * Time.deltaTime);
        }

        /// <summary>Hold the aim IK on a world point. SetupLookAtIk wires
        /// LookAtIKTarget as the solver target with a chest-to-hand chain, so
        /// moving that transform points the weapon - in elevation as well as in
        /// azimuth. The vanilla controller lerps the weight back toward zero
        /// every frame while _killTarget is null; holding it at one each frame
        /// simply wins that race.</summary>
        static void DriveAim(Fighter f, Vector3 lookAt)
        {
            if (_fAimIk == null || _fLookTarget == null || f.IkMissing) return;
            try
            {
                if (f.Ik == null)
                {
                    f.Ik = _fAimIk.GetValue(f.Ai) as Component;
                    f.Look = _fLookTarget.GetValue(f.Ai) as Transform;
                    if (f.Ik == null || f.Look == null) { f.IkMissing = true; return; }
                    if (_fSolver == null) _fSolver = AccessTools.Field(f.Ik.GetType(), "solver");
                    if (_fSolver == null) { f.IkMissing = true; return; }
                }
                object solver = _fSolver.GetValue(f.Ik);
                if (solver == null) { f.IkMissing = true; return; }
                if (_fIkWeight == null)
                {
                    _fIkWeight = AccessTools.Field(solver.GetType(), "IKPositionWeight");
                    if (_fIkWeight == null || _fIkWeight.FieldType != typeof(float))
                    { f.IkMissing = true; return; }
                }
                GameObject go = f.Ik.gameObject;
                if (!go.activeSelf) go.SetActive(true);
                // Vanilla fades by dt*5. Adding dt*4 equilibrates below one;
                // set the intended full aim weight on each driven frame.
                _fIkWeight.SetValue(solver, 1f);
                // Snap on the first frame of an engagement, then follow.
                f.Look.position = f.IkDriven
                    ? Vector3.Lerp(f.Look.position, lookAt, Time.deltaTime * 8f)
                    : lookAt;
                f.IkDriven = true;
            }
            catch (Exception ex)
            {
                f.IkMissing = true;
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: aim IK - " + ex.Message);
            }
        }

        /// <summary>Out of the aim pose. The vanilla controller fades the IK
        /// down and switches it off again on its own once the additional state
        /// is no longer Aiming or Shooting.</summary>
        static void StandDown(Fighter f)
        {
            f.IkDriven = false;
            f.InCover = false;
            f.BurstUntil = 0f;
            int add = IntField(f.Ai, _fAddState, -1);
            if (add != AddAim && add != AddFire) { f.WantAdd = AddNone; return; }
            Drive(f, MainIdle, AddNone, IntField(f.Ai, _fPoseState, PoseStand), Time.time, false);
        }

        // ----------------------------------------------------------- movement

        /// <summary>Send one man to a world point on the NavMesh and remember
        /// what he was sent to do.</summary>
        static void Go(Fighter f, Vector3 dest, int state, int pose, float now, Stance stance)
        {
            f.Stance = stance;
            f.Crouched = pose == PoseCrouch;
            f.IkDriven = false;
            f.BurstUntil = 0f;
            OrderMove(f, dest, state, AddNone, pose);
        }

        /// <summary>Send one man to a world point on the NavMesh: a tactical
        /// task, one tactical walk point there, and SetStateWithAnimAndSync,
        /// which drives the NavMeshAgent, the animation and the Photon sync.
        /// State 1 walks (IdleStateAction uses it for patrol points), 2 runs.</summary>
        static void OrderMove(Fighter f, Vector3 dest, int state, int add, int pose)
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
                Quiet(f, false);
                if (!SetState(f, state, add, pose, 0, f.Tr.eulerAngles.y)) return;
                f.WantMain = state; f.WantAdd = add; f.WantPose = pose;
                f.NextState = Time.time + 0.3f;
                f.HasOrder = true;
                f.Ordered = dest;
                // Generous: a run of 60 units at the game's pace plus slack.
                f.MoveDeadline = Time.time + 6f + Flat(dest - f.Tr.position) * 0.25f;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: move order failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        /// <summary>Make sure the game is showing what we want it to show, and
        /// send nothing when it already is. Every SetStateWithAnimAndSync is an
        /// RPC to every player around and restarts the clip.</summary>
        static void Drive(Fighter f, int main, int add, int pose, float now, bool hold)
        {
            // The reload is the game's business from OnBulletsEnded to the end
            // of the coroutine; overwriting AdditionalState 2 cancels it.
            if (Reloading(f)) return;
            if (hold) Quiet(f, true);
            int liveMain = IntField(f.Ai, _fMainState, -1);
            int liveAdd = IntField(f.Ai, _fAddState, -1);
            int livePose = IntField(f.Ai, _fPoseState, -1);
            if (liveMain == main && liveAdd == add && livePose == pose)
            {
                f.WantMain = main; f.WantAdd = add; f.WantPose = pose;
                return;
            }
            if (now < f.NextState) return;
            f.NextState = now + 0.28f;
            float rotY = f.Tr.eulerAngles.y;
            if (f.Target != null && f.Target)
            {
                Vector3 flat = f.Target.position - f.Tr.position;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f) rotY = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            }
            if (SetState(f, main, add, pose, main == MainIdle ? -1 : 0, rotY))
            {
                f.WantMain = main; f.WantAdd = add; f.WantPose = pose;
                if (main == MainIdle && add != AddNone && _mStartRotation != null
                    && f.Target != null && f.Target)
                {
                    try { _mStartRotation.Invoke(f.Ai, new object[] { f.Target.position }); }
                    catch { }
                }
            }
        }

        /// <summary>Keep the vanilla idle logic off a man we are driving.
        /// IdleStateAction queues its own intentions on the tactical task when
        /// there is no player kill target (IL_0629-06DC) and returns early
        /// while GetCalculatedPauseTime is positive, so a short pause refreshed
        /// twice a second is enough to hold a pose.</summary>
        static void Quiet(Fighter f, bool pause)
        {
            float now = Time.time;
            if (pause && now < f.PauseUntil) return;
            try
            {
                if (_mClearIntentions != null) _mClearIntentions.Invoke(f.Ai, null);
                if (_mPauseTime == null) return;
                if (!pause)
                {
                    // A man who has just been sent somewhere must not sit out
                    // the pause his last aim hold left behind.
                    f.PauseUntil = 0f;
                    _mPauseTime.Invoke(f.Ai, new object[] { 0f });
                    return;
                }
                f.PauseUntil = now + 0.5f;
                _mPauseTime.Invoke(f.Ai, new object[] { 1.1f });
            }
            catch { }
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

        // ------------------------------------------------------ ground search
        //
        // Three searches, one shape: propose points around the man, drop the
        // ones that are not on the NavMesh or are off the arrow, and score what
        // is left with one or two rays. One search per frame across all fights
        // (Search below), and each man asks at most once a second, so the whole
        // thing costs a handful of rays a second no matter how big the fight is.

        static bool Search()
        {
            if (_searchBudget <= 0) return false;
            _searchBudget--;
            return true;
        }

        /// <summary>Somewhere close that breaks the line from the threat. Best
        /// is a spot that hides a crouching man but still lets a standing one
        /// shoot - the edge of a wall rather than the middle of it.</summary>
        static bool FindCover(Fighter f, Squad s, Vector3 threat, out Vector3 spot)
        {
            spot = Vector3.zero;
            Vector3 me = f.Tr.position;
            Vector3 away = me - threat;
            away.y = 0f;
            if (away.sqrMagnitude < 1f) return false;
            float baseAngle = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg;
            Vector3 aimAtThreat = threat + Vector3.up * ChestHeight;
            float best = 0f;

            for (int i = 0; i < 8; i++)
            {
                float ang = baseAngle + UnityEngine.Random.Range(-115f, 115f);
                float rad = UnityEngine.Random.Range(7f, 28f);
                Vector3 c = me + new Vector3(Mathf.Sin(ang * Mathf.Deg2Rad), 0f,
                                             Mathf.Cos(ang * Mathf.Deg2Rad)) * rad;
                if (!Reachable(f, s, c, 1.4f, out c)) continue;
                bool hiddenLow = !Clear(c + Vector3.up * 1.9f, aimAtThreat, null);
                if (!hiddenLow) continue;              // no protection, no cover
                bool canShoot = Clear(c + Vector3.up * EyeHeight, aimAtThreat, null);
                float score = 1f + (canShoot ? 1.4f : 0f)
                            - Mathf.Abs(rad - 16f) / 40f
                            + UnityEngine.Random.value * 0.25f;
                if (score > best) { best = score; spot = c; }
            }
            return best > 0f;
        }

        /// <summary>A place from which this target CAN be shot at, preferring a
        /// different angle to the one that is blocked. This is what gets a squad
        /// past a man in a tower: step out of his firing lane and into one of
        /// your own.</summary>
        static bool FindFiringSpot(Fighter f, Squad s, Vector3 threat, out Vector3 spot)
        {
            spot = Vector3.zero;
            Vector3 me = f.Tr.position;
            Vector3 toThreat = threat - me;
            toThreat.y = 0f;
            float have = toThreat.magnitude;
            if (have < 1f) return false;
            float axis = Mathf.Atan2(toThreat.x, toThreat.z) * Mathf.Rad2Deg;
            Vector3 aimAtThreat = threat + Vector3.up * ChestHeight;
            float best = 0f;

            for (int i = 0; i < 9; i++)
            {
                // Sideways, never straight at him.
                float off = UnityEngine.Random.Range(40f, 135f)
                          * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
                float rad = UnityEngine.Random.Range(14f, 48f);
                float ang = axis + off;
                Vector3 c = me + new Vector3(Mathf.Sin(ang * Mathf.Deg2Rad), 0f,
                                             Mathf.Cos(ang * Mathf.Deg2Rad)) * rad;
                if (!Reachable(f, s, c, 1.4f, out c)) continue;
                Vector3 flat = threat - c;
                flat.y = 0f;
                float now = flat.magnitude;
                if (now < have * 0.55f || now > CfgSightRange.Value) continue;
                if (!Clear(c + Vector3.up * EyeHeight, aimAtThreat, null)) continue;
                float score = 1f + Mathf.Abs(off) / 180f - rad / 120f
                            + UnityEngine.Random.value * 0.3f;
                if (score > best) { best = score; spot = c; }
            }
            return best > 0f;
        }

        /// <summary>The wide move: well out to one side and roughly at standoff
        /// distance, on a longer leash than an ordinary reposition.</summary>
        static bool FindFlankSpot(Fighter f, Squad s, Vector3 threat, out Vector3 spot)
        {
            spot = Vector3.zero;
            if (s == null) return false;
            Vector3 back = f.Tr.position - threat;
            back.y = 0f;
            if (back.sqrMagnitude < 1f) return false;
            float axis = Mathf.Atan2(back.x, back.z) * Mathf.Rad2Deg;
            float standoff = Mathf.Clamp(CfgStandoff.Value, 20f, 400f);
            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            Vector3 aimAtThreat = threat + Vector3.up * ChestHeight;
            float best = 0f;

            for (int i = 0; i < 8; i++)
            {
                float ang = axis + side * UnityEngine.Random.Range(55f, 115f);
                float rad = standoff * UnityEngine.Random.Range(0.7f, 1.15f);
                Vector3 c = threat + new Vector3(Mathf.Sin(ang * Mathf.Deg2Rad), 0f,
                                                 Mathf.Cos(ang * Mathf.Deg2Rad)) * rad;
                if (!Reachable(f, s, c, 1.5f, out c)) continue;
                float score = 1f + UnityEngine.Random.value * 0.4f;
                if (Clear(c + Vector3.up * EyeHeight, aimAtThreat, null)) score += 1.2f;
                if (score > best) { best = score; spot = c; }
            }
            return best > 0f;
        }

        /// <summary>Is a proposed point real ground the man may stand on, and is
        /// it still within the leash his arrow gives him?</summary>
        static bool Reachable(Fighter f, Squad s, Vector3 c, float leash, out Vector3 at)
        {
            at = c;
            float y;
            Vector3 hit;
            GameObject g = Turret.RaycastObject(new Vector3(c.x, c.y + 40f, c.z),
                Vector3.down, 120f, out hit);
            if (g == null) return false;
            y = hit.y;
            if (Mathf.Abs(y - f.Tr.position.y) > 26f) return false;
            NavMeshHit nav;
            if (!NavMesh.SamplePosition(new Vector3(c.x, y + 0.2f, c.z), out nav, 8f,
                                        NavMesh.AllAreas))
                return false;
            at = nav.position;
            if (s == null) return true;
            return DistanceToSegment(at, s.Tail, s.Head) <= CfgDetour.Value * leash;
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

        /// <summary>Health left, 0..1. NPC_AI2.Specifications carries Health and
        /// HealthMax as floats (Crew reads the same pair). 1 when they cannot be
        /// read, so an unreadable NPC simply behaves like an unhurt one.</summary>
        static float HealthFraction(Fighter f)
        {
            if (_fSpecs == null || f.Ai == null) return 1f;
            try
            {
                object specs = _fSpecs.GetValue(f.Ai);
                if (specs == null) return 1f;
                if (_fHealth == null) _fHealth = AccessTools.Field(specs.GetType(), "Health");
                if (_fHealthMax == null) _fHealthMax = AccessTools.Field(specs.GetType(), "HealthMax");
                if (_fHealth == null || _fHealthMax == null) return 1f;
                float have = Convert.ToSingle(_fHealth.GetValue(specs));
                float full = Convert.ToSingle(_fHealthMax.GetValue(specs));
                return full <= 0.01f ? 1f : Mathf.Clamp01(have / full);
            }
            catch { return 1f; }
        }

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
            return f.Tr.position + Vector3.up * (f.Crouched ? CrouchEye : EyeHeight);
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

        static string DebugTail()
        {
            if (CfgDebug == null || !CfgDebug.Value || _squads.Count == 0) return "";
            Squad s = _squads[0];
            int fire = 0, move = 0, cover = 0, down = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.InCover) cover++;
                if (f.Stance == Stance.Fire) fire++;
                else if (f.Stance == Stance.Down) down++;
                else if (f.Stance != Stance.March) move++;
            }
            return " | " + s.Tag + ": " + fire + " firing, " + move + " moving, "
                + down + " down, " + cover + " in cover"
                + (s.Threat == null ? ", no contact" : (s.Closing ? ", closing" : ", holding"));
        }

        public static void Draw()
        {
            if (CfgDebug == null || !CfgDebug.Value || _status.Length == 0) return;
            GUI.Label(new Rect(8f, 8f, 720f, 22f), _status);
        }
    }
}
