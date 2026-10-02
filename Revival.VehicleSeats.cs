// Next Day: Survival - Revival Toolkit. Z K9b: every vehicle's seat roster.
// Seat ownership uses native player RPCs and MercRide event 199. Kind 7 adds
// cosmetic, owner-reported player health; it never changes native life data.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercRide
    {
        internal static MercCarrier SeatHudCarrier(GameObject player)
        {
            GameObject heli = PlayerHeli.Machine;
            if (heli != null) return CarrierOf(MercCarrier.Heli, heli, null);
            GameObject plane = PlayerAn2.Plane;
            if (plane != null) return CarrierOf(MercCarrier.Plane, plane, null);
            Component vgs = player == null ? null : GunnerAI.Carrier(player.transform);
            return SeatHudGround(vgs);
        }

        internal static MercCarrier SeatHudGround(Component vgs)
        {
            return vgs == null ? null : CarrierOf(MercCarrier.Ground, vgs.gameObject, vgs);
        }

        internal static MercCarrier SeatHudAir(GameObject go, bool plane)
        {
            return CarrierOf(plane ? MercCarrier.Plane : MercCarrier.Heli, go, null);
        }

        internal static GameObject SeatHudPlayer(MercCarrier c, int index)
        {
            if (c.Kind != MercCarrier.Ground) return null;
            Array pass = Passengers(c.Vgs);
            return pass != null && index >= 0 && index < pass.Length ? pass.GetValue(index) as GameObject : null;
        }

        internal static bool SeatHudMerc(MercCarrier c, int index, out string name, out Component ai)
        {
            name = null; ai = null;
            for (int i = 0; i < _riders.Count; i++)
            {
                MercUnit u = _riders[i];
                if (u.Ride.Carrier != c || u.Ride.Seat != index) continue;
                name = u.Name; ai = u.Ai;
                return true;
            }
            // Unresolved peer bodies still occupy their advertised seat. Never
            // expose a foreign roster, invent its name or wait for model loading.
            foreach (MercSeat st in _remote.Values)
            {
                if (Time.time - st.Heard > HeardSeconds) continue;
                if (st.WantKind != c.Kind || st.WantView != c.View || st.WantSeat != index) continue;
                ai = st.Ai;
                return true;
            }
            return false;
        }
    }

    internal static class VehicleSeats
    {
        sealed class Cell
        {
            internal VehicleSeatCard Card;
            internal readonly GUIContent Text = new GUIContent();
            internal GameObject Body;
            internal Component Source;
            internal object Photon;
            internal FieldInfo DataField, Health, Maximum;
            internal MethodInfo Nick;
            internal int Actor;
        }

        static MercCarrier _carrier;
        static int _shape;
        static Cell[] _cells;
        static float _next;
        static GUIStyle _style;
        static readonly GUIContent Heading = new GUIContent();
        static readonly Cell Local = new Cell();
        static readonly RaycastHit[] Hits = new RaycastHit[32];
        static readonly Dictionary<int, VehicleSeatVital> Vitals = new Dictionary<int, VehicleSeatVital>(64);
        static readonly float[] Packet = new float[5];
        static float _sendAt;
        static Type _vgs, _pnc, _life;
        static MethodInfo _photon;
        static string _free, _occupied, _you;
        static bool _ru, _languageReady;

        internal static void Tick()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) { _carrier = null; Vitals.Clear(); return; }
            if (Local.Body != player) Bind(Local, player, true, null);
            if (ReadHealth(Local) == 0f) { _carrier = null; return; }
            MercCarrier c = MercRide.SeatHudCarrier(player);
            bool riding = c != null;
            if (c == null) c = EntryVehicle(player);
            if (c == null || c.Go == null || !c.Go.activeInHierarchy) { _carrier = null; return; }
            bool ru = Loc.T("ru", "en") == "ru";
            if (!_languageReady || _ru != ru)
            {
                _ru = ru; _languageReady = true;
                _free = Loc.T("свободно", "free");
                _occupied = Loc.T("занято", "occupied");
                _you = Loc.T("Вы", "You");
                _cells = null;
            }
            int count = c.Seats + (c.Air ? 1 : 0);
            if (_carrier != c || _shape != c.Shape || _cells == null || _cells.Length != count)
            {
                _carrier = c; _shape = c.Shape;
                _cells = new Cell[count];
                for (int i = 0; i < count; i++)
                {
                    int seat = c.Air ? i - 1 : i;
                    string prefix = seat < 0 ? Loc.T("Пилот: ", "Pilot: ")
                        : !c.Air && seat == 0 ? Loc.T("Водитель: ", "Driver: ")
                        : seat == c.GunSeat ? Loc.T("Стрелок: ", "Gunner: ")
                        : (seat + 1).ToString() + ": ";
                    _cells[i] = new Cell();
                    _cells[i].Card = new VehicleSeatCard(prefix);
                }
            }
            string title = riding ? Loc.T("МЕСТА В МАШИНЕ", "VEHICLE SEATS") : Loc.T("ПОСАДКА: МЕСТА", "BOARDING: SEATS");
            if (Heading.text != title) Heading.text = title;
            for (int i = 0; i < count; i++)
            {
                int seat = c.Air ? i - 1 : i;
                Refresh(_cells[i], c, seat, player);
                if (_cells[i].Body == player && _cells[i].Card.Kind == VehicleSeatCard.Player)
                    Publish(c, seat, _cells[i].Card.Health);
            }
        }

        // Cosmetic data only: actual seat ownership still comes from native
        // passengers/aircraft leases. Sender identity is checked at consumption.
        internal static void OnVitals(float[] data, int sender)
        {
            VehicleSeatVital vital;
            if (!VehicleSeatVital.Parse(data, sender, Time.time, out vital)) return;
            if (Vitals.Count >= 64 && !Vitals.ContainsKey(sender)) Vitals.Clear();
            Vitals[sender] = vital;
        }

        static void Publish(MercCarrier c, int seat, float hp)
        {
            if (c.View <= 0 || hp < 0f) return;
            if (Time.time < _sendAt && Packet[1] == c.Kind && Packet[2] == c.View
                && Packet[3] == seat && Packet[4] == hp) return;
            Packet[0] = 102f; Packet[1] = c.Kind; Packet[2] = c.View; Packet[3] = seat; Packet[4] = hp;
            _sendAt = Time.time + 1f;
            MercRide.SendAAPacket(Packet);
        }

        // One short nonalloc ray at 2 Hz, only on foot. Ignore the local body
        // in third person; every other solid collider can occlude the vehicle.
        static MercCarrier EntryVehicle(GameObject player)
        {
            Camera cam = Camera.main;
            if (cam == null) return null;
            int n = Physics.RaycastNonAlloc(cam.transform.position, cam.transform.forward, Hits, 42f,
                ~0, QueryTriggerInteraction.Ignore);
            if (n == Hits.Length) return null; // saturated: do not guess past an occluder
            int best = -1;
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (Hits[i].collider == null || Hits[i].transform.IsChildOf(player.transform)) continue;
                if (Hits[i].distance >= nearest) continue;
                nearest = Hits[i].distance; best = i;
            }
            if (best < 0) return null;
            RaycastHit hit = Hits[best];
            if ((hit.point - player.transform.position).sqrMagnitude > 784f) return null;
            if (_vgs == null) _vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
            Component vgs = _vgs == null ? null : hit.collider.GetComponentInParent(_vgs);
            if (vgs != null) return MercRide.SeatHudGround(vgs);
            GameObject plane = PlayerAn2.NearestPlane();
            if (plane != null && hit.transform.IsChildOf(plane.transform)) return MercRide.SeatHudAir(plane, true);
            GameObject heli = PlayerHeli.NearestMachine(10f);
            return heli != null && hit.transform.IsChildOf(heli.transform) ? MercRide.SeatHudAir(heli, false) : null;
        }

        static void Refresh(Cell cell, MercCarrier c, int index, GameObject local)
        {
            GameObject body = MercRide.SeatHudPlayer(c, index);
            bool player = body != null;
            if (c.Air) player = SeatBinding.SeatOccupant(c.Go, index, local, out body);
            string mercName = null;
            Component ai = null;
            bool merc = !player && MercRide.SeatHudMerc(c, index, out mercName, out ai);
            int kind = VehicleSeatCard.Choose(player, merc && mercName != null, merc);
            GameObject sourceBody = player ? body : ai == null ? null : ai.gameObject;
            if (cell.Body != sourceBody || (sourceBody != null && cell.Source == null)) Bind(cell, sourceBody, player, ai);
            string name = kind == VehicleSeatCard.Free ? _free : kind == VehicleSeatCard.Merc ? mercName : _occupied;
            if (player && body != null)
            {
                string nick = cell.Photon == null || cell.Nick == null ? null : cell.Nick.Invoke(cell.Photon, null) as string;
                if (!string.IsNullOrEmpty(nick)) name = nick;
                else if (body == local) name = _you;
            }
            float hp = ReadHealth(cell);
            if (player && body != local)
            {
                VehicleSeatVital vital;
                hp = cell.Actor > 0 && Vitals.TryGetValue(cell.Actor, out vital)
                    ? vital.ForSeat(c.Kind, c.View, index, Time.time) : -1f;
            }
            if (cell.Card.Set(kind, name, hp)) cell.Text.text = cell.Card.Caption;
        }

        // Component and member discovery occurs only when an occupant changes.
        // Health uses emitted numeric getters (also handles ObscuredFloat).
        static void Bind(Cell cell, GameObject body, bool player, Component ai)
        {
            cell.Body = body; cell.Source = null;
            cell.DataField = cell.Health = cell.Maximum = null;
            cell.Photon = null; cell.Nick = null;
            cell.Actor = 0;
            if (body == null) return;
            if (player)
            {
                cell.Actor = Mercs.ActorOf(body);
                if (_pnc == null) _pnc = RevivalPlugin.TypeByName("PlayerNetworkController");
                if (_life == null) _life = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                if (_photon == null && _pnc != null) _photon = AccessTools.PropertyGetter(_pnc, "GetPhotonPlayer");
                Component net = _pnc == null ? null : body.GetComponentInChildren(_pnc, true);
                if (net != null && _photon != null)
                {
                    cell.Photon = _photon.Invoke(net, null);
                    if (cell.Photon != null)
                    {
                        Type t = cell.Photon.GetType();
                        cell.Nick = AccessTools.PropertyGetter(t, "NickName") ?? AccessTools.PropertyGetter(t, "name");
                    }
                }
                cell.Source = _life == null ? null : body.GetComponentInChildren(_life, true);
                if (cell.Source != null) cell.DataField = FastField.Find(cell.Source.GetType(), "_playerLifeData");
            }
            else
            {
                cell.Source = ai;
                cell.DataField = ai == null ? null : FastField.Find(ai.GetType(), "Specifications");
            }
            if (cell.DataField == null) return;
            Type dataType = cell.DataField.FieldType;
            cell.Health = FastField.Find(dataType, "Health");
            if (!player) cell.Maximum = FastField.Find(dataType, "HealthMax");
        }

        static float ReadHealth(Cell cell)
        {
            if (cell.Source == null || cell.DataField == null || cell.Health == null) return -1f;
            object data = cell.DataField.GetValue(cell.Source);
            if (data == null) return -1f;
            float maximum = cell.Maximum == null ? 100f : FastField.GetNumber(cell.Maximum, data);
            return VehicleSeatCard.Fraction(FastField.GetNumber(cell.Health, data), maximum);
        }

        internal static void Draw()
        {
            if (_carrier == null || _carrier.Go == null || _cells == null) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 11; _style.richText = false;
                _style.clipping = TextClipping.Clip;
                _style.normal.textColor = Color.white;
            }
            int columns = VehicleSeatCard.Columns(_cells.Length);
            int rows = VehicleSeatCard.Rows(_cells.Length);
            float width = Mathf.Min(310f, Screen.width * 0.36f);
            float height = Mathf.Min(30f, (Screen.height - 110f) / Mathf.Max(1, rows));
            float x = Screen.width - width - 12f, y = 82f;
            Color old = GUI.color;
            VanillaUi.Panel(new Rect(x - 4f, y - 4f, width + 8f, 26f + rows * height), "MarkerInfo");
            GUI.color = Color.white;
            VanillaUi.Label(new Rect(x, y, width, 20f), Heading, _style);
            y += 22f;
            for (int i = 0; i < _cells.Length; i++)
            {
                Cell cell = _cells[i];
                float w = width / columns - 4f;
                Rect r = new Rect(x + (i % columns) * width / columns, y + (i / columns) * height, w, height - 3f);
                VanillaUi.Panel(r, "groupPlayerWhite", new Color(0.329f, 0.329f, 0.329f, 1f));
                GUI.color = Color.white;
                VanillaUi.Label(new Rect(r.x + 3f, r.y, r.width - 6f, r.height - 5f), cell.Text, _style);
                if (cell.Card.Kind == VehicleSeatCard.Free) continue;
                Rect bar = new Rect(r.x + 3f, r.yMax - 5f, r.width - 6f, 3f);
                Box(bar, new Color(0.35f, 0.35f, 0.35f, 1f));
                if (cell.Card.Health < 0f) continue; // grey = unavailable, never fake full HP
                bar.width *= cell.Card.Health;
                Box(bar, cell.Card.Health > 0.35f ? VanillaUi.Green : VanillaUi.Red);
            }
            GUI.color = old;
        }

        static void Box(Rect rect, Color color)
        {
            GUI.color = color;
            VanillaUi.Texture(rect, Texture2D.whiteTexture);
        }
    }
}
