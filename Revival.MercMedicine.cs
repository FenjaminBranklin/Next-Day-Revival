// Y S5: owner-side native medkit use and roster supply protocol.
using System;
using System.Globalization;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        static bool _medicineServer;

        // Cold: appended kit lines do not change the original m= name field.
        static void MedicineAnswer(string[] lines)
        {
            _medicineServer = false;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i] == "medicine=1") _medicineServer = true;
                if (!lines[i].StartsWith("kit=", StringComparison.Ordinal)) continue;
                string[] c = lines[i].Substring(4).Split('|');
                int id;
                if (c.Length != 4 || !int.TryParse(c[0], out id)) continue;
                Record r = Find(id);
                if (r != null && !r.Session) r.Medicine.Merge(c[1], c[2], c[3]);
            }
            // An old server cannot persist native 50% heals: fail closed.
            if (!_medicineServer)
                for (int i = 0; i < _roster.Count; i++)
                    if (!_roster[i].Session) _roster[i].Medicine.Known = false;
        }

        internal static bool MedicineWanted(MercUnit u, float now)
        {
            return u != null && u.Medicine != null && !u.Deserting && now >= u.NextSelfHeal
                && u.RescueTarget == null
                && u.Ride.Carrier == null && u.Ride.Boarding == null
                && u.Medicine.Wants(u.Fight.Health, now, u.LastHit.At);
        }

        internal static void MedicineAct(MercUnit u, FightOut act, float now)
        {
            FrameProf.S(FrameProf.S_MercMedicineT);
            try
            {
                MercMedicine m = u.Medicine;
                if (m == null) return;
                if (!m.Known)
                { if (m.Active != 0) { m.Cancel(); MercMedPose.Stop(u.Ai); } return; }
                if (act.HealStart && m.Begin(now))
                {
                    MercMedPose.Start(u.Ai, m.Active, now);
                    _nextState = 0f; // consumption saved even if healing is interrupted
                }
                if (act.HealNow && m.Active != 0)
                {
                    float share = m.Finish(now);
                    if (share > 0f)
                    {
                        float have = NpcWar.MercHealth(u.Ai);
                        if (have > 0f)
                        {
                            float to = Mathf.Min(1f, have + share);
                            SetHealth(u.Ai, to * u.MaxHealth); // native SetHealthValue RPC
                            u.Fight.Health = to; u.Fight.NextHealth = 0f;
                            Record r = RecordOf(u);
                            if (r != null) r.Hp = to;
                        }
                        MercMedPose.Stop(u.Ai);
                        _nextState = 0f;
                    }
                }
                if ((act.Act != FightAct.Heal || !m.Known) && m.Active != 0)
                { m.Cancel(); MercMedPose.Stop(u.Ai); }
            }
            finally { FrameProf.E(FrameProf.S_MercMedicineT); }
        }

        // Click/order/lifecycle only: immediate command always wins over healing.
        internal static void MedicineCancel(MercUnit u, float now)
        {
            if (u == null || u.Medicine == null) return;
            if (u.Medicine.Active == 0) return;
            u.Medicine.Cancel(); MercMedPose.Stop(u.Ai);
            if (u.Fight.Brain != null) u.Fight.Brain.Leave(MercCoverService.Field);
            u.Fight.Out = new FightOut(); u.NextSelfHeal = now + 1f;
            _nextState = 0f;
        }

        internal static bool CanGiveMedkit(Record r)
        {
            if (r != null && r.Down.Down) return CanRevive(r);
            if (r == null || r.Dead || r.Deserted || r.Unit == null || r.Unit.Ai == null
                || r.Unit.Deserting || !r.Medicine.Known || r.Medicine.GiftPending || r.Medicine.Count >= MercMedicine.Capacity
                || (!r.Session && (!_medicineServer || _support != 1)) || OwnerObject == null) return false;
            return (OwnerObject.transform.position - r.Unit.Ai.transform.position).sqrMagnitude <= 28f * 28f;
        }

        // Native RemoveItem synchronizes backpack/vest/weapon slots and checks
        // the count before/after. No inventory lookup in a frame or UI paint.
        internal static void GiveMedkit(Record r)
        {
            if (r != null && r.Down.Down) { PlayerRevive(r); return; }
            if (!CanGiveMedkit(r) || PlayerDead(OwnerObject)) return;
            int item = 0;
            for (int i = 7013; i <= 7016 && item == 0; i++)
                if (Turret.TakeItem(i, "Merc medkit gift")) item = i;
            for (int i = 7011; i <= 7012 && item == 0; i++)
                if (Turret.TakeItem(i, "Merc medkit gift")) item = i;
            if (item == 0)
            { MercUi.Toast(Loc.T("В рюкзаке нет аптечки.", "No medkit in your inventory."), true); return; }
            if (r.Session) { r.Medicine.Give(item); GiftReply(r); return; }
            r.Medicine.GiftPending = true;
            // Native inventory is owner-authored, like native HP. Ownership
            // and capacity are checked by the authenticated master roster.
            Enqueue("med-give", "id=" + N(r.Id) + "\nitem=" + N(item) + "\n", delegate(string result)
            {
                r.Medicine.GiftPending = false;
                if (result == "ok") GiftReply(r);
                if (result != "ok")
                {
                    RequestRoster(0f);
                    MercUi.Toast(Loc.T("Передача аптечки не подтверждена; проверьте запас.",
                        "Medkit transfer unconfirmed; check his supplies."), true);
                }
            }, 0f);
        }

        static void GiftReply(Record r)
        {
            MercUi.OrderReply(r.Name + Loc.T(": аптечка передана.", ": medkit given."), false);
        }

        static Record _medicineTarget;
        static float _medicineAimAt;
        static readonly RaycastHit[] MedicineHits = new RaycastHit[16];
        internal static Record MedicineTarget { get { return _medicineTarget; } }

        // Living and downed bodies share the existing authenticated gift/revive
        // path. Shift+E leaves the native E dialogue binding available.
        internal static void MedicineInteractionTick()
        {
            FrameProf.S(FrameProf.S_MercMedicineInteraction);
            try
            {
                if (!GameplayCursor.CanCommand || MercUi.ListOpen || MercRide.OwnerInVehicle
                    || OwnerObject == null || _roster.Count == 0)
                { _medicineTarget = null; return; }
                bool press = Input.GetKeyDown(KeyCode.E)
                    && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
                float now = Time.time;
                if (now < _medicineAimAt && !press) return;
                _medicineAimAt = now + 0.2f;
                _medicineTarget = MedicineAim();
                if (press && _medicineTarget != null) GiveMedkit(_medicineTarget);
            }
            finally { FrameProf.E(FrameProf.S_MercMedicineInteraction); }
        }

        static Record MedicineAim()
        {
            Camera cam = Camera.main;
            if (cam == null) return null;
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Record best = null;
            Vector3 point = Vector3.zero;
            float nearest = float.MaxValue;
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Dead || r.Deserted || r.Unit == null || r.Unit.Ai == null || r.Unit.Deserting) continue;
                Vector3 feet = r.Unit.Ai.transform.position;
                if ((OwnerObject.transform.position - feet).sqrMagnitude > 8.4f * 8.4f) continue;
                // Closest point on the body axis supports aiming at head, chest
                // or legs; downed bodies use their lowered torso height.
                Vector3 chest = feet + Vector3.up * (r.Down.Down ? 0.8f : 2.8f);
                Vector3 delta = chest - ray.origin;
                float along = Vector3.Dot(delta, ray.direction);
                if (along <= 0f || along >= nearest) continue;
                Vector3 onRay = ray.origin + ray.direction * along;
                float height = Mathf.Clamp(onRay.y - feet.y, r.Down.Down ? 0.3f : 0.5f,
                    r.Down.Down ? 1.4f : 4.8f);
                Vector3 body = feet + Vector3.up * height;
                if ((onRay - body).sqrMagnitude > 1.8f * 1.8f) continue;
                nearest = along; best = r; point = body;
            }
            if (best == null) return null;
            Vector3 sight = point - ray.origin;
            int n = Physics.RaycastNonAlloc(ray.origin, sight.normalized, MedicineHits, sight.magnitude,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (n == MedicineHits.Length) return null; // saturated: fail closed
            Transform own = OwnerObject.transform, merc = best.Unit.Ai.transform;
            for (int i = 0; i < n; i++)
            {
                Transform hit = MedicineHits[i].collider.transform;
                if (hit == own || hit.IsChildOf(own) || hit == merc || hit.IsChildOf(merc)) continue;
                return null;
            }
            return best;
        }

        static readonly string[] KitCounts = { "0/5", "1/5", "2/5", "3/5", "4/5", "5/5" };
        static readonly string[] KitLabelsRu = KitLabels("Аптечки ");
        static readonly string[] KitLabelsEn = KitLabels("Medkits ");
        static string[] KitLabels(string prefix)
        {
            string[] labels = new string[7];
            for (int i = 0; i < 6; i++) labels[i] = prefix + KitCounts[i];
            labels[6] = prefix + "?/5";
            return labels;
        }
        internal static string MedkitLabel(Record r)
        {
            if (r != null && r.Down.Down) return Loc.T("Оживить", "Revive");
            return Loc.T("Дать апт.", "Give kit");
        }
        internal static string MedkitStockLabel(Record r)
        {
            int n = r != null && r.Medicine.Known ? Mathf.Clamp(r.Medicine.Count, 0, 5) : 6;
            return Loc.T(KitLabelsRu[n], KitLabelsEn[n]);
        }
        internal static string MedkitCount(Record r)
        {
            return r.Medicine.Known ? KitCounts[Mathf.Clamp(r.Medicine.Count, 0, 5)] : "?/5";
        }
    }
}
