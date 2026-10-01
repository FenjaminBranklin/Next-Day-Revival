// Direct rider parenting on every peer. Native navigation/damage stays with
// its existing owner; seat-relative corrections only undo animator/network writes.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class SeatBinding
    {
        sealed class Rider
        {
            internal Transform Body, Seat, Parent, Vehicle;
            internal Vector3 Scale, Position;
            internal Quaternion Rotation;
            internal GameObject[] Passengers;
            internal int Index;
        }
        sealed class Remote
        {
            internal int Actor, Index;
            internal GameObject Vehicle;
            internal Transform Body;
            internal bool Plane;
            internal float Until, ResolveAt;
        }
        static readonly Dictionary<Transform, Rider> Bound = new Dictionary<Transform, Rider>();
        static readonly List<Rider> Riders = new List<Rider>();
        static readonly List<Remote> Remotes = new List<Remote>();
        static readonly Dictionary<Transform, SeatBindingHost> Hosts = new Dictionary<Transform, SeatBindingHost>();
        static FieldInfo _seats, _passengers;
        static float _viewerAt;
        static Transform _viewer;

        internal static void Install(Harmony harmony)
        {
            Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
            _seats = AccessTools.Field(t, "SeatPoints");
            _passengers = AccessTools.Field(t, "Passengers");
            string[] names = { "SitToPassengerPlace", "ChangeToPassengerPlace" };
            for (int i = 0; i < names.Length; i++)
                harmony.Patch(AccessTools.Method(t, names[i], null, null), null,
                    new HarmonyMethod(typeof(SeatBinding).GetMethod("NativeSeated")), null, null, null);
        }

        // Both native methods update Passengers on every client via existing RPCs.
        // No NPC is ever inserted in that array.
        public static void NativeSeated(object __instance)
        {
            Component vgs = __instance as Component;
            if (vgs == null || !vgs.gameObject.activeInHierarchy) return;
            Transform seats = _seats.GetValue(__instance) as Transform;
            GameObject[] passengers = _passengers.GetValue(__instance) as GameObject[];
            if (seats == null || passengers == null) return;
            for (int i = 0; i < passengers.Length && i < seats.childCount; i++)
            {
                if (passengers[i] == null) continue;
                Transform body = passengers[i].transform;
                Bind(body, seats.GetChild(i), vgs.transform, Vector3.zero, Quaternion.identity);
                Rider r = Bound[body]; r.Passengers = passengers; r.Index = i;
            }
        }

        internal static Transform AircraftSeat(Transform vehicle, bool plane, int index)
        {
            // The marker caches these transforms; searches/creation happen at boarding.
            SeatBindingHost host = Host(vehicle);
            int total = plane ? Mathf.Clamp(An2Model.CabinSeats, 1, 24) : 8;
            index = index < 0 ? -1 : index % total;
            int i = index + 1;
            if (host.Seats[i] == null)
            {
                Transform seat = new GameObject("NDR_RiderSeat_" + index).transform;
                seat.SetParent(vehicle, false);
                Vector3 worldOffset = plane ? An2Model.Seat(index) * PlayerAn2.K
                    : PlayerHeli.CabinSeatLocal(index) * PlayerHeli.K;
                // Authored aircraft offsets are world units, not donor-local scale.
                seat.position = vehicle.position + vehicle.rotation * worldOffset;
                seat.rotation = vehicle.rotation;
                host.Seats[i] = seat;
            }
            return host.Seats[i];
        }

        static SeatBindingHost Host(Transform vehicle)
        {
            SeatBindingHost host;
            if (Hosts.TryGetValue(vehicle, out host) && host != null) return host;
            host = vehicle.GetComponent<SeatBindingHost>();
            if (host == null) host = vehicle.gameObject.AddComponent<SeatBindingHost>();
            Hosts[vehicle] = host;
            return host;
        }

        internal static void Bind(Transform body, Transform seat, Transform vehicle,
            Vector3 position, Quaternion rotation)
        {
            if (body == null || seat == null || vehicle == null || !vehicle.gameObject.activeInHierarchy) return;
            Rider r;
            if (!Bound.TryGetValue(body, out r))
            {
                Host(vehicle);
                r = new Rider(); r.Body = body; r.Parent = body.parent; r.Scale = body.localScale;
                Bound.Add(body, r); Riders.Add(r);
            }
            r.Seat = seat; r.Vehicle = vehicle; r.Position = position; r.Rotation = rotation;
            Hold(r);
        }

        internal static void BindSeat(Transform body, Transform seat, Transform vehicle)
        { Bind(body, seat, vehicle, Vector3.zero, Quaternion.identity); }

        // A gunner can turn/orbit within his seat while keeping the hull's up axis.
        internal static void BindWorld(Transform body, Transform seat, Transform vehicle,
            Vector3 position, Quaternion rotation)
        { Bind(body, seat, vehicle, seat.InverseTransformPoint(position), Quaternion.Inverse(seat.rotation) * rotation); }

        static void Hold(Rider r)
        {
            Transform body = r.Body;
            if (body.parent != r.Seat) body.SetParent(r.Seat, true);
            if ((body.localPosition - r.Position).sqrMagnitude > 0.00000001f)
                body.localPosition = r.Position;
            Quaternion rotation = body.localRotation;
            if (rotation.x != r.Rotation.x || rotation.y != r.Rotation.y
                || rotation.z != r.Rotation.z || rotation.w != r.Rotation.w)
                body.localRotation = r.Rotation;
        }

        internal static void Detach(Transform body)
        {
            Rider r;
            if (ReferenceEquals(body, null) || !Bound.TryGetValue(body, out r)) return;
            Bound.Remove(body); Riders.Remove(r);
            if (body == null) return;
            // Native boarding initially put the player under the vehicle. Restore
            // a scene parent only; an exit must never put him back inside the hull.
            Transform parent = r.Parent;
            if (parent != null && r.Vehicle != null && parent.IsChildOf(r.Vehicle)) parent = null;
            body.SetParent(parent, true);
            if (parent == r.Parent) body.localScale = r.Scale;
            Vector3 forward = body.forward; forward.y = 0f;
            if (forward.sqrMagnitude > 0.000001f) body.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        internal static void ReleaseVehicle(Transform vehicle)
        {
            for (int i = Riders.Count - 1; i >= 0; i--)
                if (Riders[i].Vehicle == vehicle) Detach(Riders[i].Body);
            for (int i = Remotes.Count - 1; i >= 0; i--)
                if (Remotes[i].Vehicle != null && Remotes[i].Vehicle.transform == vehicle)
                { Detach(Remotes[i].Body); Remotes.RemoveAt(i); }
            Hosts.Remove(vehicle);
        }

        internal static void AircraftBoard(GameObject vehicle, int actor, float[] data, bool plane)
        {
            if (actor == Crocodile.LocalActor() || data == null || data.Length < 3) return;
            Remote r = null;
            for (int i = 0; i < Remotes.Count; i++) if (Remotes[i].Actor == actor) { r = Remotes[i]; break; }
            if (data[1] <= 0.5f)
            {
                if (r != null) { Detach(r.Body); Remotes.Remove(r); }
                return;
            }
            if (vehicle == null) return;
            if (r == null) { r = new Remote(); r.Actor = actor; Remotes.Add(r); }
            int index = data.Length >= 4 ? Mathf.RoundToInt(data[3]) : (data[2] > 0.5f ? -1 : 0);
            if (r.Vehicle != vehicle || r.Index != index) r.ResolveAt = 0f;
            r.Vehicle = vehicle; r.Plane = plane; r.Index = index;
            r.Until = Time.time + 6f;
        }

        // Z K9b: a read-only view of native aircraft board announcements.
        // Unresolved bodies still report occupied until the existing lease ends.
        internal static bool SeatOccupant(GameObject vehicle, int index, GameObject local, out GameObject body)
        {
            body = null;
            SeatBindingHost host;
            Transform seat = vehicle != null && Hosts.TryGetValue(vehicle.transform, out host)
                && host != null && index >= -1 && index + 1 < host.Seats.Length ? host.Seats[index + 1] : null;
            for (int i = 0; i < Riders.Count; i++)
            {
                Rider r = Riders[i];
                if (r.Vehicle == null || r.Vehicle.gameObject != vehicle || r.Body == null || r.Seat == null) continue;
                // MercRide binds NPCs to these same aircraft markers. Local
                // player identity and remote board leases distinguish players.
                if (r.Body.gameObject != local) continue;
                if (seat == null || r.Seat != seat) continue;
                body = r.Body.gameObject;
                return true;
            }
            for (int i = 0; i < Remotes.Count; i++)
            {
                Remote r = Remotes[i];
                if (r.Vehicle != vehicle || r.Index != index || Time.time > r.Until) continue;
                body = r.Body == null ? null : r.Body.gameObject;
                return true;
            }
            return false;
        }

        internal static void LateFrame()
        {
            if (Riders.Count == 0 && Remotes.Count == 0) return;
            float now = Time.time;
            if (now >= _viewerAt)
            {
                _viewerAt = now + 0.5f;
                GameObject player = MapTools.LocalPlayer();
                _viewer = player == null ? null : player.transform;
            }
            for (int i = Remotes.Count - 1; i >= 0; i--)
            {
                Remote r = Remotes[i];
                if (r.Vehicle == null || now > r.Until)
                { Detach(r.Body); Remotes.RemoveAt(i); continue; }
                if (_viewer != null && (r.Vehicle.transform.position - _viewer.position).sqrMagnitude > 7840000f) continue;
                if (now < r.ResolveAt) continue;
                r.ResolveAt = now + 0.5f;
                GameObject player = Crocodile.PlayerByActor(r.Actor);
                if (player == null || !Crocodile.PlayerUp(player))
                { Detach(r.Body); r.Body = null; continue; }
                if (r.Body != player.transform) { Detach(r.Body); r.Body = player.transform; }
                BindSeat(r.Body, AircraftSeat(r.Vehicle.transform, r.Plane, r.Index), r.Vehicle.transform);
            }
            for (int i = Riders.Count - 1; i >= 0; i--)
            {
                Rider r = Riders[i];
                if (r.Body == null || r.Seat == null || r.Vehicle == null
                    || (r.Passengers != null && r.Passengers[r.Index] != r.Body.gameObject))
                { Detach(r.Body); continue; }
                // Distant bodies still move rigidly by hierarchy; no pose work.
                if (_viewer != null && (r.Vehicle.position - _viewer.position).sqrMagnitude > 7840000f) continue;
                Hold(r);
            }
        }
    }

    internal sealed class SeatBindingHost : MonoBehaviour
    {
        internal readonly Transform[] Seats = new Transform[25];
        void OnDisable() { SeatBinding.ReleaseVehicle(transform); }
        void OnDestroy() { SeatBinding.ReleaseVehicle(transform); }
    }
}
