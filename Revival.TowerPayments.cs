// Authenticated backend payment for remote tower holders. Photon carries only
// the approval challenge; the master queries its own backend for paid status.
using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class TowerSupportPayments
    {
        static int _nonce, _actor, _service, _cost, _generation, _controllerActor;
        static Vector3 _target;
        static string _faction, _controller;
        static bool _busy, _committing, _refunding, _owner, _missionConfirmed;
        static int _expected = -1, _ownerNonce;
        static float _next, _until, _expectUntil;
        static string _ownerController;
        internal static bool Busy { get { return _busy; } }
        internal static bool WalletBusy { get { return _owner; } }

        internal static void Expect(int service)
        { _expected = service; _expectUntil = Time.time + 15f; }

        internal static bool Begin(int actor, int service, int cost, Vector3 target, string faction)
        {
            if (_busy || _owner || TowerPaymentWire.Pending) return false;
            string owner = Mercs.SteamOf(Crocodile.PlayerByActor(actor));
            string controller = Mercs.SteamOf(Crocodile.PlayerByActor(Crocodile.LocalActor()));
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(controller)) return false;
            _nonce = 1 + (Guid.NewGuid().GetHashCode() & 0x7fffffff) % 16777214;
            _actor = actor; _service = service; _cost = cost; _target = target;
            _faction = faction; _controller = controller; _generation = TowerSupport.WorldGeneration;
            _busy = true; _committing = false; _refunding = false; _until = Time.time + 45f;
            bool sent = TowerPaymentWire.Send(_nonce, "op=reserve\nowner=" + owner
                + "\ncost=" + N(cost) + "\nservice=" + N(service) + "\n", Reserved);
            if (!sent) _busy = false;
            return sent;
        }

        static string N(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Args(string op)
        { return "op=" + op + "\ncontroller=" + _controller + "\n"; }
        static void Reserved(TowerPaymentWire.Reply reply)
        {
            if (reply.Result != "ok" || reply.State != "reserved") { Finish(7); return; }
            if (!Valid()) { Cancel(); return; }
            // Cost is split into exact 16-bit pieces: float cannot carry every
            // Int32 wallet amount without rounding.
            RadarNet.Send(new float[] { 8f, _actor, _nonce, _service, _cost >> 16, _cost & 65535 });
            _next = Time.time + 0.5f;
        }
        static bool Valid()
        {
            return Crocodile.IsMaster() && _generation == TowerSupport.WorldGeneration
                && (_service != 1 || Fraktion.Eigene(_faction) == Fraktion.Spielerseite(Crocodile.PlayerByActor(_actor)))
                && TowerSupport.RecheckRemote(_actor, _service, _cost, _target) == 0;
        }
        static void Finish(int reason)
        {
            _busy = false; _committing = false; _refunding = false;
            TowerSupport.PaymentReply(_actor, reason);
        }
        static void Cancel()
        {
            _refunding = true;
            if (!TowerPaymentWire.Send(_nonce, Args("refund"), Refunded)) _next = Time.time + 0.5f;
        }
        static void Refunded(TowerPaymentWire.Reply reply)
        {
            if (reply.Result == "ok" && (reply.State == "refunded" || reply.State == "cancelled")) Finish(7);
            else
            {
                // Unknown transport outcome is queried/retried with the same
                // nonce. Never report success or initiate a second purchase.
                TowerSupport.PaymentReply(_actor, 9); _next = Time.time + 1f;
            }
        }
        static void Status(TowerPaymentWire.Reply reply)
        {
            if (reply.Result != "ok") { Cancel(); return; }
            if (reply.State == "refunded" || reply.State == "cancelled") { Finish(7); return; }
            if (!Valid() || Time.time >= _until) { Cancel(); return; }
            if (reply.State == "committed" && _committing) { Committed(reply); return; }
            if (reply.State == "paid")
            {
                _committing = true;
                if (!TowerPaymentWire.Send(_nonce, Args("commit"), Committed)) Cancel();
            }
            else _next = Time.time + 0.5f;
        }
        static void Committed(TowerPaymentWire.Reply reply)
        {
            if (reply.Result == "timeout") { _next = Time.time + 0.5f; return; }
            if (reply.Result != "ok" || reply.State != "committed" || !Valid()) { Cancel(); return; }
            bool launched = TowerSupport.LaunchRemote(_actor, _service, _target, _faction);
            if (!launched) { Cancel(); return; }
            _busy = false; _committing = false;
        }

        internal static void Challenge(float[] msg, int sender)
        {
            if (msg.Length != 6 || !TowerSupport.FromMaster(sender) || _owner || TowerPaymentWire.Pending
                || (int)msg[1] != Crocodile.LocalActor() || _expected < 0 || Time.time > _expectUntil
                || (int)msg[3] != _expected || !TowerSupport.Selecting
                || Mercs.MoneyBusy
                || TowerRadar.OperatorActor != Crocodile.LocalActor() || !TowerRadar.ConsoleAlive) return;
            for (int i = 1; i < 6; i++) if (msg[i] != Mathf.RoundToInt(msg[i])) return;
            if (msg[2] < 1f || msg[2] > 16777215f || msg[4] < 0f || msg[4] > 32767f
                || msg[5] < 0f || msg[5] > 65535f) return;
            int cost = ((int)msg[4] << 16) | (int)msg[5];
            if (cost < 1) return;
            _ownerController = Mercs.SteamOf(Crocodile.PlayerByActor(sender));
            if (string.IsNullOrEmpty(_ownerController)) return;
            _ownerNonce = (int)msg[2]; _controllerActor = sender; _owner = true; _missionConfirmed = false; _expected = -1;
            _until = Time.time + 75f;
            if (!TowerPaymentWire.Send(_ownerNonce, "op=accept\ncontroller=" + _ownerController
                + "\ncost=" + N(cost) + "\nservice=" + N((int)msg[3]) + "\n", OwnerAnswer)) _owner = false;
        }
        static void OwnerAnswer(TowerPaymentWire.Reply reply)
        {
            if (reply.Result == "err:funds")
            { TowerSupport.PaymentReply(Crocodile.LocalActor(), 8); _owner = false; return; }
            if (reply.Result == "ok" && reply.Balance >= 0)
            {
                if (!ApplyWallet(reply.Balance)) { _next = Time.time + 1f; return; }
                TowerPaymentWire.Send(_ownerNonce, "op=sync\ncontroller=" + _ownerController
                    + "\nbalance=" + N(reply.Balance) + "\n", OwnerSynced);
            }
            else _next = Time.time + 1f;
        }
        static void OwnerSynced(TowerPaymentWire.Reply reply)
        {
            if (reply.Result == "ok" && ((reply.State == "committed" && _missionConfirmed)
                || reply.State == "refunded" || reply.State == "cancelled"))
            { _owner = false; return; }
            _next = Time.time + 0.5f;
        }
        internal static void MissionReply(int reason)
        { if (_owner && reason == 0) _missionConfirmed = true; }
        static bool ApplyWallet(int balance)
        {
            Component stats = Admin.LocalStats();
            if (stats == null) return false;
            try
            {
                MethodInfo get = AccessTools.Method(stats.GetType(), "GetPlayerMoney", Type.EmptyTypes, null);
                MethodInfo add = AccessTools.Method(stats.GetType(), "AddPlayerMoney",
                    new Type[] { typeof(int), typeof(bool), typeof(bool) }, null);
                if (get == null || add == null) return false;
                int current = Convert.ToInt32(get.Invoke(stats, null));
                if (current < 0) return false;
                if (current != balance) add.Invoke(stats, new object[] { balance - current, true, false });
                return Convert.ToInt32(get.Invoke(stats, null)) == balance;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Tower wallet sync: " + ex.Message); return false; }
        }

        internal static void Tick()
        {
            TowerPaymentWire.Tick();
            if (!_busy && !_owner) return;
            if (TowerPaymentWire.Pending || Time.time < _next) return;
            if (_busy)
            {
                if (_refunding || !Valid() || Time.time >= _until) Cancel();
                else TowerPaymentWire.Send(_nonce, Args("status"), Status);
            }
            else if (_owner)
            {
                // Keep polling until the backend settles. A master handoff or
                // world exit cannot turn a native wallet credit into a refund.
                TowerPaymentWire.Send(_ownerNonce, "op=status\ncontroller=" + _ownerController + "\n", OwnerAnswer);
            }
            _next = Time.time + 0.5f;
        }
    }

    internal static class TowerPaymentWire
    {
        internal sealed class Reply
        { internal string Result, State; internal int Balance = -1; }
        static object _backend, _connection;
        static Type _bag, _container, _request;
        static MethodInfo _sendBag, _send;
        static Action<Reply> _done;
        static string _text;
        static int _nonce, _tries;
        static int _sequence, _activeSequence;
        static float _due;
        internal static bool Pending { get { return _done != null; } }
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        internal static void Install(Harmony harmony)
        {
            Type backend = RevivalPlugin.TypeByName("BackendManager");
            MethodInfo receive = backend == null ? null : AccessTools.Method(backend, "MsgStorageListRecieve", null, null);
            if (receive != null)
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(TowerPaymentWire).GetMethod("StoragePrefix"));
                prefix.priority = Priority.First;
                harmony.Patch(receive, prefix, null, null, null, null);
            }
            // Automated upkeep must wait while native money is converging to
            // the backend debit/refund. Do not modify the mercenary system.
            MethodInfo busy = AccessTools.PropertyGetter(typeof(Mercs), "MoneyBusy");
            if (busy != null) harmony.Patch(busy,
                new HarmonyMethod(typeof(TowerPaymentWire).GetMethod("MoneyBusyPrefix")), null, null, null, null);
        }
        public static bool MoneyBusyPrefix(ref bool __result)
        {
            if (!TowerSupportPayments.WalletBusy) return true;
            __result = true; return false;
        }
        static object Get(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = obj.GetType().GetProperty(name);
            if (p != null) return p.GetValue(obj, null);
            FieldInfo f = FastField.Find(obj.GetType(), name); return f == null ? null : f.GetValue(obj);
        }
        static void Set(object obj, string name, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(name);
            if (p != null) { p.SetValue(obj, Convert.ChangeType(value, p.PropertyType, CI), null); return; }
            FieldInfo f = FastField.Find(obj.GetType(), name);
            if (f == null) throw new MissingFieldException(name);
            f.SetValue(obj, Convert.ChangeType(value, f.FieldType, CI));
        }
        static bool Connect()
        {
            Component stats = Admin.LocalStats();
            _backend = Get(stats, "_backendManager"); _connection = Get(_backend, "_connection");
            if (_connection == null) return false;
            MethodInfo connected = AccessTools.Method(_connection.GetType(), "isConnected", Type.EmptyTypes, null);
            if (connected == null || !(bool)connected.Invoke(_connection, null)) return false;
            _sendBag = AccessTools.Method(_backend.GetType(), "MessagePlayerStorage", null, null);
            if (_sendBag == null) return false;
            _bag = _sendBag.GetParameters()[0].ParameterType; _send = null;
            for (Type t = _connection.GetType(); t != null && _send == null; t = t.BaseType)
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "sendMessage" && m.GetParameters().Length == 1) { _send = m; break; }
            if (_send == null) return false;
            _container = _send.GetParameters()[0].ParameterType;
            PropertyInfo p = _container.GetProperty("storageListRequest");
            FieldInfo f = p == null ? AccessTools.Field(_container, "storageListRequest") : null;
            _request = p != null ? p.PropertyType : f == null ? null : f.FieldType;
            return _request != null;
        }
        static void Transmit()
        {
            object bag = Activator.CreateInstance(_bag);
            Set(bag, "storageId", 7701); Set(bag, "storageDescription", _text);
            _sendBag.Invoke(_backend, new object[] { bag });
            object request = Activator.CreateInstance(_request); Set(request, "storageId", 7701);
            object container = Activator.CreateInstance(_container); Set(container, "storageListRequest", request);
            _send.Invoke(_connection, new object[] { container });
        }
        internal static bool Send(int nonce, string args, Action<Reply> done)
        {
            if (Pending) return false;
            try
            {
                if (!Connect()) return false;
                _activeSequence = ++_sequence;
                _nonce = nonce; _text = "ndr-tower1\nnonce=" + nonce.ToString(CI)
                    + "\nseq=" + _activeSequence.ToString(CI) + "\n" + args;
                _done = done; _tries = 0; _due = Time.time + 2f; Transmit(); return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Tower payment send: " + ex.Message);
                // A command may already have reached the server. Keep its
                // callback and retry the same idempotent operation.
                return _done != null;
            }
        }
        internal static void Tick()
        {
            if (!Pending || Time.time < _due) return;
            if (++_tries > 3) { Complete(new Reply { Result = "timeout" }); return; }
            _due = Time.time + 2f;
            try { if (Connect()) Transmit(); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Tower payment retry: " + ex.Message); }
        }
        static void Complete(Reply reply)
        { Action<Reply> done = _done; _done = null; if (done != null) done(reply); }
        public static bool StoragePrefix(object __instance, object __0)
        {
            IList list = __0 as IList;
            if (list == null) return true;
            bool ours = false, plainStash = true;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                object id = Get(list[i], "storageId");
                if (id == null) { plainStash = false; continue; }
                int storageId = Convert.ToInt32(id);
                if (storageId != 7701)
                { if (storageId != 0 && storageId != 1) plainStash = false; continue; }
                string text = Get(list[i], "storageDescription") as string;
                list.RemoveAt(i); ours = true;
                if (!Pending || string.IsNullOrEmpty(text) || !text.StartsWith("ndr-tower1\n", StringComparison.Ordinal)) continue;
                int nonce = -1, sequence = -1; Reply reply = new Reply();
                foreach (string line in text.Split('\n'))
                {
                    int at = line.IndexOf('='); if (at < 1) continue;
                    string key = line.Substring(0, at), value = line.Substring(at + 1).TrimEnd('\r');
                    if (key == "nonce") int.TryParse(value, NumberStyles.Integer, CI, out nonce);
                    else if (key == "seq") int.TryParse(value, NumberStyles.Integer, CI, out sequence);
                    else if (key == "balance") int.TryParse(value, NumberStyles.Integer, CI, out reply.Balance);
                    else if (key == "result") reply.Result = value;
                    else if (key == "state") reply.State = value;
                }
                if (nonce == _nonce && sequence == _activeSequence) Complete(reply);
            }
            if (!ours && plainStash && Pending && Get(__instance, "currentStorage") == null)
            {
                // Older backends return ordinary stash bags for any id. Keep
                // that unsupported-channel response out of the native UI.
                Complete(new Reply { Result = "err:no-server-support" });
                return false;
            }
            return !ours || list.Count > 0;
        }
    }
}
