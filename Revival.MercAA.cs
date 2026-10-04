// W AA3: player-selected posts. Merc owners run M1/M2/M3; the Photon master
// grants short leases and alone lays/fires the guns. Existing event 199 is
// multiplexed: 2 = lease request, 3 = master snapshot (vehicle packets use 1).
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class MercAAPost
    {
        internal int Index, Actor = -1, View, Trait, Side = -1;
        internal Component Ai;
        internal NavMeshAgent Agent;
        internal float Until;
        internal string SideName;
        internal bool Parked, Peaceful;
        internal Animation[] Animations;
        internal Animator[] Animators;
        internal bool[] AnimationOn, AnimatorOn;
        internal Renderer[] WeaponRenderers;
        internal bool[] WeaponOn;
        // G C1: the seat owns the skeleton while parked (Revival.GunSeatPose.cs).
        internal Component Ik;
        internal object IkSolver;
        internal bool Seated;
        internal readonly PoseSwitchMeter Meter = new PoseSwitchMeter();
    }

    internal static class MercAA
    {
        internal const int Radar = 4;
        static readonly MercAAPost[] Posts = MakePosts();
        static readonly float[] Request = new float[5];
        static readonly float[] Snapshot = new float[1 + 46 * 5];
        static float _nextTick, _nextSend;
        static int _master = -1;
        static Type _viewType, _npcType;
        static MethodInfo _find;
        static PropertyInfo _id, _owner;
        static FieldInfo _ownerField;
        static readonly object[] FindArgs = new object[1];
        static PropertyInfo _masterProperty;
        static object _masterPlayer;
        static int _masterId;
        static PropertyInfo _localProperty;
        static object _localPlayer;
        static int _localId = -1;
        internal static bool Authority { get { return MasterActor() == LocalActor() && _localId >= 0; } }

        internal static int LocalActor()
        {
            if (_localProperty == null)
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) return -1;
                _localProperty = photon.GetProperty("player");
            }
            if (_localProperty == null) return -1;
            object player = _localProperty.GetValue(null, null);
            if (!ReferenceEquals(player, _localPlayer))
            {
                _localPlayer = player;
                PropertyInfo id = player == null ? null : player.GetType().GetProperty("ID");
                _localId = id == null ? -1 : Convert.ToInt32(id.GetValue(player, null));
            }
            return _localId;
        }

        internal static int MasterActor()
        {
            if (_masterProperty == null)
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) return -1;
                _masterProperty = photon.GetProperty("masterClient");
            }
            if (_masterProperty == null) return -1;
            object player = _masterProperty.GetValue(null, null);
            if (!ReferenceEquals(player, _masterPlayer))
            {
                _masterPlayer = player;
                PropertyInfo id = player == null ? null : player.GetType().GetProperty("ID");
                _masterId = id == null ? -1 : Convert.ToInt32(id.GetValue(player, null));
            }
            return _masterId;
        }

        static MercAAPost[] MakePosts()
        {
            MercAAPost[] p = new MercAAPost[46];
            for (int i = 0; i < p.Length; i++) { p[i] = new MercAAPost(); p[i].Index = i < 14 ? i : -1; }
            return p;
        }

        internal static MercAAPost Gun(int index) { return index >= 0 && index < 7 && index != Radar ? Held(index) ?? Held(index + 7) : null; }
        internal static MercAAPost Held(int index)
        {
            for (int i = 0; i < Posts.Length; i++) if (Posts[i].Index == index) return Live(i);
            return null;
        }
        static MercAAPost Slot(int index)
        {
            for (int i = 0; i < Posts.Length; i++) if (Posts[i].Index == index) return Posts[i];
            for (int i = 14; i < Posts.Length; i++) if (Posts[i].Actor < 0)
            { Posts[i].Index = index; return Posts[i]; }
            return null;
        }
        internal static MercAAPost Operator { get { return Live(Radar); } }
        static MercAAPost Live(int index)
        {
            MercAAPost p = Posts[index];
            return p.Ai != null && p.Until > Time.time ? p : null;
        }
        internal static bool IsOrder(MercOrder o) { return o.Mode == MercOrder.ManGun || o.Mode == MercOrder.ManRadar; }
        internal static bool IsVehicle(MercOrder o) { return o.Mode == MercOrder.ManGun && o.Facing.x < 0f; }
        internal static bool AvailableForOrder(int post) { return Available(post, LocalActor()); }

        static bool Look()
        {
            if (_find != null && _id != null && _npcType != null && (_owner != null || _ownerField != null)) return true;
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (_viewType == null || _npcType == null) return false;
            _find = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            _id = _viewType.GetProperty("viewID"); _owner = _viewType.GetProperty("ownerId");
            _ownerField = AccessTools.Field(_viewType, "ownerId");
            return _find != null && _id != null && (_owner != null || _ownerField != null);
        }

        internal static bool Pose(int post, out Vector3 at, out Quaternion rot)
        {
            at = Vector3.zero; rot = Quaternion.identity;
            if (post == Radar)
            {
                if (!TowerRadar.Built || TowerRadar.ConsoleRoot == null) return false;
                at = TowerRadar.OperatorSeat(true);
                rot = TowerRadar.ConsoleRoot.rotation * Quaternion.Euler(0f, 180f, 0f);
                return true;
            }
            if (post >= 100) return Mortar.MercPose(post, out at, out rot);
            Flak.Gun g = Flak.ByIndex(post >= 7 ? post - 7 : post);
            Transform seat = g == null ? null : post >= 7 ? g.SeatLoader : g.SeatGunner;
            if (seat == null) return false;
            at = seat.position - Vector3.up * (Flak.CfgSeatDrop == null ? 2.3f : Flak.CfgSeatDrop.Value);
            rot = g.Mount.rotation;
            return true;
        }

        static bool Available(int post, int actor)
        {
            if (post == Radar)
                return TowerRadar.Built && TowerRadar.Working && TowerRadar.OperatorActor < 0
                    && !RadarOperator.Alive;
            if (post >= 100) return Mortar.MercAvailable(post);
            Flak.Gun g = Flak.ByIndex(post >= 7 ? post - 7 : post);
            return g != null && AirDefenceDamage.Alive(g.Index) && g != Flak._manned && Time.time >= g.ClaimedUntil
                && !(post >= 7 ? Flak.Up(g.Loader) : Flak.Up(g.Gunner));
        }
        internal static bool CanApproach(int post, Component ai)
        {
            MercAAPost held = Held(post);
            return post >= 0 && Available(post, LocalActor()) && (held == null || held.Ai == ai);
        }

        // H M2: the living native crewman on this gun (or the radar operator)
        // whose faction the merc hates - the man he clears before he sits.
        internal static Component HostileCrew(int post, Component merc)
        {
            if (merc == null || post < 0 || post >= 7) return null;
            if (post == Radar)
            {
                Component man = RadarOperator.Man;
                return Flak.Up(man) && NpcWar.Hates(merc, man) ? man : null;
            }
            Flak.Gun g = Flak.ByIndex(post);
            if (g == null) return null;
            if (Flak.Up(g.Gunner) && NpcWar.Hates(merc, g.Gunner)) return g.Gunner;
            if (Flak.Up(g.Loader) && NpcWar.Hates(merc, g.Loader)) return g.Loader;
            return null;
        }

        internal static int Nearest(bool radar, Vector3 point)
        {
            int chosen = -1;
            float best = 60f * 60f;
            for (int i = radar ? Radar : 0; i < (radar ? Radar + 1 : Posts.Length); i++)
            {
                if (!radar && i == Radar) continue;
                Vector3 at; Quaternion rot;
                if (!Pose(i, out at, out rot)) continue;
                float d = (at - point).sqrMagnitude;
                if (d < best) { best = d; chosen = i; }
            }
            return chosen;
        }

        /// <summary>H M3: flat distance from a gun post's pit centre, -1 for
        /// the radar, a mortar or a gun without an earthwork.</summary>
        internal static float PitFlat(int post, Vector3 at)
        {
            if (post < 0 || post >= 14 || post == Radar || post == Radar + 7) return -1f;
            Flak.Gun g = Flak.ByIndex(post >= 7 ? post - 7 : post);
            if (g == null || g.Earthwork == null) return -1f;
            Vector3 d = at - g.Earthwork.position; d.y = 0f;
            return d.magnitude;
        }

        internal static int PostOf(MercUnit u)
        {
            if (!IsOrder(u.Order)) return -1;
            return IsVehicle(u.Order) ? -1 : Mathf.RoundToInt(u.Order.Facing.x) - 1;
        }

        internal static Component ViewComponent(Component ai)
        { return ai != null && Look() ? ai.GetComponent(_viewType) : null; }

        static System.Func<object, int> _readOwner;

        internal static bool Owns(Component view, int actor)
        {
            if (view == null || !Look()) return false;
            if (_ownerField != null) return FastField.GetInt(_ownerField, view) == actor;
            if (_readOwner == null)
            {
                // Property-only PUN versions use a typed delegate, bound once.
                System.Reflection.Emit.DynamicMethod dm = new System.Reflection.Emit.DynamicMethod(
                    "MercSupplyOwner", typeof(int), new Type[] { typeof(object) }, typeof(MercAA), true);
                System.Reflection.Emit.ILGenerator il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, _viewType);
                il.Emit(System.Reflection.Emit.OpCodes.Call, _owner.GetGetMethod(true));
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                _readOwner = (System.Func<object, int>)dm.CreateDelegate(typeof(System.Func<object, int>));
            }
            return _readOwner(view) == actor;
        }

        internal static int ViewOf(MercUnit u)
        {
            if (u.AAView == 0 && Look())
            {
                Component v = u.Ai.GetComponent(_viewType);
                if (v != null) u.AAView = Convert.ToInt32(_id.GetValue(v, null));
            }
            return u.AAView;
        }

        internal static void RequestPost(MercUnit u, int post)
        {
            if (u.AAView == 0 && Look())
            {
                Component v = u.Ai.GetComponent(_viewType);
                if (v != null) u.AAView = Convert.ToInt32(_id.GetValue(v, null));
            }
            if (u.AAView <= 0 || Time.time < u.AANextSend) return;
            u.AANextSend = Time.time + 0.5f;
            Request[0] = 2f; Request[1] = post; Request[2] = u.AAView;
            Request[3] = u.AAGunner; Request[4] = u.Peaceful ? 1f : 0f;
            if (Authority) OnPacket(Request, LocalActor());
            else MercRide.SendAAPacket(Request);
        }

        /// <summary>H M3 log: why this merc does not hold his post (the master's
        /// lease rules, in order). Allocates; called at most every 5 s.</summary>
        internal static string LeaseWhy(MercUnit u, int post)
        {
            if (u == null || u.Ai == null) return "merc not spawned";
            MercAAPost held = Held(post);
            if (held != null && held.Ai == u.Ai) return "seated";
            if (held != null) return "post held by actor " + held.Actor + " view " + held.View;
            if (post == Radar)
            {
                if (!TowerRadar.Built) return "radar not built";
                if (!TowerRadar.Working) return "radar not working";
                if (TowerRadar.OperatorActor >= 0) return "player actor " + TowerRadar.OperatorActor + " at the console";
                if (RadarOperator.Alive) return NpcWar.Hates(u.Ai, RadarOperator.Man) ? "enemy operator alive (to clear)" : "own-side NPC operator on the seat";
            }
            else if (post < 14)
            {
                Flak.Gun g = Flak.ByIndex(post >= 7 ? post - 7 : post);
                if (g == null) return "no gun with index " + post;
                if (!AirDefenceDamage.Alive(g.Index)) return "gun destroyed";
                if (g == Flak._manned) return "you sit at this gun";
                if (Time.time < g.ClaimedUntil) return "player actor " + g.ClaimActor + " at this gun";
                Component native = post >= 7 ? g.Loader : g.Gunner;
                if (Flak.Up(native)) return (NpcWar.Hates(u.Ai, native) ? "enemy" : "own-side") + " native crewman on the seat";
            }
            Vector3 at; Quaternion rot;
            if (!Pose(post, out at, out rot)) return "no seat pose";
            if (ViewOf(u) <= 0) return "no PhotonView id on the merc: no request sent";
            if (!Authority) return "lease asked of master actor " + MasterActor() + "; not in its snapshot yet";
            Component ai = Resolve(u.AAView, LocalActor());
            if (ai == null) return "view " + u.AAView + " does not resolve to a merc of actor " + LocalActor()
                + " (key " + Crew.GroundKey(u.Ai) + ")";
            Vector3 body = ai.transform.position;
            Vector3 d = body - at;
            if (!MercDefenceCore.LeaseNear(d.sqrMagnitude, PitFlat(post, body), d.y))
                return "too far for the lease: " + d.magnitude.ToString("0.0") + " u from the seat, pit "
                    + PitFlat(post, body).ToString("0.0") + " u";
            if (NpcWar.MercHealthForPost(ai) < 0.35f) return "health below 35 %";
            return "lease rules met; grant due on the next request";
        }

        internal static void Release(MercUnit u)
        {
            if (u.AAView <= 0) return;
            for (int i = 0; i < Posts.Length; i++)
            {
                MercAAPost p = Posts[i];
                if (p.Ai != u.Ai || p.Actor != LocalActor()) continue;
                Request[0] = 2f; Request[1] = -1; Request[2] = p.View; Request[3] = Request[4] = 0f;
                if (!Authority) MercRide.SendAAPacket(Request);
                Clear(p);
            }
        }

        static void Clear(MercAAPost p)
        {
            if (p.AnimationOn != null)
                for (int i = 0; i < p.Animations.Length; i++) if (p.Animations[i] != null) p.Animations[i].enabled = p.AnimationOn[i];
            if (p.AnimatorOn != null)
                for (int i = 0; i < p.Animators.Length; i++) if (p.Animators[i] != null) p.Animators[i].enabled = p.AnimatorOn[i];
            if (p.WeaponOn != null)
                for (int i = 0; i < p.WeaponRenderers.Length; i++) if (p.WeaponRenderers[i] != null) p.WeaponRenderers[i].enabled = p.WeaponOn[i];
            // H T2: the seated root lies under the cab floor; whoever leaves
            // the console (order, wound, death) is put back onto its floor.
            if (p.Parked && p.Index == Radar && p.Ai != null && TowerRadar.ConsoleRoot != null)
                p.Ai.transform.position = TowerRadar.OperatorSeat(false);
            if (p.Parked && p.Agent != null)
            {
                p.Agent.updatePosition = true; p.Agent.updateRotation = true;
                if (p.Agent.isActiveAndEnabled && p.Agent.isOnNavMesh && p.Ai != null)
                    p.Agent.Warp(p.Ai.transform.position);
            }
            p.Actor = -1; p.View = 0; p.Ai = null; p.Agent = null; p.Until = 0f; p.Parked = false; p.Side = -1;
            p.Animations = null; p.Animators = null; p.AnimationOn = p.AnimatorOn = null;
            p.WeaponRenderers = null; p.WeaponOn = null;
            p.Ik = null; p.IkSolver = null; p.Seated = false; p.Meter.Reset();
        }

        internal static Component Resolve(int view, int actor)
        {
            if (!Look()) return null;
            FindArgs[0] = view;
            Component v = _find.Invoke(null, FindArgs) as Component;
            if (v == null) return null;
            int owner = _ownerField != null ? FastField.GetInt(_ownerField, v) : Convert.ToInt32(_owner.GetValue(v, null));
            if (owner != actor) return null;
            Component ai = v.GetComponent(_npcType);
            string key = ai == null ? null : Crew.GroundKey(ai);
            return key != null && key.StartsWith(Mercs.KeyPrefix + actor + "/", StringComparison.Ordinal) ? ai : null;
        }

        internal static void OnPacket(float[] d, int sender)
        {
            if (d == null || d.Length < 1) return;
            for (int i = 0; i < d.Length; i++) if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return;
            if (d[0] == 2f && Authority && d.Length == 5)
            {
                int index = Mathf.RoundToInt(d[1]), view = Mathf.RoundToInt(d[2]);
                if (d[1] != index || d[2] != view || d[4] < 0f || d[4] > 1f) return;
                if (index == -1)
                {
                    for (int i = 0; i < Posts.Length; i++)
                        if (Posts[i].Actor == sender && Posts[i].View == view) Clear(Posts[i]);
                    return;
                }
                if (index < 0 || !Available(index, sender)) return;
                MercAAPost p = Slot(index);
                if (p == null) return;
                if (p.Until > Time.time && (p.Actor != sender || p.View != view)) return;
                Component ai = p.Actor == sender && p.View == view ? p.Ai : Resolve(view, sender);
                Vector3 at; Quaternion rot;
                if (!Flak.Up(ai) || !Pose(index, out at, out rot)) return;
                Vector3 body = ai.transform.position;
                if (!MercDefenceCore.LeaseNear((body - at).sqrMagnitude, PitFlat(index, body), body.y - at.y)) return;
                if (NpcWar.MercHealthForPost(ai) < 0.35f) return;
                if (index >= 100) Mortar.MercPrepare(index);
                for (int i = 0; i < Posts.Length; i++)
                    if (i != index && Posts[i].View == view && Posts[i].Actor == sender) Clear(Posts[i]);
                bool change = p.Ai != ai;
                p.Ai = ai; p.Actor = sender; p.View = view; p.Until = Time.time + 1.6f;
                // Owner AI supplies the profile trait, bounded like the existing merc traits.
                p.Trait = Mathf.Clamp(Mathf.RoundToInt(d[3]), 0, 50);
                p.Peaceful = d[4] != 0f;
                if (change) { p.Side = TowerRadar.PlayerSide(sender); p.SideName = Mercs.SideOf(p.Side); }
                if (change) p.Agent = GepardCrew.Agent(ai);
            }
            else if (d[0] == 6f && d.Length == Snapshot.Length && sender == MasterActor() && !Authority)
            {
                for (int i = 0; i < Posts.Length; i++)
                {
                    int o = 1 + i * 5;
                    int index = Mathf.RoundToInt(d[o]);
                    int actor = Mathf.RoundToInt(d[o + 1]), view = Mathf.RoundToInt(d[o + 2]);
                    MercAAPost p = Posts[i];
                    if (actor < 0 || view <= 0) { Clear(p); continue; }
                    if (p.Index != index || p.Actor != actor || p.View != view || p.Ai == null)
                    {
                        Clear(p); p.Index = index; p.Ai = Resolve(view, actor); p.Actor = actor; p.View = view;
                        if (p.Ai != null) p.Agent = GepardCrew.Agent(p.Ai);
                    }
                    MercUnit local = Mercs.UnitOf(p.Ai);
                    if (local != null && (!IsOrder(local.Order) || PostOf(local) != index || local.AARetreat
                        || MercCrewPhases.Ground(local)))
                    { Clear(p); continue; }
                    p.Peaceful = d[o + 3] < 0f;
                    p.Trait = Mathf.Clamp(Mathf.RoundToInt(p.Peaceful ? -d[o + 3] - 1f : d[o + 3]), 0, 50);
                    int side = Mathf.RoundToInt(d[o + 4]);
                    if (side != p.Side) { p.Side = side; p.SideName = Mercs.SideOf(side); }
                    p.Until = Time.time + 1.6f;
                }
            }
        }

        internal static void Tick()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.25f;
            if (!Mercs.Any && !_sent)
            {
                bool active = false;
                for (int i = 0; i < Posts.Length; i++) if (Posts[i].Actor >= 0) { active = true; break; }
                if (!active) return;
            }
            int master = MasterActor();
            if (master != _master)
            {
                _master = master;
                for (int i = 0; i < Posts.Length; i++) Clear(Posts[i]);
            }
            bool any = false;
            for (int i = 0; i < Posts.Length; i++)
            {
                MercAAPost p = Posts[i];
                if (p.Actor < 0) continue;
                if (p.Until < Time.time || p.Ai == null || (Authority && (!Available(p.Index, p.Actor) || !Flak.Up(p.Ai))))
                { Clear(p); continue; }
                any = true;
            }
            if (!Authority || Time.time < _nextSend) return;
            _nextSend = Time.time + 0.5f;
            Snapshot[0] = 6f;
            for (int i = 0; i < Posts.Length; i++)
            {
                int o = 1 + i * 5; MercAAPost p = Posts[i];
                Snapshot[o] = p.Index; Snapshot[o + 1] = p.Actor; Snapshot[o + 2] = p.View;
                Snapshot[o + 3] = p.Peaceful ? -p.Trait - 1 : p.Trait; Snapshot[o + 4] = p.Side;
            }
            // Send an empty snapshot after release too, but no work when no posts ever existed.
            if (any || _sent) { MercRide.SendAAPacket(Snapshot); _sent = any; }
        }
        static bool _sent;

        internal static bool Directed(Flak.Gun g)
        {
            return DirectionAvailable(g) && (g.Target == null || RadarShadow.Visible(g.Target.Go));
        }

        internal static bool DirectionAvailable(Flak.Gun g)
        {
            // W AA1: town crews always work by eye; an airfield gun's side is its
            // holder's (it changes on capture), a merc crew's its own.
            if (g.Town) return false;
            MercAAPost gun = Gun(g.Index), radar = Operator;
            int side = gun == null ? Flak.OwnerSide(g) : gun.Side;
            return TowerRadar.Working && ((radar != null && radar.Side == side)
                || (TowerRadar.Tier == 2 && TowerRadar.ControlSide == side));
        }

        internal static AACalibration Calibration(Flak.Gun g)
        {
            MercAAPost p = Gun(g.Index);
            bool radar = Directed(g);
            AACalibration c = MercAACore.Calibrate(p != null, radar, p == null ? 0 : p.Trait);
            // Preserve the existing tuning knobs, normalized around their shipped defaults.
            c.InitialMil *= (Flak.CfgInitialError == null ? 60f : Flak.CfgInitialError.Value) / 60f;
            c.FloorMil *= (Flak.CfgFloor == null ? 5f : Flak.CfgFloor.Value) / 5f;
            c.Walk = Mathf.Clamp01(c.Walk * (radar
                ? (Flak.CfgRadarWalk == null ? 0.45f : Flak.CfgRadarWalk.Value) / 0.45f
                : (Flak.CfgWalk == null ? 0.75f : Flak.CfgWalk.Value) / 0.75f));
            c.Reaction *= (Flak.CfgReaction == null ? 3f : Flak.CfgReaction.Value) / 3f;
            c.InitialMil *= Flak.DirError / (Flak.RadarDirected ? 0.55f : 1f);
            c.Reaction *= Flak.DirReaction / (Flak.RadarDirected ? 0.45f : 1f);
            return c;
        }

        internal static void LateFrame()
        {
            int animate = -1;
            if (Time.time >= _nextAnimation)
            {
                for (int n = 0; n < Posts.Length; n++)
                {
                    int index = (_animationTurn + n) % Posts.Length;
                    if (Live(index) != null) { animate = index; _animationTurn = (index + 1) % Posts.Length; break; }
                }
                _nextAnimation = Time.time + 0.02f;
            }
            int found = 0;
            for (int i = 0; i < Posts.Length; i++)
            {
                MercAAPost p = Live(i);
                if (p == null) continue;
                Vector3 at; Quaternion rot;
                if (!Pose(p.Index, out at, out rot)) continue;
                int foreign = 0;
                if (!p.Parked)
                {
                    if (p.Agent != null) { p.Agent.updatePosition = false; p.Agent.updateRotation = false; }
                    // Preserve a sampled pose between budgeted updates. Restore exact prior
                    // animator flags on exit; only the root follows the gun every frame.
                    p.Animations = p.Ai.GetComponentsInChildren<Animation>(true);
                    p.Animators = p.Ai.GetComponentsInChildren<Animator>(true);
                    p.AnimationOn = new bool[p.Animations.Length]; p.AnimatorOn = new bool[p.Animators.Length];
                    for (int k = 0; k < p.Animations.Length; k++) { p.AnimationOn[k] = p.Animations[k].enabled; p.Animations[k].enabled = false; }
                    for (int k = 0; k < p.Animators.Length; k++) { p.AnimatorOn[k] = p.Animators[k].enabled; p.Animators[k].enabled = false; }
                    Transform hand = TechnicalCrew.WeaponHand(p.Ai);
                    if (hand != null)
                    {
                        p.WeaponRenderers = hand.GetComponentsInChildren<Renderer>(true);
                        p.WeaponOn = new bool[p.WeaponRenderers.Length];
                        for (int k = 0; k < p.WeaponRenderers.Length; k++) p.WeaponOn[k] = p.WeaponRenderers[k].enabled;
                    }
                    p.Ik = GunSeatPose.AimIk(p.Ai); p.IkSolver = GunSeatPose.Solver(p.Ik);
                    GunSeatPose.StopGait(p.Ai);
                    p.Seated = false; p.Meter.Reset();
                    p.Parked = true;
                }
                else
                {
                    // G C1: nobody else poses a parked man. A writer that came
                    // back on since last frame is switched off again here.
                    for (int k = 0; k < p.Animations.Length; k++)
                        if (p.Animations[k] != null && p.Animations[k].enabled) { p.Animations[k].enabled = false; foreign |= GunSeatPoseCore.AnimationOn; }
                    for (int k = 0; k < p.Animators.Length; k++)
                        if (p.Animators[k] != null && p.Animators[k].enabled) { p.Animators[k].enabled = false; foreign |= GunSeatPoseCore.AnimatorOn; }
                }
                foreign |= GunSeatPose.Calm(p.Ik, p.IkSolver);
                found |= foreign;
                p.Ai.transform.position = at; p.Ai.transform.rotation = rot;
                if (p.WeaponRenderers != null)
                    for (int k = 0; k < p.WeaponRenderers.Length; k++) if (p.WeaponRenderers[k] != null) p.WeaponRenderers[k].enabled = false;
                // G R2: the radar operator sits on the console chair too.
                if (p.Index >= 100) continue;
                // Budgeted turn, plus at once after a foreign write and until
                // the first sample of this park landed.
                bool sampled = false;
                if (GunSeatPoseCore.Sample(i == animate, foreign, p.Seated))
                {
                    sampled = TechnicalCrew.Sitzen(p.Ai, p.Index >= 7 ? 1 : 0);
                    if (sampled) p.Seated = true;
                }
                // A foreign write the seat could not sample over (off screen)
                // is re-sampled on the first frame that can.
                if (foreign != 0 && !sampled) p.Seated = false;
                p.Meter.Record(GunSeatPoseCore.Shown(p.Seated, foreign, sampled), Time.time);
            }
            if (found != 0 || GunSeatPose.Reclaims != 0) GunSeatPose.Note(found);
        }

        /// <summary>G C1 proof: pose-class switches of a post in the last second.</summary>
        internal static int PoseSwitches(int post)
        {
            MercAAPost p = Held(post);
            return p == null ? -1 : p.Meter.PerSecond(Time.time);
        }
        static float _nextAnimation;
        static int _animationTurn;
    }

    public static partial class NpcWar
    {
        // Reuse the existing health reader without allocating a Fighter each renewal.
        static readonly Fighter _postHealth = new Fighter();
        internal static float MercHealthForPost(Component ai)
        {
            _postHealth.Ai = ai; return HealthFraction(_postHealth);
        }

        // H M2: NPC_AI2.IsEnemyFraction between two bodies, without allocation.
        internal static bool Hates(Component me, Component other)
        {
            return me != null && other != null && Hostile(GetHated(me), FactionOf(other));
        }

        // H M2: hold the enemy crewman as the target (one ray per half second
        // at most), fight him with the M2 brain, close on the gun without a line.
        static void MercClearPost(Fighter f, MercUnit u, Component occupant, Vector3 at, float now)
        {
            Transform tr = occupant.transform;
            if (f.Target != tr && now >= u.ClearAimAt)
            {
                u.ClearAimAt = now + 0.5f;
                float height;
                if (MercNpcTargetable(f, occupant) && AimPoint(f, tr, out height))
                { f.Target = tr; f.TargetIsPlayer = false; f.Sees = true; f.AimHeight = height; f.LastSeen = now; }
            }
            if (MercFight(f, u, now)) return;
            if ((at - f.Tr.position).sqrMagnitude > MercStationPlan.ClearReach * MercStationPlan.ClearReach)
                MercMove(f, u, at, true, now);
            else MercCrouch(f, now);
        }

        static void MercPostStep(Fighter f, MercUnit u, float now)
        {
            int post = MercAA.PostOf(u);
            Vector3 at; Quaternion rot;
            bool danger = MercDanger.Near(f.Tr.position, MercBrain.DangerRadius, now, out at);
            float hp = u.Fight.Health;
            if (hp < 0.35f) u.AARetreat = true;
            if (hp >= 0.5f) u.AARetreat = false;
            MercPostTrack t = u.DefTrack;
            if (t.Post != post || t.For != u.Order) t.Reset(post, u.Order, now);
            if (MercStationPlan.Retreat(hp, u.AARetreat, danger) || u.Deserting)
            {
                t.State = MercDefenceCore.SRetreat;
                MercAA.Release(u);
                if (u.Sense.Pick.Found) at = u.Sense.Pick.Point.Pos;
                else at = u.Owner == null ? f.Tr.position : u.Owner.position - u.Owner.forward * 8f;
                if (Flat(at - f.Tr.position) > 4f) MercMove(f, u, at, true, now);
                else MercCrouch(f, now);
                return;
            }
            if (MercCrewPhases.Ground(u)) { t.State = MercDefenceCore.SGround; MercCrewHold(f, u, now); return; }
            if (post < 0 || !MercAA.Pose(post, out at, out rot))
            { t.State = MercDefenceCore.SNoPose; MercAA.Release(u); MercFollow(f, u, now); return; }
            // H M2: an enemy crew on his gun is his target before the seat;
            // any other holder is waited out at the post, not where he stood.
            Component occupant = MercAA.HostileCrew(post, u.Ai);
            bool open = occupant == null && MercAA.CanApproach(post, u.Ai);
            int action = MercStationPlan.PostAction(occupant != null, open, !open && Mercs.AirDefenceManaged(u));
            if (action == MercStationPlan.PostClear)
            { t.State = MercDefenceCore.SClear; MercAA.Release(u); MercClearPost(f, u, occupant, at, now); return; }
            if (action == MercStationPlan.PostWait)
            {
                t.State = MercDefenceCore.SWait;
                MercAA.Release(u);
                if (MercFight(f, u, now)) return;
                if ((at - f.Tr.position).sqrMagnitude > MercStationPlan.WaitReach * MercStationPlan.WaitReach)
                    MercMove(f, u, at, true, now);
                else MercCrouch(f, now);
                return;
            }
            if (action == MercStationPlan.PostReplace)
            {
                t.State = MercDefenceCore.SReplace;
                MercAA.Release(u);
                if (!MercStations.Replace(u) && !MercFight(f, u, now)) MercCrouch(f, now);
                return;
            }
            MercAAPost held = MercAA.Held(post);
            if (held != null && held.Ai == u.Ai)
            {
                t.State = MercDefenceCore.SSeated; t.Seated = true;
                MercAA.RequestPost(u, post);
                f.Target = null; f.Sees = false; f.HasOrder = false;
                if (u.KillTargetSet != null) SetMercKillTarget(f, u, null);
                // G C1: the seat owns his pose. The order layer lowers the
                // rifle state (change-only, replicated) and drops its aim IK,
                // so neither the native controller nor DriveAim raises it.
                if (f.IkDriven) ReleaseAim(f);
                Drive(f, MainIdle, AddNone, PoseStand, now, true);
                return;
            }
            // H M3: arrival is flat reach plus a rise band (the seat pose lies
            // over the pad); a man stalled inside his pit walks in, one stuck
            // up on the tower goes down, one stalled on open ground re-paths.
            t.Seated = false;
            Vector3 me = f.Tr.position;
            float flat = Flat(at - me), rise = me.y - at.y;
            MercDefenceCore.Note(t, flat, rise, now);
            if (!MercDefenceCore.AtPost(flat, rise))
            {
                int rescue = MercDefenceCore.Rescue(t, now, MercAA.PitFlat(post, me), TowerRoof.ManUp(me));
                t.Rescue = rescue;
                if (rescue == MercDefenceCore.RescueDescend && TowerRoof.Descend(f.Tr, Agent(f)))
                { t.State = MercDefenceCore.SDescend; t.Rescues++; f.HasOrder = false; u.NextOrder = 0f; MercAA.Release(u); return; }
                if (rescue != MercDefenceCore.RescueWalkIn)
                {
                    if (rescue == MercDefenceCore.RescueRepath) { t.Rescues++; f.HasOrder = false; f.MoveDeadline = 0f; u.NextOrder = 0f; }
                    t.State = TowerRoof.ManUp(me) ? MercDefenceCore.SClimb : MercDefenceCore.SWalk;
                    MercAA.Release(u); MercStationApproach(f, u, at, now); return;
                }
                // Inside the pit and no closer: the lease seats him from here.
                if (t.State != MercDefenceCore.SWalkIn) t.Rescues++;
                t.State = MercDefenceCore.SWalkIn;
            }
            else t.State = MercDefenceCore.SRequest;
            t.Arrived = true;
            MercAA.RequestPost(u, post);
            f.Target = null; f.Sees = false; f.HasOrder = false;
            u.PlayerTarget = null;
            if (u.KillTargetSet != null) SetMercKillTarget(f, u, null);
            Hold(f, null, now);
        }
    }
}
