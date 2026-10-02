// W Tower 2. A paid command is authorized at the live tower console.
// Remote wallets use authenticated backend transactions. The master queries
// the server directly; no Photon balance or payment acknowledgement is trusted.
using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // Pure policy; the offline check compiles this exact class.
    internal static class TowerSupportPolicy
    {
        internal static int Check(bool enabled, bool lease, bool alive, bool near,
            bool obeyed, int service, bool safe, float now, float ready, bool localWallet)
        {
            if (!enabled) return 1;
            if (service < 0 || service > 2) return 2;
            if (!lease || !alive || !near || !obeyed) return 3;
            if (safe) return 4;
            if (now < ready) return 5;
            if (!localWallet) return 6;
            return 0;
        }

        internal static bool CanPay(int balance, int cost)
        {
            return cost > 0 && balance >= cost;
        }

        internal static bool OnMap(float x, float z)
        {
            return !float.IsNaN(x) && !float.IsNaN(z) && !float.IsInfinity(x)
                && !float.IsInfinity(z) && x >= -2500f && x <= 7500f && z >= -2500f && z <= 2500f;
        }
    }

    internal static class TowerSupport
    {
        static ConfigEntry<bool> _enabled;
        static readonly ConfigEntry<int>[] _prices = new ConfigEntry<int>[3];
        static readonly ConfigEntry<float>[] _cooldowns = new ConfigEntry<float>[3];
        static readonly int[] DefaultPrices = { 120000, 60000, 80000 };
        static readonly float[] DefaultCooldowns = { 600f, 900f, 480f };
        static readonly float[] _ready = new float[3];
        static readonly GUIContent[] _labels = { new GUIContent(), new GUIContent(), new GUIContent() };
        static readonly GUIContent _help = new GUIContent();
        static readonly GUIContent _status = new GUIContent();
        static readonly GUIContent _back = new GUIContent();
        static GUIStyle _style;
        internal static bool Selecting;
        internal static int WorldGeneration;
        static bool _targetSet, _mapOpened, _canOrder;
        static Vector3 _target;
        static Component _texture;
        static Camera _camera;
        static Vector2 _world, _map;
        static Rect _clip, _panel;
        static float _nextUi;
        static Vector3 _mapPosition, _mapScale;
        static int _screenW, _screenH;
        static readonly int[] _shownPrices = { -1, -1, -1 };
        static readonly int[] _shownSeconds = { -1, -1, -1 };
        static int _openFrame, _lastSerial, _localActor;
        static string _message = "";
        static readonly Vector3[] _rockets = new Vector3[16];
        static Vector3 _origin;
        static int _rocketNext = 16;
        static float _nextRocket;
        static MethodInfo _masterGetter, _actorGetter;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string section = "TowerSupport";
            _enabled = cfg.Bind(section, "Enabled", true, "Paid support from the held airfield tower console (H, map target). Remote wallets use the server tower-payment adapter.");
            string[] keys = { "An2BombRun", "Paratroopers", "Katyusha" };
            for (int i = 0; i < 3; i++)
            {
                _prices[i] = cfg.Bind(section, keys[i] + "Price", DefaultPrices[i], "Native money per mission; at least 1. Only a verified native wallet can pay.");
                _cooldowns[i] = cfg.Bind(section, keys[i] + "CooldownSeconds", DefaultCooldowns[i], "Tower-wide cooldown in seconds, shared across holders; at least 30.");
            }
        }

        static bool Enabled { get { return _enabled == null || _enabled.Value; } }
        static int Price(int i) { return Math.Max(1, _prices[i] == null ? DefaultPrices[i] : _prices[i].Value); }
        static float Cooldown(int i)
        {
            float value = _cooldowns[i] == null ? DefaultCooldowns[i] : _cooldowns[i].Value;
            return float.IsNaN(value) || float.IsInfinity(value) ? DefaultCooldowns[i] : Mathf.Max(30f, value);
        }

        internal static void WorldEnded()
        {
            WorldGeneration++;
            _lastSerial = 0;
            _rocketNext = 16;
            for (int i = 0; i < 3; i++) _ready[i] = 0f;
            if (Selecting) Close();
        }

        internal static void Tick()
        {
            TowerSupportPayments.Tick();
            // The scheduled ripple uses the existing pooled Katyusha flight loop.
            if (_rocketNext < 16 && Time.time >= _nextRocket)
            {
                _nextRocket = Time.time + 0.5f;
                KatyushaFlight.Support(_origin, _rockets[_rocketNext++], Crocodile.IsMaster());
            }
            if (!Enabled || !TowerRadar.Built || !RadarScope.InView)
            {
                if (Selecting) Close();
                return;
            }
            if (!Selecting)
            {
                if (Input.GetKeyDown(KeyCode.H)) Open();
                return;
            }
            if (Input.GetKeyDown(KeyCode.H)) { Close(); return; }
            if (Time.time >= _nextUi)
            {
                _nextUi = Time.time + 0.5f;
                if (_texture != null && !_texture.gameObject.activeInHierarchy) { Close(); return; }
                // Resolve reflection/bounds only on entry, pan or resize. A still
                // support map does not repeatedly allocate backend/UI objects.
                if (_texture == null || _camera == null || _screenW != Screen.width || _screenH != Screen.height
                    || _texture.transform.position != _mapPosition || _texture.transform.localScale != _mapScale)
                {
                    Component manager;
                    if (!MapTools.Context(out manager, out _texture, out _camera, out _world, out _map))
                    { Close(); return; }
                    Rect picture, view;
                    if (!MapTools.MapScreenRect(_texture, _camera, out picture)) { Close(); return; }
                    _clip = picture;
                    if (MapTools.MapViewportRect(_texture, _camera, out view))
                        _clip = Rect.MinMaxRect(Mathf.Max(picture.xMin, view.xMin), Mathf.Max(picture.yMin, view.yMin),
                            Mathf.Min(picture.xMax, view.xMax), Mathf.Min(picture.yMax, view.yMax));
                    _screenW = Screen.width; _screenH = Screen.height;
                    _mapPosition = _texture.transform.position;
                    _mapScale = _texture.transform.localScale;
                }
                _canOrder = TowerRadar.OperatorActor == _localActor && TowerRadar.ConsoleAlive;
                CacheLabels(false);
            }
            if (Time.frameCount == _openFrame || !Input.GetMouseButtonDown(0)) return;
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (_panel.Contains(mouse) || !_clip.Contains(mouse)) return;
            Vector3 point;
            if (Mortar.MapPoint(_texture, _camera, _world, out point) && TowerSupportPolicy.OnMap(point.x, point.z))
            {
                _target = point;
                _targetSet = true;
                _message = Loc.T("Цель выбрана. Выберите поддержку.", "Target selected. Choose support.");
                CacheLabels(true);
            }
        }

        static void Open()
        {
            _localActor = Crocodile.LocalActor();
            Selecting = true;
            _targetSet = false;
            _openFrame = Time.frameCount;
            _nextUi = 0f;
            _message = "";
            _mapOpened = Mortar.ShowMap(true);
            CacheLabels(true);
        }

        static void Close()
        {
            Selecting = false;
            _texture = null;
            _camera = null;
            if (_mapOpened) Mortar.ShowMap(false);
            _mapOpened = false;
        }

        static void CacheLabels(bool force)
        {
            if (force)
            {
                _help.text = Loc.T("Командный пост: щёлкните по карте для выбора цели. H: радар.",
                    "Tower command: click the map to choose a target. H: radar.");
                _back.text = Loc.T("Назад к радару", "Back to radar");
                _status.text = _message;
                if (_targetSet) _status.text += "  " + EastMapPanel.GridSquare(_target);
            }
            for (int i = 0; i < 3; i++)
            {
                int price = Price(i);
                int left = Mathf.CeilToInt(Mathf.Max(0f, _ready[i] - RadarClock.Now));
                if (!force && _shownPrices[i] == price && _shownSeconds[i] == left) continue;
                _shownPrices[i] = price; _shownSeconds[i] = left;
                string name = i == 0 ? Loc.T("Ан-2: 6 ФАБ-50", "An-2: 6 FAB-50")
                    : i == 1 ? Loc.T("Свои десантники: 8", "Own paratroopers: 8") : Loc.T("Катюша: 16 ракет", "Katyusha: 16 rockets");
                _labels[i].text = name + "  $" + price + (left > 0 ? "  " + left + " s" : "");
            }
        }

        internal static void Draw()
        {
            if (!Selecting) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.normal.textColor = Color.white;
            }
            float width = Mathf.Min(700f, Screen.width - 16f);
            _panel = new Rect((Screen.width - width) * 0.5f, 8f, width, 126f);
            Color keep = GUI.color;
            GUI.color = new Color(0.05f, 0.09f, 0.08f, 0.96f);
            VanillaUi.Texture(_panel, Texture2D.whiteTexture);
            GUI.color = Color.white;
            VanillaUi.Label(new Rect(_panel.x, 12f, width, 28f), _help, _style);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = _targetSet && _canOrder;
            float buttonW = (width - 16f) / 3f;
            for (int i = 0; i < 3; i++)
                if (VanillaUi.Button(new Rect(_panel.x + 4f + i * buttonW, 42f, buttonW - 4f, 32f), _labels[i])) Request(i);
            GUI.enabled = oldEnabled;
            VanillaUi.Label(new Rect(_panel.x, 76f, width, 24f), _status, _style);
            if (VanillaUi.Button(new Rect(_panel.x + width * 0.5f - 95f, 102f, 190f, 26f), _back)) Close();
            Vector2 at;
            if (_targetSet && _texture != null && _camera != null
                && MapTools.WorldToGui(_target, _texture, _camera, _world, _map, out at) && _clip.Contains(at))
            {
                GUI.color = Color.yellow;
                VanillaUi.Texture(new Rect(at.x - 10f, at.y - 1f, 20f, 2f), Texture2D.whiteTexture);
                VanillaUi.Texture(new Rect(at.x - 1f, at.y - 10f, 2f, 20f), Texture2D.whiteTexture);
            }
            GUI.color = keep;
        }

        static void Request(int service)
        {
            if (!_targetSet) return;
            if (Crocodile.IsMaster()) Purchase(Crocodile.LocalActor(), service, _target);
            else
            {
                TowerSupportPayments.Expect(service);
                RadarNet.Send(new float[] { 5f, service, _target.x, _target.z });
            }
        }

        // Master: no balance, price, faction or actor id supplied by the client.
        static void Purchase(int actor, int service, Vector3 target)
        {
            if (!Crocodile.IsMaster()) return;
            if (TowerSupportPayments.Busy || (actor == Crocodile.LocalActor() && TowerSupportPayments.WalletBusy))
            { Reply(actor, 10); return; }
            if (!TowerSupportPolicy.OnMap(target.x, target.z)) { Reply(actor, 2); return; }
            GameObject player = Crocodile.PlayerByActor(actor);
            bool near = player != null && TowerRadar.ConsoleRoot != null
                && (player.transform.position - TowerRadar.ConsoleRoot.position).sqrMagnitude <= 144f;
            bool lease = TowerRadar.SupportLease(actor);
            // Reject foreign or stale claims before a terrain/safe-zone query.
            int why = TowerSupportPolicy.Check(Enabled, lease, Crocodile.PlayerUp(player), near,
                lease && TowerRadar.Obeyed(actor), service, false, RadarClock.Now,
                service >= 0 && service < 3 ? _ready[service] : 0f, true);
            if (why != 0) { Reply(actor, why); return; }
            if (!AirEvents.SupportTarget(service == 1, target)) { Reply(actor, 4); return; }
            string side = Fraktion.Spielerseite(player), faction = null;
            for (int i = 0; i < Fraktion.Namen.Length; i++)
                if (Fraktion.Eigene(Fraktion.Namen[i]) == side) { faction = Fraktion.Namen[i]; break; }
            if (service == 1 && faction == null) { Reply(actor, 3); return; }
            if (TowerRadar.PlayerSide(actor) == TowerRadar.UnreadSide) { Reply(actor, 3); return; }
            if (!RadarNet.Ready || (service == 2 && (!Katyusha.Enabled || _rocketNext < 16))
                || (service < 2 && !AirEvents.SupportReady()))
            { Reply(actor, 7); return; }
            if (actor != Crocodile.LocalActor())
            {
                Reply(actor, TowerSupportPayments.Begin(actor, service, Price(service), target, faction) ? 10 : 7);
                return;
            }
            Component stats = Admin.LocalStats();
            if (stats == null || !NativeConnected(stats)) { Reply(actor, 7); return; }
            MethodInfo get = AccessTools.Method(stats.GetType(), "GetPlayerMoney", Type.EmptyTypes, null);
            MethodInfo add = AccessTools.Method(stats.GetType(), "AddPlayerMoney",
                new Type[] { typeof(int), typeof(bool), typeof(bool) }, null);
            if (get == null || add == null) { Reply(actor, 7); return; }
            int cost = Price(service);
            int balance;
            try { balance = Convert.ToInt32(get.Invoke(stats, null)); }
            catch { Reply(actor, 7); return; }
            if (!TowerSupportPolicy.CanPay(balance, cost)) { Reply(actor, 8); return; }
            bool debited = false;
            try
            {
                debited = true;
                add.Invoke(stats, new object[] { -cost, true, false });
                if (Convert.ToInt32(get.Invoke(stats, null)) != balance - cost) throw new InvalidOperationException("wallet did not debit exactly");
                bool launched = service == 2 ? Rockets(target) : AirEvents.TowerMission(service == 1, target, faction);
                if (!launched) throw new InvalidOperationException("support launch refused");
                _ready[service] = RadarClock.Now + Cooldown(service);
                Reply(actor, 0);
                RevivalPlugin.L.LogInfo("TowerSupport: actor " + actor + " paid " + cost + " for service " + service + " at " + target.ToString("0"));
            }
            catch (Exception ex)
            {
                bool refunded = true;
                if (debited)
                {
                    try
                    {
                        int current = Convert.ToInt32(get.Invoke(stats, null));
                        add.Invoke(stats, new object[] { balance - current, true, false });
                    }
                    catch (Exception refund) { refunded = false; RevivalPlugin.L.LogError("TowerSupport refund failed: " + refund.Message); }
                }
                RevivalPlugin.L.LogWarning("TowerSupport purchase failed: " + ex.Message);
                Reply(actor, refunded ? 7 : 9);
            }
        }

        static bool NativeConnected(Component stats)
        {
            try
            {
                FieldInfo field = AccessTools.Field(stats.GetType(), "_backendManager");
                object backend = field == null ? null : field.GetValue(stats);
                field = backend == null ? null : AccessTools.Field(backend.GetType(), "_connection");
                object connection = field == null ? null : field.GetValue(backend);
                MethodInfo connected = connection == null ? null : AccessTools.Method(connection.GetType(), "isConnected", Type.EmptyTypes, null);
                return connected != null && (bool)connected.Invoke(connection, null);
            }
            catch { return false; }
        }

        internal static int RecheckRemote(int actor, int service, int cost, Vector3 target)
        {
            if (!Crocodile.IsMaster() || service < 0 || service > 2 || Price(service) != cost) return 7;
            GameObject player = Crocodile.PlayerByActor(actor);
            bool near = player != null && TowerRadar.ConsoleRoot != null
                && (player.transform.position - TowerRadar.ConsoleRoot.position).sqrMagnitude <= 144f;
            bool lease = TowerRadar.SupportLease(actor);
            int why = TowerSupportPolicy.Check(Enabled, lease, Crocodile.PlayerUp(player), near,
                lease && TowerRadar.Obeyed(actor), service, false, RadarClock.Now, _ready[service], true);
            if (why != 0) return why;
            if (!TowerSupportPolicy.OnMap(target.x, target.z) || !AirEvents.SupportTarget(service == 1, target)) return 4;
            if (!RadarNet.Ready || TowerRadar.PlayerSide(actor) == TowerRadar.UnreadSide) return 7;
            if (service == 2) return Katyusha.Enabled && _rocketNext == 16 ? 0 : 7;
            return AirEvents.SupportReady() ? 0 : 7;
        }

        internal static bool LaunchRemote(int actor, int service, Vector3 target, string faction)
        {
            try
            {
                bool launched = service == 2 ? Rockets(target) : AirEvents.TowerMission(service == 1, target, faction);
                if (!launched) return false;
                _ready[service] = RadarClock.Now + Cooldown(service);
                Reply(actor, 0);
                return true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Tower remote dispatch: " + ex.Message); return false; }
        }

        internal static void PaymentReply(int actor, int reason)
        {
            if (Crocodile.IsMaster()) Reply(actor, reason);
            else OnReply(new float[] { 6f, actor, reason, _ready[0], _ready[1], _ready[2] });
        }

        static bool Rockets(Vector3 target)
        {
            Vector3 direction = target - TowerRadar.ConsoleRoot.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1f) direction = Vector3.forward;
            direction.Normalize();
            Vector3 origin = target - direction * (1200f * 2.8f);
            float ground;
            if (!TowerSupportPolicy.OnMap(origin.x, origin.z) || !RevivalTroopInsertion.TerrainHeight(origin, out ground))
                if (!RevivalTroopInsertion.TerrainHeight(target, out ground)) return false;
            origin.y = ground + 3f * 2.8f;
            float[] msg = new float[37];
            msg[0] = 7f; msg[1] = ++_lastSerial; msg[2] = origin.x; msg[3] = origin.y; msg[4] = origin.z;
            Vector3 right = new Vector3(direction.z, 0f, -direction.x);
            for (int i = 0; i < 16; i++)
            {
                Vector3 point = target + direction * UnityEngine.Random.Range(-80f, 80f) + right * UnityEngine.Random.Range(-40f, 40f);
                // Hold an individual rocket that would land in a protected area.
                if (AirEvents.Safe(point) || !TowerSupportPolicy.OnMap(point.x, point.z)) point = target;
                msg[5 + i * 2] = point.x; msg[6 + i * 2] = point.z;
            }
            QueueRockets(msg);
            RadarNet.Send(msg);
            return true;
        }

        static void QueueRockets(float[] msg)
        {
            _origin = new Vector3(msg[2], msg[3], msg[4]);
            for (int i = 0; i < 16; i++) _rockets[i] = new Vector3(msg[5 + i * 2], 0f, msg[6 + i * 2]);
            _rocketNext = 0;
            _nextRocket = Time.time;
        }

        static void Reply(int actor, int why)
        {
            float[] msg = { 6f, actor, why, _ready[0], _ready[1], _ready[2] };
            OnReply(msg);
            RadarNet.Send(msg);
        }

        static void OnReply(float[] msg)
        {
            for (int i = 0; i < 3; i++) _ready[i] = Mathf.Max(_ready[i], msg[3 + i]);
            if ((int)msg[1] != Crocodile.LocalActor()) return;
            int why = (int)msg[2];
            TowerSupportPayments.MissionReply(why);
            _message = why == 0 ? Loc.T("Поддержка принята. Оплата списана.", "Support accepted. Payment deducted.")
                : why == 10 ? Loc.T("Подтверждение оплаты сервером...", "Waiting for server payment confirmation...")
                : why == 3 ? Loc.T("Только живой владелец у консоли может вызвать поддержку.", "Only the living holder at the console may request support.")
                : why == 4 ? Loc.T("Цель в безопасной зоне.", "Target is in a safe zone.")
                : why == 5 ? Loc.T("Поддержка ещё перезаряжается.", "Support is still on cooldown.")
                : why == 6 ? Loc.T("Оплата другого клиента требует серверного API. Сейчас покупает только хост.", "Remote payment needs a server API. Currently only the host can purchase.")
                : why == 9 ? Loc.T("?????? ????????. ????????? ?????? ? ??????.", "Refund failed. Check the balance and log.")
                : why == 8 ? Loc.T("Не хватает денег.", "Not enough money.")
                : Loc.T("Поддержка недоступна. Деньги не потрачены.", "Support unavailable. No money spent.");
            CacheLabels(true);
        }

        static int MasterActor()
        {
            try
            {
                if (_masterGetter == null) _masterGetter = AccessTools.PropertyGetter(RevivalPlugin.TypeByName("PhotonNetwork"), "masterClient");
                object master = _masterGetter == null ? null : _masterGetter.Invoke(null, null);
                if (master == null) return -1;
                if (_actorGetter == null) _actorGetter = AccessTools.PropertyGetter(master.GetType(), "ID");
                return _actorGetter == null ? -1 : Convert.ToInt32(_actorGetter.Invoke(master, null));
            }
            catch { return -1; }
        }

        internal static bool FromMaster(int actor) { return actor == MasterActor(); }

        internal static void Receive(float[] msg, int sender)
        {
            if (msg == null || msg.Length < 2) return;
            for (int i = 0; i < msg.Length; i++) if (float.IsNaN(msg[i]) || float.IsInfinity(msg[i])) return;
            int kind = (int)msg[0];
            if (kind == 5 && msg.Length == 4 && msg[1] == Mathf.RoundToInt(msg[1]) && Crocodile.IsMaster())
                Purchase(sender, (int)msg[1], new Vector3(msg[2], 0f, msg[3]));
            else if (FromMaster(sender))
            {
                if (kind == 6 && msg.Length == 6) OnReply(msg);
                else if (kind == 7 && msg.Length == 37 && (int)msg[1] > _lastSerial)
                { _lastSerial = (int)msg[1]; QueueRockets(msg); }
                else if (kind == 8) TowerSupportPayments.Challenge(msg, sender);
            }
        }
    }
}
