// B3 mercenaries, the screens (docs/ai/tasks/mercenaries.md section 5 and the
// mockups in docs/ai/tasks/mercenaries/):
//
//   TRADE TAB   a third tab "Mercenaries" beside the trader window's Safe and
//               Trade tabs (UIController.SwitchUIMarketStorageMarketplace), at
//               every settlement storekeeper whose faction is civilian, looter or
//               traitor (the traitor trader also offers the military town's
//               profiles: the town has no trader). The hire cards cover the
//               window; NGUI's mouse is switched off while the pointer is over
//               them, so a click on a card never buys an item underneath.
//   ORDERS      K held = wheel (mouse direction or 1-6, release issues), K K =
//               FOLLOW ME, Ctrl+1..5 / Ctrl+0 selection. STAY takes the
//               crosshair point (up to 300 m) - the player chooses where.
//               B3b PATROL: a route of 1-6 points (crosshair, own spot, map
//               clicks); PERIMETER at the crosshair, picked again = radius.
//               B3c: 5 = FOLLOW MY VEHICLE; K K in a vehicle is that order; in
//               a seat the mouse belongs to the seat, so the wheel takes 1-6
//               only and PATROL (a route is set on foot) is greyed.
//   MAP         owner only: patrol loops / perimeter circles as dashed map
//               ink, his mercs as numbered squares (only while the map is open).
//   LIST        L: mercs (health, order, state, upkeep, distance; pay, peaceful,
//               dismiss twice) and the whitelist (Ctrl+L = player under the
//               crosshair, or undo within 10 s of a whitelisted player's hit).
//   HUD         a small strip bottom-left and toasts top right (hint settings).
//   W-UI3       the toasts pass the page's filter ([Mercs] Notifications: all /
//               important / deaths only; a reply to the player's own click or
//               key always shows), and LOCATE from the trader page marks one
//               merc for 30 s: a gold square on the map, a screen marker with
//               name and distance once no game window is open.
//
// Every per-frame path is a few key reads; the trade tab reads the UIController
// once per frame only while a game window is open. C# 3.0, ASCII only (the
// Cyrillic lives in Loc.T player strings).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercUi
    {
        // ============================================================ toasts
        sealed class ToastLine { internal string Text; internal float Until; internal bool Warn; }
        static readonly List<ToastLine> _toasts = new List<ToastLine>();

        internal static void Toast(string text, bool warn)
        {
            Note(text, warn ? MercPageNote.Important : MercPageNote.Info, warn);
        }

        /// <summary>W-UI3: a merc died (every filter shows it).</summary>
        internal static void Death(string text)
        {
            Note(text, MercPageNote.Death, true);
        }

        // W-UI3: the frame of the player's last click or key; a toast in the
        // same frame answers it and shows whatever the filter says.
        static int _replyFrame = -1;

        internal static void Reply() { _replyFrame = Time.frameCount; }

        static void Note(string text, int kind, bool warn)
        {
            if (!MercPageNote.Pass(MercPage.NoteMode, kind, Time.frameCount == _replyFrame)) return;
            float secs = Mathf.Clamp(Mercs.CfgToastSeconds == null ? 4f : Mercs.CfgToastSeconds.Value, 1f, 15f);
            ToastLine t = new ToastLine();
            t.Text = text; t.Until = Time.time + secs * (warn ? 1.5f : 1f); t.Warn = warn;
            _toasts.Add(t);
            while (_toasts.Count > 5) _toasts.RemoveAt(0);
        }

        static string _undoSteam, _undoName;
        static float _undoUntil, _nextWlToast;

        internal static void WhitelistHit(string shooter, string steam, string merc)
        {
            _undoSteam = steam; _undoName = shooter; _undoUntil = Time.time + 10f;
            if (Time.time < _nextWlToast) return;
            _nextWlToast = Time.time + 10f;
            Toast(shooter + Loc.T(" (белый список) стреляет в ", " (whitelisted) is shooting your ") + merc
                + Loc.T(". Ctrl+L в течение 10 с - убрать из списка.", ". Ctrl+L within 10 s removes him."), true);
        }

        static string _flashProfile;
        static float _flashUntil;

        internal static void FlashHired(string profileId)
        {
            _flashProfile = profileId; _flashUntil = Time.time + 1.5f;
        }

        internal static bool Flashing(string profileId)
        {
            return _flashProfile == profileId && Time.time < _flashUntil;
        }

        // ============================================================ locate
        const float LocateSeconds = 30f;
        static int _locateId = -1;
        static float _locateUntil;
        static int _locateKey = -1;
        static string _locateText;
        static Camera _locateCam;
        static float _locateCamAt;
        static GUIStyle _locateStyle;

        /// <summary>W-UI3 (trader page LOCATE): mark this merc for 30 s.</summary>
        internal static void Locate(Mercs.Record r)
        {
            if (r == null) return;
            _locateId = r.Id; _locateUntil = Time.time + LocateSeconds; _locateKey = -1;
        }

        internal static bool Located(int id) { return id == _locateId && Time.time < _locateUntil; }

        /// <summary>A ring and "Name - 85 m" over the located merc while no
        /// game window is open. Idle: one float compare.</summary>
        static void DrawLocate()
        {
            if (Time.time >= _locateUntil) return;
            if (Event.current.type != EventType.Repaint) return;
            Mercs.Record r = null;
            List<Mercs.Record> roster = Mercs.Roster;
            for (int i = 0; i < roster.Count; i++) if (roster[i].Id == _locateId) { r = roster[i]; break; }
            if (r == null || r.Dead || r.Unit == null || r.Unit.Ai == null) return;
            // Camera.main is a tag search: once a second.
            if (_locateCam == null || Time.time >= _locateCamAt) { _locateCam = Camera.main; _locateCamAt = Time.time + 1f; }
            if (_locateCam == null) return;
            Vector3 head = r.Unit.Ai.transform.position + Vector3.up * 5.6f;
            Vector3 sp = _locateCam.WorldToScreenPoint(head);
            if (sp.z <= 0f) return;
            float x = sp.x, y = Screen.height - sp.y;
            // 5 m steps: the text is rebuilt a few times while he walks, not per frame.
            int metres = Mathf.RoundToInt(Vector3.Distance(Mercs.OwnerPosition, head) / 2.8f / 5f) * 5;
            if (metres != _locateKey || _locateText == null)
            {
                _locateKey = metres;
                _locateText = "<b>" + r.Name + "</b>  " + metres + " m";
            }
            if (_locateStyle == null) { _locateStyle = new GUIStyle(_label); _locateStyle.alignment = TextAnchor.MiddleCenter; }
            Box(new Rect(x - 7f, y - 7f, 14f, 14f), new Color(0f, 0f, 0f, 0.85f));
            Box(new Rect(x - 5f, y - 5f, 10f, 10f), Gold);
            Rect t = new Rect(x - 90f, y - 30f, 180f, 20f);
            Box(t, new Color(0f, 0f, 0f, 0.6f));
            GUI.Label(t, _locateText, _locateStyle);
        }

        // ============================================================= input
        static KeyCode _wheelKey = KeyCode.None, _listKey = KeyCode.None;
        static string _wheelKeyText, _listKeyText;
        static bool _wheelOpen, _wheelArmed, _lastWasTap;
        static float _kDownAt, _lastTapUp = -10f;
        static Vector2 _wheelVec;
        static int _wheelPick = -1;
        static bool _listOpen;
        static int _listTab;

        internal static bool ListOpen { get { return _listOpen; } }

        static KeyCode Parse(string text, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), text.Trim(), true); }
            catch { return fallback; }
        }

        static void Keys()
        {
            string w = Mercs.CfgWheelKey == null ? "K" : Mercs.CfgWheelKey.Value;
            string l = Mercs.CfgListKey == null ? "L" : Mercs.CfgListKey.Value;
            if (w != _wheelKeyText) { _wheelKeyText = w; _wheelKey = Parse(w, KeyCode.K); }
            if (l != _listKeyText) { _listKeyText = l; _listKey = Parse(l, KeyCode.L); }
        }

        static bool Ctrl()
        {
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        /// <summary>Per frame from Mercs.Tick: key reads only.</summary>
        internal static void TickInput()
        {
            Keys();
            if (Input.anyKeyDown && !Input.GetMouseButtonDown(0)) Reply();
            if (_tabActive && Time.frameCount - _tabFrame > 2) CloseTab();
            if (Mercs.Roster.Count == 0 && !_listOpen && !_wheelOpen)
            {
                if (_listKey != KeyCode.None && Input.GetKeyDown(_listKey) && !Ctrl() && !GameUi.WindowOpen
                    && !Admin.IsOpen) _listOpen = true;
                return;
            }
            bool gameWindow = GameUi.WindowOpen;
            if (_listKey != KeyCode.None && Input.GetKeyDown(_listKey))
            {
                if (Ctrl()) CtrlL();
                else if (!gameWindow || _listOpen) { _listOpen = !_listOpen; if (!_listOpen) RestoreCursor(); }
            }
            if (_listOpen)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { _listOpen = false; RestoreCursor(); }
                else if (Input.GetKeyDown(KeyCode.P) && _listTab == 0) Mercs.PayAllDue();
            }
            if (Ctrl())
            {
                for (int n = 0; n <= 5; n++)
                    if (Input.GetKeyDown(KeyCode.Alpha0 + n)) Mercs.Select(n);
            }
            // B3b: a patrol route being set takes the order key (on the map too).
            if (_placing && !_listOpen && (!gameWindow || GameUi.State == 8))
            { _wheelOpen = false; _wheelArmed = false; RouteInput(gameWindow); return; }
            if (gameWindow || _listOpen) { _wheelOpen = false; _wheelArmed = false; return; }
            Wheel();
        }

        static void Wheel()
        {
            float now = Time.time;
            if (_wheelKey == KeyCode.None) return;
            if (Input.GetKeyDown(_wheelKey))
            {
                if (_lastWasTap && now - _lastTapUp < 0.3f)
                {
                    _lastWasTap = false; _wheelArmed = false;
                    if (MercRide.OwnerInVehicle) Mercs.OrderVehicle();
                    else Mercs.OrderFollow();
                    return;
                }
                _kDownAt = now; _wheelArmed = true; _wheelVec = Vector2.zero; _wheelPick = -1;
            }
            if (_wheelArmed && Input.GetKey(_wheelKey))
            {
                if (!_wheelOpen && now - _kDownAt > 0.22f) _wheelOpen = true;
                if (_wheelOpen)
                {
                    // B3c: in a seat the mouse aims the seat's view: keys only.
                    if (!MercRide.OwnerInVehicle)
                    {
                        _wheelVec += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
                        if (_wheelVec.magnitude > 6f) _wheelVec = _wheelVec.normalized * 6f;
                        _wheelPick = _wheelVec.magnitude > 1.2f ? Sector(_wheelVec) : -1;
                    }
                    else { _wheelVec = Vector2.zero; _wheelPick = -1; }
                    for (int n = 1; n <= 8; n++)
                        if (Input.GetKeyDown(KeyCode.Alpha0 + n)) { Issue(n - 1); _wheelOpen = false; _wheelArmed = false; return; }
                    if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
                    { _wheelOpen = false; _wheelArmed = false; return; }
                }
            }
            if (_wheelArmed && Input.GetKeyUp(_wheelKey))
            {
                _wheelArmed = false;
                if (_wheelOpen) { _wheelOpen = false; if (_wheelPick >= 0) Issue(_wheelPick); _lastWasTap = false; }
                else { _lastWasTap = true; _lastTapUp = now; }
            }
        }

        // Sector order clockwise from the top: FOLLOW, STAY, PATROL,
        // PERIMETER, VEHICLE, PEACEFUL (the mockup's wheel).
        static readonly string[] SectorEn = { "FOLLOW", "STAY", "PATROL", "PERIMETER", "VEHICLE", "PEACEFUL", "MAN GUN", "MAN RADAR" };
        static readonly string[] SectorRu = { "ЗА МНОЙ", "СТОЯТЬ", "ПАТРУЛЬ", "ПЕРИМЕТР", "ТЕХНИКА", "МИРНЫЙ", "ПУШКА", "РАДАР" };

        static int Sector(Vector2 v)
        {
            float a = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;   // 0 = up, clockwise
            if (a < 0f) a += 360f;
            return Mathf.FloorToInt(((a + 22.5f) % 360f) / 45f);
        }

        static void Issue(int sector)
        {
            Reply();
            switch (sector)
            {
                case 0: Mercs.OrderFollow(); break;
                case 1:
                    {
                        Vector3 point, facing;
                        CrosshairPoint(out point, out facing);
                        Mercs.OrderStay(point, facing);
                    }
                    break;
                case 2:
                    if (MercRide.OwnerInVehicle)
                        Toast(Loc.T("Патруль задаётся пешком, не с сиденья.", "PATROL is set on foot, not from a seat."), false);
                    else StartRoute(true);
                    break;
                case 3:
                    {
                        Vector3 point, facing;
                        CrosshairPoint(out point, out facing);
                        Mercs.OrderPerimeter(point, facing);
                    }
                    break;
                case 4: Mercs.OrderVehicle(); break;
                case 5: Mercs.TogglePeaceful(); break;
                case 6:
                case 7:
                    Vector3 aaPoint, aaFacing;
                    if (!CrosshairPoint(out aaPoint, out aaFacing))
                        Toast(Loc.T("Наведите прицел на пушку или консоль.", "Aim at the gun or console."), true);
                    else Mercs.OrderAAPost(sector == 7, aaPoint);
                    break;
            }
        }

        /// <summary>The ground point under the crosshair, at most 300 m
        /// (840 units) away; the owner's own position when the ray finds
        /// nothing. Facing = the look direction, for the watch direction.</summary>
        static bool CrosshairPoint(out Vector3 point, out Vector3 facing)
        {
            GameObject owner = Mercs.OwnerObject;
            point = owner == null ? Vector3.zero : owner.transform.position;
            facing = owner == null ? Vector3.forward : owner.transform.forward;
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return false;
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            facing = ray.direction; facing.y = 0f;
            RaycastHit hit;
            if (!Physics.Raycast(ray.origin + ray.direction * 3f, ray.direction, out hit, 840f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            Vector3 ground;
            point = RevivalGroundEnemies.TryGround(hit.point, 10f, out ground) ? ground : hit.point;
            return true;
        }

        /// <summary>Ctrl+L: undo a whitelisted player's hit toast, else put the
        /// player nearest the crosshair (within 4 degrees) on the whitelist.</summary>
        static void CtrlL()
        {
            if (_undoSteam != null && Time.time < _undoUntil)
            {
                Mercs.WhitelistRemove(_undoSteam);
                _undoSteam = null;
                return;
            }
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return;
            Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
            GameObject best = null;
            float bestCos = Mathf.Cos(4f * Mathf.Deg2Rad);
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null || go == Mercs.OwnerObject) continue;
                Vector3 d = go.transform.position + Vector3.up * 2.5f - eye;
                if (d.sqrMagnitude > 840f * 840f) continue;
                float c = Vector3.Dot(d.normalized, fwd);
                if (c > bestCos) { bestCos = c; best = go; }
            }
            if (best == null) { Toast(Loc.T("Под прицелом нет игрока.", "No player under the crosshair."), false); return; }
            string steam = Mercs.SteamOf(best);
            if (steam == null) { Toast(Loc.T("Steam-id игрока не читается.", "That player's Steam id cannot be read."), true); return; }
            if (Mercs.Whitelisted(steam)) Mercs.WhitelistRemove(steam);
            else Mercs.WhitelistAdd(steam, Mercs.NameOf(best));
        }

        // ================================================= B3b patrol route
        // PATROL from the wheel starts a route at the crosshair point. Then:
        // K tap = next point at the crosshair, Shift+K tap = at my feet, a
        // click on the open map = there; K held or Enter = go, Backspace =
        // undo, Esc = cancel. The sixth point sends them at once; a route of
        // one point (or none) is a 30 m loop around it (Mercs.OrderPatrol).
        static bool _placing;
        static readonly List<Vector3> _route = new List<Vector3>();
        static float _placeSeen, _placeKeyDown = -1f;

        internal static bool Placing { get { return _placing; } }

        static bool Shift()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }

        static float FlatDist(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        internal static void StartRoute(bool atCrosshair)
        {
            _placing = true; _route.Clear(); _placeSeen = Time.time; _placeKeyDown = -1f;
            _lastWasTap = false;
            if (atCrosshair)
            {
                Vector3 p, f;
                CrosshairPoint(out p, out f);
                _route.Add(p);
            }
            Toast(Loc.T("ПАТРУЛЬ: ", "PATROL: ") + _wheelKeyText + Loc.T(" - точка под прицелом, Shift+", " tap = point at crosshair, Shift+")
                + _wheelKeyText + Loc.T(" - здесь, клик по карте - там; удерж. ", " = here, map click = there; hold ")
                + _wheelKeyText + Loc.T(" или Enter - марш.", " or Enter = go."), false);
        }

        static void AddRoutePoint(Vector3 p)
        {
            if (!_placing) return;
            _placeSeen = Time.time;
            if (_route.Count > 0 && FlatDist(_route[_route.Count - 1], p) < 10f)
            {
                Toast(Loc.T("Слишком близко к прошлой точке.", "Too close to the last point."), false);
                return;
            }
            _route.Add(p);
            if (_route.Count >= MercOrder.MaxPoints) FinishRoute();
        }

        static void FinishRoute()
        {
            if (!_placing) return;
            _placing = false;
            List<Vector3> pts = new List<Vector3>(_route);
            _route.Clear();
            Mercs.OrderPatrol(pts);
        }

        static void CancelRoute(string why)
        {
            _placing = false; _route.Clear();
            Toast(why, false);
        }

        /// <summary>A click on the game's map (Admin's MapClickHandler.OnClick
        /// postfix): while a route is being set, the clicked point is its next
        /// point. True when the click was taken.</summary>
        internal static bool MapClick()
        {
            if (!_placing) return false;
            Vector3 point;
            if (!MapTools.MouseWorld(out point)) return true;
            Vector3 g;
            if (RevivalGroundEnemies.TryGround(point, 12f, out g)) point = g;
            int before = _route.Count;
            AddRoutePoint(point);
            if (_placing && _route.Count > before)
                Toast(Loc.T("Точка ", "Point ") + _route.Count + "/" + MercOrder.MaxPoints
                    + Loc.T(" на карте. Enter - марш.", " on the map. Enter = go."), false);
            return true;
        }

        /// <summary>Per frame while a route is being set: key reads only (the
        /// crosshair ray runs on a tap, never per frame).</summary>
        static void RouteInput(bool gameWindow)
        {
            float now = Time.time;
            if (now - _placeSeen > 120f)
            { CancelRoute(Loc.T("Маршрут патруля отменён (2 мин без точек).", "Patrol route cancelled (2 min without a point).")); return; }
            if (Input.GetKeyDown(KeyCode.Backspace) && _route.Count > 0) { _route.RemoveAt(_route.Count - 1); _placeSeen = now; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { FinishRoute(); return; }
            if (!gameWindow && Input.GetKeyDown(KeyCode.Escape))
            { CancelRoute(Loc.T("Маршрут патруля отменён.", "Patrol route cancelled.")); return; }
            if (_wheelKey == KeyCode.None) return;
            if (Input.GetKeyDown(_wheelKey)) _placeKeyDown = now;
            if (_placeKeyDown >= 0f && Input.GetKey(_wheelKey) && now - _placeKeyDown > 0.4f)
            {
                _placeKeyDown = -1f;
                FinishRoute();
                return;
            }
            if (_placeKeyDown >= 0f && Input.GetKeyUp(_wheelKey))
            {
                _placeKeyDown = -1f;
                if (gameWindow) return;           // on the map the points are clicks
                Vector3 p, f;
                if (Shift()) p = Mercs.OwnerPosition;
                else CrosshairPoint(out p, out f);
                AddRoutePoint(p);
            }
        }

        static int _crossFrame = -1;
        static bool _crossHit;
        static Vector3 _crossPoint, _crossFacing;

        /// <summary>CrosshairPoint once per frame for the OnGUI previews.</summary>
        static bool CrosshairCached(out Vector3 point)
        {
            if (_crossFrame != Time.frameCount)
            {
                _crossFrame = Time.frameCount;
                _crossHit = CrosshairPoint(out _crossPoint, out _crossFacing);
            }
            point = _crossPoint;
            return _crossHit;
        }

        /// <summary>The route being set: numbered ground markers in the view
        /// and a small panel with the keys.</summary>
        static void DrawPlacing()
        {
            Camera cam = CameraOwner.MainCamera();
            Vector3 me = Mercs.OwnerPosition;
            if (cam != null)
            {
                for (int i = 0; i < _route.Count; i++)
                {
                    Vector3 s = cam.WorldToScreenPoint(_route[i] + Vector3.up * 2f);
                    if (s.z <= 0f) continue;
                    Rect m = new Rect(s.x - 7f, Screen.height - s.y - 7f, 14f, 14f);
                    Box(new Rect(m.x - 1f, m.y - 1f, 16f, 16f), new Color(0f, 0f, 0f, 0.8f));
                    Box(m, Gold);
                    GUI.Label(new Rect(m.x + 18f, m.y - 3f, 120f, 20f), "<b>" + (i + 1) + "</b>  "
                        + (FlatDist(me, _route[i]) / 2.8f).ToString("0") + " m", _chip);
                }
            }
            float w = 560f, h = 62f;
            Rect r = new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height - h - 110f, w, h);
            Box(r, new Color(0f, 0f, 0f, 0.72f));
            Box(new Rect(r.x, r.y, 3f, r.height), Gold);
            Vector3 at;
            bool hit = CrosshairCached(out at);
            Centered(new Rect(r.x, r.y + 4f, w, 22f), "<b>" + Loc.T("МАРШРУТ ПАТРУЛЯ  ", "PATROL ROUTE  ") + _route.Count + "/"
                + MercOrder.MaxPoints + "</b>   " + (hit ? Loc.T("прицел ", "crosshair ") + (FlatDist(me, at) / 2.8f).ToString("0") + " m"
                : Loc.T("прицел: нет земли", "crosshair: no ground")), _label);
            Centered(new Rect(r.x, r.y + 28f, w, 18f), _wheelKeyText + Loc.T(" - точка   Shift+", " tap = point   Shift+") + _wheelKeyText
                + Loc.T(" - здесь   клик по карте   удерж. ", " = here   map click   hold ") + _wheelKeyText
                + Loc.T(" / Enter - марш   Backspace - назад   Esc - отмена", " / Enter = go   Backspace = undo   Esc = cancel"), _small);
            Centered(new Rect(r.x, r.y + 44f, w, 16f), _route.Count < 2
                ? Loc.T("1 точка = круг 30 м вокруг неё", "1 point = a 30 m loop around it") : "", _small);
        }

        // ======================================================== B3b map
        // Owner only (his own client draws it): each patrol route as a dashed
        // loop and each perimeter as a dashed circle in the native map ink
        // (MapInk.Raster dashes on a MapInkLayer, the no-fly zone's cadence),
        // green like the civilian patrol rings, with the merc numbers; his
        // mercs as small numbered squares; a route being set as gold points.
        sealed class Ink
        {
            internal List<Vector3> Loop;
            internal bool Closed;
            internal Vector2 Size;
            internal bool East;
            internal List<MapInk.Dash> Dashes;
            internal Vector3 LabelAt;
            internal string Label;
            internal int Seen;
        }

        static readonly Dictionary<string, Ink> _inks = new Dictionary<string, Ink>();
        static readonly List<Ink> _inkNow = new List<Ink>();
        static readonly List<string> _inkGone = new List<string>();
        static readonly Color InkGreen = new Color(0.22f, 0.78f, 0.30f, 1f);
        static readonly Color MateBlue = new Color(0.35f, 0.70f, 1.00f, 1f);
        // Map labels without a string per frame: "<b>n</b>" is built once.
        static readonly string[] Numbers = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" };
        static int _inkFrame;
        static bool _mapShown, _mapWarned;
        static GUIStyle _mapLabel;

        static void DrawMap()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            MapInkLayer layer = null;
            _mapShown = true;
            try
            {
                Component manager, texture;
                Camera camera;
                Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out camera, out world, out map)) return;
                Rect full;
                if (!MapTools.MapScreenRect(texture, camera, out full)) return;
                Rect view;
                if (!MapTools.MapViewportRect(texture, camera, out view)) view = full;
                Vector2 size = new Vector2(full.width, full.height);
                if (size.x <= 0f || size.y <= 0f) return;
                _inkFrame++;
                _inkNow.Clear();
                string scene = MercOrder.SceneKey(MapScene.Current);
                List<Mercs.Record> roster = Mercs.Roster;
                int number = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    Mercs.Record m = roster[i];
                    if (m.Dead) continue;
                    number++;
                    MercOrder o = m.Order;
                    if ((o.Mode != MercOrder.Patrol && o.Mode != MercOrder.Perimeter)
                        || MercOrder.SceneKey(o.Scene) != scene || o.Points.Length == 0) continue;
                    string key = InkKey(o);
                    Ink ink;
                    if (!_inks.TryGetValue(key, out ink)) { ink = NewInk(o); _inks[key] = ink; }
                    if (ink.Seen != _inkFrame)
                    {
                        ink.Seen = _inkFrame; ink.Label = "";
                        _inkNow.Add(ink);
                    }
                    ink.Label += (ink.Label.Length == 0 ? "" : " ") + "#" + number;
                }
                for (int i = 0; i < _inkNow.Count; i++)
                {
                    Ink ink = _inkNow[i];
                    if (ink.Dashes == null || ink.East != EastWorld.Extends || (ink.Size - size).sqrMagnitude > 0.01f)
                        BuildInk(ink, size);
                    if (ink.Dashes.Count == 0) continue;
                    if (layer == null)
                    {
                        layer = MapInkLayer.Begin("mercs", texture);
                        if (layer == null) return;
                    }
                    for (int d = 0; d < ink.Dashes.Count; d++)
                        layer.Draw(ink.Dashes[d].Bounds, ink.Dashes[d].Texture, InkGreen);
                }
                _inkGone.Clear();
                foreach (KeyValuePair<string, Ink> e in _inks) if (e.Value.Seen != _inkFrame) _inkGone.Add(e.Key);
                for (int i = 0; i < _inkGone.Count; i++) { DropInk(_inks[_inkGone[i]]); _inks.Remove(_inkGone[i]); }

                for (int i = 0; i < _inkNow.Count; i++)
                    MapText(_inkNow[i].LabelAt, _inkNow[i].Label, InkGreen, texture, camera, world, map, full, view);
                number = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    Mercs.Record m = roster[i];
                    if (m.Dead) continue;
                    number++;
                    if (m.Unit == null || m.Unit.Ai == null) continue;
                    MapSquare(m.Unit.Ai.transform.position, number < Numbers.Length ? Numbers[number] : number.ToString(),
                        Located(m.Id) ? Gold : InkGreen, texture, camera, world, map, full, view);
                }
                if (Mercs.ShelterLabel.Length > 0)
                    MapSquare(Mercs.ShelterAt, Mercs.ShelterLabel, Gold, texture, camera, world, map, full, view);
                // W: the mercs of my group mates, like squad members: a square
                // in the group colour with the owner's name.
                List<MercMates.Seen> mates = MercMates.List;
                for (int i = 0; i < mates.Count; i++)
                {
                    Component ai = mates[i].Ai;
                    if (ai == null) continue;
                    MapSquare(ai.transform.position, mates[i].Label, MateBlue, texture, camera, world, map, full, view);
                }
                for (int i = 0; i < _route.Count && _placing; i++)
                    MapSquare(_route[i], "P" + (i + 1), Gold, texture, camera, world, map, full, view);
                // W: contact pings (Revival.MercNotify.cs), a few seconds each.
                float pingNow = Time.time;
                for (int i = 0; i < MercNotify.PingSlots; i++)
                {
                    Vector3 at;
                    float age;
                    if (MercNotify.Ping(i, pingNow, out at, out age))
                        MapPing(at, age, texture, camera, world, map, full, view);
                }
            }
            catch (Exception ex)
            {
                if (!_mapWarned) { _mapWarned = true; RevivalPlugin.L.LogWarning("Mercs map: " + ex.Message); }
            }
            finally
            {
                if (layer != null) layer.End();
                else MapInkLayer.Hide("mercs");
            }
        }

        static bool MapWanted()
        {
            if (_placing || MercMates.List.Count > 0) return true;
            List<Mercs.Record> roster = Mercs.Roster;
            for (int i = 0; i < roster.Count; i++) if (!roster[i].Dead) return true;
            return false;
        }

        static void HideMap()
        {
            if (!_mapShown) return;
            _mapShown = false;
            MapInkLayer.Hide("mercs");
        }

        static string InkKey(MercOrder o)
        {
            string e = o.Encode();
            string[] c = e.Split('~');
            // mode, radius and points: the group index does not change the line.
            return c.Length >= 7 ? c[0] + "~" + c[4] + "~" + c[5] : e;
        }

        static Ink NewInk(MercOrder o)
        {
            Ink ink = new Ink();
            ink.Loop = new List<Vector3>();
            if (o.Mode == MercOrder.Perimeter)
            {
                float r = o.RadiusUnits;
                for (int i = 0; i < 48; i++)
                {
                    float a = i * Mathf.PI * 2f / 48f;
                    ink.Loop.Add(o.Centre + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r);
                }
                ink.Closed = true;
                ink.LabelAt = o.Centre + new Vector3(0f, 0f, r);
            }
            else
            {
                ink.Loop.AddRange(o.Points);
                ink.Closed = o.Points.Length > 2;
                ink.LabelAt = o.Points[0];
            }
            return ink;
        }

        static void DropInk(Ink ink)
        {
            if (ink.Dashes == null) return;
            for (int i = 0; i < ink.Dashes.Count; i++)
                if (ink.Dashes[i].Texture != null) UnityEngine.Object.Destroy(ink.Dashes[i].Texture);
            ink.Dashes = null;
        }

        /// <summary>Walk the line in screen pixels and cut it into dashes of
        /// the map ink's cadence (a closed loop ends on a full gap), then hand
        /// each mask back in the layer's 1024-square frame. Rebuilt only on a
        /// zoom or resize, never while panning (Revival.NoFly.cs BuildDashes).</summary>
        static void BuildInk(Ink ink, Vector2 size)
        {
            DropInk(ink);
            ink.Dashes = new List<MapInk.Dash>();
            ink.East = EastWorld.Extends;
            ink.Size = size;
            float artWidth = EastWorld.Extends ? 2048f : 1024f;
            List<Vector2> p = new List<Vector2>();
            for (int i = 0; i < ink.Loop.Count; i++)
            {
                Vector2 a = MapInk.Artwork(ink.Loop[i]);
                p.Add(new Vector2(a.x * size.x / artWidth, a.y * size.y / 1024f));
            }
            if (ink.Closed) p.Add(p[0]);
            if (p.Count < 2) return;
            float[] arc = new float[p.Count];
            for (int i = 1; i < p.Count; i++) arc[i] = arc[i - 1] + (p[i] - p[i - 1]).magnitude;
            float total = arc[arc.Length - 1], nominal = MapInk.DashLength + MapInk.GapLength;
            if (total < 2f) return;
            int count = Mathf.Max(ink.Closed ? 3 : 1, Mathf.RoundToInt(total / nominal));
            float period = ink.Closed ? total / count : (total + MapInk.GapLength) / Mathf.Max(1, count);
            float dash = period * MapInk.DashLength / nominal;
            for (int k = 0; k < count; k++)
            {
                List<Vector2> samples = new List<Vector2>();
                int at = 1;
                for (int j = 0; j <= 16; j++)
                {
                    float d = Mathf.Min(total, k * period + j * (dash / 16f));
                    while (at < arc.Length - 1 && arc[at] < d) at++;
                    float t = arc[at] - arc[at - 1] < .0001f ? 0f
                        : Mathf.Clamp01((d - arc[at - 1]) / (arc[at] - arc[at - 1]));
                    samples.Add(Vector2.Lerp(p[at - 1], p[at], t));
                }
                MapInk.Dash d2 = MapInk.Raster(samples, MapInk.StrokeWidth);
                d2.Bounds = new Rect(d2.Bounds.x * 1024f / size.x, d2.Bounds.y * 1024f / size.y,
                    d2.Bounds.width * 1024f / size.x, d2.Bounds.height * 1024f / size.y);
                d2.Mid = new Vector2(d2.Mid.x * 1024f / size.x, d2.Mid.y * 1024f / size.y);
                ink.Dashes.Add(d2);
            }
        }

        static bool MapGui(Vector3 point, Component texture, Camera camera, Vector2 world, Vector2 map,
                           Rect full, out Vector2 gui)
        {
            if (!MapTools.WorldToGui(point, texture, camera, world, map, out gui)) return false;
            gui = MapProject.OnPicture(gui, full);
            return true;
        }

        static void MapText(Vector3 point, string text, Color c, Component texture, Camera camera,
                            Vector2 world, Vector2 map, Rect full, Rect view)
        {
            Vector2 gui;
            if (string.IsNullOrEmpty(text) || !MapGui(point, texture, camera, world, map, full, out gui)) return;
            if (_mapLabel == null) { _mapLabel = new GUIStyle(_small); _mapLabel.richText = true; }
            string t = "<b>" + text + "</b>";
            Vector2 size = _mapLabel.CalcSize(new GUIContent(t));
            Rect r = new Rect(gui.x - size.x * 0.5f, gui.y - size.y - 4f, size.x, size.y);
            if (!view.Contains(new Vector2(r.xMin, r.yMin)) || !view.Contains(new Vector2(r.xMax, r.yMax))) return;
            Box(new Rect(r.x - 3f, r.y, r.width + 6f, r.height), new Color(0f, 0f, 0f, 0.6f));
            Color old = GUI.color;
            GUI.color = c;
            GUI.Label(r, t, _mapLabel);
            GUI.color = old;
        }

        static readonly Dictionary<string, string> _boldText = new Dictionary<string, string>();

        /// <summary>"<b>text</b>", built once per distinct label.</summary>
        static string Bold(string text)
        {
            string s;
            if (_boldText.TryGetValue(text, out s)) return s;
            if (_boldText.Count > 256) _boldText.Clear();
            s = "<b>" + text + "</b>";
            _boldText[text] = s;
            return s;
        }

        /// <summary>W: a red square that grows and fades, the spot of a contact.</summary>
        static void MapPing(Vector3 point, float age, Component texture, Camera camera,
                            Vector2 world, Vector2 map, Rect full, Rect view)
        {
            Vector2 gui;
            if (!MapGui(point, texture, camera, world, map, full, out gui) || !view.Contains(gui)) return;
            float fade = Mathf.Clamp01(1f - age);
            float pulse = Mathf.Repeat(age * MercNotify.PingSeconds * 0.8f, 1f);
            float h = 5f + 14f * pulse;
            Color c = new Color(Red.r, Red.g, Red.b, fade * (1f - 0.6f * pulse));
            Box(new Rect(gui.x - h, gui.y - h, 2f * h, 2f), c);
            Box(new Rect(gui.x - h, gui.y + h - 2f, 2f * h, 2f), c);
            Box(new Rect(gui.x - h, gui.y - h, 2f, 2f * h), c);
            Box(new Rect(gui.x + h - 2f, gui.y - h, 2f, 2f * h), c);
            Box(new Rect(gui.x - 3f, gui.y - 3f, 6f, 6f), new Color(Red.r, Red.g, Red.b, fade));
        }

        static void MapSquare(Vector3 point, string text, Color c, Component texture, Camera camera,
                              Vector2 world, Vector2 map, Rect full, Rect view)
        {
            Vector2 gui;
            if (!MapGui(point, texture, camera, world, map, full, out gui) || !view.Contains(gui)) return;
            Box(new Rect(gui.x - 4f, gui.y - 4f, 8f, 8f), new Color(0f, 0f, 0f, 0.85f));
            Box(new Rect(gui.x - 3f, gui.y - 3f, 6f, 6f), c);
            if (_mapLabel == null) { _mapLabel = new GUIStyle(_small); _mapLabel.richText = true; }
            Rect r = new Rect(gui.x + 5f, gui.y - 9f, text.Length > 3 ? 90f : 30f, 16f);
            if (!view.Contains(new Vector2(r.xMax, r.yMax))) return;
            Color old = GUI.color;
            GUI.color = c;
            GUI.Label(r, Bold(text), _mapLabel);
            GUI.color = old;
        }

        // ============================================================ drawing
        static Texture2D _white;
        static GUIStyle _label, _small, _bold, _title, _button, _chip, _center;
        static readonly Color Panel = new Color(0.07f, 0.08f, 0.07f, 0.94f);
        static readonly Color CardBg = new Color(0.13f, 0.14f, 0.12f, 1f);
        static readonly Color Gold = new Color(0.96f, 0.72f, 0.20f, 1f);
        static readonly Color Green = new Color(0.30f, 0.52f, 0.25f, 1f);
        static readonly Color Grey = new Color(0.30f, 0.30f, 0.30f, 1f);
        static readonly Color Red = new Color(0.85f, 0.28f, 0.22f, 1f);
        static readonly Color Muted = new Color(0.70f, 0.70f, 0.66f, 1f);

        static void Styles()
        {
            if (_white != null) return;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white); _white.Apply();
            _label = new GUIStyle(GUI.skin.label); _label.fontSize = 13; _label.normal.textColor = Color.white;
            _label.wordWrap = false; _label.richText = true;
            _small = new GUIStyle(_label); _small.fontSize = 11; _small.normal.textColor = Muted;
            _bold = new GUIStyle(_label); _bold.fontSize = 15;
            _title = new GUIStyle(_label); _title.fontSize = 18; _title.normal.textColor = Gold;
            _button = new GUIStyle(GUI.skin.button); _button.fontSize = 13; _button.richText = true;
            _chip = new GUIStyle(_small); _chip.normal.textColor = Gold;
            _center = new GUIStyle(_label);
        }

        static void Box(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        static void Bar(Rect r, float v, Color c)
        {
            Box(r, new Color(0.25f, 0.25f, 0.25f, 1f));
            Box(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), c);
        }

        static void Centered(Rect r, string text, GUIStyle style)
        {
            Vector2 size = style.CalcSize(new GUIContent(text));
            GUI.Label(new Rect(r.x + (r.width - size.x) * 0.5f, r.y + (r.height - size.y) * 0.5f,
                size.x + 2f, size.y), text, style);
        }

        static bool ButtonColored(Rect r, string text, Color c, bool enabled)
        {
            Box(r, enabled ? c : Grey);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.01f);
            bool hit = GUI.Button(r, "", _button) && enabled;
            if (hit) Reply();
            GUI.color = old;
            Centered(r, "<b>" + text + "</b>", _label);
            return hit;
        }

        internal static void Draw()
        {
            Styles();
            bool window = GameUi.WindowOpen;
            // W-UI2: the new trader window draws the Mercenaries page itself.
            if (window && !TraderUi.Owns) DrawTradeTab();
            else if (_tabActive) CloseTab();
            if (_wheelOpen && !window) DrawWheel();
            if (_placing && !window && !_listOpen) DrawPlacing();
            if (GameUi.State == 8 && MapWanted()) DrawMap();
            else HideMap();
            if (_listOpen) DrawList();
            if (!window && !_listOpen && (Mercs.CfgHudStrip == null || Mercs.CfgHudStrip.Value)) DrawStrip();
            if (!window) DrawLocate();
            DrawToasts();
        }

        static void DrawToasts()
        {
            float now = Time.time;
            for (int i = _toasts.Count - 1; i >= 0; i--) if (now > _toasts[i].Until) _toasts.RemoveAt(i);
            if (_toasts.Count == 0) return;
            float y = 90f;
            for (int i = 0; i < _toasts.Count; i++)
            {
                string t = _toasts[i].Text;
                Vector2 size = _label.CalcSize(new GUIContent(t));
                float w = Mathf.Min(size.x + 20f, 620f);
                Rect r = new Rect(Screen.width - w - 20f, y, w, 26f);
                Box(r, new Color(0f, 0f, 0f, 0.7f));
                Box(new Rect(r.x, r.y, 3f, r.height), _toasts[i].Warn ? Red : Gold);
                GUI.Label(new Rect(r.x + 10f, r.y + 4f, w - 12f, 20f), t, _label);
                y += 30f;
            }
        }

        static void DrawStrip()
        {
            List<Mercs.Record> roster = Mercs.Roster;
            if (roster.Count == 0) return;
            float h = 20f + 16f * roster.Count;
            Rect r = new Rect(12f, Screen.height - h - 150f, 190f, h);
            Box(r, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(new Rect(r.x + 6f, r.y + 2f, 180f, 16f), "<b>" + Loc.T("НАЁМНИКИ", "MERCS") + "</b>  (" + _listKeyText + ")", _chip);
            for (int i = 0; i < roster.Count; i++)
            {
                Mercs.Record m = roster[i];
                float y = r.y + 18f + 16f * i;
                GUI.Label(new Rect(r.x + 6f, y, 70f, 16f), Short(m.Name, 10), _small);
                Bar(new Rect(r.x + 78f, y + 5f, 40f, 6f), m.Hp, m.Dead ? Grey : HpColor(m.Hp));
                GUI.Label(new Rect(r.x + 122f, y, 60f, 16f), m.Dead ? "DEAD" : OrderText(m), _small);
                if (m.Unpaid) GUI.Label(new Rect(r.x + 170f, y, 20f, 16f), "<color=#ff5040><b>$!</b></color>", _small);
            }
        }

        static Color HpColor(float hp)
        {
            return hp > 0.6f ? new Color(0.45f, 0.75f, 0.35f, 1f) : hp > 0.3f ? Gold : Red;
        }

        static string Short(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

        static string OrderText(Mercs.Record m)
        {
            MercOrder order = m.Order;
            if (order.Survive) return Loc.T("ВЫЖИТЬ", "SURVIVE");
            if (m.Unit != null && m.Unit.Rally) return Loc.T("СБОР", "RALLY");
            string o;
            switch (order.Mode)
            {
                case MercOrder.Follow: o = Loc.T("ЗА МНОЙ", "FOLLOW"); break;
                case MercOrder.Vehicle: o = Loc.T("ТЕХНИКА", "VEHICLE"); break;
                case MercOrder.ManGun: o = Loc.T("ПУШКА", "MAN GUN"); break;
                case MercOrder.ManRadar: o = Loc.T("РАДАР", "MAN RADAR"); break;
                case MercOrder.Patrol: o = Loc.T("ПАТРУЛЬ ", "PATROL ") + order.Points.Length; break;
                case MercOrder.Perimeter: o = Loc.T("ПЕРИМ. ", "PERIM ") + order.RadiusM.ToString("0") + "m"; break;
                default: o = Loc.T("СТОИТ", "STAY"); break;
            }
            return m.Peaceful ? o + " (P)" : o;
        }

        // W-UI4: the order wheel in the UI kit's look - a round dark panel,
        // one pill per sector (accent = the pick, dimmed = not here), key caps,
        // the addressed mercs in the hub. Sector count and spacing follow
        // SectorEn. The texts come from tables or are rebuilt at 5 Hz while
        // the wheel is open (the selection and the crosshair ray with them).
        static readonly string[] KeyCaps = { "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        static float _wheelTextAt;
        static int _wheelTextPick = -2;
        static string _wheelWho, _wheelPoint;
        static bool _wheelPeaceful;
        static readonly UiMemo _wheelPointMemo = new UiMemo(), _wheelKeysMemo = new UiMemo();

        static void DrawWheel()
        {
            if (UiKit.ParagraphHeight(" ", 64f) <= 0f) return;   // builds the kit's styles on first use
            int n = SectorEn.Length;
            float now = Time.unscaledTime;
            if (now >= _wheelTextAt || _wheelTextPick != _wheelPick) WheelTexts(now);
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f, rad = UiKit.S(n > 6 ? 235f : 210f);
            UiKit.Circle(new Rect(cx - rad - UiKit.S(4f), cy - rad, (rad + UiKit.S(4f)) * 2f, (rad + UiKit.S(4f)) * 2f), UiKit.Shadow);
            UiKit.Circle(new Rect(cx - rad, cy - rad, rad * 2f, rad * 2f), UiKit.Fade(UiKit.Panel, 0.82f));
            float pw = UiKit.S(124f), ph = UiKit.S(44f);
            for (int s = 0; s < n; s++)
            {
                float a = s * (360f / n) * Mathf.Deg2Rad;
                Vector2 p = new Vector2(cx + Mathf.Sin(a) * rad * 0.66f, cy - Mathf.Cos(a) * rad * 0.66f);
                // B3c: every sector is live; PATROL is greyed in a seat.
                bool live = s != 2 || !MercRide.OwnerInVehicle;
                bool pick = s == _wheelPick;
                Rect r = new Rect(p.x - pw * 0.5f, p.y - ph * 0.5f, pw, ph);
                Color fill = pick ? (live ? UiKit.Accent : UiKit.Fade(UiKit.TextDim, 0.6f)) : UiKit.CardFill;
                UiKit.Fill(r, fill, 1);
                if (!pick) UiKit.Outline(r, UiKit.Line);
                Color fg = pick && live ? UiKit.TextOnAccent : live ? UiKit.Text : UiKit.TextDim;
                string name = s == 5 ? (_wheelPeaceful ? Loc.T("МИРНЫЙ: ВКЛ", "PEACEFUL ON") : Loc.T("МИРНЫЙ: ВЫКЛ", "PEACEFUL OFF"))
                    : Loc.T(SectorRu[s], SectorEn[s]);
                UiKit.Label(new Rect(r.x, r.y + UiKit.S(3f), r.width, UiKit.S(24f)), name, UiFont.Heading, UiFont.Center, fg);
                float kc = UiKit.S(16f);
                Rect cap = new Rect(r.x + (r.width - kc) * 0.5f - (live ? 0f : UiKit.S(30f)), r.yMax - kc - UiKit.S(4f), kc, kc);
                UiKit.Fill(cap, UiKit.Fade(pick && live ? UiKit.TextOnAccent : Color.white, 0.14f), 2);
                UiKit.Label(cap, KeyCaps[Mathf.Min(s, KeyCaps.Length - 1)], UiFont.Small, UiFont.Center, fg);
                if (!live)
                    UiKit.Label(new Rect(cap.xMax + UiKit.S(4f), cap.y, UiKit.S(70f), kc), Loc.T("пешком", "on foot"), UiFont.Small, UiFont.Left, UiKit.TextDim);
            }
            float hub = UiKit.S(64f);
            UiKit.Circle(new Rect(cx - hub, cy - hub, hub * 2f, hub * 2f), UiKit.Header);
            UiKit.Label(new Rect(cx - hub, cy - UiKit.S(24f), hub * 2f, UiKit.S(24f)), _wheelWho, UiFont.Heading, UiFont.Center, UiKit.Text);
            int lang = Loc.Lang();
            bool seat = MercRide.OwnerInVehicle;
            int kk = n * 4 + lang * 2 + (seat ? 1 : 0);
            string hint = _wheelKeysMemo.Stale(kk)
                ? _wheelKeysMemo.Set(kk, seat ? Loc.T("клавиши 1-", "keys 1-") + n : Loc.T("отпустить = приказ", "release = issue"))
                : _wheelKeysMemo.Text;
            UiKit.Label(new Rect(cx - hub, cy + UiKit.S(2f), hub * 2f, UiKit.S(20f)), hint, UiFont.Small, UiFont.Center, UiKit.TextDim);
            if (_wheelPick >= 1 && _wheelPick <= 3 && _wheelPoint != null)
            {
                float tw = UiKit.S(520f), th = UiKit.S(30f);
                Rect t = new Rect(cx - tw * 0.5f, cy + rad + UiKit.S(10f), tw, th);
                UiKit.Fill(t, UiKit.Header, 1);
                UiKit.Label(t, _wheelPoint, UiFont.Body, UiFont.Center, UiKit.Text);
            }
        }

        /// <summary>5 Hz while the wheel is open: who is addressed, the
        /// peaceful switch, and the crosshair point for PATROL/PERIMETER.</summary>
        static void WheelTexts(float now)
        {
            _wheelTextAt = now + 0.2f;
            _wheelTextPick = _wheelPick;
            List<Mercs.Record> sel = Mercs.Selection();
            _wheelWho = Mercs.Addressed(sel);
            _wheelPeaceful = sel.Count > 0 && sel[0].Peaceful;
            _wheelPoint = null;
            if (_wheelPick < 1 || _wheelPick > 3) return;
            Vector3 point;
            bool hit = CrosshairCached(out point);
            int m = hit ? Mathf.RoundToInt(Vector3.Distance(Mercs.OwnerPosition, point) / 2.8f) : -1;
            int key = _wheelPick * 1000003 + m * 2 + Loc.Lang();
            if (!_wheelPointMemo.Stale(key)) { _wheelPoint = _wheelPointMemo.Text; return; }
            string extra = _wheelPick == 2 ? Loc.T("  - первая точка маршрута", "  - first route point")
                : _wheelPick == 3 ? Loc.T("  - центр (ещё раз = радиус 15/25/40/60)", "  - centre (again = radius 15/25/40/60)") : "";
            _wheelPoint = _wheelPointMemo.Set(key, (hit ? Loc.T("точка под прицелом ", "target point ") + m + " m"
                : Loc.T("нет точки: на вашем месте", "no point: at your position")) + extra);
        }

        // ========================================================= trade tab
        static Type _uiType;
        static PropertyInfo _pUi;
        static FieldInfo _fSwitchState, _fCurrentObject, _fSwitch, _fMarketUi, _fStorageUi, _fCam;
        static FieldInfo _fMarketBtn, _fStorageBtn;
        static bool _uiLooked;
        static int _tabFrame = -10, _readFrame = -1;
        static bool _tabActive;
        static bool _tabVisible;
        static string _tabSettlement;
        static int _tabFaction = -1;
        static Component _traderAi;        // W: the storekeeper of the open trader window
        static Rect _tabRect, _safeRect, _tradeRect, _panelRect;
        static int _objectId;
        static Vector2 _cardScroll;
        static string _confirmId;
        static float _confirmUntil;
        static Component _nguiCamera;
        static FieldInfo _fUseMouse;
        static bool _mouseOff;

        static bool UiLook()
        {
            if (_uiLooked) return _uiType != null && _fSwitchState != null && _fCurrentObject != null;
            _uiLooked = true;
            _uiType = RevivalPlugin.TypeByName("UIController");
            if (_uiType == null) return false;
            _pUi = _uiType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _fSwitchState = AccessTools.Field(_uiType, "currentSwitchMarketStorageUI");
            _fCurrentObject = AccessTools.Field(_uiType, "CurrentMarketAndStorageObject");
            _fSwitch = AccessTools.Field(_uiType, "SwitchUIMarketStorageMarketplace");
            _fMarketUi = AccessTools.Field(_uiType, "HUD_MarketplaceUI");
            _fStorageUi = AccessTools.Field(_uiType, "StorageUI");
            _fCam = AccessTools.Field(_uiType, "UICam");
            Type sw = RevivalPlugin.TypeByName("SwitchUIMarketStorage");
            if (sw != null)
            {
                _fMarketBtn = AccessTools.Field(sw, "MarketBtn");
                _fStorageBtn = AccessTools.Field(sw, "StorageBtn");
            }
            if (_fSwitchState == null || _fCurrentObject == null)
                RevivalPlugin.L.LogWarning("Mercs: the trader window fields are missing - no Mercenaries tab.");
            return _fSwitchState != null && _fCurrentObject != null;
        }

        /// <summary>Once per frame: is a trader window open, whose trader,
        /// where are its tabs and body on screen.</summary>
        static void ReadTrade()
        {
            if (_readFrame == Time.frameCount) return;
            _readFrame = Time.frameCount;
            _tabVisible = false;
            if (!UiLook()) return;
            object ui = _pUi == null ? null : _pUi.GetValue(null, null);
            if (ui == null) return;
            int state = Convert.ToInt32(_fSwitchState.GetValue(ui));
            if (state != 0 && state != 1) return;
            Component obj = _fCurrentObject.GetValue(ui) as Component;
            if (obj == null) return;
            if (obj.GetInstanceID() != _objectId)
            {
                _objectId = obj.GetInstanceID();
                _tabSettlement = TraderSettlement(obj, out _tabFaction);
                _tabActive = false;
            }
            if (_tabSettlement == null) return;
            Camera cam = _fCam == null ? null : _fCam.GetValue(ui) as Camera;
            object sw = _fSwitch == null ? null : _fSwitch.GetValue(ui);
            _safeRect = _tradeRect = new Rect();
            if (cam != null && sw != null)
            {
                GameObject market = _fMarketBtn == null ? null : _fMarketBtn.GetValue(sw) as GameObject;
                GameObject storage = _fStorageBtn == null ? null : _fStorageBtn.GetValue(sw) as GameObject;
                if (market != null) MapTools.MapScreenRect(market.transform, cam, out _tradeRect);
                if (storage != null) MapTools.MapScreenRect(storage.transform, cam, out _safeRect);
            }
            Rect anchor = _tradeRect.width > 1f ? _tradeRect : _safeRect;
            if (anchor.width > 1f)
            {
                float right = Mathf.Max(_tradeRect.xMax, _safeRect.xMax);
                _tabRect = new Rect(right + 4f, anchor.y, Mathf.Max(anchor.width, 130f), anchor.height);
            }
            else _tabRect = new Rect(Screen.width * 0.5f + 180f, 40f, 150f, 34f);
            _panelRect = new Rect();
            GameObject body = null;
            if (cam != null)
            {
                object m = _fMarketUi == null ? null : _fMarketUi.GetValue(ui);
                body = state == 1 ? (m as GameObject) : null;
                if (body == null) { Component mc = m as Component; if (mc != null && state == 1) body = mc.gameObject; }
                if (body == null && _fStorageUi != null)
                {
                    object s = _fStorageUi.GetValue(ui);
                    body = s as GameObject;
                    if (body == null) { Component sc = s as Component; if (sc != null) body = sc.gameObject; }
                }
                if (body != null) MapTools.MapScreenRect(body.transform, cam, out _panelRect);
            }
            if (_panelRect.width < 400f || _panelRect.height < 250f)
                _panelRect = new Rect(Screen.width * 0.5f - 470f, _tabRect.yMax + 6f, 940f, Mathf.Min(560f, Screen.height - _tabRect.yMax - 40f));
            // Never under the tabs, and big enough for two rows of cards.
            if (_panelRect.y < _tabRect.yMax + 4f)
            {
                float cut = _tabRect.yMax + 4f - _panelRect.y;
                _panelRect.y += cut; _panelRect.height -= cut;
            }
            _panelRect.width = Mathf.Max(_panelRect.width, 700f);
            _panelRect.height = Mathf.Max(_panelRect.height, 330f);
            _tabVisible = true;
        }

        /// <summary>The settlement key for a trader object: civilian, looter
        /// or traitor by the storekeeper's own faction; null for anything else
        /// (a stash box, a neutral trader).</summary>
        static string TraderSettlement(Component obj, out int faction)
        {
            faction = -1;
            _traderAi = null;
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                Component ai = npc == null ? null : obj.GetComponentInParent(npc);
                if (ai == null && npc != null) ai = obj.GetComponentInChildren(npc);
                if (ai == null) return null;
                _traderAi = ai;
                FieldInfo fo = AccessTools.Field(npc, "MainOptions");
                object opt = fo == null ? null : fo.GetValue(ai);
                FieldInfo ff = opt == null ? null : AccessTools.Field(opt.GetType(), "MyFraction");
                object f = ff == null ? null : ff.GetValue(opt);
                if (f == null) return null;
                faction = Convert.ToInt32(f);
                return faction == 2 ? "civilian" : faction == 1 ? "looter" : faction == 6 ? "traitor" : null;
            }
            catch { return null; }
        }

        /// <summary>W: where the trader of the open Mercenaries tab stands (a
        /// merc hired there steps out beside him).</summary>
        internal static bool TraderPoint(out Vector3 point)
        {
            point = Vector3.zero;
            if ((!_tabVisible && !TraderUi.Owns) || _tabSettlement == null || _traderAi == null) return false;
            point = _traderAi.transform.position;
            return true;
        }

        static void CloseTab()
        {
            _tabActive = false;
            NguiMouse(true);
        }

        /// <summary>NGUI's UICamera.useMouse off while the pointer is over the
        /// hire cards, so a click never reaches the trade list underneath.</summary>
        static void NguiMouse(bool on)
        {
            if (on == !_mouseOff) return;
            try
            {
                if (_nguiCamera == null)
                {
                    object ui = _pUi == null ? null : _pUi.GetValue(null, null);
                    Camera cam = ui == null || _fCam == null ? null : _fCam.GetValue(ui) as Camera;
                    Type t = RevivalPlugin.TypeByName("UICamera");
                    _nguiCamera = cam == null || t == null ? null : cam.GetComponent(t);
                    _fUseMouse = t == null ? null : AccessTools.Field(t, "useMouse");
                }
                if (_nguiCamera == null || _fUseMouse == null) return;
                _fUseMouse.SetValue(_nguiCamera, on);
                _mouseOff = !on;
            }
            catch { }
        }

        static void DrawTradeTab()
        {
            ReadTrade();
            if (!_tabVisible) { if (_tabActive) CloseTab(); return; }
            _tabFrame = Time.frameCount;
            Event e = Event.current;
            if (_tabActive && e.type == EventType.MouseDown
                && (_safeRect.Contains(e.mousePosition) || _tradeRect.Contains(e.mousePosition)))
                CloseTab();
            string label = Loc.T("Наёмники", "Mercenaries");
            if (ButtonColored(_tabRect, label, _tabActive ? new Color(0.55f, 0.42f, 0.10f, 1f) : new Color(0.18f, 0.18f, 0.16f, 0.95f), true))
            {
                _tabActive = !_tabActive;
                if (_tabActive) Mercs.RequestRoster(0f);
                else NguiMouse(true);
            }
            if (!_tabActive) return;
            Vector2 mouse = e.mousePosition;
            NguiMouse(!_panelRect.Contains(mouse));
            DrawHire(_panelRect);
        }

        /// <summary>W-UI2: the trader window (TraderUi) opened this trader:
        /// remember its settlement for the embedded Mercenaries page. False
        /// when this trader offers no mercenaries (no page then).</summary>
        internal static bool EmbedFor(Component trader)
        {
            if (trader == null) return false;
            _objectId = trader.GetInstanceID();
            _tabSettlement = TraderSettlement(trader, out _tabFaction);
            _tabActive = false;
            return _tabSettlement != null;
        }

        /// <summary>W-UI3: the settlement key of the trader the window opened
        /// (civilian, looter, traitor), null for none.</summary>
        internal static string EmbeddedSettlement { get { return _tabSettlement; } }

        static void DrawHire(Rect r)
        {
            Box(r, Panel);
            GUI.BeginGroup(r);
            float w = r.width;
            GUI.Label(new Rect(14f, 8f, 360f, 26f), "<b>" + Loc.T("НАЁМНИКИ", "HIRE MERCENARIES") + "</b>", _title);
            string where = _tabSettlement == "civilian" ? Loc.T("мирное поселение", "civilian settlement")
                : _tabSettlement == "looter" ? Loc.T("поселение мародёров", "looter settlement")
                : Loc.T("лагерь предателей + военный городок", "traitor camp + military town");
            GUI.Label(new Rect(14f, 32f, 520f, 18f), where + "  -  " + Loc.T("ваша сторона: ", "your side: ")
                + Mercs.FactionLabel(Mercs.FactionOf(Mercs.OwnerObject)), _small);
            int money = Mercs.Money;
            GUI.Label(new Rect(w - 300f, 8f, 290f, 22f), Loc.T("Деньги  ", "Money  ") + "<b>"
                + (money < 0 ? "?" : Mercs.Money0(money)) + "</b>", _bold);
            GUI.Label(new Rect(w - 300f, 30f, 290f, 18f), Loc.T("Наёмники ", "Mercs ") + Mercs.AliveCount + " / " + Mercs.Cap, _small);

            List<Mercs.Profile> cards = Mercs.ProfilesFor(_tabSettlement);
            float cardW = Mathf.Floor((w - 28f - 3f * 10f) / 4f), cardH = 222f;
            int rows = (cards.Count + 3) / 4;
            float footer = 58f;
            Rect view = new Rect(0f, 56f, w, r.height - 56f - footer);
            Rect content = new Rect(0f, 0f, w - 20f, Mathf.Max(view.height - 2f, rows * (cardH + 10f)));
            _cardScroll = GUI.BeginScrollView(view, _cardScroll, content);
            if (cards.Count == 0)
                GUI.Label(new Rect(14f, 10f, w - 30f, 20f), Loc.T("Этот торговец не предлагает наёмников.", "This trader offers no mercenaries."), _label);
            for (int i = 0; i < cards.Count; i++)
            {
                Rect c = new Rect(14f + (i % 4) * (cardW + 10f), (i / 4) * (cardH + 10f), cardW, cardH);
                DrawCard(c, cards[i], i + 1);
            }
            GUI.EndScrollView();

            float fy = r.height - footer + 6f;
            GUI.Label(new Rect(14f, fy, w - 330f, 18f), Loc.T("Содержание - за каждые 24 игровых часа службы. Без оплаты: ",
                "Upkeep every 24 in-game h while deployed. Unpaid: ") + Mercs.GraceHours
                + Loc.T(" ч отсрочки, потом уходит.", " h grace, then desertion."), _small);
            string note = Mercs.LinkText();
            GUI.Label(new Rect(14f, fy + 18f, w - 330f, 18f), note + "  " + Loc.T("Профили: ", "Profiles: ") + Mercs.ProfileSource, _small);
            int due = 0;
            for (int i = 0; i < Mercs.Roster.Count; i++) if (Mercs.Roster[i].Unpaid) due++;
            if (due > 0 && ButtonColored(new Rect(w - 310f, fy, 140f, 30f), Loc.T("Оплатить долги (", "Pay debts (") + due + ")", Red, true))
                Mercs.PayAllDue();
            if (ButtonColored(new Rect(w - 160f, fy, 146f, 30f), Loc.T("Мои наёмники (", "My mercs (") + _listKeyText + ")",
                new Color(0.2f, 0.2f, 0.18f, 1f), true)) { _listOpen = true; _listTab = 0; }
            GUI.EndGroup();

            // 1-9 hire by number while the pointer is over the cards.
            Event e = Event.current;
            if (e.type == EventType.KeyDown && r.Contains(e.mousePosition))
            {
                int n = e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9 ? e.keyCode - KeyCode.Alpha0 : 0;
                if (n > 0 && n <= cards.Count) { Click(cards[n - 1]); e.Use(); }
            }
        }

        static void DrawCard(Rect c, Mercs.Profile p, int number)
        {
            bool flash = _flashProfile == p.Id && Time.time < _flashUntil;
            Box(c, flash ? new Color(0.35f, 0.30f, 0.12f, 1f) : CardBg);
            // A silhouette plate in place of a portrait.
            Rect face = new Rect(c.x + 8f, c.y + 8f, 44f, 48f);
            Box(face, new Color(0.25f, 0.26f, 0.22f, 1f));
            Centered(face, "<b>" + p.Name.Substring(0, 1) + "</b>", _title);
            GUI.Label(new Rect(c.x + 60f, c.y + 6f, c.width - 64f, 22f), "<b>" + p.Name + "</b>", _bold);
            GUI.Label(new Rect(c.x + 60f, c.y + 26f, c.width - 64f, 20f), "<color=#f5b833><b>" + Mercs.Money0(p.Price) + "</b></color>", _label);
            GUI.Label(new Rect(c.x + 60f, c.y + 42f, c.width - 64f, 16f), Loc.T("найм", "hire") + (p.Settlement == "mtown" ? Loc.T("  (военный городок)", "  (military town)") : ""), _small);
            float y = c.y + 62f;
            GUI.Label(new Rect(c.x + 8f, y, 80f, 16f), Loc.T("Содержание", "Upkeep"), _small);
            GUI.Label(new Rect(c.x + 84f, y, c.width - 90f, 16f), "<b>" + Mercs.Money0(p.Upkeep) + Loc.T(" / сутки", " / day") + "</b>", _small);
            y += 18f;
            GUI.Label(new Rect(c.x + 8f, y, c.width - 16f, 16f), Loc.T("Оружие", "Weapon"), _small);
            GUI.Label(new Rect(c.x + 8f, y + 14f, c.width - 16f, 18f), Short(p.WeaponLabel, 34), _label);
            y += 34f;
            GUI.Label(new Rect(c.x + 8f, y, c.width - 16f, 16f), Loc.T("Броня", "Armour"), _small);
            GUI.Label(new Rect(c.x + 8f, y + 14f, c.width - 16f, 18f), Short(p.ArmourLabel, 40), _small);
            Bar(new Rect(c.x + 8f, y + 32f, c.width - 16f, 5f), p.Protection, new Color(0.45f, 0.62f, 0.85f, 1f));
            y += 42f;
            string chips = "";
            if (p.Precise > 0) chips += Loc.T("точный +", "precise +") + p.Precise + "%  ";
            if (p.Fast > 0) chips += Loc.T("быстрый +", "fast +") + p.Fast + "%  ";
            if (p.AAGunner > 0) chips += Loc.T("зенитчик +", "AA gunner +") + p.AAGunner + "%  ";
            if (p.Tanky > 0) chips += Loc.T("живучий +", "tanky +") + p.Tanky + "%";
            GUI.Label(new Rect(c.x + 8f, y, c.width - 16f, 18f), chips.Length == 0 ? Loc.T("без особенностей", "no traits") : chips,
                chips.Length == 0 ? _small : _chip);
            string block = Mercs.HireBlock(p);
            bool confirm = _confirmId == p.Id && Time.time < _confirmUntil;
            string text = block != null ? block
                : confirm ? Loc.T("ПОДТВЕРДИТЬ ", "CONFIRM ") + Mercs.Money0(p.Price)
                : Loc.T("НАНЯТЬ", "HIRE") + "  (" + number + ")";
            if (ButtonColored(new Rect(c.x + 8f, c.yMax - 38f, c.width - 16f, 30f), text,
                confirm ? new Color(0.62f, 0.45f, 0.10f, 1f) : Green, block == null))
                Click(p);
        }

        /// <summary>Hiring costs a fortune: the first click (or number key)
        /// arms the card for 4 s, the second hires.</summary>
        static void Click(Mercs.Profile p)
        {
            if (Mercs.HireBlock(p) != null) { Toast(Mercs.HireBlock(p), true); return; }
            if (_confirmId == p.Id && Time.time < _confirmUntil)
            {
                _confirmId = null;
                Mercs.Hire(p);
                return;
            }
            _confirmId = p.Id; _confirmUntil = Time.time + 4f;
        }

        // ============================================================== list
        const int ListWindowId = 0x4D455243;
        static Rect _listRect = new Rect(80f, 90f, 820f, 524f);
        static Vector2 _listScroll, _wlScroll, _roomScroll;
        static string _filter = "";
        static int _dismissId;
        static float _dismissUntil;

        static void DrawList()
        {
            CursorTracker.Restoring = true;
            try { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
            finally { CursorTracker.Restoring = false; }
            _listRect.width = Mathf.Min(_listRect.width, Screen.width - 20f);
            _listRect = GUI.Window(ListWindowId, _listRect, ListWindow, "");
        }

        static void RestoreCursor()
        {
            if (!CursorTracker.SawCall) return;
            CursorTracker.Restoring = true;
            try { Cursor.lockState = CursorTracker.DesiredLock; Cursor.visible = CursorTracker.DesiredVisible; }
            finally { CursorTracker.Restoring = false; }
        }

        static void ListWindow(int id)
        {
            Rect r = new Rect(0f, 0f, _listRect.width, _listRect.height);
            Box(r, Panel);
            GUI.Label(new Rect(14f, 8f, 260f, 26f), "<b>" + Loc.T("МОИ НАЁМНИКИ", "MY MERCENARIES") + "</b>", _title);
            if (ButtonColored(new Rect(240f, 10f, 110f, 24f), Loc.T("Наёмники ", "Mercs ") + Mercs.AliveCount + "/" + Mercs.Cap,
                _listTab == 0 ? new Color(0.55f, 0.42f, 0.10f, 1f) : Grey, true)) _listTab = 0;
            if (ButtonColored(new Rect(356f, 10f, 110f, 24f), Loc.T("Белый список ", "Whitelist ") + Mercs.Whitelist.Count,
                _listTab == 1 ? new Color(0.55f, 0.42f, 0.10f, 1f) : Grey, true)) _listTab = 1;
            int money = Mercs.Money;
            GUI.Label(new Rect(r.width - 250f, 10f, 200f, 20f), Loc.T("Деньги ", "Money ") + (money < 0 ? "?" : Mercs.Money0(money)), _label);
            if (ButtonColored(new Rect(r.width - 40f, 8f, 30f, 24f), "X", Red, true)) { _listOpen = false; RestoreCursor(); }
            if (_listTab == 0) MercRows(r); else WhitelistTab(r);
            GUI.DragWindow(new Rect(0f, 0f, r.width, 36f));
        }

        static void MercRows(Rect r)
        {
            float x0 = 14f, y0 = 44f;
            string[] heads = { "", Loc.T("Имя", "Name"), Loc.T("Здоровье", "Health"), Loc.T("Приказ", "Order"),
                Loc.T("Состояние", "State"), Loc.T("Оплата", "Upkeep due"), Loc.T("Дист.", "Dist") };
            float[] cols = { 0f, 26f, 190f, 330f, 450f, 560f, 720f };
            for (int i = 1; i < heads.Length; i++) GUI.Label(new Rect(x0 + cols[i], y0, 140f, 18f), heads[i], _small);
            List<Mercs.Record> roster = Mercs.Roster;
            Rect view = new Rect(0f, y0 + 20f, r.width, r.height - y0 - 20f - 128f);
            Rect content = new Rect(0f, 0f, r.width - 20f, Mathf.Max(view.height - 2f, roster.Count * 40f));
            _listScroll = GUI.BeginScrollView(view, _listScroll, content);
            Vector3 me = Mercs.OwnerPosition;
            for (int i = 0; i < roster.Count; i++)
            {
                Mercs.Record m = roster[i];
                float y = i * 40f;
                Box(new Rect(x0 - 4f, y, r.width - 40f, 36f), m.Selected ? new Color(0.22f, 0.19f, 0.10f, 1f) : CardBg);
                bool sel = GUI.Toggle(new Rect(x0 + cols[0], y + 9f, 20f, 20f), m.Selected, "");
                if (sel != m.Selected) m.Selected = sel;
                Mercs.Profile p = Mercs.ProfileById(m.ProfileId);
                GUI.Label(new Rect(x0 + cols[1], y + 2f, 160f, 18f), "<b>" + m.Name + "</b>", _label);
                GUI.Label(new Rect(x0 + cols[1], y + 18f, 160f, 16f), (p == null ? m.ProfileId : p.Name) + (m.Session ? " (test)" : ""), _small);
                Bar(new Rect(x0 + cols[2], y + 12f, 90f, 8f), m.Hp, m.Dead ? Grey : HpColor(m.Hp));
                GUI.Label(new Rect(x0 + cols[2] + 94f, y + 7f, 50f, 18f), Mathf.RoundToInt(m.Hp * 100f).ToString(), _small);
                GUI.Label(new Rect(x0 + cols[3], y + 9f, 120f, 18f), m.Dead ? "-" : OrderText(m), _label);
                string state = m.Dead ? "<color=#ff5040>" + Loc.T("убит", "dead") + "</color>"
                    : m.Unit != null && m.Unit.Deserting ? "<color=#ff5040>" + Loc.T("уходит", "deserting") + "</color>"
                    : m.Unit == null ? Loc.T("прибывает", "arriving")
                    : m.Combat ? "<color=#ff8060>" + Loc.T("в бою", "in combat") + "</color>"
                    : MercRide.StateText(m.Unit) != null ? MercRide.StateText(m.Unit)
                    : Loc.T("в строю", "idle");
                GUI.Label(new Rect(x0 + cols[4], y + 9f, 110f, 18f), state, _label);
                string due;
                if (m.Dead) due = "-";
                else if (m.Unpaid)
                    due = "<color=#ff5040><b>" + Loc.T("НЕ ОПЛАЧЕН - уйдёт через ", "UNPAID - deserts in ")
                        + Math.Max(0.0, Mercs.GraceHours - (m.Deployed - m.PaidUntil)).ToString("0.0", CultureInfo.InvariantCulture) + " h</b></color>";
                else due = Loc.T("через ", "in ") + (m.PaidUntil - m.Deployed).ToString("0.0", CultureInfo.InvariantCulture) + " h";
                GUI.Label(new Rect(x0 + cols[5], y + 9f, 170f, 18f), due, _small);
                string dist = m.Unit == null || m.Unit.Ai == null ? "-"
                    : (Vector3.Distance(me, m.Unit.Ai.transform.position) / 2.8f).ToString("0") + " m";
                GUI.Label(new Rect(x0 + cols[6], y + 9f, 60f, 18f), dist, _small);
            }
            if (roster.Count == 0)
                GUI.Label(new Rect(x0, 10f, r.width - 40f, 40f), Mercs.Support != 1 ? Mercs.LinkText()
                    : Loc.T("Наёмников нет. Нанять: у торговца поселения, вкладка \"Наёмники\".",
                    "No mercenaries. Hire them at a settlement trader, tab \"Mercenaries\"."), _label);
            GUI.EndScrollView();

            float by = r.height - 154f;
            List<Mercs.Record> selection = Mercs.Selection();
            if (ButtonColored(new Rect(x0, by, 130f, 28f), Loc.T("Оплатить всё (P)", "Pay all due (P)"), Green, true)) Mercs.PayAllDue();
            Mercs.Record one = selection.Count == 1 ? selection[0] : null;
            if (ButtonColored(new Rect(x0 + 136f, by, 150f, 28f), one == null ? Loc.T("Оплатить выбранного", "Pay selected")
                : Loc.T("Оплатить ", "Pay ") + Short(one.Name, 10), Green, one != null && one.Unpaid)) Mercs.Pay(one, true);
            bool peaceful = selection.Count > 0 && selection[0].Peaceful;
            if (ButtonColored(new Rect(x0 + 292f, by, 130f, 28f), (peaceful ? Loc.T("Мирный: ВКЛ", "Peaceful: ON") : Loc.T("Мирный: ВЫКЛ", "Peaceful: OFF")),
                new Color(0.2f, 0.2f, 0.18f, 1f), selection.Count > 0)) Mercs.TogglePeaceful();
            if (ButtonColored(new Rect(x0 + 428f, by, 110f, 28f), Loc.T("За мной", "Follow me"), new Color(0.2f, 0.2f, 0.18f, 1f), selection.Count > 0))
                Mercs.OrderFollow();
            if (ButtonColored(new Rect(x0 + 544f, by, 110f, 28f), Loc.T("Стоять здесь", "Stay here"), new Color(0.2f, 0.2f, 0.18f, 1f), selection.Count > 0))
            {
                GameObject o = Mercs.OwnerObject;
                if (o != null) Mercs.OrderStay(o.transform.position, o.transform.forward);
            }
            bool armed = one != null && _dismissId == one.Id && Time.time < _dismissUntil;
            if (ButtonColored(new Rect(x0 + 660f, by, 120f, 28f), armed ? Loc.T("Точно уволить?", "Really dismiss?") : Loc.T("Уволить...", "Dismiss..."),
                armed ? Red : new Color(0.35f, 0.16f, 0.14f, 1f), one != null && !one.Dead))
            {
                if (armed) { Mercs.Dismiss(one); _dismissId = 0; }
                else { _dismissId = one.Id; _dismissUntil = Time.time + 4f; }
            }
            // B3b: the work orders without the wheel, at the owner's own spot.
            float by2 = by + 32f;
            Color dark = new Color(0.2f, 0.2f, 0.18f, 1f);
            if (ButtonColored(new Rect(x0, by2, 160f, 28f), Loc.T("Патруль вокруг меня", "Patrol around me"), dark, selection.Count > 0))
                Mercs.OrderPatrol(new List<Vector3>());
            if (ButtonColored(new Rect(x0 + 166f, by2, 170f, 28f), Loc.T("Маршрут по карте...", "Route on the map..."), dark, selection.Count > 0))
            {
                _listOpen = false; RestoreCursor();
                StartRoute(false);
                Toast(Loc.T("Откройте карту и кликните 2-6 точек, Enter - марш.",
                    "Open the map and click 2-6 points, Enter = go."), false);
            }
            if (ButtonColored(new Rect(x0 + 342f, by2, 160f, 28f), Loc.T("Периметр здесь", "Perimeter here"), dark, selection.Count > 0))
            {
                GameObject o = Mercs.OwnerObject;
                if (o != null) Mercs.OrderPerimeter(o.transform.position, o.transform.forward);
            }
            MercOrder perim = null;
            for (int i = 0; i < selection.Count && perim == null; i++)
                if (selection[i].Order.Mode == MercOrder.Perimeter) perim = selection[i].Order;
            if (ButtonColored(new Rect(x0 + 508f, by2, 150f, 28f), perim == null ? Loc.T("Радиус периметра", "Perimeter radius")
                : Loc.T("Радиус ", "Radius ") + perim.RadiusM.ToString("0") + " m", dark, perim != null))
                Mercs.CyclePerimeterRadius();
            // B3c: FOLLOW MY VEHICLE without the wheel.
            if (ButtonColored(new Rect(x0 + 664f, by2, 160f, 28f), Loc.T("За моей техникой", "Follow my vehicle"), dark, selection.Count > 0))
                Mercs.OrderVehicle();
            // W: the merc toasts (Revival.MercNotify.cs) - filter, radio click, map ping.
            float by3 = by2 + 32f;
            if (ButtonColored(new Rect(x0, by3, 200f, 28f), MercNotify.FilterText(), dark, true)) MercNotify.CycleFilter();
            bool click = MercNotify.CfgClick == null || MercNotify.CfgClick.Value;
            if (ButtonColored(new Rect(x0 + 206f, by3, 170f, 28f), click ? Loc.T("Щелчок рации: ВКЛ", "Radio click: ON")
                : Loc.T("Щелчок рации: ВЫКЛ", "Radio click: OFF"), dark, MercNotify.CfgClick != null))
                MercNotify.CfgClick.Value = !click;
            bool ping = MercNotify.CfgPing == null || MercNotify.CfgPing.Value;
            if (ButtonColored(new Rect(x0 + 382f, by3, 170f, 28f), ping ? Loc.T("Метка на карте: ВКЛ", "Map ping: ON")
                : Loc.T("Метка на карте: ВЫКЛ", "Map ping: OFF"), dark, MercNotify.CfgPing != null))
                MercNotify.CfgPing.Value = !ping;
            GUI.Label(new Rect(x0, by3 + 34f, r.width - 30f, 36f),
                Loc.T("Галочки = кому приказ (Ctrl+1..5, Ctrl+0 все). ", "Checked rows are who the orders address (Ctrl+1..5, Ctrl+0 = all). ")
                + _wheelKeyText + Loc.T(" удерж. - меню приказов, двойное нажатие - за мной (в машине - за моей техникой). Увольнение без возврата денег.",
                    " held = order wheel, double tap = follow me (in a vehicle: follow my vehicle). Dismiss asks twice, no refund."), _small);
        }

        static void WhitelistTab(Rect r)
        {
            GUI.Label(new Rect(14f, 42f, r.width - 30f, 18f), Loc.T("Игроков из списка ваши наёмники никогда не атакуют. Хранится на мастер-сервере вместе с наёмниками.",
                "Players on this list are never attacked by your mercs, whatever their faction. Stored on the master server with your roster.")
                + (Mercs.Support == 1 ? "" : Loc.T(" (Сейчас только на эту сессию.)", " (This session only for now.)")), _small);
            float half = (r.width - 42f) * 0.5f;
            GUI.Label(new Rect(14f, 64f, half, 20f), "<b>" + Loc.T("БЕЛЫЙ СПИСОК", "WHITELIST") + "</b>", _label);
            Rect left = new Rect(14f, 88f, half, r.height - 150f);
            List<Mercs.WlEntry> wl = Mercs.Whitelist;
            _wlScroll = GUI.BeginScrollView(left, _wlScroll, new Rect(0f, 0f, half - 20f, Mathf.Max(left.height - 2f, wl.Count * 30f)));
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < wl.Count; i++)
            {
                float y = i * 30f;
                Box(new Rect(0f, y, half - 22f, 26f), CardBg);
                GUI.Label(new Rect(6f, y + 4f, 150f, 20f), "<b>" + wl[i].Name + "</b>", _label);
                GameObject online = null;
                for (int k = 0; k < players.Count; k++)
                    if (players[k] != null && Mercs.SteamOf(players[k]) == wl[i].Steam) { online = players[k]; break; }
                GUI.Label(new Rect(160f, y + 5f, 150f, 20f), online == null ? Loc.T("не в сети", "offline")
                    : "<color=#80d060>" + Loc.T("в сети, ", "online, ") + (Vector3.Distance(Mercs.OwnerPosition, online.transform.position) / 2.8f).ToString("0") + " m</color>", _small);
                if (ButtonColored(new Rect(half - 96f, y + 2f, 70f, 22f), Loc.T("убрать", "remove"), Red, true))
                { Mercs.WhitelistRemove(wl[i].Steam); break; }
            }
            GUI.EndScrollView();

            float rx = 28f + half;
            GUI.Label(new Rect(rx, 64f, 180f, 20f), "<b>" + Loc.T("ИГРОКИ В МИРЕ", "PLAYERS IN THIS WORLD") + "</b>", _label);
            _filter = GUI.TextField(new Rect(rx + 190f, 64f, half - 190f, 20f), _filter ?? "", 24);
            Rect right = new Rect(rx, 88f, half, r.height - 150f);
            int myFaction = Mercs.FactionOf(Mercs.OwnerObject);
            List<GameObject> shown = new List<GameObject>();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null || go == Mercs.OwnerObject) continue;
                if (_filter.Length > 0 && Mercs.NameOf(go).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(go);
            }
            _roomScroll = GUI.BeginScrollView(right, _roomScroll, new Rect(0f, 0f, half - 20f, Mathf.Max(right.height - 2f, shown.Count * 30f)));
            for (int i = 0; i < shown.Count; i++)
            {
                GameObject go = shown[i];
                float y = i * 30f;
                Box(new Rect(0f, y, half - 22f, 26f), CardBg);
                string name = Mercs.NameOf(go);
                int f = Mercs.FactionOf(go);
                string steam = Mercs.SteamOf(go);
                GUI.Label(new Rect(6f, y + 4f, 120f, 20f), "<b>" + Short(name, 14) + "</b>", _label);
                GUI.Label(new Rect(126f, y + 5f, 70f, 20f), Mercs.FactionLabel(f), _small);
                GUI.Label(new Rect(196f, y + 5f, 60f, 20f), (Vector3.Distance(Mercs.OwnerPosition, go.transform.position) / 2.8f).ToString("0") + " m", _small);
                bool listed = steam != null && Mercs.Whitelisted(steam);
                string rel = listed ? "<color=#f5b833>" + Loc.T("в списке", "listed") + "</color>"
                    : f == myFaction ? "<color=#80d060>" + Loc.T("свой", "friendly") + "</color>"
                    : "<color=#ff6050>" + Loc.T("враг", "hostile") + "</color>";
                GUI.Label(new Rect(258f, y + 5f, 70f, 20f), rel, _small);
                if (!listed && steam != null && ButtonColored(new Rect(half - 96f, y + 2f, 70f, 22f), "+ " + Loc.T("добавить", "add"), Green, true))
                    Mercs.WhitelistAdd(steam, name);
            }
            GUI.EndScrollView();
            GUI.Label(new Rect(14f, r.height - 56f, r.width - 30f, 40f),
                Loc.T("\"Свой\" - ваша фракция, её наёмники не трогают и так. Быстро: навести на игрока и Ctrl+",
                      "'friendly' = your faction: mercs never shoot them anyway. Quick add: aim at a player and press Ctrl+")
                + _listKeyText + ".", _small);
        }
    }
}
