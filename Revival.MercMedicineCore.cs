// Y S5: finite native medkits. Pure core, also compiled by the offline harness.
using System;
using System.Globalization;

namespace NextDayRevival
{
    internal sealed class MercMedicine
    {
        internal const int FirstId = 7011, Types = 6, Loadout = 3, Capacity = 5;
        internal const float Below = 0.55f, AfterHit = 4f;
        // Confirmed: UseItem iterator + items.tsv. Players heal at the END,
        // not over the animation. Amounts stay absolute HP on higher-HP mercs.
        internal static float Seconds(int id) { return id < 7013 ? 19.1f : 24f; }
        internal static float Share(int id) { return id == 7011 ? 0.25f : id == 7012 ? 0.30f : 0.50f; }
        internal static bool Item(int id) { return id >= FirstId && id < FirstId + Types; }
        internal readonly int[] Granted = new int[Types], Used = new int[Types], Completed = new int[Types];
        internal int Active;
        internal float Started, MaxHealth = 150f;
        internal bool Known, GiftPending;
        internal int Revision, SentRevision = -1;

        internal int Count
        {
            get { int n = 0; for (int i = 0; i < Types; i++) n += Math.Max(0, Granted[i] - Used[i]); return n; }
        }
        internal int Next
        {
            get
            {
                // Military first: enough HP to bring a retreating merc back.
                for (int i = 2; i < Types; i++) if (Granted[i] > Used[i]) return FirstId + i;
                for (int i = 0; i < 2; i++) if (Granted[i] > Used[i]) return FirstId + i;
                return 0;
            }
        }
        internal void SessionLoadout()
        {
            Known = true; Granted[2] = Loadout; Revision++;
        }
        internal void Refill()
        {
            Granted[2] += Math.Max(0, Loadout - Count); Revision++;
        }
        internal bool Give(int id)
        {
            if (!Item(id) || Count >= Capacity) return false;
            Granted[id - FirstId]++; Revision++; return true;
        }
        internal bool Begin(float now)
        {
            int id = Next;
            if (!Known || Active != 0 || id == 0) return false;
            Active = id; Started = now; Used[id - FirstId]++; Revision++; return true;
        }
        internal float Finish(float now)
        {
            if (Active == 0 || now - Started + 0.01f < Seconds(Active)) return 0f;
            int id = Active; Active = 0; Completed[id - FirstId]++; Revision++;
            return Share(id) * 100f / Math.Max(1f, MaxHealth);
        }
        internal void Cancel() { Active = 0; } // opened kit stays spent
        internal bool Wants(float hp, float now, float lastHit)
        {
            return Known && (Active != 0 || Next != 0) && hp > 0f && hp < Below && now - lastHit >= AfterHit;
        }
        // Cold protocol paths. Counters are monotonic: a stale answer cannot
        // return an opened kit or cancel a locally completed heal.
        internal void Merge(string grants, string uses, string done)
        {
            int[] g = Decode(grants), u = Decode(uses), c = Decode(done);
            if (g == null || u == null || c == null) return;
            for (int i = 0; i < Types; i++)
            {
                if (u[i] > g[i] || c[i] > u[i]) return;
            }
            for (int i = 0; i < Types; i++)
            {
                Granted[i] = Math.Max(Granted[i], g[i]);
                Used[i] = Math.Max(Used[i], u[i]);
                Completed[i] = Math.Max(Completed[i], c[i]);
            }
            Known = true;
        }
        internal static int[] Decode(string text)
        {
            string[] s = text.Split(',');
            if (s.Length != Types) return null;
            int[] n = new int[Types];
            for (int i = 0; i < Types; i++)
                if (!int.TryParse(s[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]) || n[i] < 0 || n[i] > 1000000) return null;
            return n;
        }
        internal static string Encode(int[] counts)
        {
            return string.Join(",", new string[] {
                counts[0].ToString(CultureInfo.InvariantCulture), counts[1].ToString(CultureInfo.InvariantCulture),
                counts[2].ToString(CultureInfo.InvariantCulture), counts[3].ToString(CultureInfo.InvariantCulture),
                counts[4].ToString(CultureInfo.InvariantCulture), counts[5].ToString(CultureInfo.InvariantCulture) });
        }
    }
}
