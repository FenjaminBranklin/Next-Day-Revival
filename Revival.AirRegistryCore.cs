// Air bridge AB1, the aircraft registry - the pure core (docs/ai/AIR_BRIDGE.md
// 4.2/4.5). It knows nothing of the game or of Unity, so
// research/air_registry_check.py compiles this file UNCHANGED with the .NET 3.5
// csc, together with the master-server module
// docs/ai/tasks/air-bridge/server/AirRegistry.cs, into a deterministic
// simulation of server, rooms and clients. Revival.AirRegistry.cs carries the
// decisions out (transport on storageId 7702, spawn, adopt, write).
//
//   TEXT     the wire format: one command per op (NDR-AIR-1 / seq / op /
//            key=value / c= cargo lines) and the answer parser into reused
//            records (AirBook). Parsing happens on an answer only.
//   PLAN     the room master's decision per aircraft: claim (rehydrate a
//            parked/flying aircraft of this region), spawn (MAIN only, when
//            the registry says none or destroyed and the delay passed), drop
//            (our instance is stale: another room owns it, or an admin reset
//            it), or nothing.
//   WRITES   when the room master writes a snapshot: on park/land, on damage
//            (parts), on cargo change (throttled), on fuel change while
//            parked, and as the lease renewal - never per frame.
//   LINK     one op in flight, polled and resent with the same seq (the
//            server drops duplicates and echoes the last result).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no LINQ. ASCII only.

using System;
using System.Globalization;
using System.Text;

namespace NextDayRevival
{
    /// <summary>One aircraft as the master server stores it.</summary>
    internal sealed class AirCraft
    {
        public const int MaxCargo = 600;

        public string Id = "", State = "none", Region = "", Room = "", Holder = "";
        public float X, Y, Z, Yaw, Fuel, Dmg, Wear;
        public int Parts = -1, Tok, Ver;
        public long Lease, Since;
        public string Transfer = "", Route = "", Phase = "";
        public float Speed;
        public float Power = 1, Throttle = .65f;
        public bool Running = true;
        public int PeopleCount;
        public readonly string[] People = new string[32];
        public readonly int[] Seats = new int[32];
        public readonly bool[] Ready = new bool[32], Seated = new bool[32];

        // Cargo: parallel arrays, reused; CargoCount entries are valid.
        public readonly int[] Slot = new int[MaxCargo], Item = new int[MaxCargo], Count = new int[MaxCargo], Clip = new int[MaxCargo];
        public readonly float[] Cond = new float[MaxCargo], Water = new float[MaxCargo], Energy = new float[MaxCargo];
        public int CargoCount;

        public bool Present { get { return State == "parked" || State == "flying"; } }

        public void Clear()
        {
            State = "none"; Region = ""; Room = ""; Holder = "";
            X = Y = Z = Yaw = Fuel = Dmg = Wear = 0f; Parts = -1; Tok = 0; Ver = 0; Lease = 0; Since = 0;
            CargoCount = 0;
            Transfer = Route = Phase = ""; Speed = 0; PeopleCount = 0;
            Power = 1; Throttle = .65f; Running = true;
        }
    }

    /// <summary>The registry as the last answer showed it.</summary>
    internal sealed class AirBook
    {
        public const string Magic = "NDR-AIR-1";
        public static readonly string[] Ids = { "an2", "mi8" };

        public int Seq, Rev;
        public string Result = "";
        public long Now, Delay, LeaseSeconds = 150;
        public readonly AirCraft[] Crafts = { new AirCraft(), new AirCraft() };
        AirBook _incoming;

        public AirBook()
        {
            for (int i = 0; i < Crafts.Length; i++) Crafts[i].Id = Ids[i];
        }

        public static int IndexOf(string id) { return Array.IndexOf(Ids, id); }

        public AirCraft Get(string id)
        {
            int i = IndexOf(id);
            return i < 0 ? null : Crafts[i];
        }

