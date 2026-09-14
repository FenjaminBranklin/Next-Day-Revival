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
    //   damages NPC infantry. Two NPCs next to each other never fight.
    //
    //   NpcWar runs OPERATIONS. An operation is one squad a heli troop landing
    //   (RevivalTroopInsertion) has put on the ground, plus the combat arrow the
    //   admin drew in the editor:
    //
    //     ToStart   only when the landing zone is well away from the arrow
    //               tail: the squad runs there first
    //     Advance   the squad ASSAULTS along the arrow toward the head and
    //               attacks everything it sees that its faction hates
    //     Patrol    the survivors sweep the arrow up and down until the patrol
    //               time (at most two hours) is over
    //     removed   every man of the squad, alive or dead, leaves the map
    //
    // HOW THE SQUAD FIGHTS (6.16.5, after the 6.16.4 field report)
    //
    //   The user's order: side by side, briskly in the combat direction, fire
    //   at once, shoot exactly like every other enemy in the game, and never
    //   more than a few steps in a direction that is not the fight. So:
    //
    //   ONE LINE. Every man has a lane - a fixed lateral place in a line
    //   abreast across the direction of advance (LineSpacing apart, a second
    //   rank only above ten men). The whole line runs at a point Lead units
    //   ahead of its own centre along the arrow, so nobody arrives and stops
    //   while there is ground to take; a man who has got ahead of the line
    //   walks until it catches up. There is no cover search, no sideways
    //   firing position and no flanking any more: those were the "aimless
    //   wandering" of the field report.
    //
    //   CONTACT. A man who sees a hostile NPC - or the player his own AI has
    //   as kill target - inside AssaultRange stops where he is and fires
    //   within a fraction of a second. The line turns toward the enemy (up to
    //   100 degrees off the arrow) and keeps closing until CloseRange.
    //   Between, the two halves of the squad alternate every BoundSeconds:
    //   one half keeps firing, the other runs one bound FORWARD and fires
    //   again from there. A man with no line of fire runs on with the line
    //   until he has one.
    //
    //   THE SHOT IS THE GAME'S. NPC_AI2.ShootingActions fires only in
    //   MainState Idle + AdditionalState Shooting, every
    //   _shootingTimerDelayCached (0.2-0.3 s), without bursts, and
    //   LookAtIkController points the weapon while the man is in Aiming or
    //   Shooting (CONFIRMED IL, REVERSE_ENGINEERING "An NPC only aims its
    //   weapon at a PLAYER"). A firing man is held in exactly that state:
    //     - at a PLAYER the vanilla code does everything itself - the shot,
    //       its hit chance and the aim - and NpcWar only keeps him standing;
    //     - at an NPC the vanilla code has no target, so NpcWar drives the
    //       same aim IK and calls the NPC's own
    //       NPC_FirearmWeaponController.FireTo at the same cadence, which
    //       brings rate of fire, magazine, muzzle flash and sound on every
    //       client (RPC NetworkWeaponState). FireOneShot has no NPC damage
    //       branch, so the hit is our raycast through NPC_AI2.ApplyDamage.
    //   An empty magazine is the native reload (OnBulletsEnded). No ready
    //   native weapon means no shot, no effect and no damage.
    //
    //   FEET FIRST (6.16.6, after the 6.16.5 field report). The shooting clips
    //   are upper-body layers, so Shooting straight out of a run left the legs
    //   running on the spot. A man is first put into Idle + Aiming, which
    //   replaces the whole-body clip, and fires only once he is planted
    //   (Planted). A target that ducks away for a moment keeps him standing
    //   with the weapon up (Steady) instead of sprinting off for every lost
    //   glimpse. Walking while firing does not exist for NPCs: the game forces
    //   AdditionalState 0 in any MainState but Idle.
    //
    //   ONLY NPCS THAT CAN BE HURT. A target must be one NPC_AI2.ApplyDamage
    //   really damages - not a StoreKeeper, not in a safe settlement, not
    //   uninitialized - and enlisted defenders are checked like everyone else
    //   (Targetable, Hurtable). The 6.16.5 squad hung on a settlement trader.
    //
    //   Defenders - NPCs the squad attacks, and comrades close to them - fire
    //   back the same way and may still take cover; they are the enemy, not
    //   the assault.
    //
    // 6.16.0 FIELD BUG. InitSpawnNpc parents every NPC under the settlement's
    //   AllPeopleTr, so the settlement object is never moved; only the
    //   walk-point root follows the squad and the men move by NavMesh orders.
    //
    // SCALE. The game world is modelled about 2.8 times real size: a human NPC
    //   capsule is 5.0 units tall, so every distance below is in those units.
    //
    // AUTHORITY
    //
    //   Only the Photon master client decides anything, and only on NPCs whose
    //   photonView.isMine is true - the two conditions ApplyDamage checks.
    //   Nothing here runs without an operation, so a map without troop
    //   landings is unchanged. The aim IK is visual and driven where the fight
    //   is computed; a remote client sees the synchronised state, body yaw and
    //   the weapon's own muzzle flash.
    //
    public static class NpcWar
    {
        // -------------------------------------------------------------- config

        internal static ConfigEntry<float> CfgSightRange;
        internal static ConfigEntry<float> CfgDamage;
        internal static ConfigEntry<float> CfgAccuracy;
        internal static ConfigEntry<float> CfgSpread;
        internal static ConfigEntry<int>   CfgMaxCombatants;
        internal static ConfigEntry<bool>  CfgDebug;
        internal static ConfigEntry<float> CfgCoverChance;
        internal static ConfigEntry<bool>  CfgSuppression;
        // New in 6.16.5. New keys on purpose: Config.Bind keeps the value an
        // installed nextday.revival.toolkit.cfg already has, so a changed
        // DEFAULT would never reach a player (CLAUDE.md, point 4). The keys
        // of the old cover/flank squad (EngageRange, FireInterval,
        // ReactionMax, BurstMax, MaxDetour, Spacing, Standoff, FlankSeconds)
        // are no longer bound.
        internal static ConfigEntry<float> CfgLineSpacing;
        internal static ConfigEntry<float> CfgAssaultRange;
        internal static ConfigEntry<float> CfgCloseRange;
        internal static ConfigEntry<float> CfgBoundSeconds;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgSightRange = cfg.Bind("NpcWar", "SightRange", 110f,
                "Groesste Entfernung (Meter), auf die ein angegriffener NPC einen "
                + "Truppsoldaten als Ziel annimmt und zurueckschiesst.");
            CfgDamage = cfg.Bind("NpcWar", "DamagePerShot", 18f,
                "Schaden je Treffer an einem NPC.");
            CfgAccuracy = cfg.Bind("NpcWar", "Accuracy", 0.6f,
                "Trefferwahrscheinlichkeit auf kurze Entfernung (0..1); sie faellt "
                + "zur Reichweite hin um 40 Prozent ab. Deckung, Hinknien und das "
                + "persoenliche Koennen des Schuetzen veraendern sie zusaetzlich; "
                + "der Angriff erhaelt einen kleinen Bonus.");
            CfgSpread = cfg.Bind("NpcWar", "MissSpread", 1.6f,
                "Wie weit (Meter) ein verfehlter Schuss neben dem Ziel einschlaegt.");
            CfgMaxCombatants = cfg.Bind("NpcWar", "MaxDefenders", 32,
                "Sicherheitsgrenze: so viele angegriffene NPCs duerfen gleichzeitig "
                + "zurueckschiessen (alle Einsaetze zusammen).");
            CfgDebug = cfg.Bind("NpcWar", "Debug", false,
                "Ausfuehrliche Log-Zeilen und eine Statuszeile oben links.");
            CfgCoverChance = cfg.Bind("NpcWar", "CoverChance", 0.6f,
                "Wie bereitwillig ein ANGEGRIFFENER NPC im Feuerkampf Deckung sucht "
                + "(0 = nie, 1 = bei jeder Gelegenheit). Der Landetrupp selbst sucht "
                + "keine Deckung, er greift an.");
            CfgSuppression = cfg.Bind("NpcWar", "Suppression", true,
                "Nahe Einschlaege machen einen angegriffenen NPC unsicherer: er "
                + "schiesst schlechter und sucht eher Deckung.");

            CfgLineSpacing = cfg.Bind("NpcWar", "LineSpacing", 8f,
                "Abstand (Meter) zwischen zwei Nachbarn in der Angriffslinie des "
                + "Landetrupps. Bis zehn Mann eine Linie, darueber zwei Reihen.");
            CfgAssaultRange = cfg.Bind("NpcWar", "AssaultRange", 180f,
                "Groesste Entfernung (Meter), auf die der Landetrupp einen sichtbaren "
                + "Gegner (NPC oder Spieler) sofort beschiesst.");
            CfgCloseRange = cfg.Bind("NpcWar", "CloseRange", 30f,
                "Naeher als diese Entfernung (Meter) rueckt der Trupp nicht auf einen "
                + "Gegner vor, er bleibt stehen und schiesst.");
            CfgBoundSeconds = cfg.Bind("NpcWar", "BoundSeconds", 4f,
                "Im Feuerkampf wechseln die zwei Haelften des Trupps nach so vielen "
                + "Sekunden: eine schiesst, die andere springt ein Stueck vor.");
        }

        // -------------------------------------------------------- world scale

        const float ChestHeight = 3.3f;     // of a 5.0 unit NPC capsule
        const float HeadHeight = 4.6f;
        const float WaistHeight = 2.2f;
        const float EyeHeight = 4.2f;
        const float CrouchEye = 2.7f;
        const float Arrive = 25f;           // the line counts as arrived
        const float StartDistance = 60f;    // landing zone this far from the tail: run there first
        const float Lead = 30f;             // the line runs at a point this far ahead of itself
        const float RankDepth = 12f;        // second rank this far behind the first
        const int PerRank = 10;             // up to this many men in one line
        const float BoundStep = 20f;        // one forward bound under fire
        const float LaneSlack = 4f;         // this close to his point a man is there
        const float AheadWalk = 12f;        // this far ahead of the line he walks ...
        const float AheadRun = 4f;          // ... until the line is back within this
        const float PlantSeconds = 0.35f;   // standing in the aim clip before Shooting
        const float SteadySeconds = 1.2f;   // a target out of sight this long: still stand

        // NPC_AI2 states. NPCMainState: Idle 0, Walk 1, Run 2.
        // NPCAdditionalState: Empty 0, Aiming 1, Reloading 2, Shooting 3.
        // NPCPoseState: Normal 0, Crouch 1, Crawl 2. SwitchAnimationByStates
        // forces AdditionalState 0 whenever MainState is not Idle, and the
        // Marauder prefab has no walk or crouch aiming clip: a man aims and
        // fires standing still, exactly like every vanilla NPC.
        const int MainIdle = 0, MainWalk = 1, MainRun = 2;
        const int AddNone = 0, AddAim = 1, AddFire = 3;
        const int AddReload = 2;
        const int PoseStand = 0, PoseCrouch = 1;
        // NPCBehaviorPattern: Aggressive 0, StoreKeeper 1, Harmless 2, Boss 3.
        // NPC_AI2.ApplyDamage skips DecreaseHealth for a StoreKeeper.
        const int BehaviorStoreKeeper = 1;

        // ------------------------------------------------------- runtime state

        enum Phase { ToStart, Advance, Patrol }

        /// <summary>What a man is doing this second. For the debug line and
        /// for deciding what to order once.</summary>
        enum Stance { Advance, Fire, Bound, Hold, Reposition }

        /// <summary>One NPC in a fight: a squad man or a defender.</summary>
        class Fighter
        {
            public Component Ai;          // NPC_AI2
            public Transform Tr;
            public Squad Squad;           // null = defender
            public object Faction;        // Fraction enum value
            public Array Hated;           // Fraction[] this NPC hates
            public Transform Target;
            public bool TargetIsPlayer;   // Target is his vanilla _killTarget
            public float NextScan, NextShot, ReactUntil, NextLos, LastSeen;
            public bool Sees;
            public float AimHeight = ChestHeight;  // the part of the target he can see
            public float Skill = 1f;      // marksmanship multiplier, drawn once

            // His place in the assault line.
            public float LaneOffset, RankOffset;
            public int Team;              // 0/1: the two halves that bound in turn
            public float FireSince, BoundUntil, NextBound, BlindUntil;
            public Vector3 BoundDest;
            // Since when his feet stand in a standing aim or shooting state
            // (0 = not planted), and until when a target that has just gone
            // out of sight still keeps him standing.
            public float PlantedSince, SteadyUntil;
            public float NextTargetCheck;   // defender: next Targetable re-check

            // Defender posture.
            public float Nerve = 1f, Pace = 1f;
            public float Suppression, Hurt, NextHurt, NextCover;
            public bool InCover, Crouched;
            public Vector3 Cover;

            // Orders. What we last told the game, so a state is only re-sent
            // when it really changes - every SetStateWithAnimAndSync is an RPC
            // and restarts the animation.
            public Stance Stance = Stance.Advance;
            public bool HasOrder;
            public Vector3 Ordered;
            public GameObject Point;      // the walk point currently issued
            public float NextMove, MoveDeadline;
            public int WantMain = -1, WantAdd = -1, WantPose = -1;
            public float NextState, PauseUntil;

            // Weapon and aim IK, resolved per man on first use.
            public Component Ik;
            public Transform Look;
            public bool IkMissing, IkDriven;
            public float NextIkRetry, AimWeight, PoseSince;
            public Component Wm;
            public bool Armed, EquipWarned;
            public float NextEquip, SlotStuckSince;
            public int EquipTries;
            public int WeaponId;
            public float MuzzleBlockedSince, MateBlockedSince;
        }

        class Squad
        {
            public string Tag;
            public GameObject Settlement;
            public Transform WalkRoot;       // AllWalkPointsTr: follows the body
            public readonly List<Fighter> Men = new List<Fighter>();
            public Vector3 Lz, Tail, Head;
            public Phase Phase;
            public bool TowardHead = true;   // patrol leg
            public float PatrolSeconds, PatrolEnds, HardEnd, NextRing;
            public Vector3 Centre, Front;

            // The contact picture, refreshed four times a second.
            public Transform Threat;
            public bool ThreatSeen;          // some man has a line of fire to it
            public float NextThreat, ThreatUntil, NextBoundSwap, NextReport;
            public int BoundTeam;
            public bool Armed;               // first weapon ready was reported
            public int Shots, Hits;
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
        static FieldInfo _fMainWeaponId, _fSpawnWeaponId, _fShotDelayCached;
        static FieldInfo _fKillTarget, _fReloading, _fMainState, _fAddState, _fPoseState;
        static FieldInfo _fUseTemp, _fTempTaskField, _fWeapon, _fAimingPoint, _fRofDelay;
        static FieldInfo _fAimIk, _fLookTarget, _fSpecs, _fSolver, _fIkWeight;
        static FieldInfo _fHealth, _fHealthMax;
        static FieldInfo _fWeaponsManager, _fWeaponCategory, _fWeaponSlot, _fWeaponItem;
        static FieldInfo _fInitialized, _fBehavior, _fMySettlement, _fSafeSettlement;
        static MethodInfo _mEquipWeapon, _mSetMainWeaponId;
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
            _fMainWeaponId = AccessTools.Field(_npcType, "_mainWeaponId");
            _fSpawnWeaponId = AccessTools.Field(_npcType, "_weaponId");
            _fShotDelayCached = AccessTools.Field(_npcType, "_shootingTimerDelayCached");
            if (_fShotDelayCached != null && _fShotDelayCached.FieldType != typeof(float))
                _fShotDelayCached = null;
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
            _fWeaponsManager = AccessTools.Field(_npcType, "_weaponsManager");
            Type manager = RevivalPlugin.TypeByName("NPC_WeaponsManager");
            if (manager != null)
            {
                _fWeaponCategory = AccessTools.Field(manager, "WeaponCategoryEquiped");
                _fWeaponSlot = AccessTools.Field(manager, "_currentWeaponSlotId");
                _fWeaponItem = AccessTools.Field(manager, "_currentWeaponItemId");
            }
            // What NPC_AI2.ApplyDamage checks before it takes health off.
            _fInitialized = AccessTools.Field(_npcType, "IsInitialized");
            if (_fInitialized != null && _fInitialized.FieldType != typeof(bool)) _fInitialized = null;
            _fBehavior = AccessTools.Field(_npcType, "BehaviorPattern");
            _fMySettlement = AccessTools.Field(_npcType, "MySettlement");
            if (_fMySettlement != null)
            {
                _fSafeSettlement = AccessTools.Field(_fMySettlement.FieldType, "IsSafeSettlement");
                if (_fSafeSettlement != null && _fSafeSettlement.FieldType != typeof(bool))
                    _fSafeSettlement = null;
            }
            _mEquipWeapon = AccessTools.Method(_npcType, "EquipWeapon",
                new Type[] { typeof(bool), typeof(bool) }, null);
            _mSetMainWeaponId = AccessTools.Method(_npcType, "SetMainWeaponId",
                new Type[] { typeof(int), typeof(bool) }, null);

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
                    + "NPC-vs-NPC fire is disabled until a native weapon is ready.");
            if (_mEquipWeapon == null || _fWeaponCategory == null)
                RevivalPlugin.L.LogWarning("NpcWar: native equip members missing - men cannot draw weapons.");
            if (_mSetMainWeaponId == null || _fMainWeaponId == null)
                RevivalPlugin.L.LogWarning("NpcWar: native weapon-id setter missing - "
                    + "the crew cannot force a visible main weapon.");
            if (_fAimIk == null || _fLookTarget == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2._aimIk or LookAtIKTarget missing - "
                    + "the men will fire without pointing the weapon at the target.");
            if (_fInitialized == null || _fBehavior == null || _fSafeSettlement == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2 damage guards missing (IsInitialized "
                    + (_fInitialized != null) + ", BehaviorPattern " + (_fBehavior != null)
                    + ", MySettlement.IsSafeSettlement " + (_fSafeSettlement != null)
                    + ") - squads may fire at NPCs that cannot be hurt.");
            return _ok;
        }

        static bool IsMaster()
        {
            if (_mMasterGetter == null) return true;
            try { return (bool)_mMasterGetter.Invoke(null, null); }
            catch { return true; }
        }

        static float LineSpacing()
        {
            return Mathf.Clamp(CfgLineSpacing == null ? 8f : CfgLineSpacing.Value, 5f, 40f);
        }

        static float AssaultRange()
        {
            return Mathf.Clamp(CfgAssaultRange == null ? 180f : CfgAssaultRange.Value, 40f, 600f);
        }

        static float CloseRange()
        {
            return Mathf.Clamp(CfgCloseRange == null ? 30f : CfgCloseRange.Value, 8f, 150f);
        }

        static float BoundSeconds()
        {
            return Mathf.Clamp(CfgBoundSeconds == null ? 4f : CfgBoundSeconds.Value, 1.5f, 20f);
        }

        /// <summary>How far this man engages: the assault range for the squad,
        /// the ordinary sight range for a defender.</summary>
        static float RangeOf(Fighter f)
        {
            return f.Squad != null ? AssaultRange() : CfgSightRange.Value;
        }

        // ------------------------------------------------------------ operations

        /// <summary>Hand a freshly landed squad its combat arrow. The patrol
        /// clock starts when the line reaches the arrow head; a squad that never
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
            s.Lz = settlement.transform.position;
            s.Tail = tail;
            s.Head = head;
            s.Phase = Flat(s.Lz - tail) > StartDistance ? Phase.ToStart : Phase.Advance;
            s.PatrolSeconds = Mathf.Clamp(patrolSeconds, 60f, 7200f);
            float walk = Vector3.Distance(s.Lz, tail) + Vector3.Distance(tail, head);
            // One unit per second is a slow, fighting pace; plus half an hour.
            s.HardEnd = Time.time + walk + 1800f + s.PatrolSeconds;
            s.Centre = s.Lz;

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
                f.NextMove = Time.time + 0.2f + 0.05f * s.Men.Count;
                s.Men.Add(f);
            }
            if (s.Men.Count == 0) return false;
            AssignLanes(s);
            _squads.Add(s);
            EnsurePointsRoot();
            RevivalPlugin.L.LogInfo("NpcWar: operation " + tag + " - " + s.Men.Count
                + " men, arrow " + tail.ToString("0") + " -> " + head.ToString("0")
                + ", " + (s.Phase == Phase.ToStart ? "running to the arrow first, " : "")
                + "patrol " + (s.PatrolSeconds / 60f).ToString("0") + " min"
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
            f.WeaponId = IntField(ai, _fMainWeaponId,
                IntField(ai, _fSpawnWeaponId, 0));
            f.NextScan = Time.time + UnityEngine.Random.value * 0.3f;
            f.Skill = UnityEngine.Random.Range(0.8f, 1.2f);
            f.Nerve = UnityEngine.Random.Range(0.65f, 1.45f);
            f.Pace = UnityEngine.Random.Range(0.82f, 1.25f);
            f.NextCover = Time.time + UnityEngine.Random.value * 3f;
            return f;
        }

        /// <summary>The direction of the leg the squad is on, from a to b.</summary>
        static void Leg(Squad s, out Vector3 a, out Vector3 b)
        {
            switch (s.Phase)
            {
                case Phase.ToStart: a = s.Lz; b = s.Tail; break;
                case Phase.Advance: a = s.Tail; b = s.Head; break;
                default:
                    if (s.TowardHead) { a = s.Tail; b = s.Head; }
                    else { a = s.Head; b = s.Tail; }
                    break;
            }
        }

        static Vector3 Heading(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            d.y = 0f;
            return d.sqrMagnitude < 0.01f ? Vector3.forward : d.normalized;
        }

        /// <summary>The right-hand side of a flat direction.</summary>
        static Vector3 Side(Vector3 front)
        {
            return new Vector3(front.z, 0f, -front.x);
        }

        /// <summary>Give every living man a lane in the line across the leg he
        /// is on, in the order the men already stand from left to right, so
        /// forming up never makes two of them cross. Ten men or fewer stand in
        /// one line; a bigger squad forms a second, staggered rank.</summary>
        static void AssignLanes(Squad s)
        {
            Vector3 a, b;
            Leg(s, out a, out b);
            Vector3 side = Side(Heading(a, b));
            List<Fighter> men = new List<Fighter>();
            Vector3 centre = Vector3.zero;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                men.Add(f);
                centre += f.Tr.position;
            }
            if (men.Count == 0) return;
            centre /= men.Count;
            float[] lateral = new float[men.Count];
            for (int i = 0; i < men.Count; i++)
                lateral[i] = Vector3.Dot(men[i].Tr.position - centre, side);
            // Insertion sort keeps equal positions in list order (C# 3.0, and
            // List.Sort is not stable).
            for (int i = 1; i < men.Count; i++)
            {
                Fighter fm = men[i];
                float fl = lateral[i];
                int k = i - 1;
                while (k >= 0 && lateral[k] > fl)
                {
                    men[k + 1] = men[k];
                    lateral[k + 1] = lateral[k];
                    k--;
                }
                men[k + 1] = fm;
                lateral[k + 1] = fl;
            }
            LayOut(men.Count, LineSpacing(), men);
        }

        /// <summary>Lane numbers to offsets: index k of n, left to right.</summary>
        static void LayOut(int n, float spacing, List<Fighter> men)
        {
            int ranks = n <= PerRank ? 1 : 2;
            float step = spacing / ranks;
            for (int k = 0; k < n; k++)
            {
                Fighter f = men[k];
                f.LaneOffset = (k - (n - 1) * 0.5f) * step;
                f.RankOffset = -(k % ranks) * RankDepth;
                f.Team = k % 2;
            }
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
                bool gone = d.Ai == null || d.Tr == null || !Alive(d.Ai)
                    || NearestSquadMan(d, 2f) == null;
                // A defender who can no longer be hurt (a talk started, god
                // mode) is no target any more and leaves the fight. Twice a
                // second is plenty and keeps the reflection off every frame.
                if (!gone && now >= d.NextTargetCheck)
                {
                    d.NextTargetCheck = now + 0.5f;
                    gone = !Targetable(d.Ai);
                }
                if (gone)
                {
                    if (d.Ai != null && Alive(d.Ai)
                        && (d.WantAdd == AddAim || d.WantAdd == AddFire)) StandDown(d);
                    _defenders.RemoveAt(i);
                    continue;
                }
                if (HasKillTarget(d))
                {
                    if (d.IkDriven) ReleaseAim(d);
                    continue;   // a player: the game's fight
                }
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

            Vector3 a, b;
            Leg(s, out a, out b);
            Vector3 dir = Heading(a, b);
            float len = Flat(b - a);
            float along = Vector3.Dot(FlatV(centre - a), dir);
            if (along >= len - Arrive)
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
                AssignLanes(s);
                Leg(s, out a, out b);
                dir = Heading(a, b);
                len = Flat(b - a);
                along = Vector3.Dot(FlatV(centre - a), dir);
            }

            // The contact picture: who is the squad fighting?
            if (now >= s.NextThreat)
            {
                s.NextThreat = now + 0.25f;
                bool seen;
                Transform enemy = SquadThreat(s, centre, now, out seen);
                if (enemy != null) { s.Threat = enemy; s.ThreatSeen = seen; s.ThreatUntil = now + 6f; }
                else if (now >= s.ThreatUntil) s.Threat = null;
                else s.ThreatSeen = false;
            }
            if (s.Threat != null && !s.Threat) s.Threat = null;
            // An enemy somebody can shoot at is closed to CloseRange and fought
            // from there. One that nobody can see is walked up to until
            // somebody can - a line that stops at a wall wins nothing.
            float stopAt = s.ThreatSeen ? CloseRange() : 8f;

            // The line faces the enemy while there is one in front of it;
            // otherwise it faces along the arrow.
            Vector3 front = dir;
            float threatDist = 0f;
            if (s.Threat != null)
            {
                Vector3 to = FlatV(s.Threat.position - centre);
                threatDist = to.magnitude;
                if (threatDist > 1f && Vector3.Angle(to, dir) <= 100f) front = to / threatDist;
                if (now >= s.NextBoundSwap)
                {
                    s.NextBoundSwap = now + BoundSeconds();
                    s.BoundTeam = 1 - s.BoundTeam;
                }
            }
            s.Front = front;

            Vector3 anchor = Anchor(a, dir, len, centre, front, s.Threat != null,
                                    threatDist, stopAt);

            if (!s.Armed)
            {
                int armed = 0;
                for (int i = 0; i < s.Men.Count; i++) if (s.Men[i].Armed) armed++;
                if (armed > 0)
                {
                    s.Armed = true;
                    RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " - weapons in hand ("
                        + armed + "/" + alive + ").");
                }
            }

            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                ManStep(f, s, anchor, front, centre, now);
            }

            if (now >= s.NextReport) Report(s, now, alive);
        }

        /// <summary>The point the whole line runs at. Out of contact, and with
        /// an enemy straight down the arrow, it lies ON the arrow Lead units
        /// ahead of the line's centre, never past the leg's end - so a squad
        /// that landed beside the arrow is pulled onto it while it advances.
        /// With an enemy off the arrow it lies toward that enemy. Never closer
        /// than stopAt to the enemy.</summary>
        static Vector3 Anchor(Vector3 a, Vector3 dir, float len, Vector3 centre, Vector3 front,
                             bool contact, float threatDist, float stopAt)
        {
            float room = Mathf.Max(0f, threatDist - stopAt);
            if (contact && Vector3.Angle(front, dir) > 1f)
                return centre + front * Mathf.Min(Lead, room);
            float along = Vector3.Dot(FlatV(centre - a), dir);
            float step = Mathf.Min(len, Mathf.Max(0f, along) + Lead) - along;
            if (contact) step = Mathf.Min(step, room);
            return a + dir * (along + Mathf.Max(0f, step));
        }

        /// <summary>The enemy the squad as a whole is fighting: the nearest one
        /// any man has seen in the last few seconds, or the player a man's own
        /// AI is hunting. An enemy just round a corner that nobody sees yet
        /// still counts when it is close, so the line pushes to it instead of
        /// marching past.</summary>
        static Transform SquadThreat(Squad s, Vector3 centre, float now, out bool seen)
        {
            Transform best = null;
            seen = false;
            float bestSqr = AssaultRange() * AssaultRange();
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.Target != null && f.Target && f.Sees && now - f.LastSeen < 1f) seen = true;
                Transform t = null;
                if (f.Target != null && f.Target && now - f.LastSeen < 2.5f) t = f.Target;
                Component player = KillTarget(f);
                if (player != null)
                {
                    float pd = (player.transform.position - centre).sqrMagnitude;
                    if (pd < bestSqr) { best = player.transform; bestSqr = pd; }
                }
                if (t == null) continue;
                float d = (t.position - centre).sqrMagnitude;
                if (d < bestSqr) { best = t; bestSqr = d; }
            }
            if (best != null) return best;

            float near = CloseRange() * 2.5f;
            bestSqr = near * near;
            for (int i = 0; i < _scene.Count; i++)
            {
                Component c = _scene[i];
                if (c == null || !Alive(c)) continue;
                Fighter other = FighterOf(c);
                if (other != null && other.Squad == s) continue;
                if ((other == null || other.Squad == null) && !Targetable(c)) continue;
                float d = (c.transform.position - centre).sqrMagnitude;
                if (d >= bestSqr) continue;
                if (!HatedBySquad(s, c)) continue;
                best = c.transform; bestSqr = d;
            }
            return best;
        }

        static bool HatedBySquad(Squad s, Component c)
        {
            object faction = FactionOf(c);
            for (int m = 0; m < s.Men.Count; m++)
            {
                Fighter man = s.Men[m];
                if (man.Ai != null && Hostile(man.Hated, faction)) return true;
            }
            return false;
        }

        /// <summary>A compact line in the runtime log every fifteen seconds, so
        /// a field report can be read out of LogOutput.log without Debug.</summary>
        static void Report(Squad s, float now, int alive)
        {
            s.NextReport = now + 15f;
            int armed = 0, fire = 0, move = 0, hold = 0, reload = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.Armed) armed++;
                if (Reloading(f)) reload++;
                else if (f.Stance == Stance.Fire) fire++;
                else if (f.Stance == Stance.Hold) hold++;
                else move++;
            }
            RevivalPlugin.L.LogInfo("NpcWar: " + s.Tag + " " + s.Phase + " - " + alive + " alive, "
                + armed + " armed, " + fire + " firing, " + move + " moving, " + hold + " holding, "
                + reload + " reloading; " + s.Shots + " shots at NPCs, " + s.Hits + " hits; "
                + (s.Threat == null ? "no contact"
                   : "contact " + Flat(s.Threat.position - s.Centre).ToString("0") + " units away")
                + ", centre " + s.Centre.ToString("0") + ".");
        }

        // -------------------------------------------------------- one man's turn

        /// <summary>One squad man: see, then shoot, bound or run on with the
        /// line. Nothing here moves a man sideways away from the fight.</summary>
        static void ManStep(Fighter f, Squad s, Vector3 anchor, Vector3 front,
                            Vector3 centre, float now)
        {
            EnsureArmed(f, now);
            Acquire(f, now);
            // Sampled every frame, so a pass through a run between two shots
            // is never missed (Planted).
            Planted(f, now);

            // A barrel behind a wall, or a comrade in the line of fire: the eyes
            // may see, the rifle may not. Run on with the line for a moment
            // instead of standing there - forward, never sideways.
            if ((f.MuzzleBlockedSince > 0f && now - f.MuzzleBlockedSince > 0.8f)
                || (f.MateBlockedSince > 0f && now - f.MateBlockedSince > 1.0f))
            {
                f.BlindUntil = now + 2f;
                f.MuzzleBlockedSince = 0f;
                f.MateBlockedSince = 0f;
            }

            // The reload is the game's own animation and state.
            if (Reloading(f))
            {
                f.Stance = Stance.Hold;
                if (!f.TargetIsPlayer && f.IkDriven) ReleaseAim(f);
                Quiet(f, true);
                Face(f);
                return;
            }

            // 1  A bound in progress: finish it, then fire again at once.
            if (f.Stance == Stance.Bound && f.HasOrder)
            {
                if (now < f.BoundUntil && Flat(f.Tr.position - f.BoundDest) > LaneSlack)
                {
                    Drive(f, MainRun, AddNone, PoseStand, now, false);
                    return;
                }
                f.HasOrder = false;
                f.Stance = Stance.Fire;
                f.FireSince = now;
            }

            // 2  A visible enemy in range and a weapon in hand: shoot now.
            bool sees = f.Target != null && f.Sees && now >= f.BlindUntil;
            float dist = f.Target == null ? 0f : Flat(f.Target.position - f.Tr.position);
            if (sees && f.Armed && dist <= AssaultRange())
            {
                if (f.FireSince <= 0f) f.FireSince = now;
                f.SteadyUntil = now + SteadySeconds;
                if (BoundDue(f, s, front, dist, now))
                {
                    StartBound(f, s, front, centre, now);
                    return;
                }
                Fire(f, now);
                return;
            }

            // 2b The target has only just gone out of sight. A man who breaks
            //    into a run for every glimpse he loses never plants his feet:
            //    he stands in the aim clip for a moment and fires again the
            //    instant it shows. No line of fire at all (BlindUntil) still
            //    sends him on at once.
            if (f.Armed && f.Target != null && now < f.SteadyUntil && now >= f.BlindUntil
                && dist <= AssaultRange())
            {
                Steady(f, now);
                return;
            }
            f.FireSince = 0f;

            // 3  Otherwise run on with the line.
            Vector3 dest = anchor + Side(front) * f.LaneOffset + front * f.RankOffset;
            MoveInLine(f, s, dest, front, centre, now);
        }

        /// <summary>His half of the squad is the one that moves now, he has
        /// already fired for a moment, and the enemy is still well beyond close
        /// range and in front of the line.</summary>
        static bool BoundDue(Fighter f, Squad s, Vector3 front, float dist, float now)
        {
            if (s.Threat == null || s.BoundTeam != f.Team || now < f.NextBound) return false;
            if (now - f.FireSince < 1.5f || dist <= CloseRange() + BoundStep) return false;
            Vector3 to = FlatV(f.Target.position - f.Tr.position);
            return Vector3.Angle(to, front) <= 60f;
        }

        /// <summary>One bound forward, pulled a little toward his lane.</summary>
        static void StartBound(Fighter f, Squad s, Vector3 front, Vector3 centre, float now)
        {
            Vector3 side = Side(front);
            float drift = Vector3.Dot(FlatV(centre + side * f.LaneOffset - f.Tr.position), side);
            Vector3 dest = f.Tr.position + front * BoundStep + side * Mathf.Clamp(drift, -6f, 6f);
            if (f.Target != null)
            {
                float after = Flat(f.Target.position - dest);
                if (after < CloseRange())
                    dest -= front * Mathf.Min(BoundStep, CloseRange() - after);
            }
            f.BoundDest = dest;
            f.BoundUntil = now + 4f;
            f.NextBound = now + BoundSeconds() * 1.5f;
            f.FireSince = 0f;
            Go(f, dest, MainRun, PoseStand, now, Stance.Bound);
        }

        /// <summary>Run to his place in the line. The place is Lead units ahead
        /// of the line, so while there is ground to take he never arrives; a
        /// man who has got ahead of the others walks until they are with him
        /// again.</summary>
        static void MoveInLine(Fighter f, Squad s, Vector3 dest, Vector3 front,
                               Vector3 centre, float now)
        {
            // Never back: a man who bounded past the point where the line
            // stops, or who is ahead of it at the arrow head, only steps
            // sideways into his lane and waits there for the others.
            float back = Vector3.Dot(FlatV(dest - f.Tr.position), front);
            if (back < 0f) dest -= front * back;
            float away = Flat(dest - f.Tr.position);
            if (away <= LaneSlack)
            {
                Hold(f, s, now);
                return;
            }
            float ahead = Vector3.Dot(FlatV(f.Tr.position - centre), front) - f.RankOffset;
            bool walking = f.Stance == Stance.Advance && f.WantMain == MainWalk;
            int state = MainRun;
            if (s.Threat == null && (ahead > AheadWalk || (walking && ahead > AheadRun)))
                state = MainWalk;
            if (away < 10f) state = MainWalk;

            if (f.IkDriven) ReleaseAim(f);
            f.Stance = Stance.Advance;
            // Every order is a SetStateWithAnimAndSync RPC to every player
            // around; the point is Lead units ahead, so re-aiming it once a
            // second is plenty and keeps fifteen men off the network.
            if (f.HasOrder && IntField(f.Ai, _fMainState, -1) == state
                && Flat(dest - f.Ordered) < 12f && StillOurPoint(f) && now < f.MoveDeadline)
                return;
            if (now < f.NextMove) return;
            f.NextMove = now + 1f;
            OrderMove(f, dest, state, AddNone, PoseStand);
        }

        /// <summary>At his place (the arrow head, or close range): stand, and
        /// keep the weapon up toward the enemy while there is one.</summary>
        static void Hold(Fighter f, Squad s, float now)
        {
            f.Stance = Stance.Hold;
            f.HasOrder = false;
            if (f.Armed && s != null && s.Threat != null)
            {
                Transform keep = f.Target;
                if (f.Target == null) f.Target = s.Threat;
                Drive(f, MainIdle, AddAim, PoseStand, now, true);
                Face(f);
                if (!f.TargetIsPlayer) Aim(f, now);
                f.Target = keep;
                return;
            }
            if (f.IkDriven) ReleaseAim(f);
            Drive(f, MainIdle, AddNone, PoseStand, now, true);
        }

        /// <summary>Stand and fire, the way every vanilla NPC does: MainState
        /// Idle, AdditionalState Shooting, held, a round every
        /// _shootingTimerDelayCached. The feet come first: a man who is not
        /// planted yet is put into Idle + Aiming, and Shooting is only asked
        /// for once that clip has faded in (Planted).</summary>
        static void Fire(Fighter f, float now)
        {
            f.Stance = Stance.Fire;
            f.HasOrder = false;
            if (f.TargetIsPlayer)
            {
                // NPC_AI2.ShootingActions fires at _killTarget by itself in this
                // state, with the vanilla hit calculation, and LookAtIkController
                // aims. Every fourth round it checks CanTouchTarget and drops to
                // Empty when it cannot: then the man is blind and runs on.
                if (f.WantAdd == AddFire && IntField(f.Ai, _fAddState, -1) == AddNone
                    && now >= f.NextState)
                {
                    f.WantAdd = AddNone;
                    f.BlindUntil = now + 2f;
                    return;
                }
                f.IkDriven = false;
                Drive(f, MainIdle, Planted(f, now) ? AddFire : AddAim, PoseStand, now, true);
                Face(f);
                return;
            }
            if (!Planted(f, now))
            {
                Drive(f, MainIdle, AddAim, PoseStand, now, true);
                Aim(f, now);
                return;
            }
            Drive(f, MainIdle, AddFire, PoseStand, now, true);
            Aim(f, now);
            if (now < f.ReactUntil || !f.IkDriven || f.AimWeight < 0.6f || now < f.NextShot) return;
            if (Shoot(f)) f.NextShot = now + ShotDelay(f);
        }

        /// <summary>Are his feet planted in a standing clip? The shooting clips
        /// are upper-body layers - NPC_AI2.SetBlendingAnimLayers puts
        /// asr_shoot_auto, rifle_shoot_samopal and hg_shoot_auto on layer 6
        /// with a spine mixing transform - and SwitchAnimationByStates plays
        /// them with Animation.CrossFade, which fades out only that layer.
        /// Straight out of a run the legs kept the run clip while
        /// IdleStateAction stopped the NavMeshAgent: the 6.16.5 men ran on the
        /// spot and fired (CONFIRMED IL). idle_aiming is a whole-body clip, so
        /// a man counts as planted once he has stood in Idle + Aiming or
        /// Shooting for the crossfade. A reload is a layer-6 clip too and
        /// neither plants nor unplants him.</summary>
        static bool Planted(Fighter f, float now)
        {
            if (IntField(f.Ai, _fMainState, -1) != MainIdle
                || IntField(f.Ai, _fPoseState, -1) != PoseStand)
            { f.PlantedSince = 0f; return false; }
            int add = IntField(f.Ai, _fAddState, -1);
            if (add == AddReload) return false;
            if (add != AddAim && add != AddFire) { f.PlantedSince = 0f; return false; }
            if (f.PlantedSince <= 0f) f.PlantedSince = now;
            return now - f.PlantedSince >= PlantSeconds;
        }

        /// <summary>His target has just gone out of sight: feet stay planted in
        /// the standing aim clip and the weapon stays on the spot.</summary>
        static void Steady(Fighter f, float now)
        {
            f.Stance = Stance.Hold;
            f.HasOrder = false;
            Drive(f, MainIdle, AddAim, PoseStand, now, true);
            Face(f);
            if (f.TargetIsPlayer) f.IkDriven = false;
            else Aim(f, now);
        }

        /// <summary>The NPC's own pause between two rounds:
        /// NPC_AI2.Start draws _shootingTimerDelayCached from 0.2..0.3 s and
        /// ShootToTarget waits exactly that long (Crew shortens it for an MG42
        /// crewman). FireTo additionally keeps the weapon's rate of fire.</summary>
        static float ShotDelay(Fighter f)
        {
            if (_fShotDelayCached != null && f.Ai != null)
            {
                try
                {
                    float cached = (float)_fShotDelayCached.GetValue(f.Ai);
                    if (cached > 0.01f && cached < 2f) return cached;
                }
                catch { }
            }
            return UnityEngine.Random.Range(0.2f, 0.3f);
        }

        /// <summary>Suppression wears off, and a defender notices how badly he
        /// is hurt about twice a second.</summary>
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
            f.InCover = f.Cover != Vector3.zero && Flat(f.Tr.position - f.Cover) < 8f;
        }

        /// <summary>A defender has no arrow and no line; he fights where he was
        /// attacked and may go to ground for it.</summary>
        static void RunDefender(Fighter d, float now)
        {
            EnsureArmed(d, now);
            Decay(d, now);
            Acquire(d, now);
            Planted(d, now);
            if (d.Target == null || now - d.LastSeen > 8f)
            {
                if (d.WantAdd == AddAim || d.WantAdd == AddFire) StandDown(d);
                d.InCover = false;
                d.Cover = Vector3.zero;
                return;
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
            if (!d.InCover && now >= d.NextCover && CfgCoverChance.Value > 0f)
            {
                float appetite = Mathf.Clamp01(CfgCoverChance.Value)
                               * (0.35f + 0.8f * d.Suppression + 0.8f * d.Hurt);
                d.NextCover = now + UnityEngine.Random.Range(4f, 10f) * d.Pace;
                Vector3 spot;
                if (UnityEngine.Random.value < appetite && Search()
                    && FindCover(d, d.Target.position, out spot))
                {
                    Send(d, spot, now);
                    d.Cover = spot;
                    return;
                }
            }
            if (Reloading(d))
            {
                if (d.IkDriven) ReleaseAim(d);
                Face(d);
                return;
            }
            if (!d.Armed || !d.Sees)
            {
                Hold(d, null, now);
                Face(d);
                return;
            }
            Fire(d, now);
        }

        // ------------------------------------------------------- the weapon

        static bool ReadArmed(Fighter f)
        {
            try
            {
                if (f.Ai == null || !f.Ai.gameObject.activeInHierarchy) return false;
                if (f.Wm == null && _fWeaponsManager != null)
                    f.Wm = _fWeaponsManager.GetValue(f.Ai) as Component;
                if (IntField(f.Wm, _fWeaponCategory, 0) == 0) return false;
                Component weapon = WeaponOf(f);
                return weapon != null && _mCantWork != null
                    && !(bool)_mCantWork.Invoke(weapon, null);
            }
            catch { return false; }
        }

        /// <summary>Draw through the game's own setter, then wait for its
        /// coroutine. NPC_WeaponsManager.ShowWeapon does nothing while slot 0 is
        /// already claimed, and NetworkShowWeapon claims the slot before its
        /// delay and creates the model after it. A claimed slot without a model
        /// for five seconds - the coroutine died with a disabled object - is
        /// cleared through the native hide so the next draw can start. No
        /// permanent give-up.</summary>
        static void EnsureArmed(Fighter f, float now)
        {
            bool wasArmed = f.Armed;
            f.Armed = ReadArmed(f);
            if (f.Armed)
            {
                if (!wasArmed)
                {
                    f.NextState = 0f;
                    if (CfgDebug.Value)
                        RevivalPlugin.L.LogInfo("NpcWar: weapon ready on " + f.Ai.name
                            + " item " + IntField(f.Wm, _fWeaponItem, -1));
                }
                f.EquipTries = 0;
                f.NextEquip = 0f;
                f.SlotStuckSince = 0f;
                f.EquipWarned = false;
                return;
            }
            if (wasArmed || f.IkDriven) ReleaseAim(f);
            if (f.Ai == null || !Alive(f.Ai) || !f.Ai.gameObject.activeInHierarchy
                || !IsMine(f.Ai) || Reloading(f) || now < f.NextEquip) return;
            if (_mSetMainWeaponId == null && _mEquipWeapon == null) return;
            try
            {
                if (IntField(f.Wm, _fWeaponSlot, -1) == 0)
                {
                    if (f.SlotStuckSince <= 0f) { f.SlotStuckSince = now; return; }
                    if (now - f.SlotStuckSince < 5f) return;
                    f.SlotStuckSince = 0f;
                    f.NextEquip = now + 1f;
                    if (_mEquipWeapon != null)
                        _mEquipWeapon.Invoke(f.Ai, new object[] { false, false });
                    return;
                }
                f.SlotStuckSince = 0f;
                f.NextEquip = now + (f.EquipTries >= 4 ? 10f : 3f);
                // SetMainWeaponId(id, true) is the game's complete path: it
                // fills slot 0 and starts NetworkShowWeapon. EquipWeapon alone
                // only calls ShowWeapon, which has nothing to show after the
                // Aggressive spawn left the slot at -1.
                if (_mSetMainWeaponId != null && f.WeaponId > 0)
                    _mSetMainWeaponId.Invoke(f.Ai, new object[] { f.WeaponId, true });
                else if (_mEquipWeapon != null)
                    _mEquipWeapon.Invoke(f.Ai, new object[] { true, false });
                f.EquipTries++;
                f.NextState = 0f;
            }
            catch (Exception ex)
            {
                f.EquipTries++;
                if (CfgDebug.Value && !f.EquipWarned)
                    RevivalPlugin.L.LogWarning("NpcWar: equip failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
            if (f.EquipTries >= 4 && !f.EquipWarned)
            {
                f.EquipWarned = true;
                RevivalPlugin.L.LogWarning("NpcWar: weapon not ready on " + f.Ai.name
                    + " (item " + f.WeaponId + ", slot " + IntField(f.Wm, _fWeaponSlot, -1)
                    + ", category " + IntField(f.Wm, _fWeaponCategory, 0)
                    + "); holding fire and retrying.");
            }
        }

        // ------------------------------------------------------------- sensing

        /// <summary>Pick a target, then keep the line of fire up to date. The
        /// scan about three times a second, the line of fire about as often.</summary>
        static void Acquire(Fighter f, float now)
        {
            if (now >= f.NextScan)
            {
                f.NextScan = now + 0.3f + UnityEngine.Random.value * 0.15f;
                Transform had = f.Target;
                bool checkedLos;
                if (f.Squad != null) checkedLos = PickTargetForMan(f, now);
                else
                {
                    f.Target = PickTargetForDefender(f);
                    f.TargetIsPlayer = false;
                    checkedLos = false;
                }
                if (f.Target != null && f.Target != had)
                {
                    // Assault troops react at once; a defender needs a moment
                    // to find out what hit him.
                    f.ReactUntil = now + (f.Squad != null
                        ? UnityEngine.Random.Range(0.05f, 0.25f)
                        : UnityEngine.Random.Range(0.3f, 0.9f)) * (1.4f - 0.4f * f.Skill);
                    f.MuzzleBlockedSince = 0f;
                    f.MateBlockedSince = 0f;
                    if (!checkedLos) { f.LastSeen = now; f.NextLos = now; }
                }
            }
            if (f.Target == null || !f.Target)
            {
                f.Target = null;
                f.Sees = false;
                f.TargetIsPlayer = false;
                return;
            }
            if (now < f.NextLos) return;
            f.NextLos = now + 0.3f + UnityEngine.Random.value * 0.15f;
            float height;
            f.Sees = AimPoint(f, f.Target, out height);
            f.AimHeight = height;
            if (f.Sees) f.LastSeen = now;
        }

        static readonly Transform[] _cand = new Transform[5];
        static readonly bool[] _candPlayer = new bool[5];
        static readonly float[] _candSqr = new float[5];

        /// <summary>A squad man: keep a target he can still see; otherwise the
        /// nearest enemy he CAN see among the few nearest - a hated NPC, a man
        /// of another squad, or the player his own AI is hunting. Never his
        /// own squad, never a god-mode or safe-zone NPC. Returns whether the
        /// line of fire was checked here.</summary>
        static bool PickTargetForMan(Fighter f, float now)
        {
            float range = AssaultRange();
            Component player = KillTarget(f);
            if (f.Target != null && f.Target && now - f.LastSeen < 0.8f)
            {
                if (f.TargetIsPlayer)
                {
                    if (player != null && player.transform == f.Target) return false;
                }
                else
                {
                    Component cur = f.Target.GetComponent(_npcType);
                    Fighter current = cur == null ? null : FighterOf(cur);
                    if (cur != null && Alive(cur)
                        && (current == null || current.Squad != f.Squad)
                        && ((current != null && current.Squad != null) || Targetable(cur))
                        && Hostile(f.Hated, FactionOf(cur))
                        && Flat(f.Target.position - f.Tr.position) <= range * 1.1f)
                        return false;
                }
            }

            int n = 0;
            Vector3 p = f.Tr.position;
            float rangeSqr = range * range;
            if (player != null)
            {
                float d = (player.transform.position - p).sqrMagnitude;
                if (d < rangeSqr) Insert(ref n, player.transform, true, d);
            }
            for (int i = 0; i < _scene.Count; i++)
            {
                Component c = _scene[i];
                if (c == null || c == f.Ai) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d >= rangeSqr || (n == _cand.Length && d >= _candSqr[n - 1])) continue;
                Fighter other = FighterOf(c);
                if (other != null && other.Squad == f.Squad) continue;
                if (!Hostile(f.Hated, FactionOf(c))) continue;
                if ((other == null || other.Squad == null) && !Targetable(c)) continue;
                if (!Alive(c)) continue;
                Insert(ref n, c.transform, false, d);
            }

            f.Target = null;
            f.TargetIsPlayer = false;
            f.Sees = false;
            if (n == 0) return true;
            // The nearest three are tried for a line of fire; the nearest of all
            // stays the target when none of them can be seen, so the man at
            // least turns toward it.
            int tries = Mathf.Min(n, 3);
            for (int i = 0; i < tries; i++)
            {
                float height;
                if (!AimPoint(f, _cand[i], out height)) continue;
                f.Target = _cand[i];
                f.TargetIsPlayer = _candPlayer[i];
                f.Sees = true;
                f.AimHeight = height;
                f.LastSeen = now;
                break;
            }
            if (f.Target == null)
            {
                f.Target = _cand[0];
                f.TargetIsPlayer = _candPlayer[0];
            }
            f.NextLos = now + 0.3f + UnityEngine.Random.value * 0.15f;
            if (f.Sees && !f.TargetIsPlayer)
            {
                Component ai = f.Target.GetComponent(_npcType);
                if (ai != null && FighterOf(ai) == null) Enlist(ai);
            }
            return true;
        }

        /// <summary>Keep the candidate arrays sorted by distance.</summary>
        static void Insert(ref int n, Transform t, bool player, float sqr)
        {
            int at = n < _cand.Length ? n : _cand.Length - 1;
            if (n == _cand.Length && sqr >= _candSqr[at]) return;
            while (at > 0 && _candSqr[at - 1] > sqr)
            {
                _cand[at] = _cand[at - 1];
                _candPlayer[at] = _candPlayer[at - 1];
                _candSqr[at] = _candSqr[at - 1];
                at--;
            }
            _cand[at] = t;
            _candPlayer[at] = player;
            _candSqr[at] = sqr;
            if (n < _cand.Length) n++;
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
            float bestSqr = Mathf.Max(CfgSightRange.Value, AssaultRange()) * rangeFactor;
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
                d.ReactUntil = Time.time + UnityEngine.Random.Range(0.4f, 1.5f)
                             + away * 0.03f;
                d.NextScan = Time.time;
                _defenders.Add(d);
                TryAlarm(c);
            }
        }

        // ------------------------------------------------------------- firing

        /// <summary>One round at the man's NPC target. False when the weapon
        /// did not fire this frame (rate of fire, reload, obstruction), so the
        /// next attempt comes quickly.</summary>
        static bool Shoot(Fighter f)
        {
            if (!ReadArmed(f)) return false;
            Vector3 aimAt = AimWorld(f);
            Component weapon = WeaponOf(f);
            Vector3 from = Muzzle(weapon, f);
            if (from == Vector3.zero) return false;
            // Eyes can see over cover while the barrel is still behind it.
            if (!Clear(from, aimAt, f.Target))
            {
                if (f.MuzzleBlockedSince <= 0f) f.MuzzleBlockedSince = Time.time;
                f.NextShot = Time.time + 0.2f;
                return false;
            }
            f.MuzzleBlockedSince = 0f;
            // Never through a comrade of the line.
            if (f.Squad != null && MateInLine(f, from, aimAt))
            {
                if (f.MateBlockedSince <= 0f) f.MateBlockedSince = Time.time;
                f.NextShot = Time.time + 0.15f;
                return false;
            }
            f.MateBlockedSince = 0f;
            float dist = Vector3.Distance(from, aimAt);
            Component targetAi = f.Target.GetComponent(_npcType);
            Fighter victim = targetAi == null ? null : FighterOf(targetAi);
            Vector3 aim = aimAt + MissOffset(f, victim, from, aimAt, dist);
            bool rocket = IntField(f.Wm, _fWeaponItem, -1) == Crew.LAW_ID;
            // CrewLaw already supplies the LAW blast and NPC damage. Keep it
            // away from the shooter and nearby squad mates, and do not add a
            // second infantry hit after its FireOneShot postfix.
            if (rocket && !RocketClear(f, from, aim))
            {
                f.NextShot = Time.time + 0.5f;
                return false;
            }

            int fired = VanillaShot(f, weapon, aim);
            if (fired <= 0)
            {
                f.NextShot = Time.time + 0.05f;
                return false;
            }
            if (f.Squad != null) f.Squad.Shots++;
            if (rocket) return true;
            // Being shot at is felt whether or not the round connects.
            if (victim != null && CfgSuppression.Value)
                victim.Suppression = Mathf.Min(1f,
                    victim.Suppression + 0.34f / Mathf.Max(0.4f, victim.Nerve));

            Vector3 dir = aim - from;
            if (dir.sqrMagnitude < 0.0001f) return true;
            dir.Normalize();

            float range = Mathf.Max(dist + 5f, RangeOf(f) + 20f);
            Vector3 impact;
            // Past the shooter's own 0.75 unit capsule.
            GameObject struck = Turret.RaycastObject(from + dir * 1.0f, dir, range, out impact);

            if (struck == null) return true;
            Component hitAi = struck.GetComponentInParent(_npcType);
            if (hitAi == null || !Alive(hitAi)) return true;
            Fighter hurt = FighterOf(hitAi);
            if (hurt != null && hurt.Squad != null && hurt.Squad == f.Squad) return true;
            bool enemy = f.Squad == null
                ? hurt != null && hurt.Squad != null
                : Hostile(f.Hated, FactionOf(hitAi))
                  && ((hurt != null && hurt.Squad != null) || Targetable(hitAi));
            if (!enemy) return true;
            if (hurt == null && f.Squad != null) Enlist(hitAi);

            if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", CfgDamage.Value))
            {
                if (f.Squad != null) f.Squad.Hits++;
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogInfo("NpcWar: hit at " + dist.ToString("0") + " units.");
            }
            return true;
        }

        /// <summary>Would this round pass within a man's width of a living
        /// comrade standing between the muzzle and the target?</summary>
        static bool MateInLine(Fighter f, Vector3 from, Vector3 to)
        {
            Vector3 axis = to - from;
            float length = axis.magnitude;
            if (length < 4f) return false;
            axis /= length;
            for (int i = 0; i < f.Squad.Men.Count; i++)
            {
                Fighter m = f.Squad.Men[i];
                if (m == f || m.Tr == null || m.Ai == null || !Alive(m.Ai)) continue;
                Vector3 chest = m.Tr.position + Vector3.up * ChestHeight - from;
                float t = Vector3.Dot(chest, axis);
                if (t < 2f || t > length - 2f) continue;
                if ((chest - axis * t).sqrMagnitude < 2.0f * 2.0f) return true;
            }
            return false;
        }

        static bool RocketClear(Fighter f, Vector3 from, Vector3 aim)
        {
            float radius = RevivalPlugin.CfgPatrolCrewLawRadius == null
                ? 8f : Mathf.Max(0f, RevivalPlugin.CfgPatrolCrewLawRadius.Value);
            Vector3 dir = aim - from;
            float dist = dir.magnitude;
            if (dist < radius * 2f + 10f) return false;
            Vector3 hit;
            GameObject obstacle = Turret.RaycastObject(from, dir / dist, dist + 1f, out hit);
            Vector3 boom = obstacle == null ? aim : hit;
            if (Vector3.Distance(from, boom) < radius * 2f + 10f) return false;
            if (f.Squad != null)
                foreach (Fighter mate in f.Squad.Men)
                    if (mate.Tr != null && Alive(mate.Ai)
                        && Vector3.Distance(mate.Tr.position, boom) < radius + 6f) return false;
            return true;
        }

        /// <summary>Fire the NPC's own weapon at a point. 1 = a round went out,
        /// 0 = not this frame (rate of fire, empty and reloading), -1 = no usable
        /// native weapon. Failure never produces effects or damage.</summary>
        static int VanillaShot(Fighter f, Component weapon, Vector3 aim)
        {
            if (weapon == null || _mFireTo == null || _mHasBullets == null || _fRofDelay == null)
                return -1;
            try
            {
                if (_mCantWork != null && (bool)_mCantWork.Invoke(weapon, null)) return -1;
                // FireTo advances its delay even with an empty magazine, so
                // ammunition must be checked BEFORE using the delay as proof.
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
            ReleaseAim(f);
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

        /// <summary>Legacy event receiver for an older peer's shot packet.
        /// New NPC shots use only native weapon effects and never call this.</summary>
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
            // The user's requested small accuracy allowance is applied to the
            // assault profile. It is deliberately only eight percentage points
            // before the individual skill, range and cover modifiers.
            float acc = Mathf.Clamp01(CfgAccuracy.Value + 0.08f) * shooter.Skill;
            acc *= 1f - 0.45f * shooter.Suppression;
            if (victim != null)
            {
                if (victim.InCover) acc *= 0.55f;
                else if (victim.Crouched) acc *= 0.75f;
                if (victim.Stance == Stance.Reposition || victim.Stance == Stance.Bound
                    || victim.Stance == Stance.Advance) acc *= 0.8f;
            }
            float far = Mathf.Clamp01(dist / Mathf.Max(1f, RangeOf(shooter)));
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
        /// behind a parapet only ever shows his head and chest. Returns false
        /// when nothing is visible; the aim point is then the chest, for the
        /// pose. The HEIGHT comes back, not the point, so the weapon keeps
        /// following a target that is moving between two line-of-fire checks.</summary>
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
            if (f.Target == null || !f.Target) return;
            Face(f);
            Vector3 look = AimWorld(f);
            // A little wander, so a line of men does not hold one statue pose.
            // Roughly a hand's width at fifty units.
            float t = now * (0.7f + (f.LaneOffset * 0.013f));
            look += new Vector3(Mathf.Sin(t * 1.3f), Mathf.Sin(t * 0.9f + 1.1f),
                                Mathf.Cos(t * 1.1f)) * (0.3f + 0.5f * f.Suppression);
            DriveAim(f, look, now);
            if (_fAimingPoint != null && _fAimingPoint.FieldType == typeof(Vector3))
            {
                try { _fAimingPoint.SetValue(f.Ai, look); }
                catch { }
            }
        }

        /// <summary>Turn the body toward the target. Each man turns at his own
        /// speed, so a line that acquires together does not snap together.</summary>
        static void Face(Fighter f)
        {
            if (f.Target == null || !f.Target) return;
            Vector3 flat = f.Target.position - f.Tr.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) return;
            f.Tr.rotation = Quaternion.RotateTowards(f.Tr.rotation,
                Quaternion.LookRotation(flat), (180f + 120f * f.Skill) * Time.deltaTime);
        }

        /// <summary>Hold the aim IK on a world point. SetupLookAtIk wires
        /// LookAtIKTarget as the solver target with a chest-to-hand chain, so
        /// moving that transform points the weapon - in elevation as well as in
        /// azimuth. The vanilla controller lerps the weight toward zero every
        /// frame while _killTarget is null; we ramp our own weight like its
        /// player branch does (5 per second) and set it every frame.</summary>
        static void DriveAim(Fighter f, Vector3 lookAt, float now)
        {
            if (!AimPoseReady(f, now)) { if (f.IkDriven) ReleaseAim(f); return; }
            if (_fAimIk == null || _fLookTarget == null) return;
            if (f.IkMissing)
            {
                // Unity can finish wiring _aimIk one or two frames after the
                // NPC starts. A permanent failure bit made that race fatal.
                if (now < f.NextIkRetry) return;
                f.IkMissing = false;
                f.Ik = null;
                f.Look = null;
            }
            try
            {
                if (f.Ik == null)
                {
                    f.Ik = _fAimIk.GetValue(f.Ai) as Component;
                    f.Look = _fLookTarget.GetValue(f.Ai) as Transform;
                    if (f.Ik == null || f.Look == null)
                    { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                    if (_fSolver == null) _fSolver = AccessTools.Field(f.Ik.GetType(), "solver");
                    if (_fSolver == null)
                    { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                }
                object solver = _fSolver.GetValue(f.Ik);
                if (solver == null)
                { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                if (_fIkWeight == null)
                {
                    _fIkWeight = AccessTools.Field(solver.GetType(), "IKPositionWeight");
                    if (_fIkWeight == null || _fIkWeight.FieldType != typeof(float))
                    { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                }
                GameObject go = f.Ik.gameObject;
                if (!go.activeSelf) go.SetActive(true);
                f.AimWeight = Mathf.Min(1f, f.AimWeight + Time.deltaTime * 5f);
                _fIkWeight.SetValue(solver, f.AimWeight);
                // Snap on the first frame of an engagement, then follow.
                f.Look.position = f.IkDriven
                    ? Vector3.Lerp(f.Look.position, lookAt, Time.deltaTime * 8f)
                    : lookAt;
                f.IkDriven = true;
            }
            catch (Exception ex)
            {
                ReleaseAim(f);
                f.IkMissing = true;
                f.NextIkRetry = now + 1f;
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: aim IK - " + ex.Message);
            }
        }

        /// <summary>The same gate the vanilla LookAtIkController has - the man
        /// stands in Aiming or Shooting - plus the two things that made
        /// 6.16.2's men arch their backs: a weapon really in hand, and a short
        /// moment for the crossfade out of the run or draw clip.</summary>
        static bool AimPoseReady(Fighter f, float now)
        {
            if (!f.Armed || Reloading(f)
                || IntField(f.Ai, _fMainState, -1) != MainIdle
                || IntField(f.Ai, _fPoseState, -1) != PoseStand)
            { f.PoseSince = 0f; return false; }
            int add = IntField(f.Ai, _fAddState, -1);
            if (add != AddAim && add != AddFire) { f.PoseSince = 0f; return false; }
            if (f.PoseSince <= 0f) { f.PoseSince = now; return false; }
            return now - f.PoseSince >= 0.15f;
        }

        static void ReleaseAim(Fighter f)
        {
            f.IkDriven = false;
            f.AimWeight = 0f;
            // A lowered hand must not inherit a second of vanilla IK fade.
            try
            {
                if (f.Ik == null || _fSolver == null || _fIkWeight == null) return;
                object solver = _fSolver.GetValue(f.Ik);
                if (solver != null) _fIkWeight.SetValue(solver, 0f);
            }
            catch { }
        }

        /// <summary>Out of the aim pose. The vanilla controller switches the IK
        /// off again on its own once the additional state is no longer Aiming
        /// or Shooting.</summary>
        static void StandDown(Fighter f)
        {
            ReleaseAim(f);
            f.InCover = false;
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
            ReleaseAim(f);
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
        /// IdleStateAction queues its own intentions on the tactical task and
        /// returns early while GetCalculatedPauseTime is positive, so a short
        /// pause refreshed twice a second is enough to hold a pose.</summary>
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

        // ------------------------------------------------ defender cover search

        static bool Search()
        {
            if (_searchBudget <= 0) return false;
            _searchBudget--;
            return true;
        }

        /// <summary>Somewhere close that breaks the line from the threat. Best
        /// is a spot that hides a crouching man but still lets a standing one
        /// shoot - the edge of a wall rather than the middle of it.</summary>
        static bool FindCover(Fighter f, Vector3 threat, out Vector3 spot)
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
                if (!Reachable(f, c, out c)) continue;
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

        /// <summary>Is a proposed point real ground the man may stand on?</summary>
        static bool Reachable(Fighter f, Vector3 c, out Vector3 at)
        {
            at = c;
            Vector3 hit;
            GameObject g = Turret.RaycastObject(new Vector3(c.x, c.y + 40f, c.z),
                Vector3.down, 120f, out hit);
            if (g == null) return false;
            if (Mathf.Abs(hit.y - f.Tr.position.y) > 26f) return false;
            NavMeshHit nav;
            if (!NavMesh.SamplePosition(new Vector3(c.x, hit.y + 0.2f, c.z), out nav, 8f,
                                        NavMesh.AllAreas))
                return false;
            at = nav.position;
            return true;
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
                + ") - " + n + " men removed from the map; " + s.Shots + " shots at NPCs, "
                + s.Hits + " hits.");
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
            return Vector3.zero;
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
        /// not in a safe settlement (traders), not in a conversation, and one
        /// that NPC_AI2.ApplyDamage really hurts (Hurtable).</summary>
        static bool Targetable(Component ai)
        {
            if (!IsMine(ai)) return false;
            if (Bool(ai, "GodModeEnabled")) return false;
            if (Bool(ai, "_isSafeSettlement")) return false;
            if (Bool(ai, "IsTalkActive")) return false;
            if (!Hurtable(ai)) return false;
            return FactionOf(ai) != null;
        }

        /// <summary>Would NPC_AI2.ApplyDamage take health off this NPC? It
        /// returns early for an NPC that is not initialized or whose settlement
        /// is a safe one, and a StoreKeeper never reaches DecreaseHealth
        /// (CONFIRMED IL, 2026-09-14): the settlement trader swallowed every
        /// round of the 6.16.5 squad. A guard that cannot be read refuses
        /// nothing.</summary>
        static bool Hurtable(Component ai)
        {
            try
            {
                if (_fInitialized != null && !(bool)_fInitialized.GetValue(ai)) return false;
                if (IntField(ai, _fBehavior, -1) == BehaviorStoreKeeper) return false;
                if (_fMySettlement != null && _fSafeSettlement != null)
                {
                    object home = _fMySettlement.GetValue(ai);
                    if (home != null && (bool)_fSafeSettlement.GetValue(home)) return false;
                }
            }
            catch { }
            return true;
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

        static Vector3 FlatV(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // -------------------------------------------------------------- status

        static string DebugTail()
        {
            if (CfgDebug == null || !CfgDebug.Value || _squads.Count == 0) return "";
            Squad s = _squads[0];
            int fire = 0, move = 0, hold = 0, armed = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.Armed) armed++;
                if (f.Stance == Stance.Fire) fire++;
                else if (f.Stance == Stance.Hold) hold++;
                else move++;
            }
            return " | " + s.Tag + " " + s.Phase + ": " + armed + " armed, " + fire + " firing, "
                + move + " moving, " + hold + " holding"
                + (s.Threat == null ? ", no contact" : ", contact team " + s.BoundTeam + " bounds");
        }

        public static void Draw()
        {
            if (CfgDebug == null || !CfgDebug.Value || _status.Length == 0) return;
            GUI.Label(new Rect(8f, 8f, 720f, 22f), _status);
        }
    }
}
