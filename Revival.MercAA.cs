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
        internal bool Parked;
        internal Animation[] Animations;
        internal Animator[] Animators;
        internal bool[] AnimationOn, AnimatorOn;
        internal Renderer[] WeaponRenderers;
        internal bool[] WeaponOn;
    }

    internal static class MercAA
    {
        internal const int Radar = 4;
        static readonly MercAAPost[] Posts = MakePosts();
        static readonly float[] Request = new float[5];
        static readonly float[] Snapshot = new float[1 + 7 * 4];
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

        static int LocalActor()
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
            MercAAPost[] p = new MercAAPost[7];
            for (int i = 0; i < p.Length; i++) { p[i] = new MercAAPost(); p[i].Index = i; }
            return p;
        }

        internal static MercAAPost Gun(int index) { return index >= 0 && index < Posts.Length && index != Radar ? Live(index) : null; }
        internal static MercAAPost Operator { get { return Live(Radar); } }
        static MercAAPost Live(int index)
        {
            MercAAPost p = Posts[index];
            return p.Ai != null && p.Until > Time.time ? p : null;
        }
        internal static bool IsOrder(MercOrder o) { return o.Mode == MercOrder.ManGun || o.Mode == MercOrder.ManRadar; }

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
                at = TowerRadar.ConsoleRoot.TransformPoint(new Vector3(0f, 0.02f, 0.85f) * Flak.K);
                rot = TowerRadar.ConsoleRoot.rotation;
                return true;
            }
            Flak.Gun g = Flak.ByIndex(post);
            if (g == null || g.SeatGunner == null) return false;
            at = g.SeatGunner.position - Vector3.up * (Flak.CfgSeatDrop == null ? 2.3f : Flak.CfgSeatDrop.Value);
            rot = g.Mount.rotation;
            return true;
        }

        static bool Available(int post, int actor)
        {
            if (post == Radar)
                return TowerRadar.Built && TowerRadar.Working && TowerRadar.OperatorActor < 0
                    && !RadarOperator.Alive;
            Flak.Gun g = Flak.ByIndex(post);
            return g != null && AirDefenceDamage.Alive(g.Index) && g != Flak._manned && Time.time >= g.ClaimedUntil
                && !Flak.Up(g.Gunner) && !Flak.Up(g.Loader);
        }
        internal static bool CanApproach(int post, Component ai)
        {
            MercAAPost held = post == Radar ? Operator : Gun(post);
            return post >= 0 && post < Posts.Length && Available(post, LocalActor()) && (held == null || held.Ai == ai);
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

        internal static int PostOf(MercUnit u)
        {
            if (!IsOrder(u.Order)) return -1;
            return Mathf.RoundToInt(u.Order.Facing.x) - 1;
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
            if (p.Parked && p.Agent != null)
            {
                p.Agent.updatePosition = true; p.Agent.updateRotation = true;
                if (p.Agent.isActiveAndEnabled && p.Agent.isOnNavMesh && p.Ai != null)
                    p.Agent.Warp(p.Ai.transform.position);
            }
            p.Actor = -1; p.View = 0; p.Ai = null; p.Agent = null; p.Until = 0f; p.Parked = false; p.Side = -1;
            p.Animations = null; p.Animators = null; p.AnimationOn = p.AnimatorOn = null;
            p.WeaponRenderers = null; p.WeaponOn = null;
        }

        static Component Resolve(int view, int actor)
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
                if (index == -1)
                {
                    for (int i = 0; i < Posts.Length; i++)
                        if (Posts[i].Actor == sender && Posts[i].View == view) Clear(Posts[i]);
                    return;
                }
                if (index < 0 || index >= Posts.Length || d[4] != 0f || !Available(index, sender)) return;
                MercAAPost p = Posts[index];
                if (p.Until > Time.time && (p.Actor != sender || p.View != view)) return;
                Component ai = p.Actor == sender && p.View == view ? p.Ai : Resolve(view, sender);
                Vector3 at; Quaternion rot;
                if (!Flak.Up(ai) || !Pose(index, out at, out rot) || (ai.transform.position - at).sqrMagnitude > 36f) return;
                if (NpcWar.MercHealthForPost(ai) < 0.35f) return;
                for (int i = 0; i < Posts.Length; i++)
                    if (i != index && Posts[i].View == view && Posts[i].Actor == sender) Clear(Posts[i]);
                bool change = p.Ai != ai;
                p.Ai = ai; p.Actor = sender; p.View = view; p.Until = Time.time + 1.6f;
                // Owner AI supplies the profile trait, bounded like the existing merc traits.
                p.Trait = Mathf.Clamp(Mathf.RoundToInt(d[3]), 0, 50);
                if (change) { p.Side = TowerRadar.PlayerSide(sender); p.SideName = Mercs.SideOf(p.Side); }
                if (change) p.Agent = GepardCrew.Agent(ai);
            }
            else if (d[0] == 3f && d.Length == Snapshot.Length && sender == MasterActor() && !Authority)
            {
                for (int i = 0; i < Posts.Length; i++)
                {
                    int o = 1 + i * 4;
                    int actor = Mathf.RoundToInt(d[o]), view = Mathf.RoundToInt(d[o + 1]);
                    MercAAPost p = Posts[i];
                    if (actor < 0 || view <= 0) { Clear(p); continue; }
                    if (p.Actor != actor || p.View != view || p.Ai == null)
                    {
                        Clear(p); p.Ai = Resolve(view, actor); p.Actor = actor; p.View = view;
                        if (p.Ai != null) p.Agent = GepardCrew.Agent(p.Ai);
                    }
                    MercUnit local = Mercs.UnitOf(p.Ai);
                    if (local != null && (!IsOrder(local.Order) || PostOf(local) != i || local.AARetreat
                        || local.Sense.Count > 0 || (local.Fight.Brain != null && local.Fight.Brain.Fighting)))
                    { Clear(p); continue; }
                    p.Trait = Mathf.Clamp(Mathf.RoundToInt(d[o + 2]), 0, 50);
                    int side = Mathf.RoundToInt(d[o + 3]);
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
                if (p.Until < Time.time || p.Ai == null || (Authority && (!Available(i, p.Actor) || !Flak.Up(p.Ai))))
                { Clear(p); continue; }
                any = true;
            }
            if (!Authority || Time.time < _nextSend) return;
            _nextSend = Time.time + 0.5f;
            Snapshot[0] = 3f;
            for (int i = 0; i < Posts.Length; i++)
            {
                int o = 1 + i * 4; MercAAPost p = Posts[i];
                Snapshot[o] = p.Actor; Snapshot[o + 1] = p.View;
                Snapshot[o + 2] = p.Trait; Snapshot[o + 3] = p.Side;
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
            for (int i = 0; i < Posts.Length; i++)
            {
                MercAAPost p = Live(i);
                if (p == null) continue;
                Vector3 at; Quaternion rot;
                if (!Pose(i, out at, out rot)) continue;
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
                    p.Parked = true;
                }
                p.Ai.transform.position = at; p.Ai.transform.rotation = rot;
                if (p.WeaponRenderers != null)
                    for (int k = 0; k < p.WeaponRenderers.Length; k++) if (p.WeaponRenderers[k] != null) p.WeaponRenderers[k].enabled = false;
                if (i == animate) TechnicalCrew.Sitzen(p.Ai, 0);
            }
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

        static void MercPostStep(Fighter f, MercUnit u, float now)
        {
            int post = MercAA.PostOf(u);
            Vector3 at; Quaternion rot;
            bool danger = MercDanger.Near(f.Tr.position, MercBrain.DangerRadius, now, out at);
            float hp = u.Fight.Health;
            if (hp < 0.35f) u.AARetreat = true;
            if (hp >= 0.5f) u.AARetreat = false;
            if (!MercAACore.Safe(hp, u.AARetreat, u.Sense.Count > 0, danger, (u.Fight.Brain != null && u.Fight.Brain.Fighting)) || u.Deserting || u.Peaceful)
            {
                MercAA.Release(u);
                if (u.Sense.Pick.Found) at = u.Sense.Pick.Point.Pos;
                else at = u.Owner == null ? f.Tr.position : u.Owner.position - u.Owner.forward * 8f;
                if (Flat(at - f.Tr.position) > 4f) MercMove(f, u, at, true, now);
                else MercCrouch(f, now);
                return;
            }
            if (post < 0 || !MercAA.Pose(post, out at, out rot)) { MercAA.Release(u); MercFollow(f, u, now); return; }
            if (!MercAA.CanApproach(post, u.Ai)) { MercAA.Release(u); MercFollow(f, u, now); return; }
            MercAAPost held = post == MercAA.Radar ? MercAA.Operator : MercAA.Gun(post);
            if (held != null && held.Ai == u.Ai)
            {
                MercAA.RequestPost(u, post);
                f.Target = null; f.Sees = false; f.HasOrder = false;
                if (u.KillTargetSet != null) SetMercKillTarget(f, u, null);
                return;
            }
            if ((at - f.Tr.position).sqrMagnitude > 16f) { MercAA.Release(u); MercMove(f, u, at, true, now); return; }
            MercAA.RequestPost(u, post);
            f.Target = null; f.Sees = false; f.HasOrder = false;
            u.PlayerTarget = null;
            if (u.KillTargetSet != null) SetMercKillTarget(f, u, null);
            Hold(f, null, now);
        }
    }
}
