// Z M1: native owner inventory, master supply leases/stocks/live AA shots.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static class FlakAmmo
    {
        internal const int State = 13, Ask = 14, Grant = 15, Commit = 16, Trigger = 17, Result = 18, SnapshotAsk = 19, ReloadAsk = 20;
        static readonly float[] Packet = new float[9];
        static readonly Dictionary<int, int> FireSeen = new Dictionary<int, int>();
        static readonly Dictionary<int, float> SnapshotNext = new Dictionary<int, float>();
        // Warm once during AddItems/startup, never format a count during a raid.
        static readonly string[] CountsRu = CountLabels(" выстрелов осталось");
        static readonly string[] CountsEn = CountLabels(" rounds left");
        static float _next, _pendingUntil, _noticeUntil;
        static string _notice;
        static int _master = -1, _request, _pendingGun = -1, _pendingRequest, _trigger;
        static Flak.Gun _near;
        static GUIStyle _label;

        static string[] CountLabels(string suffix)
        {
            string[] labels = new string[FlakAmmoStock.Capacity(true) + 1];
            for (int i = 0; i < labels.Length; i++) labels[i] = i + suffix;
            return labels;
        }

        internal static void AddItems(List<ItemDef> items)
        {
            // Existing shell/crate assets are deliberately reused; these are
            // distinct native inventory goods, not virtual credits or clips.
            items.Add(new ItemDef(FlakAmmoStock.ShellItem, 2030, false,
                "85-мм снаряд 52-К (1)", "52-K 85 mm shell (1)",
                "Один снаряд для зенитки 52-К. У орудия нажмите L, чтобы пополнить запас.",
                "One shell for a 52-K AA gun. Press L at the gun to supply it.",
                "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
                "shell125_icon.png", null, 1, 0, 9.5f));
            items.Add(new ItemDef(FlakAmmoStock.BeltItem, 2030, false,
                "Лента ЗУ-23 (100)", "ZU-23 belt box (100)",
                "Коробка на 100 выстрелов для ЗУ-23. У орудия нажмите L, чтобы пополнить запас.",
                "A 100-round belt box for the ZU-23. Press L at the gun to supply it.",
                "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png",
                "mgbelt_icon.png", null, 100, 0, 28f));
        }

        internal static void Reset()
        {
            _master = -1; _next = 0f; _near = null; _pendingGun = -1;
            _notice = null; FireSeen.Clear(); SnapshotNext.Clear();
        }

        static void Notice(string text) { _notice = text; _noticeUntil = Time.time + 4f; }

        internal static void Observe(Flak.Gun g)
        {
            if (!Crocodile.IsMaster()) return;
            bool limited = Flak.PlayerAt(g) || MercAA.Gun(g.Index) != null
                || MercAA.Held(g.Index + 7) != null || (!Flak.Up(g.Gunner) && !Flak.Up(g.Loader));
            if (!g.Ammo.Mode(limited, true)) return;
            // Never carry an unlimited garrison rack into player ownership.
            g.Rounds = g.Ammo.Rack(g.ShortRange ? ShortRangeCore.Magazine : Flak.RoundsPerLoad);
            g.Reloading = false;
        }

        internal static bool Ready(Flak.Gun g) { return g.Ammo.CanShoot; }
        internal static void RequestReload(Flak.Gun g)
        {
            if (g != Flak._manned || !Ready(g) || g.Reloading || Time.time < g.AmmoNextReload) return;
            g.AmmoNextReload = Time.time + 0.5f;
            Fill(ReloadAsk, g.Index, 0f, 0f, 0f, 0f, MercAA.MasterActor());
            FlakNet.SendPacket(Packet, true);
        }
        internal static int DisplayRounds(Flak.Gun g)
        { return !g.Ammo.Known ? -2 : g.Ammo.Limited ? g.Ammo.Stock : -1; }

        internal static bool PlayerAllowed(Flak.Gun g, int actor)
        {
            if (!AirDefenceDamage.Alive(g.Index) || Flak.Up(g.Gunner) || Flak.Up(g.Loader)
                || MercAA.Gun(g.Index) != null || MercAA.Held(g.Index + 7) != null) return false;
            if (Flak._manned == g && actor != Crocodile.LocalActor()) return false;
            if (Time.time < g.ClaimedUntil && g.ClaimActor != actor) return false;
            return Near(g, actor);
        }

        static bool Near(Flak.Gun g, int actor)
        {
            GameObject p = Crocodile.PlayerByActor(actor);
            Transform seat = g.SeatGunner != null ? g.SeatGunner : g.Root;
            return seat != null && Crocodile.PlayerUp(p)
                && (p.transform.position - seat.position).sqrMagnitude <= 10f * 10f;
        }

        internal static bool MaySupply(Flak.Gun g, int actor)
        {
            if (!g.Ammo.Known || !g.Ammo.Limited || !AirDefenceDamage.Alive(g.Index) || !Near(g, actor)) return false;
            MercAAPost merc = MercAA.Gun(g.Index);
            if (merc == null) merc = MercAA.Held(g.Index + 7);
            if (merc != null && merc.Actor != actor && merc.Side != TowerRadar.PlayerSide(actor)) return false;
            int man = g == Flak._manned ? Crocodile.LocalActor() : Time.time < g.ClaimedUntil ? g.ClaimActor : -1;
            return man < 0 || man == actor || TowerRadar.PlayerSide(man) == TowerRadar.PlayerSide(actor);
        }

        internal static bool BeginFire(Flak.Gun g, bool player)
        {
            if (!Crocodile.IsMaster())
            {
                if (player && Ready(g) && g.Rounds > 0 && !g.Reloading)
                {
                    Fill(Trigger, g.Index, ++_trigger, g.FuzeRange, 0f, 0f, MercAA.MasterActor());
                    FlakNet.SendPacket(Packet, true);
                }
                return false;
            }
            Observe(g);
            if (g.Reloading || g.Rounds <= 0 || g.Muzzle == null || g.Cradle == null
                || (g.ShortRange && g.MuzzleRight == null)) return false;
            return g.Ammo.Spend(true);
        }

        internal static int ShotActor(Flak.Gun g)
        {
            if (g == Flak._manned) return Mathf.Max(0, Crocodile.LocalActor());
            if (Time.time < g.ClaimedUntil) return Mathf.Max(0, g.ClaimActor);
            MercAAPost merc = MercAA.Gun(g.Index);
            return merc == null ? 0 : Mathf.Max(0, merc.Actor);
        }

        internal static void Tick(List<Flak.Gun> guns, bool master)
        {
            FrameProf.S(FrameProf.S_FlakAmmoT);
            try
            {
                if (_pendingGun >= 0 && Time.time >= _pendingUntil)
                {
                    _pendingGun = -1;
                    Notice(Loc.T("Передача не подтверждена. Проверьте запас.", "Supply unconfirmed. Check the gun stock."));
                }
                if (Time.time >= _next)
                {
                    _next = Time.time + 0.5f;
                    int owner = MercAA.MasterActor();
                    if (_master != owner)
                    {
                        if (_pendingGun >= 0 && _pendingRequest < 0)
                            Notice(Loc.T("Сменился мастер. Проверьте переданные боеприпасы.", "Master changed. Check the supplied ammunition."));
                        _master = owner; _pendingGun = -1; FireSeen.Clear(); SnapshotNext.Clear();
                        for (int i = 0; i < guns.Count; i++)
                        { guns[i].Ammo.NewMaster(); guns[i].AmmoSent = -1; }
                    }
                    _near = null;
                    GameObject me = MapTools.LocalPlayer();
                    for (int i = 0; i < guns.Count; i++)
                    {
                        Flak.Gun g = guns[i];
                        if (master)
                        {
                            Observe(g);
                            // Changed stocks plus a slow snapshot while awake:
                            // late join and migration use the same master state.
                            if (g.AmmoSent != g.Ammo.Revision || (g.Awake && Time.time >= g.AmmoHeartbeat)) SendState(g);
                        }
                        Transform seat = g.SeatGunner != null ? g.SeatGunner : g.Root;
                        if (me != null && seat != null && Crocodile.PlayerUp(me)
                            && (me.transform.position - seat.position).sqrMagnitude <= 100f) _near = g;
                        // An idle distant gun does not broadcast forever.
                        // A late joiner asks only when near a gun or using radar.
                        if (!master && !g.Ammo.Known && (_near == g || g == Flak._manned || RadarScope.InView))
                        {
                            Fill(SnapshotAsk, g.Index, 0f, 0f, 0f, 0f, owner);
                            FlakNet.SendPacket(Packet, true);
                        }
                        CacheText(g);
                    }
                }
                Flak.Gun near = Flak._manned != null ? Flak._manned : _near;
                if (near != null && _pendingGun < 0 && !RadarScope.InView && GameUi.KeyDown(KeyCode.L))
                {
                    int actor = Crocodile.LocalActor();
                    if (!MaySupply(near, actor)) return;
                    _pendingGun = near.Index; _pendingRequest = ++_request; _pendingUntil = Time.time + 10f;
                    Fill(Ask, near.Index, _pendingRequest, 0f, 0f, 0f, _master);
                    if (master) OnPacket(Packet, actor); else FlakNet.SendPacket(Packet, true);
                }
            }
            finally { FrameProf.E(FrameProf.S_FlakAmmoT); }
        }

        static void CacheText(Flak.Gun g)
        {
            int count = DisplayRounds(g);
            if (g.AmmoText != null && g.AmmoTextCount == count) return;
            g.AmmoTextCount = count;
            g.AmmoText = count == -2 ? Loc.T("запас неизвестен", "stock unknown")
                : count == -1 ? Loc.T("гарнизон - без лимита", "garrison - unlimited")
                : Loc.T(CountsRu[count], CountsEn[count]);
            g.AmmoPrompt = count >= 0 ? g.ShortRange
                ? Loc.T("L: добавить ленту (100)", "L: supply belt box (100)")
                : Loc.T("L: добавить снаряд", "L: supply shell") : "";
        }

        internal static void Draw()
        {
            Flak.Gun g = Flak._manned != null ? Flak._manned : _near;
            if (g == null || g.Root == null || RadarScope.InView || g.AmmoPrompt == null) return;
            if (_label == null) { _label = new GUIStyle(GUI.skin.label); _label.alignment = TextAnchor.MiddleCenter; }
            float y = Flak._manned != null ? Screen.height - 164f : Screen.height * 0.55f;
            float centre = Screen.width * 0.5f;
            GUI.Label(new Rect(centre - 200f, y, 100f, 26f), g.Id, _label);
            GUI.Label(new Rect(centre - 100f, y, 300f, 26f), g.AmmoText, _label);
            GUI.Label(new Rect(0f, y + 26f, Screen.width, 26f), g.AmmoPrompt, _label);
            if (_notice != null && Time.time < _noticeUntil)
                GUI.Label(new Rect(0f, y - 26f, Screen.width, 26f), _notice, _label);
        }

        static void Fill(int kind, int gun, float a, float b, float c, float d, float e)
        {
            Packet[0] = kind; Packet[1] = gun; Packet[2] = a; Packet[3] = b;
            Packet[4] = c; Packet[5] = d; Packet[6] = e; Packet[7] = Packet[8] = 0f;
        }

        internal static void SendState(Flak.Gun g)
        {
            Fill(State, g.Index, g.Ammo.Stock, g.Ammo.Revision, g.Ammo.Limited ? 1f : 0f,
                Mathf.Max(0, g.Rounds), g.Reloading ? Mathf.Max(0f, g.ReloadUntil - Time.time) : 0f);
            g.AmmoSent = g.Ammo.Revision; g.AmmoHeartbeat = Time.time + 2f;
            FlakNet.SendPacket(Packet, true);
        }

        internal static bool ReceiveState(Flak.Gun g, int sender, float stock, float revision, float limited, float rounds, float reload)
        {
            if (Crocodile.IsMaster() || sender != MercAA.MasterActor() || stock < 0f || stock > FlakAmmoStock.Capacity(g.ShortRange)
                || stock != Mathf.RoundToInt(stock) || revision < 0f || revision != Mathf.RoundToInt(revision)
                || (limited != 0f && limited != 1f) || rounds < 0f
                || rounds > (g.ShortRange ? ShortRangeCore.Magazine : Flak.RoundsPerLoad)
                || rounds != Mathf.RoundToInt(rounds) || (limited == 1f && rounds > stock)
                || reload < 0f || reload > 120f) return false;
            if (g.AmmoSender == sender && revision < g.Ammo.Revision) return false;
            g.AmmoSender = sender; g.Ammo.Known = true; g.Ammo.Stock = (int)stock;
            g.Ammo.Revision = (int)revision; g.Ammo.Limited = limited != 0f;
            g.Rounds = (int)rounds; g.Reloading = reload > 0f; g.ReloadUntil = Time.time + reload;
            CacheText(g); return true;
        }

        internal static void OnPacket(float[] f, int sender)
        {
            if (f == null || f.Length != 9) return;
            for (int i = 0; i < f.Length; i++) if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
            int index = Mathf.RoundToInt(f[1]), kind = Mathf.RoundToInt(f[0]);
            if (index != f[1] || kind != f[0]) return;
            Flak.Gun g = Flak.ByIndex(index);
            if (g == null) return;
            bool master = Crocodile.IsMaster();
            int host = MercAA.MasterActor();
            if (kind == State)
            {
                ReceiveState(g, sender, f[2], f[3], f[4], f[5], f[6]); return;
            }
            if (kind == Grant)
            {
                if (sender != host || f[6] != Crocodile.LocalActor() || index != _pendingGun
                    || f[3] != _pendingRequest || Time.time >= _pendingUntil || f[2] < 0f
                    || f[2] != Mathf.RoundToInt(f[2])) return;
                if (f[2] == 0f)
                {
                    _pendingGun = -1;
                    Notice(f[4] == 1f ? Loc.T("Снабжение со склада или грузовика. Проверьте запас.", "Storage/truck supply requested. Check gun stock.")
                        : Loc.T("Запас полон или передача занята.", "Gun stock full or supply busy.")); return;
                }
                // The native owner alone can remove his backpack/vest item.
                // The master admits one fixed-size item under this one-use lease.
                int token = (int)f[2];
                _pendingRequest = -token; // Duplicate grants cannot remove another item.
                bool taken = Turret.TakeItem(g.ShortRange ? FlakAmmoStock.BeltItem : FlakAmmoStock.ShellItem, "AA supply");
                if (!taken)
                {
                    _pendingGun = -1;
                    Notice(Loc.T("В инвентаре нет подходящих зенитных боеприпасов.", "No suitable AA ammunition in your inventory."));
                }
                Fill(Commit, index, token, taken ? 1f : 0f, 0f, 0f, host);
                if (master) OnPacket(Packet, Crocodile.LocalActor()); else FlakNet.SendPacket(Packet, true);
                return;
            }
            if (kind == Result)
            {
                if (sender != host || f[6] != Crocodile.LocalActor() || index != _pendingGun
                    || f[2] != -_pendingRequest) return;
                _pendingGun = -1;
                Notice(f[3] == 1f ? Loc.T("Боеприпасы переданы.", "Ammunition supplied.")
                    : Loc.T("Передача не подтверждена. Проверьте запас.", "Supply unconfirmed. Check the gun stock."));
                return;
            }
            if (!master || f[6] != host) return;
            Observe(g);
            if (kind == SnapshotAsk)
            {
                float next;
                if (!Crocodile.PlayerUp(Crocodile.PlayerByActor(sender))
                    || (SnapshotNext.TryGetValue(sender, out next) && Time.time < next)) return;
                SnapshotNext[sender] = Time.time + 0.5f;
                SendState(g);
            }
            else if (kind == ReloadAsk)
            {
                if (!PlayerAllowed(g, sender)) return;
                g.ClaimActor = sender; g.ClaimedUntil = Time.time + 1.5f;
                Flak.StartReload(g, g.ShortRange ? ShortRangeCore.ReloadSeconds : Flak.ManualReloadSeconds());
                SendState(g);
            }
            else if (kind == Ask)
            {
                if (f[2] <= 0f || f[2] != Mathf.RoundToInt(f[2]) || !MaySupply(g, sender)) return;
                int request = (int)f[2];
                // Storage/truck delivery consumes real goods on their authority.
                // Existing backpack supply remains the fallback.
                if (AmmoDepot.TrySupply(g, sender))
                {
                    Fill(Grant, index, 0f, request, 1f, 0f, sender);
                    if (sender == Crocodile.LocalActor()) OnPacket(Packet, host); else FlakNet.SendPacket(Packet, true);
                    return;
                }
                int token = g.Ammo.Reserve(sender, request, Time.time, g.ShortRange, true);
                Fill(Grant, index, token, request, 0f, 0f, sender);
                if (sender == Crocodile.LocalActor()) OnPacket(Packet, host); else FlakNet.SendPacket(Packet, true);
            }
            else if (kind == Commit)
            {
                if (f[2] <= 0f || f[2] != Mathf.RoundToInt(f[2])) return;
                if (f[3] == 0f) { g.Ammo.Cancel(sender, (int)f[2]); return; }
                if (f[3] != 1f) return;
                int token = (int)f[2];
                bool accepted = g.Ammo.Commit(sender, token, Time.time, true);
                Fill(Result, index, token, accepted ? 1f : 0f, 0f, 0f, sender);
                if (sender == Crocodile.LocalActor()) OnPacket(Packet, host); else FlakNet.SendPacket(Packet, true);
                if (!accepted) return;
                if (g.Rounds <= 0 && !g.Reloading) Flak.StartReload(g, Flak.PlayerAt(g)
                    ? Flak.ManualReloadSeconds() : g.ShortRange ? ShortRangeCore.ReloadSeconds : Flak.ReloadSeconds());
                SendState(g); CacheText(g);
            }
            else if (kind == Trigger)
            {
                if (f[2] <= 0f || f[2] != Mathf.RoundToInt(f[2]) || f[3] < 30f
                    || f[3] > (g.ShortRange ? ShortRangeCore.RangeM * Flak.K : Flak.MaxFuze)
                    || !PlayerAllowed(g, sender)) return;
                // One sequence per actor covers all guns and survives changing seats.
                int seen;
                if (FireSeen.TryGetValue(sender, out seen) && f[2] <= seen) return;
                FireSeen[sender] = (int)f[2];
                if (Time.time < g.AmmoNextShot || g.Reloading || g.Rounds <= 0) return;
                g.ClaimActor = sender; g.ClaimedUntil = Time.time + 1.5f;
                g.FuzeRange = f[3];
                if (Time.time >= g.AmmoNextSearch)
                { g.AmmoNextSearch = Time.time + 0.5f; FlakFire.Search(g); }
                Flak.Fire(g, Vector3.zero, false, f[3], g.Hostile, true);
                g.AmmoNextShot = Time.time + (g.ShortRange ? ShortRangeCore.ShotSeconds : Flak.ManualInterval());
                if (g.Rounds <= 0) Flak.StartReload(g, g.ShortRange ? ShortRangeCore.ReloadSeconds : Flak.ManualReloadSeconds());
            }
        }
    }
}