        /// <summary>Reads an answer into this book. False when the text is not
        /// a registry answer (the book is left as it was).</summary>
        public bool Parse(string text)
        {
            if (_incoming == null) _incoming = new AirBook();
            if (!_incoming.Read(text) || _incoming.Rev < Rev || _incoming.Seq < Seq) return false;
            Seq = _incoming.Seq; Rev = _incoming.Rev; Result = _incoming.Result;
            Now = _incoming.Now; Delay = _incoming.Delay; LeaseSeconds = _incoming.LeaseSeconds;
            for (int i = 0; i < Crafts.Length; i++)
            {
                AirCraft a = _incoming.Crafts[i], c = Crafts[i];
                c.State = a.State; c.Region = a.Region; c.Room = a.Room; c.Holder = a.Holder;
                c.X = a.X; c.Y = a.Y; c.Z = a.Z; c.Yaw = a.Yaw; c.Fuel = a.Fuel; c.Dmg = a.Dmg;
                c.Wear = a.Wear;
                c.Parts = a.Parts; c.Tok = a.Tok; c.Ver = a.Ver; c.Lease = a.Lease; c.Since = a.Since;
                c.Transfer = a.Transfer; c.Route = a.Route; c.Phase = a.Phase; c.Speed = a.Speed; c.PeopleCount = a.PeopleCount;
                c.Power = a.Power; c.Throttle = a.Throttle; c.Running = a.Running;
                Array.Copy(a.People, c.People, a.PeopleCount); Array.Copy(a.Seats, c.Seats, a.PeopleCount);
                Array.Copy(a.Ready, c.Ready, a.PeopleCount); Array.Copy(a.Seated, c.Seated, a.PeopleCount);
                c.CargoCount = a.CargoCount;
                Array.Copy(a.Slot, c.Slot, a.CargoCount); Array.Copy(a.Item, c.Item, a.CargoCount);
                Array.Copy(a.Count, c.Count, a.CargoCount); Array.Copy(a.Cond, c.Cond, a.CargoCount);
                Array.Copy(a.Clip, c.Clip, a.CargoCount); Array.Copy(a.Water, c.Water, a.CargoCount);
                Array.Copy(a.Energy, c.Energy, a.CargoCount);
            }
            return true;
        }

        bool Read(string text)
        {
            if (text == null || !text.StartsWith(Magic + "\n", StringComparison.Ordinal)) return false;
            CultureInfo ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < Crafts.Length; i++) { Crafts[i].Clear(); Crafts[i].Id = Ids[i]; }
            Result = ""; Seq = Rev = 0; Now = Delay = LeaseSeconds = 0;
            int head = 0, aircraft = 0;
            int at = Magic.Length + 1;
            while (at < text.Length)
            {
                int end = text.IndexOf('\n', at);
                if (end < 0) end = text.Length;
                int eq = text.IndexOf('=', at);
                if (eq > at && eq < end)
                {
                    string k = text.Substring(at, eq - at);
                    string v = text.Substring(eq + 1, end - eq - 1).TrimEnd('\r');
                    if (k == "seq") { if (!int.TryParse(v, NumberStyles.Integer, ci, out Seq) || Seq < 0) return false; head |= 1; }
                    else if (k == "result") { Result = v; if (v.Length == 0) return false; head |= 2; }
                    else if (k == "now") { if (!long.TryParse(v, NumberStyles.Integer, ci, out Now) || Now < 0) return false; head |= 4; }
                    else if (k == "rev") { if (!int.TryParse(v, NumberStyles.Integer, ci, out Rev) || Rev < 0) return false; head |= 8; }
                    else if (k == "delay") { if (!long.TryParse(v, NumberStyles.Integer, ci, out Delay) || Delay < 0) return false; head |= 16; }
                    else if (k == "lease") { if (!long.TryParse(v, NumberStyles.Integer, ci, out LeaseSeconds) || LeaseSeconds <= 0) return false; head |= 32; }
                    else if (k == "a")
                    {
                        int index = IndexOf(v.Split('|')[0]);
                        if (index < 0 || (aircraft & (1 << index)) != 0 || !ReadCraft(v, ci)) return false;
                        aircraft |= 1 << index;
                    }
                    else if (k == "c" && !ReadSlot(v, ci)) return false;
                    else if ((k == "t" || k == "p") && !ReadTravel(k, v, ci)) return false;
                }
                at = end + 1;
            }
            for (int i = 0; i < Crafts.Length; i++)
                if (Crafts[i].State == "in_transit" && (Crafts[i].Transfer.Length == 0 || Crafts[i].PeopleCount == 0)) return false;
            return head == 63 && aircraft == 3;
        }

