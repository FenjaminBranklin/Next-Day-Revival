// Z K7: owner-authoritative merc damage, server-confirmed rescue and persistence.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        static bool _downServer, _downHook;
        static string _downSession = Guid.NewGuid().ToString("N");
        static void DownInstall(Harmony h)
        {
            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            MethodInfo set = npc == null ? null : AccessTools.Method(npc, "SetHealthValue", null, null);
            if (set == null) { RevivalPlugin.L.LogWarning("Merc downed: health seam unavailable."); return; }
            h.Patch(set, new HarmonyMethod(typeof(Mercs).GetMethod("DownHealthPrefix")), null, null, null, null);
            string[] blocked = { "OnWoundedAction", "ApplyHitAnimations", "StartRegeneration", "StartRespawnTimer" };
            for (int i = 0; i < blocked.Length; i++)
            {
                MethodInfo method = AccessTools.Method(npc, blocked[i], null, null);
                if (method != null) h.Patch(method, new HarmonyMethod(typeof(Mercs).GetMethod("DownNativePrefix")), null, null, null, null);
            }
            _downHook = true;
        }
        public static bool DownNativePrefix(object __instance)
        {
            MercUnit u = UnitOf(__instance);
            return u == null ? !MercDownPose.Down(__instance as Component) : !IsDown(u);
        }
        internal static bool IsDown(MercUnit u)
        {
            Record r = RecordOf(u); return r != null && r.Down.Down;
        }
        static bool DownEligible(MercUnit u)
        {
            Record r = RecordOf(u);
            return _downHook && r != null && !r.Dead && (r.Session || _downServer);
        }
        // The native health seam follows adaptive damage/armor and precedes DeathAction.
        // Only the owner's UnitOf table can intercept it. Other clients get native HP.
        public static void DownHealthPrefix(object __instance, ref float __0)
        {
            MercUnit u = UnitOf(__instance);
            if (u == null || !DownEligible(u)) return;
            Record r = RecordOf(u);
            if (r.Down.Final) return;
            if (__0 <= 0f && !r.Down.Down)
            {
                EnterDown(r, Time.time);
                __0 = 1f; // alive native hit collider, no death/loot/reward yet
            }
            else if (r.Down.Down) __0 = 1f; // no passive/self-heal escape
        }
        static bool DownHit(MercUnit u, ref float damage)
        {
            Record r = RecordOf(u);
            if (r == null || !r.Down.Down || damage <= 0f) return false;
            FinalDown(r, Time.time);
            return true; // final native death already applied, never apply the hit twice
        }
        static void EnterDown(Record r, float now)
        {
            if (!r.Down.Enter(now)) return;
            MedicCancel(r);
            MercUnit u = r.Unit;
            MedicineCancel(u, now); RescueCancelFor(u, now); MercAA.Release(u); MercRide.Forget(u);
            NpcWar.MercDownHalt(u);
            if (u.Fight.Brain != null) u.Fight.Brain.Leave(MercCoverService.Field);
            u.Fight.Out = new FightOut(); u.PlayerTarget = null;
            r.Hp = 0f; r.Combat = true;
            MercDownPose.Set(u.Ai, true, now);
            r.HelpAt = now + 25f;
            HelpDown(r);
            r.DownConfirmed = r.Session;
            if (!r.Session)
                Enqueue("down", "id=" + N(r.Id) + "\n", delegate(string result)
                {
                    if (r.Dead) return;
                    if (result == "ok") r.DownConfirmed = true;
                    else { FinalDown(r, Time.time); RequestRoster(0f); }
                }, 0f);
        }
        static void HelpDown(Record r)
        {
            MercUi.OrderReply(r.Name + Loc.T(": ранен! Нужна аптечка, помогите!", ": down! Need a medkit, help!"), true);
        }
        static void DownAnswer(string[] lines)
        {
            _downServer = false;
            for (int i = 1; i < lines.Length; i++)
                if (lines[i] == "downed=1") _downServer = true;
            for (int i = 1; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("down=", StringComparison.Ordinal)) continue;
                string[] c = lines[i].Substring(5).Split('|'); int id, token;
                if (c.Length != 7 || !int.TryParse(c[0], out id) || !int.TryParse(c[5], out token)) continue;
                Record r = Find(id); if (r == null) continue;
                if (token > r.Down.Serial) r.Down.Serial = token;
                // A body restored from cache remains blocked until this session's get settles it.
            }
            // Down state is on the server. New sessions may not reuse a cached body.
            // Current local down transitions are not undone by older queued answers.
        }
        static void DownTick(float now, GameObject owner)
        {
            FrameProf.S(FrameProf.S_MercDownT);
            try
            {
                MercDownPose.Tick(now);
                for (int i = 0; i < _roster.Count; i++)
                {
                    Record r = _roster[i];
                    if (!r.Down.Down || r.Dead) continue;
                    if (owner == null || r.Down.Expired(now)) { FinalDown(r, now); continue; }
                    if (now >= r.HelpAt) { HelpDown(r); r.HelpAt = now + 25f; }
                    if (r.Unit == null || r.Unit.Ai == null) { FinalDown(r, now); continue; }
                    if (r.Down.Healing)
                    {
                        MercUnit helper = r.Down.Healer == 0 ? null : UnitFor(r.Down.Healer);
                        Transform from = helper == null || helper.Ai == null ? (r.Down.Healer == 0 ? owner.transform : null) : helper.Ai.transform;
                        if (from == null || (r.Down.Healer == 0 && PlayerDead(owner)) || (from.position - r.Unit.Ai.transform.position).sqrMagnitude > MercDownState.Reach * MercDownState.Reach
                            || (helper != null && !RescueSafe(helper, now))) { CancelRescue(r); continue; }
                        if (r.Down.Ready(now) && !r.Down.Pending) FinishRescue(r, now);
                        continue;
                    }
                    if (r.Down.Pending || !r.DownConfirmed) continue;
                    bool assigned = false;
                    for (int j = 0; j < _roster.Count; j++)
                        if (_roster[j].Unit != null && _roster[j].Unit.RescueTarget == r) { assigned = true; break; }
                    if (assigned) continue;
                    // Bounded roster only. No scene search, physics query or allocation.
                    MercUnit best = null; float near = 70f * 70f;
                    for (int j = 0; j < _roster.Count; j++)
                    {
                        Record h = _roster[j]; MercUnit u = h.Unit;
                        if (h == r || (!r.Session && h.Session) || h.Dead || h.Down.Down || u == null || u.Ai == null || u.RescueTarget != null
                            || IsMedic(u)
                            || !u.Medicine.Known || u.Medicine.Next == 0 || !RescueSafe(u, now)) continue;
                        float d = (u.Ai.transform.position - r.Unit.Ai.transform.position).sqrMagnitude;
                        if (d < near) { near = d; best = u; }
                    }
                    if (best != null) best.RescueTarget = r;
                }
            }
            finally { FrameProf.E(FrameProf.S_MercDownT); }
        }
        static MercUnit UnitFor(int id)
        {
            Record r = Find(id); return r == null || r.Dead || r.Down.Down ? null : r.Unit;
        }
        internal static bool RescueSafe(MercUnit u, float now)
        {
            Vector3 danger;
            return u != null && u.Ai != null && !IsDown(u) && !u.Deserting && u.Ride.Carrier == null && u.Ride.Boarding == null
                && u.Medicine.Active == 0 && u.Fight.Health >= 0.35f && now - u.LastHit.At >= 1.5f
                && now >= u.NextRescue && (!u.Fight.Sees || IsMedic(u))
                && (!IsMedic(u) || u.Fight.In.Suppression < .15f)
                && (u.Sense.Count == 0 || (now - u.Sense.ExposedAt < 1.5f && !u.Sense.Exposed))
                && !MercDanger.Near(u.Ai.transform.position, 20f, now, out danger);
        }
        internal static bool CanRevive(Record r)
        {
            return r != null && r.Down.Down && !r.Down.Final && !r.Down.Healing && !r.Down.Pending && r.DownConfirmed
                && r.Unit != null && r.Unit.Ai != null && _owner != null && !PlayerDead(_owner)
                && (_owner.transform.position - r.Unit.Ai.transform.position).sqrMagnitude <= MercDownState.Reach * MercDownState.Reach;
        }
        internal static void RescueCancelFor(MercUnit u, float now)
        {
            if (u == null) return;
            Record patient = RecordOf(u);
            if (patient != null && (patient.Aid.Active || patient.Aid.Pending)) MedicCancel(patient);
            if (u.RescueTarget != null) CancelRescue(u.RescueTarget);
            u.NextRescue = now + 5f; // execute new combat/move commands before optional aid duty
        }
        static void PlayerRevive(Record r)
        {
            if (!CanRevive(r)) return;
            int item = 0;
            for (int i = 7013; i <= 7016 && item == 0; i++) if (Turret.TakeItem(i, "Merc revive")) item = i;
            for (int i = 7011; i <= 7012 && item == 0; i++) if (Turret.TakeItem(i, "Merc revive")) item = i;
            if (item == 0) { MercUi.Toast(Loc.T("Нужна аптечка.", "A medkit is required."), true); return; }
            BeginRescue(r, null, item, Time.time);
        }
        internal static void TryMercRevive(MercUnit helper, Record r, float now)
        {
            Record medic = RecordOf(helper);
            if (medic != null && medic.MedicPending) return;
            if (r.Down.Pending || r.Down.Healing || !r.DownConfirmed || !RescueSafe(helper, now)) return;
            if ((helper.Ai.transform.position - r.Unit.Ai.transform.position).sqrMagnitude > MercDownState.Reach * MercDownState.Reach) return;
            int item = helper.Medicine.Next;
            if (!helper.Medicine.Begin(now)) return;
            helper.Medicine.Cancel(); // consumed, never credited as self healing
            _nextState = 0f;
            BeginRescue(r, helper, item, now);
        }
        static void BeginRescue(Record r, MercUnit helper, int item, float now)
        {
            r.Down.Pending = true;
            int ticket = ++r.Down.Serial;
            int healer = helper == null ? 0 : helper.Id;
            if (helper != null) helper.RescueTarget = r;
            Action<string> begin = delegate(string result)
            {
                if (ticket != r.Down.Serial) return;
                r.Down.Pending = false;
                if (result != "ok" || r.Dead || !r.Down.Begin(Time.time, item, healer)) { CancelRescue(r); return; }
                if (helper != null && IsMedic(helper)) r.Down.MedicTimer(MercMedicPolicy.ReviveSeconds);
                if (helper != null) MercMedPose.Start(helper.Ai, item, Time.time);
                MercUi.OrderReply(r.Name + Loc.T(": перевязка начата.", ": revival started."), false);
            };
            if (r.Session) begin("ok");
            else Enqueue("revive-start", "id=" + N(r.Id) + "\nhealer=" + N(healer) + "\nitem=" + N(item) + "\ntoken=" + N(ticket) + "\n", begin, 0f);
        }
        static void FinishRescue(Record r, float now)
        {
            r.Down.Pending = true;
            int ticket = r.Down.Serial;
            Action<string> finish = delegate(string result)
            {
                if (ticket != r.Down.Serial) return;
                r.Down.Pending = false;
                if (result != "ok" || r.Dead || !r.Down.Ready(Time.time)) { CancelRescue(r); return; }
                if (r.Unit == null || r.Unit.Ai == null) { FinalDown(r, Time.time); return; }
                // Recheck distance and combat after asynchronous server acknowledgement.
                MercUnit helper = r.Down.Healer == 0 ? null : UnitFor(r.Down.Healer);
                Transform from = helper == null || helper.Ai == null ? (r.Down.Healer == 0 && _owner != null ? _owner.transform : null) : helper.Ai.transform;
                if (from == null || (from.position - r.Unit.Ai.transform.position).sqrMagnitude > MercDownState.Reach * MercDownState.Reach
                    || (helper != null && !RescueSafe(helper, Time.time))) { FinalDown(r, Time.time); return; }
                float hp = MercMedicine.Share(r.Down.Item) * 100f;
                if (helper != null) { helper.RescueTarget = null; MercMedPose.Stop(helper.Ai); }
                if (!r.Down.Revive(Time.time)) return;
                MercDownPose.Set(r.Unit.Ai, false, Time.time);
                SetHealth(r.Unit.Ai, hp); r.Hp = hp / r.Unit.MaxHealth;
                r.Unit.Fight.Health = r.Hp; r.Unit.Fight.NextHealth = 0f;
                NpcWar.MercDownRecover(r.Unit);
                r.Unit.NextOrder = 0f; r.Unit.NextSelfHeal = Time.time + 1f;
                _nextState = 0f;
                MercUi.OrderReply(r.Name + Loc.T(": снова в строю!", ": back in action!"), false);
            };
            if (r.Session) finish("ok");
            else Enqueue("revive-finish", "id=" + N(r.Id) + "\ntoken=" + N(ticket) + "\n", finish, 0f);
        }
        static void CancelRescue(Record r)
        {
            MedicCancel(r);
            if (r.Down.Healing || r.Down.Pending)
            {
                if (!r.Session) Enqueue("revive-cancel", "id=" + N(r.Id) + "\ntoken=" + N(r.Down.Serial) + "\n", null, 0f);
                r.Down.Serial++;
            }
            MercUnit helper = r.Down.Healer == 0 ? null : UnitFor(r.Down.Healer);
            if (helper != null && helper.RescueTarget == r) { helper.RescueTarget = null; MercMedPose.Stop(helper.Ai); }
            // Also release an assigned helper still approaching / waiting for start.
            for (int i = 0; i < _roster.Count; i++)
            {
                MercUnit u = _roster[i].Unit;
                if (u != null && u.RescueTarget == r) { u.RescueTarget = null; MercMedPose.Stop(u.Ai); }
            }
            r.Down.Cancel();
        }
        static void FinalDown(Record r, float now)
        {
            if (r == null || r.Dead) return;
            CancelRescue(r); r.Down.Kill();
            if (r.Unit != null && r.Unit.Ai != null)
            { MercDownPose.Set(r.Unit.Ai, false, now); SetHealth(r.Unit.Ai, 0f); }
            if (r.Unit != null) OnDeath(r, now);
            else { r.Dead = true; r.DeadAt = now; EndContract(r, "died"); }
        }
        static void DownForget(Record r)
        {
            CancelRescue(r);
            if (r.Unit != null && r.Unit.Ai != null) MercDownPose.Set(r.Unit.Ai, false, Time.time);
        }
    }
    public static partial class NpcWar
    {
        internal static void MercDownHalt(MercUnit u)
        {
            Fighter f = MercOrderFighter(u); if (f == null) return;
            f.Target = null; f.Sees = false; f.TargetIsPlayer = false; f.HasOrder = false;
            Quiet(f, false); ReleaseAim(f);
            SetState(f, MainIdle, AddNone, PoseCrouch, -1, f.Tr.eulerAngles.y);
        }
        internal static void MercDownRecover(MercUnit u)
        {
            Fighter f = MercOrderFighter(u); if (f == null) return;
            f.NextState = 0f; f.HasOrder = false; f.MoveDeadline = 0f;
            f.NextScan = 0f; f.NextLos = 0f;
            u.GoalFor = null; u.GoalLeg = -1; u.Sense.NextSense = 0f;
            SetState(f, MainIdle, AddNone, PoseCrouch, -1, f.Tr.eulerAngles.y);
        }
        static bool MercRescueStep(Fighter f, MercUnit u, float now)
        {
            if (Mercs.MedicPatient(u, now))
            { Hold(f, null, now); Drive(f, MainIdle, AddNone, PoseCrouch, now, true); return true; }
            Mercs.Record r = u.RescueTarget;
            if (r == null) return false;
            if (Mercs.IsMedic(u)) return MercMedicStep(f, u, r, now);
            if (f.Sees && f.Target != null)
            { Mercs.RescueCancelFor(u, now); return false; } // weapon duty immediately wins on new contact
            if (r.Dead || !r.Down.Down || r.Unit == null || r.Unit.Ai == null || !Mercs.RescueSafe(u, now))
            { u.RescueTarget = null; return false; } // inherited fight/cover loop takes over immediately
            Vector3 at = r.Unit.Ai.transform.position;
            if ((f.Tr.position - at).sqrMagnitude > MercDownState.Reach * MercDownState.Reach)
            {
                // Use a fresh M1 shelter already sampled by the normal fight sense.
                CoverPick pick = u.Sense.Pick;
                Vector3 goal = at;
                if (pick.Found && pick.Confirmed && now - u.Sense.PickAt < 1.5f
                    && (pick.Point.Pos - f.Tr.position).sqrMagnitude > 9f
                    && (pick.Point.Pos - at).sqrMagnitude + 9f < (f.Tr.position - at).sqrMagnitude)
                    goal = pick.Point.Pos;
                MercMove(f, u, goal, true, now);
            }
            else
            {
                Hold(f, null, now);
                Drive(f, MainIdle, AddNone, PoseCrouch, now, true);
                Mercs.TryMercRevive(u, r, now);
            }
            return true;
        }
    }
}
