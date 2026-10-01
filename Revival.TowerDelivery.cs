// Z M4: command-room paid supplies in the game's own humanitarian crate.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class TowerDelivery
    {
        const string Prefab = "GamePlayObjects/Helicopters/AirDrop_Container";
        const string Tag = "ndr-airdrop-m4";
        internal static bool Selecting;
        static ConfigEntry<bool> _enabled;
        static Transform _console, _tower;
        static Material _material;
        static float _next, _ready, _workNext, _queryNext, _requestedUntil;
        static int _candidate, _mask = 1, _serial, _planActor, _planMask, _attempt, _seed, _slot;
        static bool _near, _canOrder, _mapOpened, _planning, _filling, _awaitCommit;
        static Vector3 _landing;
        static GameObject _crate;
        static Component _container;
        static Behaviour _drop;
        static Rigidbody _body;
        static Type _containerType, _dropType, _viewType;
        static FieldInfo _animationState;
        static MethodInfo _instantiate, _destroy, _add, _animation;
        static readonly Dictionary<int, int> Requests = new Dictionary<int, int>();
        sealed class Cargo
        { internal Component Drop; internal Rigidbody Body; internal int Controller; internal float Next; }
        static readonly Dictionary<int, Cargo> Cargoes = new Dictionary<int, Cargo>();
        static readonly Vector3[] Candidates = {
            new Vector3(5.7f, 0f, -2.8f), new Vector3(9.7f, 0f, -2.8f),
            new Vector3(5.7f, 0f, 2.8f), new Vector3(9.7f, 0f, 2.8f) };
        static readonly string[] _labels = new string[4];
        static string _title, _prompt, _order, _close, _help, _status = "";
        static int _lang = -1, _shownMask = -1, _shownWait = -1, _generation = -1, _placement = -1;
        static GUIStyle _text;
        static bool Enabled { get { return _enabled == null || _enabled.Value; } }
        internal static void BindConfig(ConfigFile cfg)
        { _enabled = cfg.Bind("TowerDelivery", "Enabled", true, "Paid supply airdrops from the tower command room. Server payment adapter required."); }

        internal static void AddItems(List<ItemDef> items)
        {
            if (!Enabled) return;
            if (!HasItem(items, TowerDeliveryCore.ShellId)) items.Add(new ItemDef(TowerDeliveryCore.ShellId, 2030, false,
                "85-мм снаряд 52-К", "52-K 85 mm shell",
                "Один снаряд для 52-К. Доставьте его в боевую позицию.",
                "One 52-K shell. Carry it to the gun position.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png", "ammo50_icon.png", null, 1, 0, 9f));
            if (!HasItem(items, TowerDeliveryCore.BeltId)) items.Add(new ItemDef(TowerDeliveryCore.BeltId, 2030, false,
                "Лента ЗУ-23 (100)", "ZU-23 belt (100)",
                "100 патронов для ЗУ-23. Доставьте ленту в боевую позицию.",
                "100 rounds for the ZU-23. Carry the belt to the gun position.",
                "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png", "mgbelt_icon.png", null, 100, 0, 28f));
        }

        static bool HasItem(List<ItemDef> items, int id)
        {
            for (int i = 0; i < items.Count; i++) if (items[i].Id == id) return true;
            return false;
        }

        internal static void Install(Harmony harmony)
        {
            _containerType = RevivalPlugin.TypeByName("ItemsContainer");
            _dropType = RevivalPlugin.TypeByName("AirDropObject");
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_containerType == null || _dropType == null || _viewType == null || photon == null) return;
            _instantiate = AccessTools.Method(photon, "InstantiateSceneObject",
                new Type[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(int), typeof(object[]) }, null);
            _destroy = AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
            _add = AccessTools.Method(_containerType, "AddNewContainerItemFromResources", new Type[] { typeof(int) }, null);
            _animationState = AccessTools.Field(_dropType, "animationState");
            _animation = AccessTools.Method(_dropType, "SetAnimationState", new Type[] { typeof(int) }, null);
            MethodInfo start = AccessTools.Method(_containerType, "Start", Type.EmptyTypes, null);
            if (start != null) harmony.Patch(start, new HarmonyMethod(typeof(TowerDelivery).GetMethod("ContainerStartPrefix")), null, null, null, null);
            harmony.Patch(AccessTools.Method(_dropType, "Update", Type.EmptyTypes, null),
                new HarmonyMethod(typeof(TowerDelivery).GetMethod("DropUpdatePrefix")), null, null, null, null);
            harmony.Patch(AccessTools.Method(_containerType, "OnDestroy", Type.EmptyTypes, null),
                new HarmonyMethod(typeof(TowerDelivery).GetMethod("ContainerDestroyPrefix")), null, null, null, null);
            harmony.Patch(AccessTools.Method(_containerType, "NetworkInteractingContainerRequest", null, null),
                new HarmonyMethod(typeof(TowerDelivery).GetMethod("InteractingPrefix")), null, null, null, null);
            harmony.Patch(AccessTools.Method(_containerType, "OnInteractingWithContainer", null, null),
                new HarmonyMethod(typeof(TowerDelivery).GetMethod("InteractingPrefix")), null, null, null, null);
        }
        static object Get(object o, string name)
        { FieldInfo f = o == null ? null : AccessTools.Field(o.GetType(), name); return f == null ? null : f.GetValue(o); }
        static void Set(object o, string name, object value)
        { FieldInfo f = o == null ? null : AccessTools.Field(o.GetType(), name); if (f == null) throw new MissingFieldException(name); f.SetValue(o, value); }

        // Instantiation data is present by Start on every peer, including late join.
        // Native random loot must never run in a paid crate.
        public static void ContainerStartPrefix(Component __instance)
        {
            if (_viewType == null) return;
            Component view = __instance.GetComponent(_viewType);
            if (view == null) return;
            PropertyInfo p = AccessTools.Property(_viewType, "instantiationData");
            object[] data = p == null ? null : p.GetValue(view, null) as object[];
            if (data == null || data.Length != 3 || !Tag.Equals(data[0])) return;
            Set(__instance, "OnCallSpawn", true); Set(__instance, "IsSpawnedData", true);
            Set(__instance, "IsFractionSafelock", false); Set(__instance, "KeyItemID", 0);
            object cargo = Get(__instance, "_containerData");
            // A late-join snapshot can arrive before Start. Never clear loot
            // already replayed by the native container RPCs on that peer.
            if (Convert.ToInt32(Get(cargo, "MaxSlots")) < 42)
            {
                FieldInfo[] fields = cargo.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Array[] previous = new Array[fields.Length];
                for (int i = 0; i < fields.Length; i++)
                    if (fields[i].FieldType.IsArray) previous[i] = fields[i].GetValue(cargo) as Array;
                Set(cargo, "MaxSlots", 42);
                AccessTools.Method(_containerType, "SetContainerDataArraysLenght", new Type[] { typeof(int) }, null)
                    .Invoke(__instance, new object[] { 42 });
                for (int i = 0; i < fields.Length; i++)
                    if (previous[i] != null)
                    {
                        Array current = fields[i].GetValue(cargo) as Array;
                        if (current != null) Array.Copy(previous[i], current, Math.Min(previous[i].Length, current.Length));
                    }
            }
            Set(cargo, "MaxWeight", 1000f);
            Component drop = __instance.GetComponent(_dropType);
            Cargoes[__instance.gameObject.GetInstanceID()] = new Cargo { Drop = drop,
                Body = __instance.GetComponent<Rigidbody>(), Controller = (int)data[2] };
        }
        static bool Staged(Cargo cargo)
        { return cargo.Drop != null && FastField.GetInt(_animationState, cargo.Drop) < 2; }
        public static bool InteractingPrefix(Component __instance)
        { Cargo cargo; return !Cargoes.TryGetValue(__instance.gameObject.GetInstanceID(), out cargo) || !Staged(cargo); }
        public static void ContainerDestroyPrefix(Component __instance)
        { Cargoes.Remove(__instance.gameObject.GetInstanceID()); }
        public static bool DropUpdatePrefix(Component __instance)
        {
            FrameProf.S(FrameProf.S_TowerDeliveryGate);
            try
            {
                Cargo cargo;
                if (!Cargoes.TryGetValue(__instance.gameObject.GetInstanceID(), out cargo) || !Staged(cargo)) return true;
                if (cargo.Body != null) cargo.Body.isKinematic = true;
                if (Time.time >= cargo.Next)
                {
                    cargo.Next = Time.time + 0.5f;
                    // A staged crate belongs to an in-flight payment. A replacement
                    // master cannot finish that controller's backend transaction.
                    // Destroy it; the original controller refunds, preventing free loot.
                    if (Crocodile.IsMaster() && (cargo.Controller != Crocodile.LocalActor()
                        || !_filling || _crate != __instance.gameObject) && _destroy != null)
                        _destroy.Invoke(null, new object[] { __instance.gameObject });
                }
                return false;
            }
            finally { FrameProf.E(FrameProf.S_TowerDeliveryGate); }
        }

        static bool Near(GameObject player)
        { return player != null && _console != null && (player.transform.position - _console.position).sqrMagnitude <= 8.4f * 8.4f; }
        static bool Held(int actor)
        { int side = TowerRadar.PlayerSide(actor); return side >= 0 && side != TowerRadar.UnreadSide && side == AirfieldHold.State.Holder; }
        static bool Available(int actor, int mask, bool busy)
        { GameObject p = Crocodile.PlayerByActor(actor); return TowerDeliveryCore.CanOrder(Enabled, Crocodile.PlayerUp(p), Near(p), Held(actor), busy, mask, RadarClock.Now, _ready); }
        internal static int Recheck(int actor, int service, int cost, Vector3 target)
        {
            return Crocodile.IsMaster() && TowerDeliveryCore.Service(service)
                && cost == TowerDeliveryCore.Price(TowerDeliveryCore.Mask(service))
                && Available(actor, TowerDeliveryCore.Mask(service), false)
                && (_landing - target).sqrMagnitude < 1f && RadarNet.Ready ? 0 : 7;
        }
        internal static bool ChallengeAllowed(int service, int cost)
        { return Selecting && _canOrder && service == _mask + 3 && cost == TowerDeliveryCore.Price(_mask); }

        internal static void Tick()
        {
            if (_generation != TowerSupport.WorldGeneration)
            {
                _generation = TowerSupport.WorldGeneration;
                Requests.Clear(); _ready = 0f; _planning = false; _placement = -1;
                _requestedUntil = _queryNext = 0f;
                if (_filling) FinishCrate(false);
                if (Selecting) Close();
            }
            if (_tower != TowerRadar.Tower || !TowerRadar.Built)
            {
                if (_console != null) UnityEngine.Object.Destroy(_console.gameObject);
                if (_material != null) UnityEngine.Object.Destroy(_material);
                _console = null; _material = null; _tower = TowerRadar.Tower; _candidate = 0; _placement = -1;
                if (Selecting) Close();
                _near = _canOrder = false;
            }
            if (_planning || _filling) Work();
            if (!Enabled || !TowerRadar.Built) return;
            if (Time.time >= _next)
            {
                _next = Time.time + 0.25f;
                GameObject player = MapTools.LocalPlayer();
                bool atTower = player != null && (player.transform.position - TowerRadar.TowerBase).sqrMagnitude < 336f * 336f;
                if (_console == null)
                {
                    if (Crocodile.IsMaster())
                    { if (_candidate < Candidates.Length && (atTower || Time.time < _requestedUntil)) BuildConsole(); }
                    else if (_placement >= 0) BuildConsole();
                    else if (atTower && Time.time >= _queryNext)
                    {
                        _queryNext = Time.time + 2f;
                        RadarNet.Send(new float[] { 23f, Crocodile.LocalActor() });
                    }
                }
                _near = Near(player);
                _canOrder = _near && Crocodile.PlayerUp(player) && Held(Crocodile.LocalActor());
                if (Selecting && !_canOrder) Close();
                if (Selecting || (_near && _lang < 0)) Labels();
            }
            if (!Selecting)
            { if (_canOrder && !GameUi.WindowOpen && Input.GetKeyDown(KeyCode.J)) { Labels(); _mapOpened = Mortar.ShowMap(true); Selecting = _mapOpened; } }
            else if (Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.Escape)) Close();
        }
        static void Close()
        { Selecting = false; if (_mapOpened) Mortar.ShowMap(false); _mapOpened = false; }

        static bool ClearBox(Vector3 center, Vector3 half, Quaternion rotation)
        {
            // Cold placement only: ALL layers/props/slabs/fences, no model-only mask.
            return Physics.OverlapBox(center, half, rotation, ~0, QueryTriggerInteraction.Ignore).Length == 0;
        }
        static void BuildConsole()
        {
            bool master = Crocodile.IsMaster();
            int index = master ? _candidate++ : _placement;
            if (index < 0 || index >= Candidates.Length) return;
            Vector3 at = TowerRadar.TowerPoint(Candidates[index]);
            at.y = TowerRadar.CabFloorY;
            Quaternion rotation = Quaternion.Euler(0f, TowerRadar.TowerYaw, 0f);
            Vector3 body = new Vector3(0.55f, 0.8f, 0.3f) * TowerDeliveryCore.Units;
            if (master && (!ClearBox(at + Vector3.up * (body.y / 2f + 0.08f), body / 2f, rotation)
                || !ClearBox(at + rotation * new Vector3(0f, 2.6f, 3.1f), new Vector3(1f, 2.4f, 0.9f), rotation))) return;
            GameObject desk = GameObject.CreatePrimitive(PrimitiveType.Cube);
            desk.name = "NDR tower supply order console";
            _console = desk.transform;
            _console.position = at + Vector3.up * (body.y / 2f + 0.08f);
            _console.rotation = rotation; _console.localScale = body;
            _material = new Material(Shader.Find("Standard")); _material.color = new Color(0.22f, 0.3f, 0.24f);
            desk.GetComponent<Renderer>().sharedMaterial = _material;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(desk, TowerRadar.ConsoleRoot.gameObject.scene);
            GameObject plate = new GameObject("Supply order sign"); plate.transform.SetParent(_console, false);
            plate.transform.localPosition = new Vector3(0f, 0.65f, 0f);
            plate.transform.localScale = new Vector3(1f / body.x, 1f / body.y, 1f / body.z);
            TextMesh text = plate.AddComponent<TextMesh>(); text.text = Loc.T("СНАБЖЕНИЕ АЭРОДРОМА [J]", "AIRFIELD SUPPLIES [J]");
            text.characterSize = 0.14f; text.fontSize = 36; text.anchor = TextAnchor.MiddleCenter;
            _placement = index;
            if (master) RadarNet.Send(new float[] { 22f, index });
            RevivalPlugin.L.LogInfo("TowerDelivery: all-collider clearance passed; console at " + at);
        }
        static void Labels()
        {
            int lang = Loc.Ru ? 1 : 0;
            int wait = Mathf.Max(0, Mathf.CeilToInt(_ready - RadarClock.Now));
            if (_lang != lang)
            {
                _lang = lang;
                _labels[0] = Loc.T("52-К: 24 снаряда - 24 000", "52-K: 24 shells - 24,000");
                _labels[1] = Loc.T("ЗУ-23: 6 лент (100) - 18 000", "ZU-23: 6 belts (100) - 18,000");
                _labels[2] = Loc.T("6 аптечек - 12 000", "6 medkits - 12,000");
                _labels[3] = Loc.T("2 набора инструментов - 10 000", "2 toolkits - 10,000");
                _title = Loc.T("ЛУФТПОСТ: СНАБЖЕНИЕ АЭРОДРОМА", "AIRFIELD SUPPLY AIRDROP");
                _prompt = Loc.T("[J] Заказать снабжение (платно)", "[J] Order supplies (paid)");
                _close = Loc.T("Закрыть [J / Esc]", "Close [J / Esc]");
                _help = Loc.T("Доставка: 10 000. Перерыв: 10 минут. Ящик упадёт в 65-145 м от башни. Враги могут его забрать.",
                    "Delivery: 10,000. Cooldown: 10 minutes. Crate lands 65-145 m from the tower. Enemies can take it.");
                _shownMask = -1;
            }
            if (_shownMask != _mask || _shownWait != wait)
            {
                _shownMask = _mask; _shownWait = wait;
                _order = wait > 0 ? Loc.T("Следующая доставка через ", "Next delivery in ") + wait + " s"
                    : Loc.T("Заказать: ", "Order: ") + TowerDeliveryCore.Price(_mask).ToString("N0");
            }
        }
        internal static void Draw()
        {
            if (!_near) return;
            if (_text == null) { _text = new GUIStyle(GUI.skin.label); _text.wordWrap = true; }
            if (!Selecting) { if (_canOrder) GUI.Label(new Rect(Screen.width / 2f - 200f, Screen.height - 150f, 400f, 30f), _prompt, _text); return; }
            Rect r = new Rect(Screen.width / 2f - 230f, Screen.height / 2f - 205f, 460f, 410f);
            GUI.Box(r, _title);
            for (int i = 0; i < 4; i++)
            {
                bool old = (_mask & (1 << i)) != 0;
                bool chosen = GUI.Toggle(new Rect(r.x + 20f, r.y + 45f + 34f * i, 420f, 30f), old, _labels[i]);
                if (chosen != old) { _mask ^= 1 << i; Labels(); }
            }
            GUI.Label(new Rect(r.x + 20f, r.y + 185f, 420f, 70f), _help, _text);
            bool enabled = GUI.enabled;
            GUI.enabled = _canOrder && _mask > 0 && _ready <= RadarClock.Now && !Mercs.MoneyBusy
                && !TowerSupportPayments.Busy && !TowerSupportPayments.WalletBusy && !_planning && !_filling;
            if (GUI.Button(new Rect(r.x + 20f, r.y + 265f, 420f, 32f), _order)) Request();
            GUI.enabled = enabled;
            GUI.Label(new Rect(r.x + 20f, r.y + 302f, 420f, 62f), _status, _text);
            if (GUI.Button(new Rect(r.x + 20f, r.y + 370f, 420f, 28f), _close)) Close();
        }
        static void Request()
        {
            TowerSupportPayments.Expect(_mask + 3);
            _status = Loc.T("Проверка доставки и оплаты...", "Checking drop site and payment...");
            int serial = ++_serial; if (serial > 16777215) serial = _serial = 1;
            if (Crocodile.IsMaster()) Receive(new float[] { 20f, _mask, serial }, Crocodile.LocalActor());
            else RadarNet.Send(new float[] { 20f, _mask, serial });
        }
        internal static void Receive(float[] msg, int sender)
        {
            if (msg.Length == 2 && msg[0] == 22f && TowerSupport.FromMaster(sender))
            {
                int index = (int)msg[1];
                if (msg[1] == index && index >= 0 && index < Candidates.Length && _console == null) _placement = index;
            }
            else if (msg.Length == 2 && msg[0] == 23f && Crocodile.IsMaster())
            {
                GameObject player = Crocodile.PlayerByActor(sender);
                if (!Enabled || !TowerRadar.Built || (int)msg[1] != sender || player == null
                    || (player.transform.position - TowerRadar.TowerBase).sqrMagnitude > 336f * 336f) return;
                if (_console != null) RadarNet.Send(new float[] { 22f, _placement });
                else
                {
                    // A remote visitor can request construction while the host
                    // is elsewhere. Retry a previously obstructed room on demand.
                    if (Time.time >= _requestedUntil || (_candidate >= Candidates.Length && Time.time >= _queryNext))
                    { _candidate = 0; _queryNext = Time.time + 10f; }
                    _requestedUntil = Time.time + 10f;
                }
            }
            else if (msg.Length == 3 && msg[0] == 20f && Crocodile.IsMaster())
            {
                int mask = (int)msg[1], serial = (int)msg[2], previous;
                if (msg[1] != mask || msg[2] != serial || serial < 1 || serial > 16777215
                    || (Requests.TryGetValue(sender, out previous) && serial <= previous)) return;
                Requests[sender] = serial;
                if (!Available(sender, mask, _planning || _filling || TowerSupportPayments.Busy || TowerSupportPayments.WalletBusy || TowerPaymentWire.Pending))
                { PaymentReply(sender, 7); return; }
                _planning = true; _planActor = sender; _planMask = mask; _attempt = 0;
                _seed = Guid.NewGuid().GetHashCode(); _workNext = Time.time;
            }
            else if (msg.Length == 4 && msg[0] == 21f && TowerSupport.FromMaster(sender))
            {
                _ready = Mathf.Max(_ready, msg[3]);
                if ((int)msg[1] == Crocodile.LocalActor()) OnReply((int)msg[2]);
            }
        }
        internal static void PaymentReply(int actor, int reason)
        {
            if (Crocodile.IsMaster()) RadarNet.Send(new float[] { 21f, actor, reason, _ready });
            if (actor == Crocodile.LocalActor()) OnReply(reason);
        }
        static void OnReply(int reason)
        {
            TowerSupportPayments.MissionReply(reason);
            _status = reason == 0 ? Loc.T("Оплачено. Ящик спускается у аэродрома.", "Paid. Crate descending near the airfield.")
                : reason == 10 ? Loc.T("Ожидание подтверждения сервера...", "Waiting for server payment confirmation...")
                : reason == 8 ? Loc.T("Недостаточно денег.", "Not enough money.")
                : reason == 9 ? Loc.T("Оплата не подтверждена. Не повторяйте заказ; проверьте баланс.", "Payment unsettled. Do not reorder; check your balance.")
                : Loc.T("Заказ отклонён. Проверьте контроль башни, перерыв и сервер оплаты.", "Order refused. Check tower control, cooldown and payment server.");
        }

        static bool Site(int seed, int attempt, out Vector3 landing)
        {
            float x, z; TowerDeliveryCore.Scatter(seed, attempt, out x, out z);
            landing = TowerRadar.TowerBase + new Vector3(x, 0f, z);
            float ground;
            if (!RevivalTroopInsertion.TerrainHeight(landing, out ground)) return false;
            RaycastHit hit;
            // Any roof/prop/fence over the footprint refuses this candidate.
            if (!Physics.Raycast(new Vector3(landing.x, ground + 400f, landing.z), Vector3.down,
                out hit, 410f, ~0, QueryTriggerInteraction.Ignore) || hit.collider.GetComponent<TerrainCollider>() == null) return false;
            landing.y = hit.point.y;
            return ClearBox(landing + Vector3.up * 6f, new Vector3(6f, 5.8f, 6f), Quaternion.identity);
        }
        static void Work()
        {
            if (Time.time < _workNext) return;
            _workNext = Time.time + (_planning ? 0.5f : 0.1f);
            if (!Crocodile.IsMaster() || !TowerRadar.Built)
            { _planning = false; if (_filling) FinishCrate(false); return; }
            if (_awaitCommit) return;
            if (_planning)
            {
                if (!Available(_planActor, _planMask, TowerSupportPayments.Busy || TowerSupportPayments.WalletBusy || TowerPaymentWire.Pending))
                { _planning = false; PaymentReply(_planActor, 7); return; }
                if (!Site(_seed, _attempt++, out _landing))
                { if (_attempt >= 12) { _planning = false; PaymentReply(_planActor, 7); } return; }
                _planning = false;
                bool sent = _instantiate != null && _destroy != null && _add != null && _animation != null
                    && TowerSupportPayments.Begin(_planActor, _planMask + 3, TowerDeliveryCore.Price(_planMask), _landing, "");
                PaymentReply(_planActor, sent ? 10 : 7);
                return;
            }
            try
            {
                if (_crate == null || _container == null || _drop == null || _body == null) { FinishCrate(false); return; }
                int item = TowerDeliveryCore.ItemAt(_planMask, _slot);
                if (item == 0) { FinishCrate(true); return; }
                object cargo = Get(_container, "_containerData");
                int before = Turret.CountInContainer(cargo, item);
                _add.Invoke(_container, new object[] { item });
                if (Turret.CountInContainer(cargo, item) != before + 1) { FinishCrate(false); return; }
                _slot++;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerDelivery fill: " + ex.Message); FinishCrate(false); }
        }
        internal static bool Launch(int actor, int service, Vector3 target)
        {
            if (_filling || Recheck(actor, service, TowerDeliveryCore.Price(TowerDeliveryCore.Mask(service)), target) != 0) return false;
            try
            {
                _crate = _instantiate.Invoke(null, new object[] { Prefab, target + Vector3.up * 336f,
                    Quaternion.identity, 0, new object[] { Tag, TowerDeliveryCore.Mask(service), Crocodile.LocalActor() } }) as GameObject;
                if (_crate == null) return false;
                _container = _crate.GetComponent(_containerType); _drop = _crate.GetComponent(_dropType) as Behaviour;
                _body = _crate.GetComponent<Rigidbody>();
                if (_container == null || _drop == null || _body == null) { DestroyCrate(); return false; }
                _body.isKinematic = true;
                _planActor = actor; _planMask = TowerDeliveryCore.Mask(service); _slot = 0;
                _awaitCommit = false; _filling = true; _workNext = Time.time + 0.1f;
                return true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerDelivery spawn: " + ex.Message); DestroyCrate(); return false; }
        }
        static void DestroyCrate()
        {
            try { if (_crate != null && Crocodile.IsMaster() && _destroy != null) _destroy.Invoke(null, new object[] { _crate }); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerDelivery staged cleanup retry: " + ex.Message); }
            _crate = null;
        }
        static void FinishCrate(bool success)
        {
            // Keep the backend transaction refundable while filling. Commit
            // only a complete crate, then release on the authenticated reply.
            if (success) _awaitCommit = true;
            else Abort();
            TowerSupportPayments.DeliveryFinished(success);
        }
        internal static void Abort()
        {
            _filling = _awaitCommit = false;
            DestroyCrate(); _container = null; _drop = null; _body = null;
        }
        internal static bool Release()
        {
            if (!_filling || !_awaitCommit || _crate == null || _drop == null || _body == null) return false;
            try
            {
                _animation.Invoke(_drop, new object[] { 2 });
                _body.isKinematic = false;
                _ready = RadarClock.Now + TowerDeliveryCore.Cooldown;
                RevivalPlugin.L.LogInfo("TowerDelivery: basket " + _planMask + " delivered for actor " + _planActor + " at " + _landing);
                _filling = _awaitCommit = false;
                _crate = null; _container = null; _drop = null; _body = null;
                return true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerDelivery release: " + ex.Message); return false; }
        }
    }
}