        bool ReadCraft(string v, CultureInfo ci)
        {
            string[] p = v.Split('|');
            if (p.Length != 14 && p.Length != 15) return false;
            AirCraft c = Get(p[0]);
            if (c == null) return false;
            c.State = p[1]; c.Region = p[2];
            if (c.State != "none" && c.State != "parked" && c.State != "flying" && c.State != "destroyed" && c.State != "blocked" && c.State != "in_transit") return false;
            if ((c.Present || c.State == "in_transit") && c.Region != "MAIN" && c.Region != "PRIM" && c.Region != "SWAMP") return false;
            string[] xyz = p[3].Split(',');
            if (xyz.Length == 3)
            {
                if (!float.TryParse(xyz[0], NumberStyles.Float, ci, out c.X) || !(Math.Abs(c.X) < 1e6f)
                    || !float.TryParse(xyz[1], NumberStyles.Float, ci, out c.Y) || !(Math.Abs(c.Y) < 1e6f)
                    || !float.TryParse(xyz[2], NumberStyles.Float, ci, out c.Z) || !(Math.Abs(c.Z) < 1e6f)) return false;
            }
            else return false;
            if (!float.TryParse(p[4], NumberStyles.Float, ci, out c.Yaw) || float.IsNaN(c.Yaw) || float.IsInfinity(c.Yaw)
                || !float.TryParse(p[5], NumberStyles.Float, ci, out c.Fuel) || !(c.Fuel >= 0 && c.Fuel <= 1)
                || !int.TryParse(p[6], NumberStyles.Integer, ci, out c.Parts) || c.Parts < -1 || c.Parts > 0xFFFF
                || !float.TryParse(p[7], NumberStyles.Float, ci, out c.Dmg) || !(c.Dmg >= 0 && c.Dmg <= 1)
                || !int.TryParse(p[8], NumberStyles.Integer, ci, out c.Tok) || c.Tok < 0) return false;
            c.Room = p[9]; c.Holder = p[10];
            if (p.Length == 15 && (!float.TryParse(p[14], NumberStyles.Float, ci, out c.Wear) || !(c.Wear >= 0 && c.Wear <= 1))) return false;
            return long.TryParse(p[11], NumberStyles.Integer, ci, out c.Lease) && c.Lease >= 0
                && long.TryParse(p[12], NumberStyles.Integer, ci, out c.Since) && c.Since >= 0
                && int.TryParse(p[13], NumberStyles.Integer, ci, out c.Ver) && c.Ver >= 0;
        }

        bool ReadTravel(string key, string value, CultureInfo ci)
        {
            string[] v = value.Split('|');
            AirCraft c = (v.Length == 5 || (key == "t" && v.Length == 8)) ? Get(v[0]) : null;
            if (c == null) return false;
            if (key == "t")
            {
                if (c.Transfer.Length > 0 || v[1].Length != 32 || v[2].Length == 0
                    || (v[3] != "departed" && v[3] != "arriving" && v[3] != "confirmed" && v[3] != "recovered")
                    || !float.TryParse(v[4], NumberStyles.Float, ci, out c.Speed) || !(c.Speed >= 0 && c.Speed <= 1000)) return false;
                c.Transfer = v[1]; c.Route = v[2]; c.Phase = v[3];
                if (v.Length == 8)
                {
                    if (!float.TryParse(v[5], NumberStyles.Float, ci, out c.Power) || !(c.Power >= 0 && c.Power <= 1)
                        || (v[6] != "0" && v[6] != "1")
                        || !float.TryParse(v[7], NumberStyles.Float, ci, out c.Throttle) || !(c.Throttle >= 0 && c.Throttle <= 1)) return false;
                    c.Running = v[6] == "1";
                }
            }
            else
            {
                int n = c.PeopleCount, seat;
                if (n >= 32 || v[1].Length == 0 || !int.TryParse(v[2], out seat) || seat < -1 || seat >= (c.Id == "an2" ? 6 : 31)
                    || (v[3] != "0" && v[3] != "1") || (v[4] != "0" && v[4] != "1")) return false;
                for (int i = 0; i < n; i++) if (c.People[i] == v[1] || c.Seats[i] == seat) return false;
                c.People[n] = v[1]; c.Seats[n] = seat; c.Ready[n] = v[3] == "1"; c.Seated[n] = v[4] == "1";
                c.PeopleCount++;
            }
            return true;
        }

