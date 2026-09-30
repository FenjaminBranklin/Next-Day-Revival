// Next Day: Survival - Revival Toolkit
//
// W TOWER 3 - HOLDING THE AIRFIELD PAYS (docs/ai/tasks/w-tower3-holding.md).
//
// The airfield (tower radar + guns) is a strategic objective. Somebody always
// holds it: at first the airfield's own garrison ([Airfield] DefenderFaction,
// TowerRadar.HomeSide). It is taken ON THE GROUND:
//   - the HQ's NPC operator at the tower console (TowerRadar, radar/c1/op)
//     must be down - while he lives the garrison holds the tower;
//   - then living players of ONE side inside CaptureRadius of the C1 tower,
//     on the ground (not more than MaxGroundHeight above the terrain, so a
//     helicopter or An-2 overhead never counts), for CaptureSeconds, with no
//     player of the holding side there. Two attacking sides freeze the count;
//     defenders present (or nobody) wind it back.
//   - a player holder loses it to the garrison when the HQ operator respawns
//     (TowerRadar: 35 min with no player within 150 u), or to another side.
// What the holder gets:
//   1 THE AN-2. Only the holder's side takes the controls of an An-2 standing
//     inside the airfield fence and only the holder refuels one there (canister
//     or the D1 pump). The apron respawn waits while a capture is running.
//     The loser loses it: a new holder owns the parked aeroplane at once.
//   2 NO INCOME (Y B4). Holding the airfield used to pay 20000 every 10
//     minutes to each player of the holding side. That passive money is
//     parked until the airfield economy is designed (the airfield grind:
//     vault, running costs, fuel sales). The master pays nothing, and a
//     grant (RadarNet kind 10, reserved) from an older master is dropped
//     unbooked. The pure clock / clamp / ledger stay in AirfieldHoldPolicy
//     for that economy; [AirfieldHold] IncomeAmount / IncomeMinutes in an
//     old config file are ignored.
//   3 THE ANNOUNCEMENT. "<SIDE> hold the airfield" as a banner on every client
//     and a line in the radar log; the fence is tinted in the holder's colour
//     on the map (native map ink, MapInkLayer), the name on hover.
//
// AUTHORITY. The master alone decides presence, capture and the holder;
// clients only draw its state (RadarNet kind 9, once a second on
// change and every 5 s). A master handoff continues from the last state.
//
// PERFORMANCE. AirfieldHold.Tick: time compares per frame; the master's step
// (players, faction reads, terrain heights of players near the tower) runs
// once a second, the local zone check once a second. AirfieldHold.Draw: the
// banner only while shown (cached GUIContent), the map tint only while the
// map is open, rebuilt when the picture moves, otherwise re-armed (Keep).
//
// C# 3.0 (csc from .NET 3.5). Player-facing strings go through Loc.T (real
// Cyrillic), so this file is UTF-8 without BOM.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>The master's hold state, as every client knows it.</summary>
    internal sealed class HoldState
    {
        internal int Holder = -1;          // Fraction value, -1 not known yet
        internal bool ByPlayers;           // taken by players (else the garrison)
        internal int Capturer = -1;        // side whose capture runs, -1 none
        internal float Progress;           // 0..1 of that capture
    }

    // Pure policy; research/airfield_hold_check.py compiles this exact class.
    internal static class AirfieldHoldPolicy
    {
        internal const int None = 0, Captured = 1, Retaken = 2;

        /// <summary>One master step. <paramref name="sides"/>: the distinct
        /// sides with a living player on the ground in the zone.</summary>
        internal static int Step(HoldState s, int home, bool hqUp, int[] sides, int count, float dt, float seconds)
        {
            if (home < 0) return None;
            if (s.Holder < 0) { s.Holder = home; s.ByPlayers = false; }
            if (seconds < 1f) seconds = 1f;
            if (dt < 0f) dt = 0f;
            if (hqUp)
            {
                s.Capturer = -1;
                s.Progress = 0f;
                if (s.ByPlayers && s.Holder != home)
                {
                    s.Holder = home;
                    s.ByPlayers = false;
                    return Retaken;
                }
                return None;
            }
            bool defended = false;
            int other = -1, others = 0;
            for (int i = 0; i < count; i++)
            {
                if (s.ByPlayers && sides[i] == s.Holder) { defended = true; continue; }
                bool seen = false;
                for (int j = 0; j < i; j++) if (sides[j] == sides[i]) seen = true;
                if (seen) continue;
                others++;
                other = sides[i];
            }
            if (defended || others == 0)
            {
                s.Progress -= dt / seconds;
                if (s.Progress <= 0f) { s.Progress = 0f; s.Capturer = -1; }
                return None;
            }
            if (others > 1) return None;             // contested: frozen
            if (s.Capturer != other) { s.Capturer = other; s.Progress = 0f; }
            s.Progress += dt / seconds;
            if (s.Progress < 1f) return None;
            s.Holder = other;
            s.ByPlayers = true;
            s.Capturer = -1;
            s.Progress = 0f;
            return Captured;
        }

        /// <summary>The income clock: runs only while players hold the
        /// airfield, restarts full on every capture, never pays a burst.
        /// Y B4: not called - the passive income is parked until the airfield
        /// economy; this clock, Grant and GrantLedger are kept for it.</summary>
        internal static bool IncomeDue(ref float left, bool byPlayers, float dt, float period)
        {
            if (period < 60f) period = 60f;
            if (!byPlayers) { left = period; return false; }
            if (left > period) left = period;
            left -= dt;
            if (left > 0f) return false;
            left = period;
            return true;
        }

        /// <summary>May <paramref name="side"/> fly or refuel an An-2 here?</summary>
        internal static bool MayUse(bool enabled, bool inside, int holder, int side)
        {
            return !enabled || !inside || holder < 0 || side == holder;
        }

        /// <summary>The apron respawn waits while a capture is running.</summary>
        internal static bool SpawnAllowed(bool enabled, int capturer, float progress)
        {
            return !enabled || capturer < 0 || progress <= 0f;
        }

        internal static int Grant(int amount, int balance)
        {
            if (amount <= 0 || balance < 0) return 0;
            if (amount > MaxGrant) amount = MaxGrant;
            long room = (long)int.MaxValue - balance;
            return room <= 0 ? 0 : (int)Math.Min(room, (long)amount);
        }

        internal const int MaxGrant = 1000000;   // exact in a float message
    }

    /// <summary>Grants already booked (master epoch + serial), a small ring.</summary>
    internal sealed class GrantLedger
    {
        readonly long[] _seen = new long[32];
        int _next, _count;

        internal bool Accept(int epoch, int id)
        {
            if (epoch <= 0 || id <= 0) return false;
            long key = ((long)epoch << 32) | (uint)id;
            for (int i = 0; i < _count; i++) if (_seen[i] == key) return false;
            _seen[_next] = key;
            _next = (_next + 1) % _seen.Length;
            if (_count < _seen.Length) _count++;
            return true;
        }
    }

    internal static class AirfieldHold
    {
        const float K = 2.8f;
        internal const int StateMsg = 9, GrantMsg = 10;   // RadarNet kinds (TowerSupport uses 5..8)

        static ConfigEntry<bool> CfgEnabled, CfgAn2, CfgAnnounce, CfgMap;
        static ConfigEntry<float> CfgRadius, CfgSeconds, CfgHeight;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "AirfieldHold";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "W Tower 3: the airfield is held by a side. With the HQ operator down, players of one side "
                + "on the ground near the C1 tower take it; the holder alone flies and refuels the An-2 there "
                + "and is announced and coloured on the map. No passive income (parked until the airfield economy). "
                + "Master-authoritative.");
            CfgRadius = cfg.Bind(S, "CaptureRadius", 150f, "Metres around the C1 tower that count for a capture.");
            CfgSeconds = cfg.Bind(S, "CaptureSeconds", 60f, "Seconds one side must stand there uncontested.");
            CfgHeight = cfg.Bind(S, "MaxGroundHeight", 20f,
                "Metres above the terrain up to which a player counts (tower cab yes, aircraft overhead no).");
            CfgAn2 = cfg.Bind(S, "An2HolderOnly", true,
                "Only the holder's side takes the controls of, or refuels, an An-2 inside the airfield fence.");
            CfgAnnounce = cfg.Bind(S, "Announce", true, "Banner on every client when the airfield changes hands.");
            CfgMap = cfg.Bind(S, "MapColour", true, "Tint the airfield fence on the map in the holder's colour.");
        }

        internal static bool Enabled { get { return TowerRadar.On && (CfgEnabled == null || CfgEnabled.Value); } }
        static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }
        static float RadiusU { get { return Mathf.Clamp(TowerRadar.F(CfgRadius, 150f), 20f, 600f) * K; } }
        static float Seconds { get { return Mathf.Clamp(TowerRadar.F(CfgSeconds, 60f), 5f, 1800f); } }
        static void Log(string s) { RevivalPlugin.L.LogInfo("AirfieldHold: " + s); }

        internal static readonly HoldState State = new HoldState();
        internal static int LocalSide = -1;
        static bool _known, _dirty, _inZone;
        static float _nextStep, _lastStep = -1f, _nextBroadcast;
        static int _sentCapturer = -1, _sentHolder = -2;
        static float _sentProgress;
        static bool _sentBy;
        static readonly int[] _sides = new int[16];
        static int _sideCount;

        // ------------------------------------------------------------- frame

        internal static void Tick()
        {
            float now = Time.time;
            if (!Enabled || !TowerRadar.Built) return;
            if (now < _nextStep) return;
            _nextStep = now + 1f;
            try
            {
                LocalRefresh();
                if (Crocodile.IsMaster()) MasterStep(now);
                else _lastStep = -1f;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("AirfieldHold: " + ex); }
        }

        /// <summary>The tower's world went away (TowerRadar.Clear): the room's
        /// hold state starts over with the next world.</summary>
        internal static void WorldEnded()
        {
            State.Holder = -1;
            State.ByPlayers = false;
            State.Capturer = -1;
            State.Progress = 0f;
            _known = false;
            _lastStep = -1f;
            _sentHolder = -2;
            _bannerUntil = 0f;
            _inZone = false;
            _zoneText = null;
            _mapLabel.text = "";
            MapInkLayer.Hide(Layer);
        }

        static void LocalRefresh()
        {
            GameObject me = MapTools.LocalPlayer();
            LocalSide = SideOf(me);
            bool inZone = me != null && InZone(me.transform.position);
            if (inZone != _inZone) { _inZone = inZone; _zoneKey = -99; }
            ZoneText();
        }

        static int SideOf(GameObject p)
        {
            if (p == null) return -1;
            int f = Mortar.FactionShield.FactionOf(p);
            return f < 0 ? TowerRadar.UnreadSide : f;
        }

        static bool InZone(Vector3 p)
        {
            float dx = p.x - TowerRadar.TowerSpot.x, dz = p.z - TowerRadar.TowerSpot.y, r = RadiusU;
            return dx * dx + dz * dz <= r * r;
        }

        // ------------------------------------------------------------ master

        static void MasterStep(float now)
        {
            float dt = _lastStep < 0f ? 1f : Mathf.Clamp(now - _lastStep, 0f, 2f);
            _lastStep = now;
            int home = TowerRadar.HomeSide();
            if (home < 0) return;
            Presence();
            int before = State.Holder;
            bool beforeBy = State.ByPlayers;
            int ev = AirfieldHoldPolicy.Step(State, home, TowerRadar.NpcOperatorUp, _sides, _sideCount, dt, Seconds);
            // W AA1 merged: the AA guns and the radar belong to this holder too.
            AirfieldOwnership.Follow(State.Holder, State.ByPlayers);
            if (ev != AirfieldHoldPolicy.None)
            {
                Log((ev == AirfieldHoldPolicy.Captured ? "captured by " : "retaken by the garrison, ")
                    + TowerRadar.SideLabel(State.Holder) + " (was " + TowerRadar.SideLabel(before) + ").");
                Announce(ev, before, beforeBy);
                _dirty = true;
            }
            // Y B4: no passive income here until the airfield economy is designed.
            if (State.Holder != _sentHolder || State.ByPlayers != _sentBy || State.Capturer != _sentCapturer
                || Mathf.Abs(State.Progress - _sentProgress) >= 0.049f) _dirty = true;
            if (_dirty || now >= _nextBroadcast) SendState(now);
        }

        /// <summary>The distinct sides with a living player on the ground in
        /// the zone (master, once a second).</summary>
        static void Presence()
        {
            _sideCount = 0;
            List<GameObject> players = Crocodile.Players();
            float maxUp = Mathf.Max(3f, TowerRadar.F(CfgHeight, 20f)) * K;
            for (int i = 0; i < players.Count; i++)
            {
                GameObject p = players[i];
                if (p == null) continue;
                Vector3 pos = p.transform.position;
                if (!InZone(pos) || !Crocodile.PlayerUp(p)) continue;
                float ground;
                if (RevivalTroopInsertion.TerrainHeight(pos, out ground) && pos.y - ground > maxUp) continue;
                int side = SideOf(p);
                bool seen = false;
                for (int j = 0; j < _sideCount; j++) if (_sides[j] == side) seen = true;
                if (!seen && _sideCount < _sides.Length) _sides[_sideCount++] = side;
            }
        }

        static void SendState(float now)
        {
            _dirty = false;
            _nextBroadcast = now + 5f;
            _sentHolder = State.Holder;
            _sentBy = State.ByPlayers;
            _sentCapturer = State.Capturer;
            _sentProgress = State.Progress;
            _known = true;
            ZoneText();
            RadarNet.Send(new float[] { StateMsg, State.Holder, State.ByPlayers ? 1f : 0f, State.Capturer,
                State.Progress, 0f });   // f[5]: the parked income timer, kept for the layout
        }

        // ------------------------------------------------------------- wire

        internal static void Receive(float[] f, int sender)
        {
            if (f == null || f.Length < 2 || !TowerSupport.FromMaster(sender)) return;
            int kind = Mathf.RoundToInt(f[0]);
            if (kind == StateMsg && f.Length >= 6)
            {
                if (Crocodile.IsMaster()) return;
                int holder = Whole(f[1]), capturer = Whole(f[3]);
                if (holder < -1 || capturer < -1) return;
                bool by = f[2] > 0.5f;
                bool changed = _known && holder >= 0 && (holder != State.Holder || by != State.ByPlayers);
                int before = State.Holder;
                bool beforeBy = State.ByPlayers;
                State.Holder = holder;
                State.ByPlayers = by;
                State.Capturer = capturer;
                State.Progress = Mathf.Clamp01(f[4]);
                _known = true;
                if (changed) Announce(by ? AirfieldHoldPolicy.Captured : AirfieldHoldPolicy.Retaken, before, beforeBy);
                ZoneText();
            }
            // kind == GrantMsg: an older master's income grant - parked (Y B4), not booked.
        }

        static int Whole(float v)
        {
            int i = Mathf.RoundToInt(v);
            return Mathf.Abs(v - i) > 0.001f ? int.MinValue : i;
        }

        // ------------------------------------------------------------- An-2

        static bool An2Gate { get { return Enabled && B(CfgAn2); } }

        static bool AtAirfield(GameObject go)
        {
            return go != null && !NpcAircraft.Is(go) && Airfield.Inside(go.transform.position, 0f);
        }

        /// <summary>May the local player take the controls of, or refuel, this An-2?</summary>
        internal static bool LocalMayUse(GameObject go)
        {
            return AirfieldHoldPolicy.MayUse(An2Gate, AtAirfield(go), State.Holder, LocalSide);
        }

        /// <summary>Master: may this actor (-1 = the master himself) refuel it?</summary>
        internal static bool ActorMayUse(GameObject go, int actor)
        {
            if (!An2Gate || !AtAirfield(go)) return true;
            int side = actor < 0 || actor == Crocodile.LocalActor() ? LocalSide : TowerRadar.PlayerSide(actor);
            return AirfieldHoldPolicy.MayUse(true, true, State.Holder, side);
        }

        internal static bool SpawnAllowed()
        {
            return AirfieldHoldPolicy.SpawnAllowed(Enabled, State.Capturer, State.Progress);
        }

        internal static string LockedText()
        {
            return Loc.T("Аэродром держат " + Name(State.Holder, true) + " - Ан-2 только для них",
                "The airfield is held by " + Name(State.Holder, false) + " - the An-2 is theirs");
        }

        // ------------------------------------------------------------ texts

        static string Name(int side, bool ru)
        {
            if (side == TowerRadar.UnreadSide) return ru ? "игроки" : "PLAYERS";
            string n = TowerRadar.SideLabel(side);
            switch (n)
            {
                case "PEACE": return ru ? "мирные" : "CIVILIANS";
                case "MARAUDER": return ru ? "мародёры" : "MARAUDERS";
                case "TRAITOR": return ru ? "предатели" : "TRAITORS";
                case "NEUTRAL": return ru ? "нейтралы" : "NEUTRALS";
                default: return n;
            }
        }

        static string Name(int side) { return Loc.T(Name(side, true), Name(side, false)); }

        internal static Color SideColour(int side)
        {
            switch (TowerRadar.SideLabel(side))
            {
                case "PEACE": return new Color(0.35f, 0.9f, 0.4f, 1f);
                case "MARAUDER": return new Color(0.95f, 0.28f, 0.22f, 1f);
                case "TRAITOR": return new Color(1f, 0.6f, 0.15f, 1f);
                case "NEUTRAL": return new Color(0.45f, 0.7f, 1f, 1f);
                default: return new Color(0.95f, 0.9f, 0.35f, 1f);
            }
        }

        static readonly GUIContent _head = new GUIContent(), _line = new GUIContent(), _zone = new GUIContent(),
            _mapLabel = new GUIContent();
        static float _bannerUntil;
        static Color _bannerColour = Color.white;
        static bool _measure;
        static Vector2 _headSize, _lineSize, _zoneSize;
        static string _zoneText;
        static int _zoneKey = -99;

        static void Announce(int ev, int before, bool beforeBy)
        {
            string holder = Name(State.Holder);
            if (ev == AirfieldHoldPolicy.Captured)
            {
                _head.text = "<b>" + Loc.T(holder.ToUpperInvariant() + " удерживают аэродром", holder + " hold the airfield") + "</b>";
                _line.text = before >= 0 && before != State.Holder
                    ? Loc.T("Отбит у: " + Name(before), "Taken from " + Name(before))
                    : Loc.T("Перешёл от гарнизона к игрокам", "Taken over from the garrison by players");
            }
            else
            {
                _head.text = "<b>" + Loc.T("Гарнизон (" + holder + ") вернул аэродром", "The garrison (" + holder + ") retook the airfield") + "</b>";
                _line.text = before >= 0 ? Loc.T("Потеряли: " + Name(before), Name(before) + " lost it") : "";
            }
            if (LocalSide >= 0 && LocalSide == State.Holder)
                _line.text += Loc.T(" - Ан-2 и его заправка ваши", " - the An-2 and its fuel are yours");
            _bannerColour = SideColour(State.Holder);
            _mapLabel.text = MapLabel();
            _mapBuilt = false;
            _measure = true;
            if (CfgAnnounce == null || CfgAnnounce.Value) _bannerUntil = Time.time + 10f;
            if (TowerRadar.Built) RadarScope.Note(Name(State.Holder, false) + " HOLD THE AIRFIELD.");
        }

        static string MapLabel()
        {
            return "<b>" + Loc.T("Аэродром - держат " + Name(State.Holder, true), "Airfield - held by " + Name(State.Holder, false)) + "</b>";
        }

        /// <summary>The line a player inside the zone sees, rebuilt only when
        /// what it says changes (once a second at most).</summary>
        static void ZoneText()
        {
            if (!_inZone || State.Holder < 0) { _zoneText = null; _zoneKey = -99; return; }
            int key;
            if (TowerRadar.NpcOperatorUp && !(State.ByPlayers && State.Holder == TowerRadar.HomeSide())) key = -2;
            else if (State.Capturer >= 0) key = State.Capturer * 1000 + Mathf.RoundToInt(State.Progress * 100f);
            else key = -3 - State.Holder * 2 - (State.ByPlayers ? 1 : 0);
            if (key == _zoneKey) return;
            _zoneKey = key;
            if (key == -2)
                _zoneText = Loc.T("Аэродром держит гарнизон: выведите из строя оператора КДП, чтобы захватить",
                    "The garrison holds the airfield: take out the HQ operator in the tower to capture it");
            else if (State.Capturer >= 0)
                _zoneText = Loc.T("Захват аэродрома: " + Name(State.Capturer, true) + " " + Mathf.RoundToInt(State.Progress * 100f) + "%",
                    "Capturing the airfield: " + Name(State.Capturer, false) + " " + Mathf.RoundToInt(State.Progress * 100f) + "%");
            else
                _zoneText = Loc.T("Аэродром держат " + Name(State.Holder, true), "The airfield is held by " + Name(State.Holder, false));
            _zone.text = _zoneText;
            _measure = true;
        }

        // ------------------------------------------------------------- draw

        static GUIStyle _style;
        const string Layer = "airfield-hold";

        internal static void Draw()
        {
            if (!Enabled) return;
            Event e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            DrawMap();
            bool banner = Time.time < _bannerUntil;
            if (!banner && _zoneText == null) return;
            if (GameUi.State != 0) return;          // the game's windows are open
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.richText = true;
                _measure = true;
            }
            if (_measure)
            {
                _measure = false;
                _style.fontSize = 20;
                _headSize = _style.CalcSize(_head);
                _style.fontSize = 15;
                _lineSize = _style.CalcSize(_line);
                _zoneSize = _style.CalcSize(_zone);
            }
            Color old = GUI.color;
            try
            {
                float cx = Screen.width * 0.5f, y = Screen.height * 0.12f;
                if (banner)
                {
                    float a = Mathf.Clamp01(_bannerUntil - Time.time);
                    float w = Mathf.Max(_headSize.x, _lineSize.x) + 40f;
                    GUI.color = new Color(0f, 0f, 0f, 0.6f * a);
                    GUI.DrawTexture(new Rect(cx - w * 0.5f, y, w, 62f), Texture2D.whiteTexture);
                    GUI.color = new Color(_bannerColour.r, _bannerColour.g, _bannerColour.b, a);
                    _style.fontSize = 20;
                    GUI.Label(new Rect(cx - _headSize.x * 0.5f, y + 5f, _headSize.x, _headSize.y), _head, _style);
                    _style.fontSize = 15;
                    GUI.color = new Color(1f, 1f, 1f, 0.9f * a);
                    GUI.Label(new Rect(cx - _lineSize.x * 0.5f, y + 34f, _lineSize.x, _lineSize.y), _line, _style);
                    y += 68f;
                }
                if (_zoneText != null)
                {
                    _style.fontSize = 15;
                    float w = _zoneSize.x + 24f;
                    GUI.color = new Color(0f, 0f, 0f, 0.5f);
                    GUI.DrawTexture(new Rect(cx - w * 0.5f, y, w, _zoneSize.y + 6f), Texture2D.whiteTexture);
                    GUI.color = State.Capturer >= 0 ? SideColour(State.Capturer) : SideColour(State.Holder);
                    GUI.Label(new Rect(cx - _zoneSize.x * 0.5f, y + 3f, _zoneSize.x, _zoneSize.y), _zone, _style);
                }
            }
            finally { GUI.color = old; }
        }

        static bool _mapBuilt;
        static Rect _mapFull, _mapGui;
        static float _mapAlpha;
        static int _mapHolder = -2;

        /// <summary>The fence rectangle in the holder's colour on the native
        /// map: a light fill and a strong border, re-armed while unchanged.</summary>
        static void DrawMap()
        {
            if (GameUi.State != 8 || !B(CfgMap) || State.Holder < 0 || !TowerRadar.Built || !EastWorld.Extends)
            {
                if (_mapHolder != -2) { MapInkLayer.Hide(Layer); _mapHolder = -2; }
                return;
            }
            MapInkLayer layer = null;
            try
            {
                Component manager, texture;
                Camera camera;
                Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out camera, out world, out map)) return;
                Rect full;
                if (!MapTools.MapScreenRect(texture, camera, out full) || full.width < 2f || full.height < 2f) return;
                layer = MapInkLayer.Begin(Layer, texture);
                if (layer == null) return;
                bool rebuild = !_mapBuilt || !layer.CanKeep || _mapHolder != State.Holder || full != _mapFull
                    || Mathf.Abs(layer.Alpha - _mapAlpha) > 0.001f;
                if (!rebuild) { layer.Keep(); HoverLabel(); return; }
                _mapBuilt = false;
                Rect fence = Airfield.Fence;
                Vector2 a, b;
                if (!MapTools.WorldToGui(new Vector3(fence.xMin, 0f, fence.yMin), texture, camera, world, map, out a)
                    || !MapTools.WorldToGui(new Vector3(fence.xMax, 0f, fence.yMax), texture, camera, world, map, out b)) return;
                a = MapProject.OnPicture(a, full);
                b = MapProject.OnPicture(b, full);
                _mapGui = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                float sx = 1024f / full.width, sy = 1024f / full.height;
                Rect art = new Rect((_mapGui.x - full.x) * sx, (_mapGui.y - full.y) * sy, _mapGui.width * sx, _mapGui.height * sy);
                Color c = SideColour(State.Holder);
                Texture white = Texture2D.whiteTexture;
                float t = Mathf.Max(1.2f, art.width * 0.03f);
                layer.Draw(art, white, new Color(c.r, c.g, c.b, 0.22f));
                Color edge = new Color(c.r, c.g, c.b, 0.85f);
                layer.Draw(new Rect(art.x, art.y, art.width, t), white, edge);
                layer.Draw(new Rect(art.x, art.yMax - t, art.width, t), white, edge);
                layer.Draw(new Rect(art.x, art.y, t, art.height), white, edge);
                layer.Draw(new Rect(art.xMax - t, art.y, t, art.height), white, edge);
                _mapFull = full;
                _mapAlpha = layer.Alpha;
                _mapHolder = State.Holder;
                _mapBuilt = true;
                if (string.IsNullOrEmpty(_mapLabel.text))
                    _mapLabel.text = MapLabel();
                HoverLabel();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("AirfieldHold map: " + ex.Message);
            }
            finally
            {
                if (layer != null) layer.End();
            }
        }

        static void HoverLabel()
        {
            Vector2 mouse = Event.current.mousePosition;
            if (!_mapGui.Contains(mouse)) return;
            if (_style == null) { _style = new GUIStyle(GUI.skin.label); _style.richText = true; _measure = true; }
            _style.fontSize = 15;
            Color old = GUI.color;
            GUI.color = SideColour(State.Holder);
            GUI.Label(new Rect(mouse.x + 14f, mouse.y - 11f, 360f, 24f), _mapLabel, _style);
            GUI.color = old;
        }
    }
}
