// Native vehicle seat captions. Cosmetic only: native passenger/button authority stays intact.
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercRide
    {
        static readonly object[] SeatNamesPacket = new object[] { 108, new int[0], new string[0] };
        static float _nextSeatNames;
        static void PublishSeatNames()
        {
            if (!_dirty && Time.time < _nextSeatNames) return;
            _nextSeatNames = Time.time + 1f;
            int count = 0;
            for (int i = 0; i < _riders.Count; i++)
                if (_riders[i].Ride.Carrier != null && _riders[i].Ride.Carrier.View > 0) count++;
            int[] ids = (int[])SeatNamesPacket[1];
            string[] names = (string[])SeatNamesPacket[2];
            if (ids.Length != count)
            {
                ids = new int[count]; names = new string[count];
                SeatNamesPacket[1] = ids; SeatNamesPacket[2] = names;
            }
            int at = 0;
            for (int i = 0; i < _riders.Count; i++)
            {
                MercUnit unit = _riders[i];
                if (unit.Ride.Carrier == null || unit.Ride.Carrier.View <= 0) continue;
                ids[at] = unit.Id; names[at] = unit.Name; at++;
            }
            // Reuse the existing reliable Photon delegate and event; no roster writes.
            SendAAPacket(SeatNamesPacket);
        }

        static void ReceiveSeatNames(object[] packet, int sender)
        {
            int[] ids = packet[1] as int[];
            string[] names = packet[2] as string[];
            if (sender <= 0 || ids == null || names == null || ids.Length != names.Length || ids.Length > 16) return;
            for (int i = 0; i < ids.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i]) || names[i].Length > 96) return;
                for (int k = 0; k < names[i].Length; k++) if (char.IsControl(names[i][k])) return;
            }
            // Struct enumerator and cached numeric identity avoid per-heartbeat key strings.
            foreach (MercSeat st in _remote.Values)
            {
                if (st.Actor != sender) continue;
                for (int i = 0; i < ids.Length; i++)
                    if (st.DisplayId == ids[i]) { st.DisplayName = names[i]; break; }
            }
        }
    }

    internal static class NativeSeatNames
    {
        sealed class Row
        {
            internal object View, Label;
            internal string Caption, Name;
            internal bool Russian;
        }
        static FieldInfo _rows, _manager, _vehicle, _label;
        delegate void WriteText(object label, string text);
        static WriteText _write;
        static Row[] _cache;
        static object _inventory;
        static MercCarrier _carrier;
        static Array _passengers;
        static Type _vgs;
        static FieldInfo _pass;
        static float _next;

        internal static void Install(Harmony harmony, Type inventory)
        {
            if (inventory == null) return;
            _rows = AccessTools.Field(inventory, "PlayersInVehicle");
            _manager = AccessTools.Field(inventory, "_plrVehicleManager");
            _vehicle = AccessTools.Field(RevivalPlugin.TypeByName("PlayerVehicleManager"), "Vehicle");
            _vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
            _pass = AccessTools.Field(_vgs, "Passengers");
            Type row = RevivalPlugin.TypeByName("VehiclePlayerUI");
            _label = AccessTools.Field(row, "label");
            MethodInfo setter = AccessTools.PropertySetter(_label.FieldType, "text");
            System.Reflection.Emit.DynamicMethod dm = new System.Reflection.Emit.DynamicMethod(
                "NativeSeatText", typeof(void), new Type[] { typeof(object), typeof(string) }, typeof(NativeSeatNames), true);
            System.Reflection.Emit.ILGenerator il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, _label.FieldType);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt, setter);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            _write = (WriteText)dm.CreateDelegate(typeof(WriteText));
            harmony.Patch(AccessTools.Method(inventory, "UpdateVehiclePlayersUI", null, null),
                new HarmonyMethod(typeof(NativeSeatNames).GetMethod("Before")),
                new HarmonyMethod(typeof(NativeSeatNames).GetMethod("After")), null, null, null);
            RevivalPlugin.L.LogInfo("NativeSeatNames: native seat labels active, including overflow rows.");
        }

        public static void Before(object __instance)
        {
            FrameProf.S(FrameProf.S_VehicleSeatsT);
            try
            {
                if (_inventory == __instance && Time.time < _next) return;
                _next = Time.time + 0.5f;
                _inventory = __instance;
                object manager = _manager.GetValue(__instance);
                GameObject vehicle = manager == null ? null : _vehicle.GetValue(manager) as GameObject;
                Component vgs = vehicle == null ? null : vehicle.GetComponent(_vgs);
                _carrier = MercRide.SeatHudGround(vgs);
                _passengers = vgs == null ? null : _pass.GetValue(vgs) as Array;
                IList rows = _rows.GetValue(__instance) as IList;
                if (_carrier == null || rows == null || rows.Count == 0) { _cache = null; return; }
                // The shipped inventory has 12 rows, the Ural has 15 seats. Clone the native
                // final row and continue its spacing so native Update never indexes past it.
                while (rows.Count < _carrier.Seats)
                {
                    Component last = rows[rows.Count - 1] as Component;
                    Component prior = rows.Count > 1 ? rows[rows.Count - 2] as Component : null;
                    if (last == null || prior == null) break;
                    Vector3 spacing = last.transform.localPosition - prior.transform.localPosition;
                    GameObject clone = (GameObject)UnityEngine.Object.Instantiate(last.gameObject);
                    clone.transform.SetParent(last.transform.parent, false);
                    clone.transform.localPosition = last.transform.localPosition + spacing;
                    clone.SetActive(true);
                    rows.Add(clone.GetComponent(last.GetType()));
                }
                if (_cache == null || _cache.Length != rows.Count) _cache = new Row[rows.Count];
                bool ru = Loc.T("ru", "en") == "ru";
                for (int i = 0; i < rows.Count; i++)
                {
                    object view = rows[i];
                    Row row = _cache[i];
                    if (view == null) continue;
                    if (row == null || row.View != view)
                    {
                        row = new Row(); row.View = view; row.Label = _label.GetValue(view); _cache[i] = row;
                        // Names are plain text even though native NGUI enables markup by default.
                        FieldInfo encoding = AccessTools.Field(_label.FieldType, "mEncoding");
                        if (encoding != null) encoding.SetValue(row.Label, false);
                    }
                    string name = null; Component ai;
                    bool merc = i < _carrier.Seats && MercRide.SeatHudPlayer(_carrier, i) == null
                        && MercRide.SeatHudMerc(_carrier, i, out name, out ai);
                    if (!merc) name = null;
                    // Rebuild strings only on identity/language changes; free/player rows remain native.
                    if (row.Name != name || row.Russian != ru)
                    {
                        if (row.Caption != null && name == null && MercRide.SeatHudPlayer(_carrier, i) == null)
                            _write(row.Label, Loc.T("Свободно", "free"));
                        row.Name = name; row.Russian = ru;
                        row.Caption = name == null ? null : Loc.T("Место ", "Seat ") + (i + 1) + ": " + name;
                    }
                }
            }
            finally { FrameProf.E(FrameProf.S_VehicleSeatsT); }
        }

        public static void After()
        {
            if (_cache == null) return;
            FrameProf.S(FrameProf.S_VehicleSeatsT);
            try
            {
                for (int i = 0; i < _cache.Length; i++)
                {
                    Row row = _cache[i];
                    if (row != null && row.Caption != null && row.Label != null
                        && _carrier != null && (_passengers == null || i >= _passengers.Length || _passengers.GetValue(i) == null)) _write(row.Label, row.Caption);
                }
            }
            finally { FrameProf.E(FrameProf.S_VehicleSeatsT); }
        }
    }
}
