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
    internal static partial class MercUi
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

        internal static void OrderReply(string text, bool warn)
        {
            Reply();
            Toast(text, warn);
        }

        static void Note(string text, int kind, bool warn)
        {
            if (!MercPageNote.Pass(MercPage.NoteMode, kind, Time.frameCount == _replyFrame)) return;
            if (Time.frameCount == _replyFrame) MercNotify.OrderClick(warn);
            int nativeType = kind == MercPageNote.Death ? NativeMessage.Kill
                : warn ? NativeMessage.Warning : NativeMessage.Group;
            if (NativeMessage.Show(text, nativeType)) return;
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
            VanillaUi.Label(t, _locateText, _locateStyle);
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
            Mercs.MedicineInteractionTick();
            CacheStrip();
            bool quickOwns = QuickInput();
            if (Input.anyKeyDown && !Input.GetMouseButtonDown(0)) Reply();
            if (_tabActive && Time.frameCount - _tabFrame > 2) CloseTab();
            if (_tabActive)
            {
                Vector3 pointer = Input.mousePosition;
                NguiMouse(!_listOpen && !VanillaSkin.Asking && !_panelRect.Contains(new Vector2(pointer.x, Screen.height - pointer.y)));
            }
            if (Mercs.Roster.Count == 0 && !_listOpen && !_wheelOpen)
            {
                if (_listKey != KeyCode.None && Input.GetKeyDown(_listKey) && !Ctrl() && GameplayCursor.CommandUiState == 0
                    && !Admin.IsOpen) _listOpen = true;
                return;
            }
            int uiState = GameplayCursor.CommandUiState;
            bool gameWindow = uiState != 0;
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
            if (_placing && !_listOpen && (!gameWindow || uiState == 8))
            { _wheelOpen = false; _wheelArmed = false; RouteInput(gameWindow); return; }
            if (gameWindow || _listOpen || !GameplayCursor.CanCommand) { _wheelOpen = false; _wheelArmed = false; return; }
            if (!quickOwns) Wheel();
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
                _kDownAt = now; _wheelArmed = true; _wheelVec = Vector2.zero; _wheelPick = -1; _wheelScrollPick = false;
            }
            if (_wheelArmed && (Input.GetKey(_wheelKey) || Input.GetKeyUp(_wheelKey)))
            {
                if (!_wheelOpen && now - _kDownAt > 0.22f) _wheelOpen = true;
                if (_wheelOpen)
                {
                    if (!PickWheel()) { _wheelOpen = false; _wheelArmed = false; return; }
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
        // PERIMETER, VEHICLE, PEACEFUL, AIR DEFENCE, TAKE COVER and
        // (merc-attack-orders) ATTACK on key 9.
        static readonly string[] SectorEn = { "FOLLOW", "STAY", "PATROL", "PERIMETER", "VEHICLE", "PEACEFUL", "Man air defence", "TAKE COVER", "ATTACK", "FETCH AIRDROP", "MEDIC DUTY" };
        static readonly string[] SectorRu = { "ЗА МНОЙ", "СТОЯТЬ", "ПАТРУЛЬ", "ПЕРИМЕТР", "ТЕХНИКА", "МИРНЫЙ", "ЗАНЯТЬ ПВО", "В УКРЫТИЕ", "АТАКА", "ЗАБРАТЬ ГРУЗ", "САНИТАР" };
        const int AttackSector = 8;

        static int Sector(Vector2 v)
        {
            float a = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;   // 0 = up, clockwise
            if (a < 0f) a += 360f;
            float step = 360f / SectorEn.Length;
            return Mathf.FloorToInt(((a + step * 0.5f) % 360f) / step) % SectorEn.Length;
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
                case 6: Mercs.ToggleAirDefence(); break;
                case 7: Mercs.OrderRaidCover(); break;
                case AttackSector: AttackAtCrosshair(); break;
                case 9: MercFetch.Start(); break;
                case 10: Mercs.ToggleMedics(); break;
            }
        }

        // ============================================== merc-attack-orders
        // ATTACK from the wheel: the ground under the crosshair (300 m) is the
        // objective; with none (sky, too far) the flat look direction gives a
        // bounded endpoint - 150 m, or the first of 110 / 75 / 40 m with
        // ground. From the map: the list's "Attack on the map..." and a click.
        static readonly float[] DirectionSteps = { 420f, 308f, 210f, 112f };

        static void AttackAtCrosshair()
        {
            Vector3 point, facing;
            if (CrosshairPoint(out point, out facing)) { Mercs.OrderAttack(point, MercOrder.AtPoint); return; }
            Vector3 end;
            if (AttackDirection(facing, out end)) Mercs.OrderAttack(end, MercOrder.AtDirection);
            else Toast(Loc.T("В этом направлении нет земли - наведите на землю или задайте точку на карте (L).",
                "No ground in that direction - aim at the ground or set a map point (L list)."), true);
        }

        static bool AttackDirection(Vector3 facing, out Vector3 end)
        {
            Vector3 me = Mercs.OwnerPosition;
            end = me;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.01f) return false;
            facing.Normalize();
            for (int i = 0; i < DirectionSteps.Length; i++)
            {
                Vector3 g;
                if (RevivalGroundEnemies.TryGround(me + facing * DirectionSteps[i], 20f, out g)) { end = g; return true; }
            }
            return false;
        }

        static bool _placeAttack;

        internal static void StartAttackMap()
        {
            _placeDrive = false;
            _placing = true; _placeAttack = true; _route.Clear(); _placeSeen = Time.time; _placeKeyDown = -1f;
            _lastWasTap = false;
            Toast(Loc.T("АТАКА: кликните цель на карте, ", "ATTACK: click the objective on the map, ") + _wheelKeyText
                + Loc.T(" - точка под прицелом, Esc - отмена.", " tap = the crosshair point, Esc = cancel."), false);
        }

        /// <summary>Per frame while an attack point is being set: key reads
        /// only (the crosshair ray runs on a tap).</summary>
        static void AttackInput(bool gameWindow)
        {
            float now = Time.time;
            if (now - _placeSeen > 120f)
            { CancelRoute(Loc.T("Точка атаки не задана (2 мин) - отмена.", "Attack point not set (2 min) - cancelled.")); return; }
            if (!gameWindow && Input.GetKeyDown(KeyCode.Escape))
            { CancelRoute(Loc.T("Атака отменена.", "Attack cancelled.")); return; }
            if (gameWindow) return;   // on the map the point is a click
            if (PlaceDown())
            {
                _placing = false; _placeAttack = false;
                AttackAtCrosshair();
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
            _placeDrive = false;
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
            _placeDrive = false;
            _placing = false; _placeAttack = false; _route.Clear();
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
            if (_placeDrive) { DriveMapClick(point); return true; }
            Vector3 g;
            if (RevivalGroundEnemies.TryGround(point, 12f, out g)) point = g;
            if (_placeAttack)
            {
                _placing = false; _placeAttack = false;
                Mercs.OrderAttack(point, MercOrder.AtMap);
                return true;
            }
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
            if (_placeDrive) { DriveInput(gameWindow); return; }
            if (_placeAttack) { AttackInput(gameWindow); return; }
            float now = Time.time;
            if (now - _placeSeen > 120f)
            { CancelRoute(Loc.T("Маршрут патруля отменён (2 мин без точек).", "Patrol route cancelled (2 min without a point).")); return; }
            if (Input.GetKeyDown(KeyCode.Backspace) && _route.Count > 0) { _route.RemoveAt(_route.Count - 1); _placeSeen = now; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { FinishRoute(); return; }
            if (!gameWindow && Input.GetKeyDown(KeyCode.Escape))
            { CancelRoute(Loc.T("Маршрут патруля отменён.", "Patrol route cancelled.")); return; }
            if (PlaceDown()) _placeKeyDown = now;
            if (_placeKeyDown >= 0f && PlaceHeld() && now - _placeKeyDown > 0.4f)
            {
                _placeKeyDown = -1f;
                FinishRoute();
                return;
            }
            if (_placeKeyDown >= 0f && PlaceUp())
            {
                _placeKeyDown = -1f;
                if (gameWindow) return;           // on the map the points are clicks
                Vector3 p, f;
                if (Shift()) p = Mercs.OwnerPosition;
                else CrosshairPoint(out p, out f);
                AddRoutePoint(p);
            }
        }

        static float _crossNext;
        static bool _crossHit;
        static Vector3 _crossPoint, _crossFacing;

        /// <summary>CrosshairPoint at most 5 Hz for all OnGUI previews.</summary>
        static bool CrosshairCached(out Vector3 point)
        {
            if (Time.unscaledTime >= _crossNext)
            {
                _crossNext = Time.unscaledTime + 0.2f;
                _crossHit = CrosshairPoint(out _crossPoint, out _crossFacing);
            }
            point = _crossPoint;
            return _crossHit;
        }

        /// <summary>The route being set: numbered ground markers in the view
        /// and a small panel with the keys.</summary>
        static void DrawPlacing()
        {
            if (_placeDrive) { DrawDrivePlacing(); return; }
            if (_placeAttack) { DrawAttackPlacing(); return; }
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
                    VanillaUi.Label(new Rect(m.x + 18f, m.y - 3f, 120f, 20f), "<b>" + (i + 1) + "</b>  "
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

        static void DrawAttackPlacing()
        {
            float w = 560f, h = 46f;
            Rect r = new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height - h - 110f, w, h);
            Box(r, new Color(0f, 0f, 0f, 0.72f));
            Box(new Rect(r.x, r.y, 3f, r.height), AttackRed);
            Vector3 at;
            bool hit = CrosshairCached(out at);
            Centered(new Rect(r.x, r.y + 4f, w, 22f), "<b>" + Loc.T("ТОЧКА АТАКИ", "ATTACK POINT") + "</b>   "
                + (hit ? Loc.T("прицел ", "crosshair ") + (FlatDist(Mercs.OwnerPosition, at) / 2.8f).ToString("0") + " m"
                : Loc.T("прицел: нет земли - направление до 150 м", "crosshair: no ground - direction up to 150 m")), _label);
            Centered(new Rect(r.x, r.y + 28f, w, 18f), Loc.T("клик по карте - цель   ", "map click = objective   ") + _wheelKeyText
                + Loc.T(" - под прицелом   Esc - отмена", " tap = crosshair   Esc = cancel"), _small);
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
            internal bool Move;
            internal int MoveMask, MoveLabelMask;
            internal Color Tint;
            internal string Prefix = "";       // merc-attack-orders: "ATTACK " before the numbers
        }

        static readonly Dictionary<string, Ink> _inks = new Dictionary<string, Ink>();
        static readonly List<Ink> _inkNow = new List<Ink>();
        static readonly List<string> _inkGone = new List<string>();
        static readonly Color InkGreen = new Color(0.22f, 0.78f, 0.30f, 1f);
        static readonly Color MateBlue = new Color(0.35f, 0.70f, 1.00f, 1f);
        // merc-attack-orders: the target-ring red of the game's map markers.
        static readonly Color MoveBlue = new Color(0.32f, 0.75f, 0.88f, 1f);
        static readonly Color AttackRed = new Color(0.72f, 0.13f, 0.125f, 1f);
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
                    if (o.MoveNear && o.MoveInkScene == null) o.MoveInkScene = MercOrder.SceneKey(o.Scene);
                    if ((o.Mode != MercOrder.Patrol && o.Mode != MercOrder.Perimeter && o.Mode != MercOrder.Attack && !o.MoveNear)
                        || (o.MoveNear ? o.MoveInkScene : MercOrder.SceneKey(o.Scene)) != scene || o.Points.Length == 0) continue;
                    string key = InkKey(o);
                    Ink ink;
                    if (!_inks.TryGetValue(key, out ink)) { ink = NewInk(o); _inks[key] = ink; }
                    if (ink.Seen != _inkFrame)
                    {
                        ink.Seen = _inkFrame; ink.MoveMask = 0;
                        if (!ink.Move) ink.Label = ink.Prefix;
                        _inkNow.Add(ink);
                    }
                    if (ink.Move) ink.MoveMask |= 1 << number;
                    else ink.Label += (ink.Label.Length == ink.Prefix.Length ? "" : " ") + "#" + number;
                }
                for (int i = 0; i < _inkNow.Count; i++)
                {
                    Ink ink = _inkNow[i];
                    if (ink.Move) MoveInkLabel(ink);
                    if (ink.Dashes == null || ink.East != EastWorld.Extends || (ink.Size - size).sqrMagnitude > 0.01f)
                        BuildInk(ink, size);
                    if (ink.Dashes.Count == 0) continue;
                    if (layer == null)
                    {
                        layer = MapInkLayer.Begin("mercs", texture);
                        if (layer == null) return;
                    }
                    for (int d = 0; d < ink.Dashes.Count; d++)
                        layer.Draw(ink.Dashes[d].Bounds, ink.Dashes[d].Texture, ink.Tint);
                }
                _inkGone.Clear();
                foreach (KeyValuePair<string, Ink> e in _inks) if (e.Value.Seen != _inkFrame) _inkGone.Add(e.Key);
                for (int i = 0; i < _inkGone.Count; i++) { DropInk(_inks[_inkGone[i]]); _inks.Remove(_inkGone[i]); }

                for (int i = 0; i < _inkNow.Count; i++)
                    MapText(_inkNow[i].LabelAt, _inkNow[i].Label, _inkNow[i].Tint, texture, camera, world, map, full, view);
                number = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    Mercs.Record m = roster[i];
                    if (m.Dead) continue;
                    number++;
                    if (m.Unit == null || m.Unit.Ai == null) continue;
                    MapSquare(m.Unit.Ai.transform.position, m.Down.Down ? "SOS" : number < Numbers.Length ? Numbers[number] : number.ToString(),
                        m.Down.Down ? Red : Located(m.Id) ? Gold : InkGreen, texture, camera, world, map, full, view);
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
                    MapSquare(ai.transform.position, MercDownPose.Down(ai) ? "SOS" : mates[i].Label, MercDownPose.Down(ai) ? Red : MateBlue, texture, camera, world, map, full, view);
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
            if (o.MoveNear && o.MoveInkKey != null) return o.MoveInkKey;
            string e = o.Encode();
            string[] c = e.Split('~');
            // mode, radius and points: the group index does not change the line.
            string key = c.Length >= 7 ? c[0] + "~" + c[4] + "~" + c[5] : e;
            if (o.MoveNear) { key += "~move"; o.MoveInkKey = key; }
            return key;
        }

        static void MoveInkLabel(Ink ink)
        {
            if (ink.MoveMask == ink.MoveLabelMask) return;
            ink.MoveLabelMask = ink.MoveMask; ink.Label = ink.Prefix;
            for (int i = 1; i < 32; i++)
                if ((ink.MoveMask & (1 << i)) != 0)
                    ink.Label += (ink.Label.Length == ink.Prefix.Length ? "" : " ") + "#"
                        + (i < Numbers.Length ? Numbers[i] : i.ToString());
        }

        static Ink NewInk(MercOrder o)
        {
            Ink ink = new Ink();
            ink.Loop = new List<Vector3>();
            ink.Tint = InkGreen;
            if (o.MoveNear)
            {
                ink.Move = true;
                float r = MercMovePlan.Radius;
                for (int i = 0; i < 32; i++)
                {
                    float angle = i * Mathf.PI * 2f / 32f;
                    ink.Loop.Add(o.Centre + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * r);
                }
                ink.Closed = true; ink.LabelAt = o.Centre;
                ink.Tint = MoveBlue; ink.Prefix = Loc.T("ДВИЖЕНИЕ ", "MOVE ");
                return ink;
            }
            if (o.Mode == MercOrder.Attack)
            {
                // merc-attack-orders: one dashed stroke in the target red - the
                // approach from where it was given to the hold circle, then
                // once round the circle at the objective.
                float r = o.RadiusUnits;
                Vector3 c = o.Centre, d = o.Centre - o.Origin;
                d.y = 0f;
                float len = d.magnitude;
                d = len < 0.01f ? Vector3.forward : d / len;
                if (len > r + 4f) ink.Loop.Add(o.Origin);
                float a0 = Mathf.Atan2(-d.x, -d.z);
                for (int i = 0; i <= 48; i++)
                {
                    float a = a0 + i * Mathf.PI * 2f / 48f;
                    ink.Loop.Add(c + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r);
                }
                ink.Closed = false;
                ink.LabelAt = c + new Vector3(0f, 0f, r);
                ink.Tint = AttackRed;
                ink.Prefix = Loc.T("АТАКА ", "ATTACK ");
                return ink;
            }
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
            Vector2 size = Measure(_mapLabel, t);
            Rect r = new Rect(gui.x - size.x * 0.5f, gui.y - size.y - 4f, size.x, size.y);
            if (!view.Contains(new Vector2(r.xMin, r.yMin)) || !view.Contains(new Vector2(r.xMax, r.yMax))) return;
            Box(new Rect(r.x - 3f, r.y, r.width + 6f, r.height), new Color(0f, 0f, 0f, 0.6f));
            Color old = GUI.color;
            GUI.color = c;
            VanillaUi.Label(r, t, _mapLabel);
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
            VanillaUi.Label(r, Bold(text), _mapLabel);
            GUI.color = old;
        }

        // ============================================================ drawing
        static Texture2D _white;
        static GUIStyle _label, _small, _bold, _title, _button, _chip, _center, _stripName;
        static readonly GUIContent _measure = new GUIContent();
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
            _stripName = new GUIStyle(_label); _stripName.fontSize = 23;
            _title = new GUIStyle(_label); _title.fontSize = 18; _title.normal.textColor = Gold;
            _button = new GUIStyle(GUI.skin.button); _button.fontSize = 13; _button.richText = true;
            _chip = new GUIStyle(_small); _chip.normal.textColor = Gold;
            _center = new GUIStyle(_label);
        }

        static void Box(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            VanillaUi.Texture(r, _white);
            GUI.color = old;
        }

        static void Bar(Rect r, float v, Color c)
        {
            Box(r, new Color(0.25f, 0.25f, 0.25f, 1f));
            Box(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), c);
        }

        static Vector2 Measure(GUIStyle style, string text)
        {
            _measure.text = text;
            return style.CalcSize(_measure);
        }

        static void Centered(Rect r, string text, GUIStyle style)
        {
            Vector2 size = Measure(style, text);
            VanillaUi.Label(new Rect(r.x + (r.width - size.x) * 0.5f, r.y + (r.height - size.y) * 0.5f,
                size.x + 2f, size.y), text, style);
        }

        static bool ButtonColored(Rect r, string text, Color c, bool enabled)
        {
            Box(r, enabled ? c : Grey);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.01f);
            bool hit = VanillaUi.Button(r, "", _button) && enabled;
            if (hit) Reply();
            GUI.color = old;
            Centered(r, "<b>" + text + "</b>", _label);
            return hit;
        }

        internal static void Draw()
        {
            Styles();
            bool window = GameUi.WindowOpen;
            // a-u2: only the merc tab is custom; safe/market are always native.
            if (window)
            {
                FrameProf.S(FrameProf.S_MercTabD); DrawTradeTab(); FrameProf.E(FrameProf.S_MercTabD);
            }
            else if (_tabVisible || _tabActive) CloseTab();
            if (_wheelOpen && !window) DrawWheel();
            bool repaint = Event.current.type == EventType.Repaint;
            if (repaint && !window && !_listOpen && !_wheelOpen && GameplayCursor.CanCommand)
                DrawMedicineInteraction();
            if (repaint && _placing && !window && !_listOpen) DrawPlacing();
            if (GameUi.State == 8 && MapWanted()) DrawMap();
            else HideMap();
            if (_listOpen) DrawList();
            if (repaint && !window && !_listOpen && (Mercs.CfgHudStrip == null || Mercs.CfgHudStrip.Value)) DrawStrip();
            if (repaint && !window) DrawLocate();
            DrawCommandPing();
            if (repaint) DrawToasts();
        }

        static void DrawMedicineInteraction()
        {
            Mercs.Record r = Mercs.MedicineTarget;
            if (r == null || r.Unit == null || r.Unit.Ai == null) return;
            float x = Screen.width * 0.5f - 180f, y = Screen.height * 0.5f + 44f;
            VanillaUi.Panel(new Rect(x - 8f, y - 6f, 376f, 74f), "InteractItem");
            VanillaUi.Label(new Rect(x, y, 360f, 20f), r.Name, _label);
            VanillaUi.Label(new Rect(x, y + 20f, 360f, 20f), Mercs.MedkitStockLabel(r), _small);
            string action = r.Down.Down ? Loc.T("Shift+E: оживить аптечкой", "Shift+E: revive with medkit")
                : Loc.T("Shift+E: дать аптечку из рюкзака", "Shift+E: give inventory medkit");
            VanillaUi.Label(new Rect(x, y + 40f, 360f, 20f), Mercs.CanGiveMedkit(r) ? action
                : Loc.T("Передача недоступна (запас полон или ожидается сервер)",
                    "Unavailable (stock full or awaiting server)"), _small);
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
                Vector2 size = Measure(_label, t);
                float w = Mathf.Min(size.x + 20f, 620f);
                Rect r = new Rect(Screen.width - w - 20f, y, w, 26f);
                VanillaUi.Panel(r, _toasts[i].Warn ? "Warning_Msg" : "Group_Msg");
                VanillaUi.Label(new Rect(r.x + 10f, r.y + 4f, w - 12f, 20f), t, _label);
                y += 30f;
            }
        }

        sealed class StripRow
        {
            internal Mercs.Record Record;
            internal string FullName;
            internal readonly GUIContent Name = new GUIContent(), Order = new GUIContent();
            internal MercOrder For;
            internal bool Dead, Peaceful, Rally, Survive, Down;
            internal int Mode, Phase, Metres, Points, Radius, Lang = -1;
        }
        static readonly List<StripRow> _strip = new List<StripRow>(12);
        static readonly GUIContent _stripTitle = new GUIContent();
        static int _stripCount, _stripLang = -1;
        static string _stripKey;
        static float _stripAt;

        // At most 5 Hz, and only for the visible HUD. Labels change only when
        // their displayed values change. Repaint only consumes cached content.
        static void CacheStrip()
        {
            if (_listOpen || GameUi.WindowOpen || (Mercs.CfgHudStrip != null && !Mercs.CfgHudStrip.Value)) return;
            if (Time.unscaledTime < _stripAt) return;
            _stripAt = Time.unscaledTime + 0.2f;
            int lang = Loc.Lang();
            if (lang != _stripLang || _stripKey != _listKeyText)
            {
                _stripLang = lang; _stripKey = _listKeyText;
                _stripTitle.text = "<b>" + Loc.T("НАЁМНИКИ", "MERCS") + "</b>  (" + _listKeyText + ")";
            }
            List<Mercs.Record> roster = Mercs.Roster;
            _stripCount = roster.Count;
            while (_strip.Count < _stripCount) _strip.Add(new StripRow());
            for (int i = 0; i < _strip.Count; i++)
            {
                StripRow row = _strip[i];
                if (i >= _stripCount) { row.Record = null; continue; }
                Mercs.Record m = roster[i];
                bool changed = row.Record != m;
                row.Record = m;
                if (changed || row.FullName != m.Name)
                {
                    row.FullName = m.Name;
                    row.Name.text = Short(m.Name, 10);
                }
                MercOrder order = m.Order;
                MercUnit u = m.Unit;
                bool rally = u != null && u.Rally;
                int phase = u == null || u.Attack.For != order ? MercAttackRun.Advance : u.Attack.Phase;
                int metres = -1;
                if (order.Mode == MercOrder.Attack && phase != MercAttackRun.Holding
                    && phase != MercAttackRun.Stalled && phase != MercAttackRun.Search)
                {
                    float d = u == null || u.Ai == null ? MercAttackGeo.Length(order)
                        : MercAttackGeo.Flat(order.Centre - u.Ai.transform.position);
                    metres = (int)Math.Round(d / 2.8f, MidpointRounding.AwayFromZero);
                }
                int points = order.Points.Length, radius = (int)Math.Round(order.RadiusM, MidpointRounding.AwayFromZero);
                if (changed || row.For != order || row.Mode != order.Mode || row.Dead != m.Dead || row.Down != m.Down.Down || row.Peaceful != m.Peaceful
                    || row.Rally != rally || row.Survive != order.Survive || row.Lang != lang
                    || row.Phase != phase || row.Metres != metres || row.Points != points || row.Radius != radius)
                {
                    row.For = order; row.Mode = order.Mode; row.Dead = m.Dead; row.Down = m.Down.Down; row.Peaceful = m.Peaceful;
                    row.Rally = rally; row.Survive = order.Survive; row.Lang = lang;
                    row.Phase = phase; row.Metres = metres; row.Points = points; row.Radius = radius;
                    row.Order.text = m.Dead ? "DEAD" : OrderText(m);
                }
            }
        }

        static void DrawStrip()
        {
            if (_stripCount == 0) return;
            float height = 24f + 35f * _stripCount;
            Rect frame = new Rect(12f, Screen.height - height - 150f, 290f, height);
            VanillaUi.Panel(frame, "MarkerInfo");
            VanillaUi.Label(new Rect(frame.x + 8f, frame.y + 2f, 274f, 20f), _stripTitle, _chip);
            for (int i = 0; i < _stripCount; i++)
            {
                StripRow row = _strip[i];
                Mercs.Record m = row.Record;
                float y = frame.y + 24f + 35f * i;
                Rect r = new Rect(frame.x + 4f, y, 282f, 33f);
                VanillaUi.Panel(r, "groupPlayerWhite", new Color(0.329f, 0.329f, 0.329f, 1f));
                VanillaUi.Instrument(new Rect(r.x + 6f, y, 110f, 27f), row.Name, _stripName);
                Bar(new Rect(r.x + 116f, y + 13f, 48f, 5f), m.Hp, m.Dead ? Grey : HpColor(m.Hp));
                VanillaUi.Label(new Rect(r.x + 170f, y, 90f, 27f), row.Order, _small);
                if (m.Unpaid) VanillaUi.Label(new Rect(r.x + 262f, y, 20f, 27f), "$!", _small);
            }
        }

        static Color HpColor(float hp)
        {
            return hp > 0.6f ? VanillaUi.Green : hp > 0.3f ? VanillaUi.Gold : VanillaUi.Red;
        }

        static string Short(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

        internal static string OrderText(Mercs.Record m)
        {
            if (m.Down.Down) return Loc.T("НУЖНА ПОМОЩЬ", "DOWNED / SOS");
            MercOrder order = m.Order;
            if (order.MoveNear) return Loc.T("К УКРЫТИЮ", "MOVE / HOLD");
            if (order.Survive) return Loc.T("В УКРЫТИИ", "TAKING COVER");
            if (m.Unit != null && m.Unit.Rally) return Loc.T("СБОР", "RALLY");
            string o;
            switch (order.Mode)
            {
                case MercOrder.Follow: o = Loc.T("ЗА МНОЙ", "FOLLOW"); break;
                case MercOrder.Vehicle: o = Loc.T("ТЕХНИКА", "VEHICLE"); break;
                case MercOrder.Drive: o = Loc.T("ПОЕЗДКА", "DRIVE"); break;
                case MercOrder.ManGun: o = Loc.T("ПУШКА", "MAN GUN"); break;
                case MercOrder.ManRadar: o = Loc.T("РАДАР", "MAN RADAR"); break;
                case MercOrder.Patrol: o = Loc.T("ПАТРУЛЬ ", "PATROL ") + order.Points.Length; break;
                case MercOrder.Perimeter: o = Loc.T("ПЕРИМ. ", "PERIM ") + order.RadiusM.ToString("0") + "m"; break;
                case MercOrder.Attack: o = AttackText(m); break;
                default: o = Loc.T("СТОИТ", "STAY"); break;
            }
            return m.Peaceful ? o + " (P)" : o;
        }

        /// <summary>merc-attack-orders: the attack's state in the list.</summary>
        static string AttackText(Mercs.Record m)
        {
            MercUnit u = m.Unit;
            byte phase = u == null || u.Attack.For != m.Order ? MercAttackRun.Advance : u.Attack.Phase;
            if (phase == MercAttackRun.Holding) return Loc.T("АТАКА: ДЕРЖИТ", "ATTACK: HOLD");
            if (phase == MercAttackRun.Stalled) return Loc.T("АТАКА: ЗАСТРЯЛ", "ATTACK: STUCK");
            if (phase == MercAttackRun.Search) return Loc.T("АТАКА: ПОИСК", "ATTACK: SEARCH");
            float d = u == null || u.Ai == null ? MercAttackGeo.Length(m.Order) : MercAttackGeo.Flat(m.Order.Centre - u.Ai.transform.position);
            return Loc.T("АТАКА ", "ATTACK ") + (d / 2.8f).ToString("0") + "m";
        }

        // W-UI4: the order wheel in the UI kit's look - a round dark panel,
        // one pill per sector (accent = the pick, dimmed = not here), key caps,
        // the addressed mercs in the hub. Sector count and spacing follow
        // SectorEn. The texts come from tables or are rebuilt at 5 Hz while
        // the wheel is open (the selection and the crosshair ray with them).
        static readonly string[] KeyCaps = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
        static float _wheelTextAt;
        static int _wheelTextPick = -2;
        static string _wheelWho, _wheelPoint;
        static bool _wheelPeaceful;
        static readonly List<Mercs.Record> _wheelSelection = new List<Mercs.Record>(10);
        static int _wheelWhoKey;
        static readonly UiMemo _wheelPointMemo = new UiMemo(), _wheelKeysMemo = new UiMemo();

        static void DrawWheel()
        {
            if (UiKit.ParagraphHeight(" ", 64f) <= 0f) return;   // builds the kit's styles on first use
            int n = SectorEn.Length;
            float now = Time.unscaledTime;
            if (now >= _wheelTextAt || _wheelTextPick != _wheelPick) WheelTexts(now);
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f, rad = UiKit.S(n > 9 ? 320f : n > 8 ? 285f : n > 6 ? 235f : 210f);
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
                ? _wheelKeysMemo.Set(kk, seat ? (n >= 10 ? Loc.T("клавиши 1-9, 0", "keys 1-9, 0") : Loc.T("клавиши 1-", "keys 1-") + n) : Loc.T("отпустить = приказ", "release = issue"))
                : _wheelKeysMemo.Text;
            UiKit.Label(new Rect(cx - hub, cy + UiKit.S(2f), hub * 2f, UiKit.S(20f)), hint, UiFont.Small, UiFont.Center, UiKit.TextDim);
            if (((_wheelPick >= 1 && _wheelPick <= 3) || _wheelPick == AttackSector || _wheelPick == 6 || _wheelPick == 7) && _wheelPoint != null)
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
            // The held wheel is steady state: reuse its selection buffer and
            // build the addressed text only when the selection/language changes.
            List<Mercs.Record> sel = _wheelSelection;
            sel.Clear();
            List<Mercs.Record> roster = Mercs.Roster;
            int alive = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].Dead) continue;
                alive++;
                if (roster[i].Selected) sel.Add(roster[i]);
            }
            if (sel.Count == 0)
                for (int i = 0; i < roster.Count; i++) if (!roster[i].Dead) sel.Add(roster[i]);
            int who = alive * 31 + Loc.Lang();
            for (int i = 0; i < sel.Count; i++) who = unchecked(who * 31 + sel[i].Id);
            if (_wheelWho == null || _wheelWhoKey != who)
            { _wheelWhoKey = who; _wheelWho = Mercs.Addressed(sel); }
            _wheelPeaceful = sel.Count > 0 && sel[0].Peaceful;
            _wheelPoint = null;
            if (_wheelPick == 6)
            {
                _wheelPoint = Mercs.AirDefenceActive
                    ? Loc.T("ещё раз - освободить ПВО", "click again to release air defence")
                    : Loc.T("весь отряд: 52-К, радар, ЗУ-23; остальные за вами", "whole squad: 52-K, radar, ZU-23; others follow");
                return;
            }
            if ((_wheelPick < 1 || _wheelPick > 3) && _wheelPick != AttackSector) return;
            Vector3 point;
            bool hit = CrosshairCached(out point);
            int m = hit ? Mathf.RoundToInt(Vector3.Distance(Mercs.OwnerPosition, point) / 2.8f) : -1;
            int key = _wheelPick * 1000003 + m * 2 + Loc.Lang();
            if (!_wheelPointMemo.Stale(key)) { _wheelPoint = _wheelPointMemo.Text; return; }
            string extra = _wheelPick == 2 ? Loc.T("  - первая точка маршрута", "  - first route point")
                : _wheelPick == 3 ? Loc.T("  - центр (ещё раз = радиус 15/25/40/60)", "  - centre (again = radius 15/25/40/60)")
                : _wheelPick == AttackSector ? Loc.T("  - цель атаки (карта: список L)", "  - attack objective (map: L list)") : "";
            string none = _wheelPick == AttackSector ? Loc.T("нет земли: направление, конец до 150 м", "no ground: direction, endpoint up to 150 m")
                : Loc.T("нет точки: на вашем месте", "no point: at your position");
            _wheelPoint = _wheelPointMemo.Set(key, (hit ? Loc.T("точка под прицелом ", "target point ") + m + " m" : none) + extra);
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
        static Component _nguiCamera;
        static FieldInfo _fUseMouse;
        static bool _mouseOff, _savedMouse;
        static FieldInfo _fUseKeyboard, _fUseController, _fUseTouch;
        static bool _keysOff, _savedKeyboard, _savedController, _savedTouch;
        static object _tabUi;
        static GameObject _marketRoot;
        static Transform _marketButton, _storageButton;
        static Camera _tradeCamera;
        static float _nextTradeRead;
        static int _tradeWidth, _tradeHeight;
        static MethodInfo _closeNativeTrade;

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
            _closeNativeTrade = AccessTools.Method(_uiType, "SwitchMarketStorgeUI", null, null);
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

        /// <summary>2 Hz while a native trader window is open. Ref fields
        /// do not box; enum reads and input writes use FastField delegates.</summary>
        static void ReadTrade()
        {
            if (_readFrame == Time.frameCount) return;
            _readFrame = Time.frameCount;
            float now = Time.unscaledTime;
            bool resized = _tradeWidth != Screen.width || _tradeHeight != Screen.height;
            if (now < _nextTradeRead && !resized) return;
            _nextTradeRead = now + 0.5f;
            _tabVisible = false;
            if (!UiLook()) return;
            UnityEngine.Object live = _tabUi as UnityEngine.Object;
            if (_tabUi == null || live == null) _tabUi = _pUi == null ? null : _pUi.GetValue(null, null);
            object ui = _tabUi;
            if (ui == null) return;
            int state = FastField.GetInt(_fSwitchState, ui);
            if (state != 0 && state != 1) return;
            Component obj = _fCurrentObject.GetValue(ui) as Component;
            if (obj == null) return;
            if (obj.GetInstanceID() != _objectId)
            {
                CloseTab();
                _objectId = obj.GetInstanceID();
                _tabSettlement = TraderSettlement(obj, out _tabFaction);
            }
            if (_tabSettlement == null) return;
            _tradeCamera = _fCam == null ? null : _fCam.GetValue(ui) as Camera;
            object sw = _fSwitch == null ? null : _fSwitch.GetValue(ui);
            GameObject market = sw == null || _fMarketBtn == null ? null : _fMarketBtn.GetValue(sw) as GameObject;
            GameObject storage = sw == null || _fStorageBtn == null ? null : _fStorageBtn.GetValue(sw) as GameObject;
            _marketButton = market == null ? null : market.transform;
            _storageButton = storage == null ? null : storage.transform;
            object m = _fMarketUi == null ? null : _fMarketUi.GetValue(ui);
            _marketRoot = m as GameObject;
            Component mc = m as Component;
            if (_marketRoot == null && mc != null) _marketRoot = mc.gameObject;
            VanillaSkin.Ui = ui;
            VanillaSkin.Resolve(_marketRoot);
            if (!VanillaSkin.FormRect(_tradeCamera, out _panelRect))
            {
                float k = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
                _panelRect = new Rect((Screen.width - 922f * k) * 0.5f, (Screen.height - 510f * k) * 0.5f, 922f * k, 510f * k);
            }
            VanillaSkin.Begin(_panelRect);
            if (!VanillaSkin.WidgetRect(_marketButton, _tradeCamera, 160f, 40f, out _tradeRect))
                _tradeRect = VanillaSkin.Px(464f, -100f, 160f, 40f);
            if (!VanillaSkin.WidgetRect(_storageButton, _tradeCamera, 160f, 40f, out _safeRect))
                _safeRect = VanillaSkin.Px(298f, -100f, 160f, 40f);
            _tabRect = new Rect(_tradeRect.xMax + 4f * VanillaSkin.K, _tradeRect.y, _tradeRect.width, _tradeRect.height);
            _tradeWidth = Screen.width; _tradeHeight = Screen.height;
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
            if (!_tabVisible || _tabSettlement == null || _traderAi == null) return false;
            point = _traderAi.transform.position;
            return true;
        }

        static void CloseTab()
        {
            _tabActive = false;
            _tabVisible = false;
            _nextTradeRead = 0f;
            MercPage.Close();
            NguiMouse(true);
            NguiKeys(true);
        }

        // Only input is gated while the pointer is on the merc form.
        // The native camera, safe/market widgets and trade logic are untouched.
        static bool NguiCamera()
        {
            if (_nguiCamera != null) return true;
            Type t = RevivalPlugin.TypeByName("UICamera");
            _nguiCamera = _tradeCamera == null || t == null ? null : _tradeCamera.GetComponent(t);
            if (_nguiCamera == null) return false;
            _fUseMouse = AccessTools.Field(t, "useMouse");
            _fUseKeyboard = AccessTools.Field(t, "useKeyboard");
            _fUseController = AccessTools.Field(t, "useController");
            _fUseTouch = AccessTools.Field(t, "useTouch");
            return true;
        }

        static void NguiMouse(bool on)
        {
            if (on == !_mouseOff) return;
            if (!NguiCamera() || _fUseMouse == null) return;
            if (!on) _savedMouse = FastField.GetBool(_fUseMouse, _nguiCamera);
            FastField.SetBool(_fUseMouse, _nguiCamera, on ? _savedMouse : false);
            _mouseOff = !on;
        }

        static void NguiKeys(bool on)
        {
            if (on == !_keysOff) return;
            if (!NguiCamera()) return;
            if (!on)
            {
                if (_fUseKeyboard != null) _savedKeyboard = FastField.GetBool(_fUseKeyboard, _nguiCamera);
                if (_fUseController != null) _savedController = FastField.GetBool(_fUseController, _nguiCamera);
                if (_fUseTouch != null) _savedTouch = FastField.GetBool(_fUseTouch, _nguiCamera);
            }
            if (_fUseKeyboard != null) FastField.SetBool(_fUseKeyboard, _nguiCamera, on ? _savedKeyboard : false);
            if (_fUseController != null) FastField.SetBool(_fUseController, _nguiCamera, on ? _savedController : false);
            if (_fUseTouch != null) FastField.SetBool(_fUseTouch, _nguiCamera, on ? _savedTouch : false);
            _keysOff = !on;
        }

        static void DrawTradeTab()
        {
            FrameProf.S(FrameProf.S_MercTabT); ReadTrade(); FrameProf.E(FrameProf.S_MercTabT);
            if (!_tabVisible) { if (_tabActive) CloseTab(); return; }
            _tabFrame = Time.frameCount;
            // The original roster window retains whitelist/station controls.
            // Its input must not reach the native trader or the merc page.
            if (_listOpen) { NguiMouse(false); NguiKeys(false); return; }
            Event e = Event.current;
            if (_tabActive && !VanillaSkin.Asking && e.type == EventType.MouseDown
                && (_safeRect.Contains(e.mousePosition) || _tradeRect.Contains(e.mousePosition)))
                CloseTab();
            VanillaSkin.Begin(_panelRect);
            if (!VanillaSkin.Asking && VanillaSkin.TabButton(_tabRect, Loc.T("Наёмники", "Mercenaries"), _tabActive))
            {
                if (_tabActive) CloseTab();
                else
                {
                    _tabActive = true;
                    MercPage.Open();
                    Mercs.RequestRoster(0f);
                    NguiKeys(false);
                    VanillaSkin.Sound("OpenWindow");
                }
            }
            if (!_tabActive) { NguiMouse(true); NguiKeys(true); return; }
            NguiMouse(!VanillaSkin.Asking && !_panelRect.Contains(e.mousePosition));
            FrameProf.S(FrameProf.S_MercPageT); MercPage.Tick(Time.unscaledTime); FrameProf.E(FrameProf.S_MercPageT);
            FrameProf.S(FrameProf.S_MercPageD); bool close = MercPage.Draw(_panelRect); FrameProf.E(FrameProf.S_MercPageD);
            if (close)
            {
                CloseTab();
                if (_tabUi != null && _closeNativeTrade != null)
                    _closeNativeTrade.Invoke(_tabUi, new object[] { 2, null, true, true, false });
            }
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

        internal static void OpenTraderRoster()
        {
            _listOpen = true;
            _listTab = 0;
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
            VanillaUi.Label(new Rect(14f, 8f, 260f, 26f), "<b>" + Loc.T("МОИ НАЁМНИКИ", "MY MERCENARIES") + "</b>", _title);
            if (ButtonColored(new Rect(240f, 10f, 110f, 24f), Loc.T("Наёмники ", "Mercs ") + Mercs.AliveCount + "/" + Mercs.Cap,
                _listTab == 0 ? new Color(0.55f, 0.42f, 0.10f, 1f) : Grey, true)) _listTab = 0;
            if (ButtonColored(new Rect(356f, 10f, 110f, 24f), Loc.T("Белый список ", "Whitelist ") + Mercs.Whitelist.Count,
                _listTab == 1 ? new Color(0.55f, 0.42f, 0.10f, 1f) : Grey, true)) _listTab = 1;
            int money = Mercs.Money;
            VanillaUi.Label(new Rect(r.width - 250f, 10f, 200f, 20f), Loc.T("Деньги ", "Money ") + (money < 0 ? "?" : Mercs.Money0(money)), _label);
            if (ButtonColored(new Rect(r.width - 40f, 8f, 30f, 24f), "X", Red, true)) { _listOpen = false; RestoreCursor(); }
            if (_listTab == 0) MercRows(r); else WhitelistTab(r);
            GUI.DragWindow(new Rect(0f, 0f, r.width, 36f));
        }

        static readonly string[] _mercHeads = new string[7];
        static readonly float[] MercCols = { 0f, 26f, 190f, 330f, 450f, 560f, 720f };
        static int _mercHeadsLang = -1;

        static void MercRows(Rect r)
        {
            float x0 = 14f, y0 = 44f;
            if (_mercHeadsLang != Loc.Lang())
            {
                _mercHeadsLang = Loc.Lang();
                _mercHeads[0] = "";
                _mercHeads[1] = Loc.T("Имя", "Name");
                _mercHeads[2] = Loc.T("Здоровье", "Health");
                _mercHeads[3] = Loc.T("Приказ", "Order");
                _mercHeads[4] = Loc.T("Состояние", "State");
                _mercHeads[5] = Loc.T("Оплата", "Upkeep due");
                _mercHeads[6] = Loc.T("Дист.", "Dist");
            }
            string[] heads = _mercHeads;
            float[] cols = MercCols;
            for (int i = 1; i < heads.Length; i++) VanillaUi.Label(new Rect(x0 + cols[i], y0, 140f, 18f), heads[i], _small);
            List<Mercs.Record> roster = Mercs.Roster;
            Rect view = new Rect(0f, y0 + 20f, r.width, r.height - y0 - 20f - 160f);
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
                VanillaUi.Label(new Rect(x0 + cols[1], y + 2f, 160f, 18f), "<b>" + m.Name + "</b>", _label);
                VanillaUi.Label(new Rect(x0 + cols[1], y + 18f, 160f, 16f), (p == null ? m.ProfileId : p.Name) + (m.Session ? " (test)" : ""), _small);
                Bar(new Rect(x0 + cols[2], y + 12f, 90f, 8f), m.Hp, m.Dead ? Grey : HpColor(m.Hp));
                VanillaUi.Label(new Rect(x0 + cols[2], y + 22f, 90f, 14f), Mercs.MedkitCount(m), _small);
                VanillaUi.Label(new Rect(x0 + cols[2] + 94f, y + 7f, 50f, 18f), Mathf.RoundToInt(m.Hp * 100f).ToString(), _small);
                VanillaUi.Label(new Rect(x0 + cols[3], y + 9f, 120f, 18f), m.Dead ? "-" : OrderText(m), _label);
                string state = m.Dead ? "<color=#fd4848>" + Loc.T("убит", "dead") + "</color>"
                    : m.Unit != null && m.Unit.Deserting ? "<color=#fd4848>" + Loc.T("уходит", "deserting") + "</color>"
                    : m.Unit == null ? Loc.T("прибывает", "arriving")
                    : m.Combat ? "<color=#ff8060>" + Loc.T("в бою", "in combat") + "</color>"
                    : MercRide.StateText(m.Unit) != null ? MercRide.StateText(m.Unit)
                    : Loc.T("в строю", "idle");
                VanillaUi.Label(new Rect(x0 + cols[4], y + 9f, 110f, 18f), state, _label);
                string due;
                if (m.Dead) due = "-";
                else if (m.Unpaid)
                    due = "<color=#fd4848><b>" + Loc.T("НЕ ОПЛАЧЕН - уйдёт через ", "UNPAID - deserts in ")
                        + Math.Max(0.0, Mercs.GraceHours - (m.Deployed - m.PaidUntil)).ToString("0.0", CultureInfo.InvariantCulture) + " h</b></color>";
                else due = Loc.T("через ", "in ") + (m.PaidUntil - m.Deployed).ToString("0.0", CultureInfo.InvariantCulture) + " h";
                VanillaUi.Label(new Rect(x0 + cols[5], y + 9f, 170f, 18f), due, _small);
                string dist = m.Unit == null || m.Unit.Ai == null ? "-"
                    : (Vector3.Distance(me, m.Unit.Ai.transform.position) / 2.8f).ToString("0") + " m";
                VanillaUi.Label(new Rect(x0 + cols[6], y + 9f, 60f, 18f), dist, _small);
            }
            if (roster.Count == 0)
                VanillaUi.Label(new Rect(x0, 10f, r.width - 40f, 40f), Mercs.Support != 1 ? Mercs.LinkText()
                    : Loc.T("Наёмников нет. Нанять: у торговца поселения, вкладка \"Наёмники\".",
                    "No mercenaries. Hire them at a settlement trader, tab \"Mercenaries\"."), _label);
            GUI.EndScrollView();

            float by = r.height - 186f;
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
            // merc-attack-orders: ATTACK a point clicked on the map.
            if (ButtonColored(new Rect(x0 + 558f, by3, 180f, 28f), Loc.T("Атака по карте...", "Attack on the map..."),
                new Color(0.42f, 0.14f, 0.12f, 1f), selection.Count > 0))
            {
                _listOpen = false; RestoreCursor();
                StartAttackMap();
            }
            if (ButtonColored(new Rect(x0 + 744f, by3, 80f, 28f), Mercs.MedkitLabel(one),
                Green, Mercs.CanGiveMedkit(one))) Mercs.GiveMedkit(one);
            DriveButtons(new Rect(x0, by3 + 32f, r.width - 30f, 28f), dark, selection.Count > 0);
            MercFetch.EscortRequested = GUI.Toggle(new Rect(x0 + 270f, by3 + 66f, 282f, 28f), MercFetch.EscortRequested,
                Loc.T("Сопровождение: 2 последних выбранных", "Escort: last 2 selected"));
            if (ButtonColored(new Rect(x0 + 558f, by3 + 66f, 266f, 28f), Loc.T("Забрать воздушный груз", "Fetch airdrop"),
                dark, selection.Count > 0)) MercFetch.Start();
            VanillaUi.Label(new Rect(x0, by3 + 100f, 548f, 36f),
                Loc.T("Галочки = кому приказ (Ctrl+1..5, Ctrl+0 все). ", "Checked rows are who the orders address (Ctrl+1..5, Ctrl+0 = all). ")
                + _wheelKeyText + Loc.T(" удерж. - меню приказов, двойное нажатие - за мной (в машине - за моей техникой). Увольнение без возврата денег.",
                    " held = order wheel, double tap = follow me (in a vehicle: follow my vehicle). Dismiss asks twice, no refund."), _small);
        }

        static void WhitelistTab(Rect r)
        {
            VanillaUi.Label(new Rect(14f, 42f, r.width - 30f, 18f), Loc.T("Игроков из списка ваши наёмники никогда не атакуют. Хранится на мастер-сервере вместе с наёмниками.",
                "Players on this list are never attacked by your mercs, whatever their faction. Stored on the master server with your roster.")
                + (Mercs.Support == 1 ? "" : Loc.T(" (Сейчас только на эту сессию.)", " (This session only for now.)")), _small);
            float half = (r.width - 42f) * 0.5f;
            VanillaUi.Label(new Rect(14f, 64f, half, 20f), "<b>" + Loc.T("БЕЛЫЙ СПИСОК", "WHITELIST") + "</b>", _label);
            Rect left = new Rect(14f, 88f, half, r.height - 150f);
            List<Mercs.WlEntry> wl = Mercs.Whitelist;
            _wlScroll = GUI.BeginScrollView(left, _wlScroll, new Rect(0f, 0f, half - 20f, Mathf.Max(left.height - 2f, wl.Count * 30f)));
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < wl.Count; i++)
            {
                float y = i * 30f;
                Box(new Rect(0f, y, half - 22f, 26f), CardBg);
                VanillaUi.Label(new Rect(6f, y + 4f, 150f, 20f), "<b>" + wl[i].Name + "</b>", _label);
                GameObject online = null;
                for (int k = 0; k < players.Count; k++)
                    if (players[k] != null && Mercs.SteamOf(players[k]) == wl[i].Steam) { online = players[k]; break; }
                VanillaUi.Label(new Rect(160f, y + 5f, 150f, 20f), online == null ? Loc.T("не в сети", "offline")
                    : "<color=#80d060>" + Loc.T("в сети, ", "online, ") + (Vector3.Distance(Mercs.OwnerPosition, online.transform.position) / 2.8f).ToString("0") + " m</color>", _small);
                if (ButtonColored(new Rect(half - 96f, y + 2f, 70f, 22f), Loc.T("убрать", "remove"), Red, true))
                { Mercs.WhitelistRemove(wl[i].Steam); break; }
            }
            GUI.EndScrollView();

            float rx = 28f + half;
            VanillaUi.Label(new Rect(rx, 64f, 180f, 20f), "<b>" + Loc.T("ИГРОКИ В МИРЕ", "PLAYERS IN THIS WORLD") + "</b>", _label);
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
                VanillaUi.Label(new Rect(6f, y + 4f, 120f, 20f), "<b>" + Short(name, 14) + "</b>", _label);
                VanillaUi.Label(new Rect(126f, y + 5f, 70f, 20f), Mercs.FactionLabel(f), _small);
                VanillaUi.Label(new Rect(196f, y + 5f, 60f, 20f), (Vector3.Distance(Mercs.OwnerPosition, go.transform.position) / 2.8f).ToString("0") + " m", _small);
                bool listed = steam != null && Mercs.Whitelisted(steam);
                string rel = listed ? "<color=#f5b833>" + Loc.T("в списке", "listed") + "</color>"
                    : f == myFaction ? "<color=#80d060>" + Loc.T("свой", "friendly") + "</color>"
                    : "<color=#ff6050>" + Loc.T("враг", "hostile") + "</color>";
                VanillaUi.Label(new Rect(258f, y + 5f, 70f, 20f), rel, _small);
                if (!listed && steam != null && ButtonColored(new Rect(half - 96f, y + 2f, 70f, 22f), "+ " + Loc.T("добавить", "add"), Green, true))
                    Mercs.WhitelistAdd(steam, name);
            }
            GUI.EndScrollView();
            VanillaUi.Label(new Rect(14f, r.height - 56f, r.width - 30f, 40f),
                Loc.T("\"Свой\" - ваша фракция, её наёмники не трогают и так. Быстро: навести на игрока и Ctrl+",
                      "'friendly' = your faction: mercs never shoot them anyway. Quick add: aim at a player and press Ctrl+")
                + _listKeyText + ".", _small);
        }
    }
}