        bool ReadSlot(string v, CultureInfo ci)
        {
            string[] p = v.Split('|');
            if (p.Length != 5 && p.Length != 8) return false;
            AirCraft c = Get(p[0]);
            if (c == null || c.CargoCount >= AirCraft.MaxCargo) return false;
            int n = c.CargoCount;
            if (!int.TryParse(p[1], NumberStyles.Integer, ci, out c.Slot[n]) || c.Slot[n] < 0 || c.Slot[n] >= AirCraft.MaxCargo) return false;
            for (int i = 0; i < n; i++) if (c.Slot[i] == c.Slot[n]) return false;
            if (!int.TryParse(p[2], NumberStyles.Integer, ci, out c.Item[n]) || c.Item[n] <= 0
                || !int.TryParse(p[3], NumberStyles.Integer, ci, out c.Count[n]) || c.Count[n] < 0
                || !float.TryParse(p[4], NumberStyles.Float, ci, out c.Cond[n]) || !(c.Cond[n] >= 0 && c.Cond[n] <= 1e6f)) return false;
            c.Clip[n] = 0; c.Water[n] = c.Energy[n] = 0f;
            if (p.Length >= 8)
            {
                if (!int.TryParse(p[5], NumberStyles.Integer, ci, out c.Clip[n]) || c.Clip[n] < 0
                    || !float.TryParse(p[6], NumberStyles.Float, ci, out c.Water[n]) || !(c.Water[n] >= 0 && c.Water[n] <= 1e6f)
                    || !float.TryParse(p[7], NumberStyles.Float, ci, out c.Energy[n]) || !(c.Energy[n] >= 0 && c.Energy[n] <= 1e6f)) return false;
            }
            c.CargoCount = n + 1;
            return true;
        }

