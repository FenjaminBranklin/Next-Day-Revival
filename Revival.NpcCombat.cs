using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

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
    //     Advance   it moves as one body along the arrow toward the head and
    //               attacks every NPC it sees that its faction hates
    //     Patrol    the survivors walk the arrow up and down until the patrol
    //               time (at most two hours) is over
    //     removed   every man of the squad, alive or dead, leaves the map
    //
    //   An NPC the squad fires on becomes a DEFENDER and fires back at the
    //   squad, so a fight is two-sided. Players are left to the game: squad men
    //   stand under their settlement's general alarm (CrewAlarm), and defenders
    //   are alarmed as well, so the vanilla pipeline engages hostile players on
    //   the player's own client with correct attribution.
    //
    //   The shot is a real raycast with a miss model; a hit on a living hostile
    //   NPC is applied through the game's own NPC_AI2.ApplyDamage - the damage
    //   road the patrol guns already use (Revival.Patrol.cs, Gun.Schaden), which
    //   keeps native health, wounds, death, ragdoll and Photon replication.
    //   Tracer and sound are broadcast so every client sees the exchange.
    //
    // AUTHORITY
    //
    //   Only the Photon master client decides anything, and only on NPCs whose
    //   photonView.isMine is true - the two conditions ApplyDamage checks.
    //   Nothing here runs without an operation, so a map without troop landings
    //   is unchanged. Movement uses the native running state that the crew
    //   disembark path proved (Revival.Crew.cs, StartSectorMove).
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
            public float NextScan, NextShot, ReactUntil, NextMove;
            public int Burst, Slot;
            public GameObject Point;      // the walk point currently issued
        }

        class Squad
        {
            public string Tag;
            public GameObject Settlement;
            public readonly List<Fighter> Men = new List<Fighter>();
            public Vector3 Tail, Head;
            public Phase Phase;
            public bool TowardHead = true;   // patrol leg
            public float PatrolSeconds, PatrolEnds, HardEnd, ContactUntil;
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
        static MethodInfo _mIsAlive, _mTempTask, _mTargetWp, _mStateSync, _mAlarm;
        static MethodInfo _mPhotonView, _mIsMine, _mMasterGetter, _mDestroy;
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

            _mIsAlive = AccessTools.Method(_npcType, "IsAlive", null, null);
            _mTempTask = AccessTools.Method(_npcType, "SetTemporaryTask", null, null);
            _mTargetWp = AccessTools.Method(_npcType, "SetTargetWalkPoint", null, null);
            _mStateSync = AccessTools.Method(_npcType, "SetStateWithAnimAndSync", null, null);
            _mAlarm = AccessTools.Method(_npcType, "SetGeneralAlarm", null, null);
            _mPhotonView = AccessTools.Method(_npcType, "get_photonView", null, null);

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
            s.Tail = tail;
            s.Head = head;
            s.Phase = Phase.ToStart;
            s.PatrolSeconds = Mathf.Clamp(patrolSeconds, 60f, 7200f);
            float walk = Vector3.Distance(settlement.transform.position, tail)
                       + Vector3.Distance(tail, head);
            // One metre per second is a slow, fighting pace; plus half an hour.
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
                s.Men.Add(f);
            }
            if (s.Men.Count == 0) return false;
            _squads.Add(s);
            EnsurePointsRoot();
            RevivalPlugin.L.LogInfo("NpcWar: operation " + tag + " - " + s.Men.Count
                + " men, arrow " + tail.ToString("0") + " -> " + head.ToString("0")
                + ", patrol " + (s.PatrolSeconds / 60f).ToString("0") + " min.");
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
                    _defenders.RemoveAt(i);
                    continue;
                }
                Fight(d, now);
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

            // The settlement and its ring of walk and tactical points travel
            // with the body. Whatever the vanilla alarm picks between two of our
            // orders is then next to the squad, not back at the landing zone.
            s.Settlement.transform.position = centre;

            // Phase changes are decided by the body, not by one fast runner.
            Vector3 goal = Goal(s);
            if (Flat(centre - goal) < 14f)
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

            // The body moves in short bounds along the line toward the goal, so
            // the men arrive together instead of strung out over the map. A
            // hostile seen near the arrow pulls the body toward it.
            Transform enemy = NearestEnemyOfSquad(s, centre);
            if (enemy != null) s.ContactUntil = now + 4f;
            if (enemy != null && DistanceToSegment(enemy.position, s.Tail, s.Head) <= CfgDetour.Value)
                s.Body = enemy.position;
            else
            {
                Vector3 to = goal - centre;
                to.y = 0f;
                float bound = Mathf.Min(to.magnitude, 30f);
                s.Body = to.sqrMagnitude < 0.01f ? goal : centre + to.normalized * bound;
            }

            Vector3 along = s.Head - s.Tail;
            along.y = 0f;
            if (along.sqrMagnitude < 0.01f) along = Vector3.forward;
            along.Normalize();
            if (s.Phase == Phase.Patrol && !s.TowardHead) along = -along;

            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                Fight(f, now);
                bool engaged = f.Target != null
                    && Vector3.Distance(f.Tr.position, f.Target.position) <= CfgEngageRange.Value;
                if (engaged || now < f.NextMove) continue;
                f.NextMove = now + 2.5f + UnityEngine.Random.value * 0.5f;
                Vector3 spot = s.Body + Formation(f.Slot, along);
                // A man already at his spot keeps it; re-issuing a point he
                // stands on only restarts his run animation.
                if (Flat(f.Tr.position - spot) < 3f) continue;
                int state = s.Phase == Phase.Patrol && now >= s.ContactUntil ? 1 : 2;
                OrderMove(f, spot, state);
            }
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

        /// <summary>Two files abreast, four metres apart, rows five metres deep,
        /// laid out in the direction of travel.</summary>
        static Vector3 Formation(int slot, Vector3 along)
        {
            Vector3 side = new Vector3(along.z, 0f, -along.x);
            float lateral = (slot % 2 == 0 ? -2f : 2f);
            float back = (slot / 2) * 5f;
            return side * lateral - along * back;
        }

        // ------------------------------------------------------------- combat

        static void Fight(Fighter f, float now)
        {
            if (now >= f.NextScan)
            {
                f.NextScan = now + 0.75f + UnityEngine.Random.value * 0.25f;
                Transform had = f.Target;
                f.Target = f.Squad != null ? PickTargetForMan(f) : PickTargetForDefender(f);
                if (f.Target != null && f.Target != had)
                    f.ReactUntil = now + UnityEngine.Random.Range(0.2f, Mathf.Max(0.2f, CfgReactionMax.Value));
            }
            if (f.Target == null) return;
            if (Vector3.Distance(f.Tr.position, f.Target.position) > CfgEngageRange.Value) return;

            FaceTarget(f);
            if (now < f.ReactUntil || now < f.NextShot) return;
            if (!LineOfSight(f.Tr, f.Target)) { f.NextShot = now + 0.5f; return; }
            Shoot(f);
            ScheduleNextShot(f, now);
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
                    if ((c.transform.position - at).sqrMagnitude > 25f * 25f) continue;
                    object other = FactionOf(c);
                    if (other == null || !other.Equals(faction)) continue;
                }
                Fighter d = NewFighter(c, null);
                d.ReactUntil = Time.time + UnityEngine.Random.Range(0.4f, 1.5f);
                _defenders.Add(d);
                TryAlarm(c);
            }
        }

        static Transform NearestEnemyOfSquad(Squad s, Vector3 centre)
        {
            Transform best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Target == null || f.Ai == null || !Alive(f.Ai)) continue;
                float d = (f.Target.position - centre).sqrMagnitude;
                if (d < bestSqr) { best = f.Target; bestSqr = d; }
            }
            return best;
        }

        // ------------------------------------------------------------- firing

        static void Shoot(Fighter f)
        {
            Vector3 from = f.Tr.position + Vector3.up * 1.45f;
            Vector3 aimAt = f.Target.position + Vector3.up * 1.1f;
            float dist = Vector3.Distance(from, aimAt);

            Vector3 aim = aimAt + MissOffset(from, aimAt, dist);
            Vector3 dir = aim - from;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            float range = Mathf.Max(dist + 5f, CfgSightRange.Value + 20f);
            Vector3 impact;
            GameObject struck = Turret.RaycastObject(from + dir * 0.6f, dir, range, out impact);
            Vector3 end = struck == null ? from + dir * range : impact;
            ShotEffect(from, end);
            RevivalTroopInsertion.Net.SendShot(from, end);

            if (struck == null) return;
            Component hitAi = struck.GetComponentInParent(_npcType);
            if (hitAi == null || !Alive(hitAi)) return;
            Fighter victim = FighterOf(hitAi);
            if (victim != null && victim.Squad != null && victim.Squad == f.Squad) return;
            bool enemy = f.Squad == null
                ? victim != null && victim.Squad != null
                : Hostile(f.Hated, FactionOf(hitAi)) && (victim != null || Targetable(hitAi));
            if (!enemy) return;
            if (victim == null && f.Squad != null) Enlist(hitAi);

            if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", CfgDamage.Value)
                && CfgDebug.Value)
                RevivalPlugin.L.LogInfo("NpcWar: hit at " + dist.ToString("0") + " m.");
        }

        /// <summary>Tracer and report, identical on the master and on every
        /// client that receives the broadcast.</summary>
        internal static void ShotEffect(Vector3 from, Vector3 end)
        {
            try
            {
                VehicleShotSound.Play(from, false);
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
            Vector3 from = shooter.position + Vector3.up * 1.45f;
            Vector3 to = target.position + Vector3.up * 1.1f;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.5f) return true;
            dir /= dist;
            Vector3 point;
            // Start past the shooter's own capsule and ragdoll.
            GameObject hit = Turret.RaycastObject(from + dir * 0.6f, dir, dist, out point);
            if (hit == null) return true;
            if (hit.transform.IsChildOf(target)) return true;
            return (to - point).sqrMagnitude < 2.25f;
        }

        // ----------------------------------------------------------- movement

        static void FaceTarget(Fighter f)
        {
            Vector3 flat = f.Target.position - f.Tr.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) return;
            f.Tr.rotation = Quaternion.RotateTowards(f.Tr.rotation,
                Quaternion.LookRotation(flat), 360f * Time.deltaTime);
        }

        /// <summary>Send one man to a world point: a tactical task, one tactical
        /// walk point there, and SetStateWithAnimAndSync, which drives the
        /// NavMeshAgent, the animation and the Photon sync (Crew.StartSectorMove).
        /// State 2 runs; state 1 is the calmer patrol pace (HYPOTHESIS: the two
        /// moving states SetStateWithAnimAndSync treats alike are walk and run).</summary>
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

                _mTempTask.Invoke(f.Ai, new object[] { 2 });      // TemporaryTask Tactical
                _fTempPoints.SetValue(f.Ai, list);
                if (_fTempIndex != null) _fTempIndex.SetValue(f.Ai, 0);
                _mTargetWp.Invoke(f.Ai, new object[] { wp });
                _mStateSync.Invoke(f.Ai, new object[] {
                    f.Tr.position, state, 0, 0, 0, true, 2, f.Tr.eulerAngles.y });
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: move order failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        static Vector3 Ground(Vector3 p)
        {
            Vector3 hit;
            GameObject g = Turret.RaycastObject(p + Vector3.up * 30f, Vector3.down, 80f, out hit);
            return g == null ? p : hit + Vector3.up * 0.1f;
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
