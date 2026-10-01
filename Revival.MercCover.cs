// M1 mercenaries, cover perception - the game side of the cover field
// (Revival.MercCoverCore.cs, docs/ai/tasks/m1-merc-cover-points.md).
//
//   WORLD     PhysicsCoverWorld answers the core's three questions: a ray
//             (Physics.RaycastNonAlloc into a fixed buffer, triggers and
//             people skipped - a man is no wall), the ground below a point,
//             and a spot a man can stand on (NavMesh.SamplePosition). What a
//             collider is (solid, can drive away, a person) is looked up once
//             per collider and kept.
//   SERVICE   MercCoverService owns the one CoverField of this client. Mercs
//             ask for the cells around them once a second; the cells are
//             built at CoverRaysPerFrame world calls a frame (F6 slot
//             MercCover.Tick) and cost nothing once built. It exposes the
//             three queries the merc AI builds on: Best (cover toward up to
//             four threats), Exposed (can a threat see this spot) and Peek
//             (which side of a cover point sees the threat, and where he
//             stands for it).
//   SENSE     NpcWar keeps each merc's MercSense current, twice a second and
//             staggered: his threats (his target and the other hostiles his
//             target scan weighed), whether they can see him, and the best
//             cover toward them, claimed so two mercs do not share a spot.
//             M2's fight loop (Revival.MercFight.cs) runs every merc to
//             that pick and fights from it; FindCover's random samples are
//             its fallback while the service has no pick.
//   NETWORK   perception runs on the owner's client only, where the merc AI
//             runs. What it makes him do (move, crouch, fire) reaches the
//             other players through the NPC's existing Photon replication;
//             nothing new is sent.
//   COST      per merc: 2 Hz sense = at most 4 rays (exposure to the two
//             nearest threats) + a Best of at most CoverField.QueryCost (16)
//             calls every 1.5 s at most, one Best a frame for all mercs;
//             about 20 rays a second per merc in a fight, none out of one.
//             Mapping: at most CoverRaysPerFrame (24) calls a frame, only
//             while a merc enters new ground (~300 calls a cell, nine cells
//             around him: ~2 s at 60 fps), then 0 until a cell is 180 s old.
//             Steady state allocates nothing.
//
// Mercenaries only: normal NPCs, defenders and squads never touch it.
// Units: game units (NpcWar SCALE note, ~2.8 per metre). C# 3.0, ASCII only.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    /// <summary>The cover field's world: PhysX rays and the NavMesh.</summary>
    internal sealed class PhysicsCoverWorld : ICoverWorld, IMercRaidProtection
    {
        public bool Revetment(Vector3 at)
        {
            // Existing registered earth-walled gun pits already stop outside
            // blasts on the master. No new damage immunity or safe zone.
            return AirKills.Sheltered(at, at + Vector3.right * 280f)
                && AirKills.Sheltered(at, at - Vector3.right * 280f);
        }

        const byte Solid = 1, Moving = 2, Person = 3;
        const int KindCap = 4096;

        readonly RaycastHit[] _hits = new RaycastHit[16];
        readonly Dictionary<int, byte> _kind = new Dictionary<int, byte>(KindCap);
        readonly Type _npcType;

        internal PhysicsCoverWorld(Type npcType) { _npcType = npcType; }

        internal void Forget() { _kind.Clear(); }

        /// <summary>A collider once: a person (player, NPC, his ragdoll), a
        /// thing that can drive away (a rigidbody) or plain solid.</summary>
        byte KindOf(Collider c)
        {
            int id = c.GetInstanceID();
            byte k;
            if (_kind.TryGetValue(id, out k)) return k;
            if (_kind.Count >= KindCap) _kind.Clear();
            if (c.GetComponentInParent<CharacterController>() != null
                || (_npcType != null && c.GetComponentInParent(_npcType) != null)) k = Person;
            else k = c.attachedRigidbody != null ? Moving : Solid;
            _kind[id] = k;
            return k;
        }

        int Nearest(Vector3 from, Vector3 dir, float range, out byte kind)
        {
            kind = 0;
            int n = Physics.RaycastNonAlloc(from, dir, _hits, range, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            int at = -1;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = _hits[i].collider;
                if (c == null || _hits[i].distance >= best) continue;
                byte k = KindOf(c);
                if (k == Person) continue;
                best = _hits[i].distance; at = i; kind = k;
            }
            return at;
        }

        public bool Cast(Vector3 from, Vector3 dir, float range, out Vector3 point, out Vector3 normal, out bool dynamic)
        {
            byte kind;
            int at = Nearest(from, dir, range, out kind);
            if (at < 0) { point = Vector3.zero; normal = Vector3.zero; dynamic = false; return false; }
            point = _hits[at].point; normal = _hits[at].normal; dynamic = kind == Moving;
            return true;
        }

        public bool Ground(Vector3 top, float depth, out Vector3 point, out Vector3 normal)
        {
            byte kind;
            int at = Nearest(top, Vector3.down, depth, out kind);
            if (at < 0) { point = Vector3.zero; normal = Vector3.up; return false; }
            point = _hits[at].point; normal = _hits[at].normal;
            return true;
        }

        public bool Stand(Vector3 near, float reach, out Vector3 at)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(near, out hit, reach, NavMesh.AllAreas)) { at = hit.position; return true; }
            at = near;
            return false;
        }
    }

    /// <summary>What one merc perceives of the fight, kept by NpcWar twice a
    /// second on his owner's client (MercSenseTick). Steps 2 and 3 of the
    /// merc AI read it; it never allocates after the merc spawned.</summary>
    internal sealed class MercSense
    {
        internal const int MaxThreats = 4, Remembered = 6;
        internal const float Every = 0.5f;       // sense interval
        internal const float Memory = 12f;       // a hostile the scan weighed counts this long
        internal const float Reach = 420f;       // threats further than this are ignored (150 m)

        // The threats, primary first (his target), positions as sensed.
        internal readonly Vector3[] At = new Vector3[MaxThreats];
        internal readonly float[] Weight = new float[MaxThreats];
        internal readonly Transform[] Who = new Transform[MaxThreats];
        internal int Count;
        // The hostiles his target scan weighed lately (NpcWar.MercNoteThreats).
        internal readonly Transform[] Seen = new Transform[Remembered];
        internal readonly bool[] SeenPlayer = new bool[Remembered];
        internal readonly float[] SeenAt = new float[Remembered];

        internal int Turn;                       // M3: which of threats 3..4 is tested for exposure this tick
        internal bool Exposed;                   // a threat sees him where he is now
        internal float ExposedAt;                // when that was measured (M2 trusts it only fresh)
        internal bool RushMissed;                // x-merc-competence: the near search found nothing, next one full
        internal CoverPick Pick;                 // the best cover toward the threats
        internal float PickAt;
        internal Vector3 PickFrom, PickThreat;   // his spot and the primary threat when chosen
        internal Transform PickFor;              // the primary threat it was chosen against
        internal float NextSense, NextPick, NextWant;
        internal bool Listed;                    // in MercCoverService's list (overlay, status)

        internal void Note(Transform t, bool player, float now)
        {
            if (t == null) return;
            int free = -1, oldest = 0;
            for (int i = 0; i < Remembered; i++)
            {
                if (Seen[i] == t) { SeenAt[i] = now; SeenPlayer[i] = player; return; }
                if (free < 0 && Seen[i] == null) free = i;
                if (SeenAt[i] < SeenAt[oldest]) oldest = i;
            }
            int k = free >= 0 ? free : oldest;
            Seen[k] = t; SeenPlayer[k] = player; SeenAt[k] = now;
        }

        internal void Add(Transform t, float weight)
        {
            if (Count >= MaxThreats) return;
            Who[Count] = t; At[Count] = t.position; Weight[Count] = weight;
            Count++;
        }

        internal void Reset()
        {
            Count = 0; Exposed = false; Pick = new CoverPick(); PickFor = null;
            for (int i = 0; i < Remembered; i++) Seen[i] = null;
            for (int i = 0; i < MaxThreats; i++) Who[i] = null;
        }
    }

    /// <summary>The merc cover service of this client (see the file head).</summary>
    internal static class MercCoverService
    {
        internal const float Radius = 40f;       // Best: how far he runs for cover (about 14 m)
        internal const int Capacity = 64;        // cached cells

        internal static ConfigEntry<int> CfgRaysPerFrame;

        static CoverField _field;
        static PhysicsCoverWorld _world;
        internal static ICoverWorld World { get { return _world; } }
        static int _scene = int.MinValue;
        static int _queryFrame = -1;
        static float _nextStat;
        static int _callsAt;
        static int _callsPerSecond, _peakFrame, _peak;
        static string _status = "";
        static float _statusAt;
        static readonly List<MercUnit> _units = new List<MercUnit>(8);
        internal static List<MercUnit> Units { get { return _units; } }   // M2: F8 fight status
        internal static bool Show = false;               // F8: draw the cover points near the camera

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgRaysPerFrame = cfg.Bind("Mercs", "CoverRaysPerFrame", 24,
                "Merc cover perception: world probes (rays, ground and NavMesh samples) per frame spent "
                + "mapping cover around mercs who enter new ground (" + CoverField.CharacteriseCost
                + "..96). Nothing is spent once an area is mapped.");
        }

        static int Budget
        {
            get
            {
                int b = CfgRaysPerFrame == null ? 24 : CfgRaysPerFrame.Value;
                return Mathf.Clamp(b, CoverField.CharacteriseCost, 96);
            }
        }

        /// <summary>The field, made on first use.</summary>
        internal static CoverField Field
        {
            get
            {
                if (_field == null)
                {
                    _world = new PhysicsCoverWorld(RevivalPlugin.TypeByName("NPC_AI2"));
                    _field = new CoverField(_world, Capacity);
                }
                return _field;
            }
        }

        /// <summary>A merc is here: map the cells around him (once a second).</summary>
        internal static void Near(MercUnit u, Vector3 pos, float now)
        {
            if (!u.Sense.Listed) { u.Sense.Listed = true; _units.Add(u); }
            if (now < u.Sense.NextWant) return;
            u.Sense.NextWant = now + 1f;
            Field.Want(pos, now);
        }

        /// <summary>The merc is gone: his claim and his place in the list.</summary>
        internal static void Forget(MercUnit u)
        {
            if (u == null) return;
            if (_field != null) _field.Release(u.Id);
            if (u.Fight.Brain != null) u.Fight.Brain.Leave(null);
            MercTeam.Drop(u);
            u.Sense.Listed = false;
            u.Sense.Reset();
            _units.Remove(u);
        }

        /// <summary>One Best a frame for all mercs.</summary>
        internal static bool MayQuery()
        {
            if (_queryFrame == Time.frameCount) return false;
            _queryFrame = Time.frameCount;
            return true;
        }

        // --------------------------------------------------------- queries

        /// <summary>The best cover within radius of from toward the threats
        /// (threats[0] the primary), inside the leash (leashRadius 0: none),
        /// skipping points other mercs claimed.</summary>
        internal static bool Best(Vector3 from, Vector3[] threats, float[] weights, int count, float radius,
            Vector3 leash, float leashRadius, int who, out CoverPick pick)
        {
            return Field.Best(from, threats, weights, count, radius, leash, leashRadius, who, Time.time, out pick);
        }

        /// <summary>Can a man at the threat's position see one at pos
        /// (crouched or standing)? Two rays at most.</summary>
        internal static bool Exposed(Vector3 pos, bool crouched, Vector3 threat)
        {
            return Field.Exposed(pos, crouched, threat);
        }

        /// <summary>The side of a cover point that sees the threat
        /// (CoverPeek.None: none) and where he stands for it; verify costs
        /// one ray.</summary>
        internal static byte Peek(ref CoverPick pick, Vector3 threat, bool verify, out Vector3 at)
        {
            return Field.Peek(ref pick, threat, verify, out at);
        }

        // ------------------------------------------------------------ tick

        /// <summary>Per frame (RevivalPlugin.Update, F6 MercCover.Tick): the
        /// mapping budget and, once a second, the status line.</summary>
        internal static void Tick()
        {
            if (_field == null) return;
            float now = Time.time;
            if (_field.BuildingNow || _field.Queued > 0)
            {
                int spent = _field.Build(now, Budget);
                if (spent > _peakFrame) _peakFrame = spent;
            }
            if (now >= _nextStat)
            {
                _nextStat = now + 1f;
                // Another map: every cell and every collider kind is stale
                // (a Scene's hash code is its handle).
                int scene = SceneManager.GetActiveScene().GetHashCode();
                if (scene != _scene)
                {
                    if (_scene != int.MinValue) { _field.Clear(); _world.Forget(); }
                    _scene = scene;
                }
                _callsPerSecond = _field.Calls - _callsAt;
                _callsAt = _field.Calls;
                _peak = _peakFrame;
                _peakFrame = 0;
            }
        }

        static string BuildStatus()
        {
            int cells, points, rounds, crests;
            _field.Count(out cells, out points, out rounds, out crests);
            int exposed = 0, covered = 0;
            for (int i = 0; i < _units.Count; i++)
            {
                MercSense s = _units[i].Sense;
                if (s.Count > 0 && s.Exposed) exposed++;
                if (s.Pick.Found) covered++;
            }
            return cells + " cells, " + points + " points, " + rounds + " round, " + crests + " crest probes"
                + (_field.BuildingNow || _field.Queued > 0 ? ", mapping (" + _field.Queued + " queued)" : "")
                + " | " + _callsPerSecond + " calls/s, peak " + _peak + "/frame (budget " + Budget + ")"
                + " | " + _units.Count + " merc(s): " + exposed + " exposed, " + covered + " with cover"
                + " | queries " + _field.Queries + ", confirm fails " + _field.ConfirmFails + "/" + _field.Confirms;
        }

        /// <summary>F8: the line is rebuilt once a second while it is read.</summary>
        internal static string Status()
        {
            if (_field == null) return "idle (no merc has asked yet)";
            if (Time.time >= _statusAt) { _statusAt = Time.time + 1f; _status = BuildStatus(); }
            return _status;
        }

        // ------------------------------------------------------------ draw

        static GUIStyle _style;

        /// <summary>F8 "Show merc cover": the cached points near the camera
        /// (tall green, low yellow, a vehicle cyan; the letters are the peek
        /// sides), each merc's pick and peek spot and whether he is exposed.
        /// Nothing is drawn or allocated while it is off.</summary>
        internal static void Draw()
        {
            if (!Show || !Admin.IsOpen || _field == null || Event.current.type != EventType.Repaint) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_style == null) { _style = new GUIStyle(GUI.skin.label); _style.fontSize = 12; }
            Color was = GUI.color;
            Vector3 eye = cam.transform.position;
            int cx0 = CoverField.CellOf(eye.x), cz0 = CoverField.CellOf(eye.z);
            for (int cx = cx0 - 1; cx <= cx0 + 1; cx++)
                for (int cz = cz0 - 1; cz <= cz0 + 1; cz++)
                {
                    CoverCell c = _field.Cell(cx, cz);
                    if (c == null) continue;
                    for (int i = 0; i < c.Count; i++)
                    {
                        GUI.color = c.Points[i].Dynamic ? Color.cyan : c.Points[i].Tall ? Color.green : Color.yellow;
                        Mark(cam, c.Points[i].Pos, CoverPeek.Name(c.Points[i].Peek));
                    }
                    GUI.color = Color.green;
                    for (int i = 0; i < c.RoundCount; i++) Mark(cam, c.RoundAt[i], "o");
                    GUI.color = Color.white;
                    for (int i = 0; i < c.GroundCount; i++) Mark(cam, c.GroundAt[i], ".");
                }
            for (int i = 0; i < _units.Count; i++)
            {
                MercUnit u = _units[i];
                if (u.Ai == null) continue;
                MercSense s = u.Sense;
                if (s.Count > 0)
                {
                    GUI.color = s.Exposed ? Color.red : Color.green;
                    // M2: and what his fight loop is doing (HIDE, PEEK, BURST, ...).
                    Mark(cam, u.Ai.transform.position + Vector3.up * 6f,
                        (s.Exposed ? "EXPOSED " : "hidden ") + MercBrain.Name(u.Fight.State)
                        + (u.Fight.Brain != null && u.Fight.Brain.Mode != MercBrain.Normal
                            ? " " + MercBrain.ModeName(u.Fight.Brain.Mode) : ""));
                }
                if (!s.Pick.Found) continue;
                GUI.color = Color.magenta;
                Mark(cam, s.Pick.Point.Pos + Vector3.up * 1f, "COVER");
                if (s.Pick.PeekVerified) Mark(cam, s.Pick.PeekPos + Vector3.up * 4f, "PEEK");
            }
            GUI.color = was;
        }

        static void Mark(Camera cam, Vector3 world, string text)
        {
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f || s.z > 90f) return;
            GUI.Label(new Rect(s.x - 20f, Screen.height - s.y - 9f, 60f, 18f), text, _style);
        }
    }

    public static partial class NpcWar
    {
        /// <summary>M1: the hostiles a merc's target scan weighed are his
        /// threats for a while (PickTargetForMan's candidates).</summary>
        static void MercNoteThreats(MercUnit u, int n, float now)
        {
            for (int i = 0; i < n && i < _cand.Length; i++) u.Sense.Note(_cand[i], _candPlayer[i], now);
        }

        /// <summary>M1: a merc's cover perception, twice a second on his
        /// owner's client (RunGround, on foot): his threats, whether they see
        /// him, and the best cover toward them, claimed for him.</summary>
        static void MercSenseTick(Fighter f, MercUnit u, float now)
        {
            MercSense s = u.Sense;
            Vector3 me = f.Tr.position;
            MercCoverService.Near(u, me, now);
            // merc-combat-response: a target in sight while he knows no
            // threat is sensed at once, not at the next 0.35..0.5 s tick - the
            // brain can only answer a contact its sense lists.
            if (s.Count == 0 && f.Sees && f.Target != null && f.Target) s.NextSense = 0f;
            if (now < s.NextSense) return;
            // M3: a higher grade is told sooner (0.5 s up to grade 0.5, 0.35 s at 1).
            s.NextSense = now + MercBrain.SenseEvery(u.Grade) + (u.Id & 7) * 0.01f;
            // M3: what his mates fight, he knows (call-outs).
            MercTeam.Share(u, me, now);

            // The threats: his target first, then the others he weighed.
            s.Count = 0;
            if (f.Target != null && f.Target && now - f.LastSeen < MercSense.Memory)
                s.Add(f.Target, 1f);
            for (int i = 0; i < MercSense.Remembered && s.Count < MercSense.MaxThreats; i++)
            {
                Transform t = s.Seen[i];
                if (t == null) continue;
                if (now - s.SeenAt[i] > MercSense.Memory || !t) { s.Seen[i] = null; continue; }
                if (t == f.Target || Flat(t.position - me) > MercSense.Reach) continue;
                if (!s.SeenPlayer[i])
                {
                    Component c = _npcType == null ? null : t.GetComponent(_npcType);
                    if (c == null || !Alive(c)) { s.Seen[i] = null; continue; }
                }
                s.Add(t, 0.6f);
            }
            if (s.Count > 0) u.Approach = s.At[0];
            if (s.Count == 0 && (u.Order.Survive || u.Rally || Mercs.MedicineWanted(u, now)
                || MercCrewPhases.Ground(u)))
            {
                // A virtual approach ranks shelter, but is never a combat contact.
                s.At[0] = u.Approach; s.Weight[0] = 1f;
                s.Exposed = MercCoverService.Exposed(me, true, u.Approach);
                s.ExposedAt = now;
                if (now >= s.NextPick && MercCoverService.MayQuery())
                {
                    s.NextPick = now + 1.5f;
                    Vector3 quietFrom = u.Fight.Out.AnchorOn ? u.Fight.Out.Anchor : me;
                    CoverPick quiet;
                    bool bound = u.Rally && u.Fight.Out.AnchorOn;
                    Vector3 quietLeash = bound ? quietFrom : me;
                    float quietRadius = bound ? 24f : 60f;
                    if (MercCrewPhases.Ground(u)) MercLeash(f, u, out quietLeash, out quietRadius);
                    bool roof = MercRaidCover.Pick(u, quietFrom, now, out quiet);
                    if (u.RaidProbeDeferred) s.NextPick = now + 0.1f;
                    if (!roof)
                        MercCoverService.Best(quietFrom, s.At, s.Weight, 1, MercCoverService.Radius,
                            quietLeash, quietRadius, u.Id, out quiet);
                    else if (u.Fight.Holding && u.Fight.Brain != null
                        && (u.Fight.Brain.Cover.Point.Pos - quiet.Point.Pos).sqrMagnitude > 9f
                        && (u.Medicine == null || u.Medicine.Active == 0))
                    {
                        // Once mapped, a roof replaces open sandbags in a quiet
                        // raid; real contact still uses the normal fight loop.
                        u.Fight.Brain.Leave(MercCoverService.Field);
                        u.Fight.Out = default(FightOut);
                    }
                    s.Pick = quiet; s.PickAt = now; s.PickFrom = quietFrom; s.PickFor = null;
                }
                if (MercRaidCover.Sheltered(u, me, now)) s.Exposed = false;
                return;
            }
            if (s.Count == 0)
            {
                s.Exposed = false;
                if (s.Pick.Found && now - s.PickAt > 10f)
                {
                    s.Pick = new CoverPick(); s.PickFor = null;
                    MercCoverService.Field.Release(u.Id);
                }
                return;
            }

            // Seen where he is? The two nearest threats and (M3) one of the
            // others in turn, two rays each at most. merc-combat-response:
            // only a threat within MercThreat.SightUnits counts (and costs a
            // ray); a distant one with a geometric line is no reason to leave
            // a firing position - its hits still are.
            bool low = f.Crouched || f.InCover;
            int extra = s.Count > 2 ? 2 + (s.Turn++ % (s.Count - 2)) : -1;
            s.Exposed = MercSeenFrom(me, low, s.At[0])
                || (s.Count > 1 && MercSeenFrom(me, low, s.At[1]))
                || (extra > 0 && MercSeenFrom(me, low, s.At[extra]));
            s.ExposedAt = now;

            // The pick: kept while it fits, re-chosen when it went stale.
            if (now < s.NextPick) return;
            // M3: the brain may ask for cover searched from an anchor (a
            // flank, the fall-back on the owner, a retreat).
            Vector3 from = u.Fight.Out.AnchorOn ? u.Fight.Out.Anchor : me;
            bool stale = !s.Pick.Found || now - s.PickAt > 4f || s.PickFor != s.Who[0]
                || Flat(from - s.PickFrom) > 10f || Flat(s.At[0] - s.PickThreat) > 15f;
            if (!stale || !MercCoverService.MayQuery()) return;
            s.NextPick = now + 1.5f;
            Vector3 leash;
            float leashRadius;
            MercLeash(f, u, out leash, out leashRadius);
            CoverPick pick;
            // x-merc-competence: seen in the open, the NEAR cover first
            // (MercCompetence.Rush); nothing near: the full radius at once.
            bool rush = MercCompetence.Rush(s.Exposed, u.Fight.Holding, u.Fight.Out.AnchorOn, s.RushMissed);
            bool found = MercCoverService.Best(from, s.At, s.Weight, s.Count,
                rush ? MercCompetence.RushRadius : MercCoverService.Radius, leash, leashRadius, u.Id, out pick);
            s.RushMissed = rush && !found;
            if (s.RushMissed) s.NextPick = now + MercCompetence.RushRetry;
            // M2: a merc holding a cover point keeps his claim on it; the
            // fight loop (MercBrain) claims it and moves it when he moves.
            if (!u.Fight.Holding)
            {
                if (found) MercCoverService.Field.Claim(u.Id, pick.Point.Pos, now + 6f, now);
                else MercCoverService.Field.Release(u.Id);
            }
            s.Pick = pick; s.PickAt = now; s.PickFrom = from; s.PickThreat = s.At[0]; s.PickFor = s.Who[0];
        }

        /// <summary>Where his order lets him take cover: around the post or
        /// the stay point, near the owner he follows; a patrol anywhere.</summary>
        static void MercLeash(Fighter f, MercUnit u, out Vector3 centre, out float radius)
        {
            if (u.Order.Survive || u.Rally)
            {
                bool bound = u.Rally && u.Fight.Out.AnchorOn;
                centre = bound ? u.Fight.Out.Anchor : f.Tr.position;
                radius = bound ? 24f : 60f; return;
            }
            Flak.Gun gun = MercCrewPhases.Gun(u);
            if (MercCrewPhases.Ground(u) && gun != null)
            { centre = gun.Earthwork.position; radius = MercCrewPhase.PostRadius; return; }
            MercOrder o = u.Order;
            switch (o.Mode)
            {
                case MercOrder.Perimeter:
                    centre = o.Centre; radius = o.RadiusUnits + 40f; return;
                case MercOrder.Stay:
                    centre = o.Centre; radius = MercStayLeash; return;
                case MercOrder.Attack:
                    // merc-attack-orders: the corridor near him, or his hold circle.
                    MercAttackLeash(f, u, out centre, out radius); return;
                case MercOrder.Follow:
                case MercOrder.Vehicle:
                    if (u.Owner != null)
                    {
                        bool marksman = u.Fight.Overwatch.Role == MercRole.Marksman;
                        bool protectedMove = marksman && MercRoleProtected(u, Time.time);
                        centre = marksman && !protectedMove
                            ? MercRole.Slot(MercRole.Marksman, u.Owner.position, u.Owner.forward, u.Slot, u.Fight.Overwatch.Lane)
                            : u.Owner.position;
                        radius = marksman ? (protectedMove ? MercRole.Far : MercRole.CoverRadius) : MercBreakOffUnits;
                        return;
                    }
                    break;
            }
            centre = f.Tr.position; radius = 0f;
        }

    }
}
