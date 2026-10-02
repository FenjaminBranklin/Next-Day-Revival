// W AA7: fixed guns, antenna and console. No automatic repair or new channel.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirDefenceDamage
    {
        internal const int Radar = 7, Console = 8, Count = 9;
        internal const int BlastMsg = 10, StateMsg = 11, RepairMsg = 12;
        const float K = 2.8f, Reach = 3f * K;
        const string Progress = "aa-repair";
        static readonly AaDamageState[] States = Fresh();
        static readonly float[][] Packets = PacketsFor();
        static readonly DamageLook[] Looks = new DamageLook[Count];
        static readonly Vector4[] Booms = new Vector4[32];
        static int _boom, _near = -1, _job = -1, _jobRevision, _sendId, _scene = -1;
        static float _next, _nextSend, _nextPulse;
        static bool _master, _waitRelease;
        static bool _pressed;
        static string _prompt;

        static AaDamageState[] Fresh()
        {
            AaDamageState[] a = new AaDamageState[Count];
            for (int i = 0; i < a.Length; i++) a[i] = AaDamageCore.Fresh();
            return a;
        }
        static float[][] PacketsFor()
        {
            float[][] a = new float[Count + 2][];
            for (int i = 0; i < a.Length; i++) a[i] = new float[7];
            return a;
        }
        static Func<bool> _authority;
        static bool _authorityLooked;
        static bool Authority()
        {
            if (!_authorityLooked)
            {
                _authorityLooked = true;
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                MethodInfo get = photon == null ? null : AccessTools.PropertyGetter(photon, "isMasterClient");
                if (get == null && photon != null) get = AccessTools.PropertyGetter(photon, "IsMasterClient");
                if (get != null && get.ReturnType == typeof(bool))
                    _authority = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), get);
            }
            return _authority != null ? _authority() : Crocodile.IsMaster();
        }

        internal static string DestroyedPrompt { get { return Loc.T("Орудие уничтожено - нажмите R для ремонта", "Gun destroyed - tap R to repair"); } }
        internal static bool Repairing { get { return _job >= 0; } }
        internal static bool Alive(int id) { return id >= 0 && id < Count && States[id].Hp > 0f; }
        internal static float Hp(int id) { return id >= 0 && id < Count ? States[id].Hp : 1f; }
        internal static int Revision(int id) { return id >= 0 && id < Count ? States[id].Revision : -1; }
        internal static bool DepotRepairNeeded(int id)
        { return id >= 0 && id < Count && id != 4 && Root(id) != null && States[id].Hp < 1f && States[id].RepairActor < 0; }
        internal static bool DepotRepairPoint(int id, out Vector3 point)
        {
            Quaternion rotation;
            if (MercAA.Pose(id >= Radar ? MercAA.Radar : id, out point, out rotation)) return true;
            Transform root = Root(id); point = root == null ? Vector3.zero : root.position;
            return root != null;
        }
        // MercSupplyLedger already owns the finite toolkit, elapsed work and
        // authenticated carrier. Damage revisions prevent repairing a new hit.
        internal static bool DepotRepair(int id, int revision)
        {
            if (!Authority() || !DepotRepairNeeded(id) || revision != States[id].Revision) return false;
            States[id].Hp = 1f; States[id].Revision++; AaDamageCore.Cancel(ref States[id]);
            Apply(id); SendState(id); return true;
        }
        static float Seconds(int id) { return id < Radar ? AaDamageCore.GunSeconds : AaDamageCore.RadarSeconds; }

        static Transform Root(int id)
        {
            if (id == Radar) return TowerRadar.On && TowerRadar.Built ? TowerRadar.RadarRoot : null;
            if (id == Console) return TowerRadar.On && TowerRadar.Built ? TowerRadar.ConsoleRoot : null;
            Flak.Gun g = Flak.On ? Flak.ByIndex(id) : null;
            return g == null ? null : g.Root;
        }
        static Vector3 RepairPoint(int id, Transform root)
        {
            // Work at the trailer, rather than at the array eight metres up.
            return id == Console ? root.position : root.position + Vector3.up * K;
        }
        static bool Eligible(int id, int actor)
        {
            Transform t = Root(id);
            GameObject p = Crocodile.PlayerByActor(actor);
            return t != null && Crocodile.PlayerUp(p)
                && (p.transform.position - RepairPoint(id, t)).sqrMagnitude <= Reach * Reach;
        }

        internal static void Install(Harmony harmony)
        {
            Type t = RevivalPlugin.TypeByName("ExplosionObject");
            MethodInfo m = t == null ? null : AccessTools.Method(t, "NetworkVisualizeExplode", null, null);
            if (m == null) { RevivalPlugin.L.LogWarning("AA damage: native explosion hook missing."); return; }
            harmony.Patch(m, null, new HarmonyMethod(typeof(AirDefenceDamage).GetMethod("ExplodePostfix")), null, null, null);
        }
        public static void ExplodePostfix(object __instance)
        {
            // Native explosions are already networked; only the host bills them.
            if (!Authority()) return;
            Component c = __instance as Component;
            if (c == null) return;
            Vector3 at = c.transform.position;
            // Native explosions elsewhere in the world do not read radius data.
            bool near = (AmmoDepot.At - at).sqrMagnitude < 650f * 650f;
            for (int i = 0; i < Count; i++)
            {
                Transform root = Root(i);
                if (root != null && (root.position - at).sqrMagnitude < 650f * 650f) { near = true; break; }
            }
            if (near) Blast(at, RadarDamage.Radius(c), 1f);
        }

        internal static void ReportBlast(Vector3 at, float radius, float peak)
        {
            if (!Flak.On && !TowerRadar.On) return;
            if (Authority()) { Blast(at, radius, peak); return; }
            float[] p = Packets[Count];
            p[0] = BlastMsg; p[1] = at.x; p[2] = at.y; p[3] = at.z;
            p[4] = radius; p[5] = peak; p[6] = 0f;
            FlakNet.SendPacket(p, true);
        }
        static void Blast(Vector3 at, float radius, float peak)
        {
            if (!AaDamageCore.Finite(radius) || !AaDamageCore.Finite(peak)) return;
            radius = Mathf.Clamp(radius, 0.5f, 200f * K);
            peak = Mathf.Clamp(peak, 0f, 2f);
            // One bill for multiple native/sweep reports of the same impact.
            for (int i = 0; i < Booms.Length; i++)
            {
                Vector4 b = Booms[i];
                if (b.w > 0f && Time.time - b.w < 0.2f
                    && (at - new Vector3(b.x, b.y, b.z)).sqrMagnitude < 0.25f) return;
            }
            Booms[_boom] = new Vector4(at.x, at.y, at.z, Time.time);
            _boom = (_boom + 1) % Booms.Length;
            AmmoDepot.Blast(at, radius, peak);
            for (int id = 0; id < Radar; id++)
            {
                Transform root = Root(id);
                if (root == null) continue;
                Flak.Gun g = Flak.ByIndex(id);
                Vector3 mid = g.Cradle == null ? RepairPoint(id, root) : g.Cradle.position;
                float amount = AaDamageCore.Blast(Vector3.Distance(mid, at), radius, 2f * K, peak);
                if (amount > 0f) Hit(id, amount);
            }
            if (TowerRadar.On && TowerRadar.Built) RadarDamage.Blast(at, radius, peak);
        }

        internal static void RadarHit(int part, float amount)
        {
            int max = part == 0 ? TowerRadar.I(TowerRadar.CfgRadarHits, 30) : TowerRadar.I(TowerRadar.CfgConsoleHits, 12);
            Hit(part == 0 ? Radar : Console, amount / Mathf.Max(1, max));
        }
        static void Hit(int id, float amount)
        {
            if (!Authority() || Root(id) == null) return;
            if (!AaDamageCore.Hit(ref States[id], amount)) return;
            Apply(id);
            SendState(id);
        }
        static void Apply(int id)
        {
            if (id >= Radar) { TowerRadar.SetDamageHealth(States[Radar].Hp, States[Console].Hp); return; }
            Flak.Gun g = Flak.ByIndex(id);
            if (g == null || States[id].Hp > 0f) return;
            if (g == Flak._manned) FlakPlayer.Leave("the gun is destroyed");
            FlakFire.Release(g);
            g.Reloading = false; g.ClaimedUntil = -100f; g.ClaimActor = -1;
            g.Recoil = 0f; g.CaseDue = false;
            g.Seq = Mathf.Max(g.Seq, g.RemoteSeq); // no queued shots after repair
        }
        static void SendState(int id)
        {
            if (Root(id) == null) return;
            AaDamageState s = States[id];
            float[] p = Packets[id];
            p[0] = StateMsg; p[1] = id; p[2] = s.Hp; p[3] = s.Revision;
            p[4] = s.RepairActor; p[5] = s.Work; p[6] = 0f;
            FlakNet.SendPacket(p, true);
        }

        internal static void OnPacket(float[] p, int sender)
        {
            int kind = Mathf.RoundToInt(p[0]);
            bool master = Authority();
            if (kind == BlastMsg && master)
            {
                GameObject player = Crocodile.PlayerByActor(sender);
                Vector3 at = new Vector3(p[1], p[2], p[3]);
                // Mod projectiles may be fired remotely. Native tank/AT blasts
                // reach the host themselves; this path is for Mortar.Sweep.
                if (player != null && (player.transform.position - at).sqrMagnitude < 20000f * 20000f)
                    Blast(at, p[4], p[5]);
                return;
            }
            int id = Mathf.RoundToInt(p[1]);
            if (id < 0 || id >= Count || id == 4) return;
            if (kind == StateMsg && !master && sender == MercAA.MasterActor())
            {
                if (AaDamageCore.Snapshot(ref States[id], p[2], Mathf.RoundToInt(p[3]),
                    Mathf.RoundToInt(p[4]), p[5], Seconds(id))) Apply(id);
            }
            else if (kind == RepairMsg && master)
            {
                if (p[2] < 0.5f)
                {
                    if (States[id].RepairActor == sender) { AaDamageCore.Cancel(ref States[id]); SendState(id); }
                    return;
                }
                if (!Eligible(id, sender)) return;
                // One person works on one object at a time, even on a remote client.
                for (int i = 0; i < Count; i++)
                    if (i != id && States[i].RepairActor == sender) return;
                bool fixedPart = AaDamageCore.Pulse(ref States[id], sender, Mathf.RoundToInt(p[3]),
                    Time.time, Seconds(id), true);
                if (fixedPart) Apply(id);
                SendState(id);
            }
        }

        internal static void Reset(int first, int last)
        {
            for (int i = first; i <= last; i++) { States[i] = AaDamageCore.Fresh(); Looks[i] = null; }
            if (_job >= first && _job <= last) Stop();
            _near = -1; _prompt = null;
        }

        internal static void Tick()
        {
            try { TickInner(); }
            catch (Exception ex)
            {
                _waitRelease = true;
                Stop();
                RevivalPlugin.L.LogWarning("AA damage/repair: " + ex.Message);
            }
        }
        static void TickInner()
        {
            // No scans, inventory reads, queries or network sends per frame.
            if (_job >= 0 && !NativeActionProgress.IsActive(Progress)) { Stop(); return; }
            // Capture the edge every frame; the existing 2 Hz scan must not miss a tap.
            if (_job < 0 && RepairTap.CanStart && Input.GetKeyDown(KeyCode.R))
            { _pressed = true; _waitRelease = false; }
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;
            bool pressed = _pressed; _pressed = false;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            if (scene != _scene)
            {
                Reset(0, Count - 1); _scene = scene;
                ToolContainers.Clear(); _toolPlayer = null;
                for (int i = 0; i < Booms.Length; i++) Booms[i] = Vector4.zero;
            }
            bool master = Authority();
            if (master != _master)
            {
                for (int i = 0; i < Count; i++) AaDamageCore.Cancel(ref States[i]);
                Stop(); _master = master; _nextSend = 0f;
            }
            GameObject me = MapTools.LocalPlayer();
            int near = -1;
            float best = Reach * Reach;
            bool send = master && Time.time >= _nextSend;
            if (send)
            {
                _nextSend = Time.time + 0.5f;
                // Time-slice the late-join snapshots: one packet per tick,
                // all existing objects within 4.5 s, no burst of eight sends.
                for (int i = 0; i < Count; i++)
                {
                    int id = _sendId; _sendId = (_sendId + 1) % Count;
                    if (Root(id) != null) { SendState(id); break; }
                }
            }
            for (int id = 0; id < Count; id++)
            {
                Transform root = Root(id);
                if (root == null) continue;
                if (master && AaDamageCore.Expire(ref States[id], Time.time,
                    States[id].RepairActor < 0 || Eligible(id, States[id].RepairActor))) SendState(id);
                if (me == null) continue;
                float d = (me.transform.position - RepairPoint(id, root)).sqrMagnitude;
                if (d < 2000f * 2000f) Look(id, root);
                if (States[id].Hp < 1f && d <= best) { best = d; near = id; }
            }
            if (near >= 0 && (ConvoyRepair.InVehicle() || PlayerHeli.Aboard || PlayerAn2.Aboard
                || Flak._manned != null || RadarScope.InView)) near = -1;
            _near = near;
            if (_job >= 0)
            {
                if (_near != _job || !Crocodile.PlayerUp(me) || !Tools()
                    || States[_job].Revision != _jobRevision || States[_job].Hp >= 1f
                    || (States[_job].RepairActor >= 0 && States[_job].RepairActor != Crocodile.LocalActor()))
                { Stop(); return; }
                if (Time.time >= _nextPulse) { _nextPulse = Time.time + 0.5f; Repair(true); }
                return;
            }
            _prompt = near < 0 ? null : Loc.T("Нажмите R: ремонт (набор инструментов, 45/60 с); R ещё раз: отмена",
                "Tap R: repair (toolkit, gun 45 s / radar 60 s); R again: cancel");
            if (near < 0 || _waitRelease || !pressed || !RepairTap.CanStart || GameUi.WindowOpen
                || Flak._manned != null || RadarScope.InView || !Crocodile.PlayerUp(me)) return;
            ToolContainers.Clear(); // refresh ownership/containers once per new interaction
            if (!Tools()) { Turret.Hinweis(Loc.T("Нужен набор инструментов", "A toolkit is needed"), 2f); _waitRelease = true; return; }
            if (States[near].RepairActor >= 0 && States[near].RepairActor != Crocodile.LocalActor())
            { Turret.Hinweis(Loc.T("Уже ремонтируют", "Another player is repairing this"), 2f); _waitRelease = true; return; }
            if (!NativeActionProgress.Begin(Progress, Loc.T("Ремонт ПВО", "Repairing air defence"),
                Seconds(near), true, "repair", "vehicle_repair_01")) return;
            _job = near; _jobRevision = States[near].Revision; _nextPulse = Time.time + 0.5f;
            Repair(true); _prompt = null;
        }
        static void Repair(bool on)
        {
            if (_job < 0) return;
            float[] p = Packets[Count + 1];
            p[0] = RepairMsg; p[1] = _job; p[2] = on ? 1f : 0f; p[3] = _jobRevision;
            p[4] = p[5] = p[6] = 0f;
            if (Authority()) OnPacket(p, Crocodile.LocalActor());
            else FlakNet.SendPacket(p, true);
        }
        static void Stop()
        {
            if (_job < 0) return;
            Repair(false); NativeActionProgress.End(Progress);
            _job = -1; _waitRelease = true;
            _pressed = false;
        }
        internal static void Draw()
        {
            if (_prompt == null || _job >= 0 || _near < 0) return;
            VanillaUi.Prompt(_prompt, Screen.height * 0.68f);
        }

        // Inventory containers are discovered only on an interaction. Keep
        // their readers and arrays; no boxed ObscuredInt reads on repair ticks.
        delegate int ItemAt(Array a, int i);
        sealed class ToolData { internal object Inv; internal FieldInfo Data; internal FieldInfo Ids; internal ItemAt Read; }
        static readonly List<ToolData> ToolContainers = new List<ToolData>();
        static GameObject _toolPlayer;
        static bool Tools()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me != _toolPlayer || ToolContainers.Count == 0)
            {
                _toolPlayer = me; ToolContainers.Clear();
                List<object> inventories = Turret.PlayerInventories();
                for (int i = 0; i < inventories.Count; i++)
                {
                    AddTools(inventories[i], "_backpackData");
                    AddTools(inventories[i], "_gearsData");
                    AddTools(inventories[i], "_weaponsData");
                }
            }
            for (int i = 0; i < ToolContainers.Count; i++)
            {
                ToolData t = ToolContainers[i];
                Component c = t.Inv as Component;
                if (t.Inv is Component && (c == null || !c.gameObject.activeInHierarchy)) continue;
                object data = t.Data.GetValue(t.Inv);
                Array ids = data == null ? null : t.Ids.GetValue(data) as Array;
                if (ids == null) continue;
                for (int j = 0; j < ids.Length; j++)
                { int id = t.Read(ids, j); if (id == 10005 || id == ConvoyRepair.DEF_TOOLKIT) return true; }
            }
            return false;
        }
        static void AddTools(object inv, string name)
        {
            FieldInfo field = AccessTools.Field(inv.GetType(), name);
            object data = field == null ? null : field.GetValue(inv);
            FieldInfo ids = data == null ? null : AccessTools.Field(data.GetType(), "ItemID");
            Array array = ids == null ? null : ids.GetValue(data) as Array;
            if (array == null) return;
            Type element = array.GetType().GetElementType();
            MethodInfo cast = null;
            if (element != typeof(int))
            {
                MethodInfo[] methods = element.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    ParameterInfo[] args = methods[i].GetParameters();
                    if (methods[i].Name == "op_Implicit" && methods[i].ReturnType == typeof(int)
                        && args.Length == 1 && args[0].ParameterType == element) { cast = methods[i]; break; }
                }
            }
            if (element != typeof(int) && (cast == null || cast.ReturnType != typeof(int))) return;
            DynamicMethod dm = new DynamicMethod("AaToolSlot", typeof(int), new Type[] { typeof(Array), typeof(int) }, typeof(AirDefenceDamage), true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, array.GetType()); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldelem, element); if (cast != null) il.Emit(OpCodes.Call, cast); il.Emit(OpCodes.Ret);
            ToolData t = new ToolData(); t.Inv = inv; t.Data = field; t.Ids = ids;
            t.Read = (ItemAt)dm.CreateDelegate(typeof(ItemAt)); ToolContainers.Add(t);
        }

        sealed class DamageLook
        {
            internal Transform Root;
            internal Renderer[] Renderers;
            internal Material[][] Original, Burnt;
            internal bool Dead;
        }
        static Material _charred;
        static void Look(int id, Transform root)
        {
            bool dead = States[id].Hp <= 0f;
            if (id >= Radar && !TowerRadar.B(TowerRadar.CfgDamageLook)) dead = false;
            DamageLook l = Looks[id];
            if (l == null || l.Root != root)
            {
                if (!dead) return;
                if (_charred == null)
                { _charred = new Material(Shader.Find("Standard")); _charred.color = new Color(0.07f, 0.065f, 0.055f); }
                l = new DamageLook(); l.Root = root; l.Renderers = root.GetComponentsInChildren<Renderer>(true);
                l.Original = new Material[l.Renderers.Length][]; l.Burnt = new Material[l.Renderers.Length][];
                for (int i = 0; i < l.Renderers.Length; i++)
                {
                    l.Original[i] = l.Renderers[i].sharedMaterials;
                    l.Burnt[i] = new Material[l.Original[i].Length];
                    for (int j = 0; j < l.Burnt[i].Length; j++) l.Burnt[i][j] = _charred;
                }
                Looks[id] = l;
            }
            if (l.Dead == dead) return;
            l.Dead = dead;
            if (dead && id < Radar)
            {
                Flak.Gun g = Flak.ByIndex(id);
                if (g != null && g.Cradle != null) g.Cradle.localRotation = Quaternion.Euler(12f, 0f, 8f);
            }
            for (int i = 0; i < l.Renderers.Length; i++)
                if (l.Renderers[i] != null) l.Renderers[i].sharedMaterials = dead ? l.Burnt[i] : l.Original[i];
        }
    }
}