        /// <summary>The admin's "show": one line per aircraft.</summary>
        public string Describe(long now)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Crafts.Length; i++)
            {
                AirCraft c = Crafts[i];
                if (i > 0) sb.Append('\n');
                sb.Append(c.Id == "an2" ? "An-2: " : "Mi-8: ").Append(c.State);
                if (c.Present || c.State == "destroyed")
                {
                    if (c.Region.Length > 0) sb.Append(" in ").Append(c.Region);
                    sb.Append(" at ").Append(Mathf0(c.X)).Append(", ").Append(Mathf0(c.Z));
                }
                if (c.Present)
                {
                    sb.Append(", fuel ").Append(Mathf0(c.Fuel * 100f)).Append("%, damage ")
                      .Append(Mathf0(c.Dmg * 100f)).Append("%, cargo ").Append(c.CargoCount.ToString(CultureInfo.InvariantCulture))
                      .Append(", token ").Append(c.Tok.ToString(CultureInfo.InvariantCulture));
                    long left = c.Lease - now;
                    sb.Append(left > 0 ? ", held by room " + c.Room + " (" + left.ToString(CultureInfo.InvariantCulture) + " s)"
                                       : ", free (no room holds it)");
                }
                else if (c.State == "destroyed")
                {
                    long left = c.Since + Delay - now;
                    sb.Append(left > 0 ? ", respawn in " + (left / 60 + 1).ToString(CultureInfo.InvariantCulture) + " min"
                                       : ", respawn due at the airfield");
                }
                else sb.Append(c.State == "none" ? " - the airfield builds one" : " - registry blocked; admin reset all required");
            }
            return sb.ToString();
        }

        static string Mathf0(float f) { return ((int)Math.Round(f)).ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>Command texts. Building one allocates; it happens per op only.</summary>
    internal static class AirText
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        static StringBuilder Head(int seq, string op, string id)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(AirBook.Magic).Append('\n');
            sb.Append("seq=").Append(seq.ToString(CI)).Append('\n');
            sb.Append("op=").Append(op).Append('\n');
            if (id != null) sb.Append("id=").Append(id).Append('\n');
            return sb;
        }

        static void Pose(StringBuilder sb, float x, float y, float z, float yaw, float fuel01, int parts, float dmg01)
        {
            sb.Append("pos=").Append(x.ToString("0.##", CI)).Append(',').Append(y.ToString("0.##", CI)).Append(',')
              .Append(z.ToString("0.##", CI)).Append('\n');
            sb.Append("yaw=").Append(yaw.ToString("0.#", CI)).Append('\n');
            sb.Append("fuel=").Append(Clamp01(fuel01).ToString("0.####", CI)).Append('\n');
            sb.Append("parts=").Append(parts.ToString(CI)).Append('\n');
            sb.Append("dmg=").Append(Clamp01(dmg01).ToString("0.####", CI)).Append('\n');
        }

        public static string Claim(int seq, string id, string room, string region, int tok)
        {
            StringBuilder sb = Head(seq, "claim", id);
            sb.Append("room=").Append(AirPlan.SafeRoom(room)).Append('\n');
            sb.Append("region=").Append(region).Append('\n');
            sb.Append("tok=").Append(tok.ToString(CI)).Append('\n');
            return sb.ToString();
        }

        public static string Spawn(int seq, string id, string room, string region,
                                   float x, float y, float z, float yaw, float fuel01, int parts, float dmg01)
        {
            StringBuilder sb = Head(seq, "spawn", id);
            sb.Append("room=").Append(AirPlan.SafeRoom(room)).Append('\n');
            sb.Append("region=").Append(region).Append('\n');
            Pose(sb, x, y, z, yaw, fuel01, parts, dmg01);
            return sb.ToString();
        }

        /// <summary>A snapshot. <paramref name="cargoCount"/> &lt; 0: the cargo is not
        /// part of this write and stays as stored.</summary>
        public static string Snap(int seq, string id, int tok, string room, bool flying,
                                  float x, float y, float z, float yaw, float fuel01, int parts, float dmg01,
                                  int[] slot, int[] item, int[] count, float[] cond, int cargoCount)
        {
            return Snap(seq, id, tok, room, flying, x, y, z, yaw, fuel01, parts, dmg01,
                slot, item, count, cond, null, null, null, cargoCount);
        }

        public static string Snap(int seq, string id, int tok, string room, bool flying,
                                  float x, float y, float z, float yaw, float fuel01, int parts, float dmg01,
                                  int[] slot, int[] item, int[] count, float[] cond, int[] clip, float[] water, float[] energy, int cargoCount)
        {
            StringBuilder sb = Head(seq, "snap", id);
            sb.Append("tok=").Append(tok.ToString(CI)).Append('\n');
            sb.Append("room=").Append(AirPlan.SafeRoom(room)).Append('\n');
            sb.Append("state=").Append(flying ? "flying" : "parked").Append('\n');
            Pose(sb, x, y, z, yaw, fuel01, parts, dmg01);
            if (cargoCount >= 0)
            {
                sb.Append("cargo=1\n");
                int n = Math.Min(cargoCount, AirCraft.MaxCargo);
                for (int i = 0; i < n; i++)
                {
                    if (item[i] <= 0) continue;
                    sb.Append("c=").Append(slot[i].ToString(CI)).Append('|').Append(item[i].ToString(CI)).Append('|')
                      .Append(Math.Max(0, count[i]).ToString(CI)).Append('|')
                      .Append(Raw(cond[i]).ToString("0.###", CI)).Append('|')
                      .Append(clip == null ? 0 : Math.Max(0, clip[i])).Append('|')
                      .Append((water == null ? 0f : Raw(water[i])).ToString("0.###", CI)).Append('|')
                      .Append((energy == null ? 0f : Raw(energy[i])).ToString("0.###", CI)).Append('\n');
                }
            }
            return sb.ToString();
        }

        public static string Destroy(int seq, string id, int tok, string room, float x, float y, float z)
        {
            StringBuilder sb = Head(seq, "destroy", id);
            sb.Append("tok=").Append(tok.ToString(CI)).Append('\n');
            sb.Append("room=").Append(AirPlan.SafeRoom(room)).Append('\n');
            sb.Append("pos=").Append(x.ToString("0.##", CI)).Append(',').Append(y.ToString("0.##", CI)).Append(',')
              .Append(z.ToString("0.##", CI)).Append('\n');
            return sb.ToString();
        }

        /// <summary>id: an2, mi8 or all.</summary>
        public static string AdminReset(int seq, string id)
        {
            return Head(seq, "admin-reset", id).ToString();
        }

        static float Clamp01(float f) { return f < 0f || float.IsNaN(f) ? 0f : f > 1f ? 1f : f; }
        static float Raw(float f) { return f < 0f || float.IsNaN(f) || float.IsInfinity(f) ? 0f : f; }
    }

    /// <summary>What the room master does about one aircraft.</summary>
    internal enum AirAction { None, Claim, Spawn, Drop, Wait }

    internal static class AirPlan
    {
        public const long LeaseGuard = 5; // expire locally before another room can acquire the lease
        public static bool CanMigrate(AirCraft c)
        {
            return c != null && c.State == "none" && c.Since == 0 && c.Ver == 0;
        }
        /// <summary>The registry's region name of a game scene (GW_Scene_1/2/3);
        /// null: no registry region (underground, menus).</summary>
        public static string RegionOf(string scene)
        {
            if (scene == "GW_Scene_1") return "MAIN";
            if (scene == "GW_Scene_2") return "PRIM";
            if (scene == "GW_Scene_3") return "SWAMP";
            return null;
        }

        /// <summary>A room name as the server stores it (AirRegistry.Room).</summary>
        public static string SafeRoom(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch > ' ' && ch < 127 && ch != '|' && ch != '=' && sb.Length < 64) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <param name="c">the aircraft as the last answer showed it.</param>
        /// <param name="now">server clock (unix s), estimated.</param>
        /// <param name="delay">respawn delay of the server.</param>
        /// <param name="region">this room's registry region.</param>
        /// <param name="room">this room's safe name.</param>
        /// <param name="haveInstance">this room shows the aircraft (a tracked object).</param>
        /// <param name="myTok">the token that instance was built under.</param>
        /// <param name="canSpawn">this room can build at the MAIN airfield now.</param>
        public static AirAction Decide(AirCraft c, long now, long delay, string region, string room,
                                       bool haveInstance, int myTok, bool canSpawn)
        {
            if (c == null || region == null) return AirAction.None;
            if (haveInstance)
            {
                if (c.State == "in_transit" && c.Phase == "arriving" && c.Region == region && c.Room == room
                    && c.Lease > now + LeaseGuard && c.Tok == myTok) return AirAction.None;
                if (c.Present && c.Region == region && c.Room == room && c.Lease > now + LeaseGuard)
                {
                    if (myTok < 0) return AirAction.Claim; // adopted object needs an authenticated lease
                    if (c.Tok == myTok) return AirAction.None;
                }
                return AirAction.Drop;
            }
            if (c.Present)
            {
                if (c.Region != region) return AirAction.None;
                if (c.Lease > now && c.Room != room) return AirAction.Wait;
                return AirAction.Claim;
            }
            if (region != "MAIN" || (c.State != "none" && c.State != "destroyed")) return AirAction.None;
            if (c.State == "destroyed" && now < c.Since + delay) return AirAction.Wait;
            return canSpawn ? AirAction.Spawn : AirAction.Wait;
        }

        /// <summary>Damage 0..1 out of the fitted repair parts (bits of
        /// <paramref name="all"/>): the missing fraction.</summary>
        public static float DamageOf(int mask, int all)
        {
            if (mask < 0 || all <= 0) return 0f;
            int have = Bits(mask & all), total = Bits(all);
            return total == 0 ? 0f : 1f - (float)have / total;
        }

        /// <summary>The parts mask that carries at least <paramref name="dmg"/>
        /// damage: parts are taken off from the highest bit down (the ditch
        /// rule: an aircraft that came down unflown has lost parts).</summary>
        public static int PartsFor(int mask, int all, float dmg)
        {
            if (mask < 0 || all <= 0) return mask;
            mask &= all;
            for (int bit = 30; bit >= 0 && DamageOf(mask, all) + 1e-4f < dmg; bit--)
                if ((all & (1 << bit)) != 0) mask &= ~(1 << bit);
            return mask;
        }

        static int Bits(int v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }

        /// <summary>FNV-1a over a cargo list: a change test without keeping a
        /// copy of the hold. No allocation.</summary>
        public static int CargoHash(int[] slot, int[] item, int[] count, float[] cond, int n)
        {
            return CargoHash(slot, item, count, cond, null, null, null, n);
        }

        public static int CargoHash(int[] slot, int[] item, int[] count, float[] cond, int[] clip, float[] water, float[] energy, int n)
        {
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < n; i++)
                {
                    if (item[i] <= 0) continue;
                    h = (h ^ slot[i]) * 16777619;
                    h = (h ^ item[i]) * 16777619;
                    h = (h ^ count[i]) * 16777619;
                    h = (h ^ (int)(cond[i] * 1000f)) * 16777619;
                    h = (h ^ (clip == null ? 0 : clip[i])) * 16777619;
                    h = (h ^ (water == null ? 0 : (int)(water[i] * 1000f))) * 16777619;
                    h = (h ^ (energy == null ? 0 : (int)(energy[i] * 1000f))) * 16777619;
                }
                return h;
            }
        }
    }

    /// <summary>When the room master writes a snapshot. One instance per
    /// tracked aircraft; Due() is asked a few times a second at most.</summary>
    internal sealed class AirWriteGate
    {
        public const float MinGap = 3f;          // never two writes closer than this
        public const float CargoGap = 10f;       // cargo changes are batched
        public const float FlyingEvery = 30f;    // lease renewal + position in flight
        public const float ParkedEvery = 60f;    // lease renewal while parked (lease 150 s)
        public const float FuelStep = 0.02f;     // parked fuel change worth a write

        bool _primed, _flying;
        int _parts, _cargo;
        float _fuel, _wear, _last = -1e9f, _cargoChangedAt = -1f;
        public string Reason = "";

        /// <summary>The state the server holds now (after an accepted write,
        /// a claim or a spawn).</summary>
        public void Written(float now, bool flying, int parts, float fuel01, int cargoHash)
        { Written(now, flying, parts, fuel01, cargoHash, 0f); }

        public void Written(float now, bool flying, int parts, float fuel01, int cargoHash, float wear)
        {
            _primed = true; _flying = flying; _parts = parts; _fuel = fuel01; _cargo = cargoHash;
            _wear = wear;
            _last = now; _cargoChangedAt = -1f;
        }

        public void Reset() { _primed = false; _last = -1e9f; _cargoChangedAt = -1f; Reason = ""; }

        public bool Due(float now, bool flying, int parts, float fuel01, int cargoHash)
        { return Due(now, flying, parts, fuel01, cargoHash, 0f); }

        public bool Due(float now, bool flying, int parts, float fuel01, int cargoHash, float wear)
        {
            Reason = "";
            if (!_primed) { Reason = "first"; return true; }
            if (now - _last < MinGap) return false;
            if (flying != _flying) { Reason = flying ? "took off" : "parked"; return true; }
            if (parts != _parts || Math.Abs(wear - _wear) >= 0.001f) { Reason = "damage"; return true; }
            if (cargoHash != _cargo)
            {
                if (_cargoChangedAt < 0f) _cargoChangedAt = now;
                if (now - _cargoChangedAt >= CargoGap || now - _last >= CargoGap) { Reason = "cargo"; return true; }
            }
            if (!flying && Math.Abs(fuel01 - _fuel) >= FuelStep) { Reason = "fuel"; return true; }
            if (now - _last >= (flying ? FlyingEvery : ParkedEvery)) { Reason = "lease"; return true; }
            return false;
        }
    }

    /// <summary>One op in flight: send, poll, resend with the same seq, give
    /// up. The answer of a poll that does not yet echo our seq is still a
    /// fresh registry view.</summary>
    internal sealed class AirOpLink
    {
        public const float PollEvery = 2f;
        public const int PollsPerSend = 3;
        public const int Sends = 3;

        public string Text;
        public int Seq;
        public bool Busy { get { return Text != null; } }
        float _due;
        int _polls, _sends;

        public void Begin(string text, int seq, float now)
        {
            Text = text; Seq = seq; _polls = 0; _sends = 1; _due = now + PollEvery;
        }

        /// <summary>0 nothing, 1 poll again, 2 send the command again, 3 timed out.</summary>
        public int Step(float now)
        {
            if (Text == null || now < _due) return 0;
            _due = now + PollEvery;
            if (++_polls < PollsPerSend) return 1;
            _polls = 0;
            if (_sends >= Sends) { Text = null; return 3; }
            _sends++;
            return 2;
        }

        /// <summary>True when the answer settles our op (it echoes our seq or a later one).</summary>
        public bool Settles(int answerSeq) { return Text != null && answerSeq >= Seq; }

        public void Done() { Text = null; }
    }
}
