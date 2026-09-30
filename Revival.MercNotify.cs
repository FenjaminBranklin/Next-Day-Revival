// W merc notifications - the game side of MercNoteBoard (Revival.MercNotifyCore.cs,
// docs/ai/tasks/w-merc-notify.md). Short toasts for the owner with the merc's
// name: contact (direction + distance from the owner), under fire, wounded,
// target down, fallen; at a vehicle gun: target acquired, kill. A contact
// pings the map for a few seconds; every toast can give a short radio click.
//
//   IN      foot mercs: NpcWar.MercNotice, after each brain Think (10 Hz per
//           merc in a fight, Revival.MercFight.cs) - the M1 sense (a fresh
//           threat after a quiet spell), hits and suppression (under fire),
//           the health bands 60 % / 30 % (wounded / badly), the target he
//           shot at going down (his one-man squad's Hits / Shots since he
//           took it). Gunners: MercRide.Pick (target acquired) and the gun
//           loop (the target he hit is dead: kill). Deaths: Mercs.OnDeath.
//   OUT     MercUi.Toast (no UI kit toast is merged; this is the merc toast
//           stack, top right), a radio click (one procedural clip, made on
//           the first click), the map pings MercUi.DrawMap paints.
//   CONFIG  [Mercs] Notify, NotifyFilter (all / important / deaths),
//           NotifyRadioClick, NotifyMapPing - all on, filter "important".
//           The L list has buttons for the filter, the click and the ping.
//   NETWORK nothing: mercs run on their owner's client and only he is told.
//   COST    per merc per Think a few compares (no ray, no lookup; one
//           GetComponent when his target changes). Tick: one bool while
//           nothing is due. Text is built only when a toast is shown.
//
// C# 3.0, ASCII only (the Cyrillic lives in Loc.T player strings).
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>One merc's view for his notifications, kept between Thinks.</summary>
    internal sealed class MercWatch
    {
        internal bool Primed;
        internal float LastThreat = -1000f;
        internal int Hits;
        internal float Suppression;
        internal bool Danger;
        internal int Band;                    // 0 fit, 1 wounded (< 60 %), 2 badly (< 30 %)
        internal Transform Target;
        internal Component TargetAi;
        internal GameObject TargetGo;
        internal bool TargetPlayer;
        internal int HitsAt, ShotsAt;
    }

    internal static class MercNotify
    {
        internal static ConfigEntry<bool> CfgOn, CfgClick, CfgPing;
        internal static ConfigEntry<string> CfgFilter;

        internal const float ContactQuiet = 8f;      // a contact after this long without a threat is news
        internal const float PingSeconds = 6f;
        internal const float ClickGap = 1.0f;
        const int MaxPings = 6;

        static readonly MercNoteBoard _board = new MercNoteBoard();
        static readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        static readonly Vector3[] _pingAt = new Vector3[MaxPings];
        static readonly float[] _pingUntil = new float[MaxPings];
        static int _pingNext;
        static float _nextClick;
        static string _filterText;
        static int _filter = MercNote.FilterImportant;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgOn = cfg.Bind("Mercs", "Notify", true,
                "Short toasts about your mercs' fights: contact (direction + distance), under fire, wounded, "
                + "target down, fallen; at a vehicle gun: target acquired, kill. A death is always shown.");
            CfgFilter = cfg.Bind("Mercs", "NotifyFilter", "important",
                "Which merc toasts: all / important (contact, wounded, target down, fallen, gun kill) / deaths.");
            CfgClick = cfg.Bind("Mercs", "NotifyRadioClick", true,
                "A short radio click with each merc toast.");
            CfgPing = cfg.Bind("Mercs", "NotifyMapPing", true,
                "A contact pings its spot on the map for a few seconds.");
        }

        internal static int Filter
        {
            get
            {
                string s = CfgFilter == null ? null : CfgFilter.Value;
                if (!ReferenceEquals(s, _filterText)) { _filterText = s; _filter = MercNote.ParseFilter(s); }
                return _filter;
            }
        }

        static bool On { get { return CfgOn == null || CfgOn.Value; } }

        /// <summary>L list button: all -> important -> deaths -> all (switched
        /// off in the config: on again, as it was).</summary>
        internal static void CycleFilter()
        {
            if (CfgFilter == null) return;
            if (!On) { CfgOn.Value = true; return; }
            int next = (Filter + 1) % 3;
            CfgFilter.Value = MercNote.FilterName(next);
        }

        internal static string FilterText()
        {
            if (!On) return Loc.T("Сообщения: ВЫКЛ", "Alerts: OFF");
            int f = Filter;
            return f == MercNote.FilterAll ? Loc.T("Сообщения: все", "Alerts: all")
                : f == MercNote.FilterDeaths ? Loc.T("Сообщения: гибель", "Alerts: deaths only")
                : Loc.T("Сообщения: важные", "Alerts: important");
        }

        // ============================================================ posts
        /// <summary>One event of one merc; at is the spot the toast points to
        /// (the threat, or the merc himself when hasSpot is false).</summary>
        internal static void Post(MercUnit u, int kind, Vector3 at, bool hasSpot, float value, bool severe)
        {
            if (u == null || (!On && kind != MercNote.Fallen)) return;
            Post(u.Id, u.Name, u.Owner, kind, at, hasSpot, value, severe);
        }

        static void Post(int id, string name, Transform owner, int kind, Vector3 at, bool hasSpot, float value, bool severe)
        {
            float dist = -1f;
            int dir = -1;
            if (hasSpot)
            {
                Vector3 from = owner != null && owner ? owner.position : Mercs.OwnerPosition;
                float dx = at.x - from.x, dz = at.z - from.z;
                dist = Mathf.Sqrt(dx * dx + dz * dz) / 2.8f;
                dir = MercNote.Compass8(dx, dz);
            }
            if (!_board.Post(id, kind, Filter, Time.time, at.x, at.y, at.z, dist, dir, value, severe)) return;
            string had;
            if (!_names.TryGetValue(id, out had) || !ReferenceEquals(had, name))
            {
                if (_names.Count > 64) _names.Clear();
                _names[id] = name ?? "";
            }
        }

        /// <summary>Mercs.OnDeath: always shown, whatever the filter.</summary>
        internal static void Fallen(int id, string name, Vector3 at)
        {
            Post(id, name, null, MercNote.Fallen, at, false, 0f, true);
        }

        internal static void GunTarget(MercUnit u, Vector3 at) { Post(u, MercNote.GunTarget, at, true, 0f, false); }

        internal static void GunKill(MercUnit u, Vector3 at) { Post(u, MercNote.GunKill, at, true, 0f, false); }

        // ============================================================= tick
        /// <summary>Per frame (F6 "MercNotify.Tick"): one bool while nothing is due.</summary>
        internal static void Tick()
        {
            if (!_board.Pending) return;
            float now = Time.time;
            MercNoteOut o;
            int guard = 0;
            while (guard++ < MercNote.Kinds && _board.Next(now, out o)) Emit(o, now);
        }

        static void Emit(MercNoteOut o, float now)
        {
            string text;
            try { text = Text(o); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MercNotify: " + ex.Message); return; }
            if (o.Kind == MercNote.Fallen) MercUi.Death(text);   // W-UI3: every page filter shows a death
            else MercUi.Toast(text, MercNote.Warn(o.Kind));
            if ((o.Kind == MercNote.Contact || o.Kind == MercNote.GunTarget) && o.Dist >= 0f
                && (CfgPing == null || CfgPing.Value))
            {
                _pingAt[_pingNext] = new Vector3(o.X, o.Y, o.Z);
                _pingUntil[_pingNext] = now + PingSeconds;
                _pingNext = (_pingNext + 1) % MaxPings;
            }
            if ((CfgClick == null || CfgClick.Value) && now >= _nextClick)
            {
                _nextClick = now + ClickGap;
                Click(o.Kind == MercNote.Fallen || o.Kind == MercNote.Wounded);
            }
        }

        static string NameOf(int id)
        {
            string s;
            return _names.TryGetValue(id, out s) && !string.IsNullOrEmpty(s) ? s : "#" + id;
        }

        static string Mercs2(int n)
        {
            // Russian plural: 2-4 and 5+ take different endings.
            return n + (n >= 5 ? Loc.T(" наёмников", " mercs") : Loc.T(" наёмника", " mercs"));
        }

        static string Where(MercNoteOut o)
        {
            if (o.Dist < 0f) return "";
            float m = o.Dist >= 100f ? Mathf.Round(o.Dist / 10f) * 10f : Mathf.Round(o.Dist);
            return Dir(o.Dir) + " " + m.ToString("0") + Loc.T(" м", " m");
        }

        static string Dir(int d)
        {
            switch (d)
            {
                case 0: return Loc.T("С", "N");
                case 1: return Loc.T("СВ", "NE");
                case 2: return Loc.T("В", "E");
                case 3: return Loc.T("ЮВ", "SE");
                case 4: return Loc.T("Ю", "S");
                case 5: return Loc.T("ЮЗ", "SW");
                case 6: return Loc.T("З", "W");
                case 7: return Loc.T("СЗ", "NW");
                default: return "";
            }
        }

        static string Text(MercNoteOut o)
        {
            bool many = o.Count > 1;
            string who = many ? Mercs2(o.Count) : NameOf(o.Id0);
            switch (o.Kind)
            {
                case MercNote.Contact:
                    return many ? who + Loc.T(" в контакте - ", " in contact - ") + Where(o)
                        : who + Loc.T(": контакт, ", ": contact, ") + Where(o);
                case MercNote.UnderFire:
                    return who + Loc.T(many ? " под огнём" : ": под огнём", many ? " under fire" : ": under fire");
                case MercNote.Wounded:
                    if (many) return who + Loc.T(" ранены", " wounded") + (o.Severe ? Loc.T(", есть тяжёлые", ", some badly") : "");
                    return who + (o.Severe ? Loc.T(": тяжело ранен (", ": badly wounded (") : Loc.T(": ранен (", ": wounded ("))
                        + Mathf.RoundToInt(o.Value * 100f) + "%)";
                case MercNote.TargetDown:
                    return many ? who + Loc.T(": цели поражены", ": targets down") : who + Loc.T(": цель поражена", ": target down");
                case MercNote.Fallen:
                {
                    string names = NameOf(o.Id0);
                    if (o.Count >= 2) names += ", " + NameOf(o.Id1);
                    if (o.Count >= 3) names += ", " + NameOf(o.Id2);
                    if (o.Count > 3) names += " +" + (o.Count - 3);
                    return names + (many ? Loc.T(" погибли.", " have fallen.") : Loc.T(" погиб.", " has fallen."));
                }
                case MercNote.GunTarget:
                    return many ? who + Loc.T(" у орудий: цели, ", " on the guns: targets, ") + Where(o)
                        : who + Loc.T(" у орудия: цель, ", " on the gun: target, ") + Where(o);
                case MercNote.GunKill:
                    return many ? who + Loc.T(" у орудий: цели уничтожены", " on the guns: kills")
                        : who + Loc.T(" у орудия: цель уничтожена", " on the gun: kill");
                default:
                    return who;
            }
        }

        // ============================================================== map
        /// <summary>The map ping i (0..5) while it lives: its spot and 0..1 of
        /// its life gone.</summary>
        internal static bool Ping(int i, float now, out Vector3 at, out float age)
        {
            at = _pingAt[i];
            float left = _pingUntil[i] - now;
            age = 1f - left / PingSeconds;
            return left > 0f;
        }

        internal static int PingSlots { get { return MaxPings; } }

        // ============================================================ sound
        static AudioSource _src;
        static AudioClip _click;
        static bool _audioFailed;

        static void Click(bool loud)
        {
            if (_audioFailed) return;
            try
            {
                if (_src == null)
                {
                    GameObject go = new GameObject("NDR merc radio");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _src = go.AddComponent<AudioSource>();
                    _src.playOnAwake = false;
                    _src.spatialBlend = 0f;
                }
                if (_click == null) _click = MakeClick();
                _src.PlayOneShot(_click, loud ? 0.5f : 0.35f);
            }
            catch (Exception ex)
            {
                _audioFailed = true;
                RevivalPlugin.L.LogWarning("MercNotify click: " + ex.Message);
            }
        }

        /// <summary>A handset key-up: a hard click, a short squelch of band
        /// noise and a second, softer click (~0.12 s). Made once.</summary>
        static AudioClip MakeClick()
        {
            const int rate = 22050;
            int n = (int)(0.12f * rate);
            float[] s = new float[n];
            System.Random rnd = new System.Random(7);
            float lp = 0f, hp = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
                // A crude radio band: low-pass then high-pass.
                lp += (noise - lp) * 0.35f;
                hp = 0.9f * (hp + lp - prev);
                prev = lp;
                float squelch = t < 0.095f ? 0.45f * Mathf.Min(1f, t / 0.01f) * (1f - t / 0.095f * 0.4f) : 0f;
                float v = hp * squelch;
                if (t < 0.004f) v += 0.9f * (1f - t / 0.004f) * Mathf.Sin(2f * Mathf.PI * 1800f * t);
                float t2 = t - 0.098f;
                if (t2 >= 0f && t2 < 0.006f) v += 0.5f * (1f - t2 / 0.006f) * Mathf.Sin(2f * Mathf.PI * 1400f * t2);
                s[i] = Mathf.Clamp(v, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create("NDR merc radio click", n, 1, rate, false);
            clip.SetData(s, 0);
            return clip;
        }

        internal static string Status()
        {
            return "Merc alerts: filter " + MercNote.FilterName(Filter) + (On ? "" : " (off)")
                + ", posted " + _board.Posted + ", merged " + _board.Merged + ", throttled " + _board.Throttled
                + ", filtered " + _board.Filtered + ", stale " + _board.Stale + ", shown " + _board.Emitted;
        }
    }

    public static partial class NpcWar
    {
        /// <summary>After a merc's Think: what the owner should hear
        /// (Revival.MercNotify.cs head). Arithmetic on what the Think just
        /// read; one GetComponent when his target changes.</summary>
        static void MercNotice(Fighter f, MercUnit u, MercFight ft, float now)
        {
            MercWatch w = u.Watch;
            MercSense s = u.Sense;
            int band = ft.Health < 0.3f ? 2 : ft.Health < 0.6f ? 1 : 0;
            if (!w.Primed)
            {
                w.Primed = true;
                w.Hits = ft.Hits;
                w.Band = band;
                w.Suppression = f.Suppression;
                w.Danger = ft.In.Danger;
                if (s.Count > 0) w.LastThreat = now;
                return;
            }
            Vector3 me = ft.In.Me;
            bool threat = s.Count > 0;
            Vector3 spot = threat ? s.At[0] : me;

            // Contact: a threat after a quiet spell.
            if (threat)
            {
                if (now - w.LastThreat >= MercNotify.ContactQuiet)
                    MercNotify.Post(u, MercNote.Contact, spot, true, 0f, false);
                w.LastThreat = now;
            }

            // Under fire: hit, pinned (suppression over a third) or a blast / grenade near him.
            bool hit = ft.Hits != w.Hits;
            bool pinned = f.Suppression >= 0.34f && w.Suppression < 0.34f;
            bool blast = ft.In.Danger && !w.Danger;
            w.Hits = ft.Hits; w.Suppression = f.Suppression; w.Danger = ft.In.Danger;
            if (hit || pinned || blast) MercNotify.Post(u, MercNote.UnderFire, spot, threat, 0f, hit);

            // Wounded: into a worse band (a dressing lifts him out again).
            if (band > w.Band) MercNotify.Post(u, MercNote.Wounded, me, false, ft.Health, band == 2);
            w.Band = band;

            // Target down: the man he shot at is dead.
            Transform t = ft.Target;
            if (!ReferenceEquals(t, w.Target))
            {
                MercTargetDown(f, u, w);
                w.Target = t;
                w.TargetGo = t == null ? null : t.gameObject;
                w.TargetPlayer = t != null && ft.TargetIsPlayer;
                w.TargetAi = t == null || w.TargetPlayer || _npcType == null ? null : t.GetComponent(_npcType);
                w.HitsAt = f.Squad == null ? 0 : f.Squad.Hits;
                w.ShotsAt = f.Squad == null ? 0 : f.Squad.Shots;
            }
            else if (!ReferenceEquals(w.TargetAi, null) && !Alive(w.TargetAi))
            {
                MercTargetDown(f, u, w);
                w.Target = null; w.TargetAi = null; w.TargetGo = null; w.TargetPlayer = false;
            }
        }

        /// <summary>His last target is dead and he hit it (an NPC: his squad's
        /// hits grew; a player: he fired - the game's own round hits players).</summary>
        static void MercTargetDown(Fighter f, MercUnit u, MercWatch w)
        {
            if (ReferenceEquals(w.Target, null) || f.Squad == null) return;
            bool down, mine;
            if (!ReferenceEquals(w.TargetAi, null))
            {
                down = !Alive(w.TargetAi);
                mine = f.Squad.Hits > w.HitsAt;
            }
            else if (w.TargetPlayer)
            {
                down = w.TargetGo != null && Mercs.PlayerDead(w.TargetGo);
                mine = f.Squad.Shots > w.ShotsAt;
            }
            else return;
            if (!down || !mine) return;
            Vector3 at = w.Target != null ? w.Target.position : u.Ai != null ? u.Ai.transform.position : Vector3.zero;
            MercNotify.Post(u, MercNote.TargetDown, at, w.Target != null, 0f, false);
        }
    }
}
