// A hidden patrol crew has no NPC bodies until its hull dies. Retain the
// fatal explosion only, then bill the newly spawned men through their owners.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public sealed class CrewBlastImpact : MonoBehaviour
    {
        internal CrewBlast.Profile Blast;
        internal Component Vehicle;
        internal Vector3[] SeatPositions;
        internal bool Consumed;
    }

    internal static class CrewBlast
    {
        public struct Profile
        {
            public Vector3 Point;
            public float Radius, Peak;
            public bool Native;
        }

        static Profile _active;
        static FieldInfo _durability, _seats;
        static Func<bool> _master;

        internal static void Install(Harmony harmony, MethodInfo vehicleDamage)
        {
            Type vehicle = RevivalPlugin.TypeByName("VehicleGameSystem");
            _durability = AccessTools.Field(vehicle, "Durability");
            _seats = AccessTools.Field(vehicle, "SeatPoints");
            _master = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),
                AccessTools.PropertyGetter(RevivalPlugin.TypeByName("PhotonNetwork"), "isMasterClient"));
            if (_durability == null) throw new Exception("crew blast durability binding missing");
            harmony.Patch(vehicleDamage,
                new HarmonyMethod(typeof(CrewBlast).GetMethod("BeforeVehicle")),
                new HarmonyMethod(typeof(CrewBlast).GetMethod("AfterVehicle")), null, null, null);

        }

        internal static Profile Begin(Vector3 point, float radius, float peak, bool native)
        {
            Profile previous = _active;
            _active.Point = point; _active.Radius = radius;
            _active.Peak = peak; _active.Native = native;
            return previous;
        }

        internal static void End(Profile previous) { _active = previous; }

        public static void BeforeVehicle(Component __instance, int __1, out Profile __state)
        {
            __state = new Profile();
            if (__1 != OrdnanceBlast.Explosion || !_master() || _active.Peak <= 0f
                || _active.Radius <= 0f || Convert.ToSingle(_durability.GetValue(__instance)) <= 0f) return;
            __state = _active;
        }

        public static void AfterVehicle(Component __instance, Profile __state)
        {
            if (__state.Peak <= 0f || !_master()
                || Convert.ToSingle(_durability.GetValue(__instance)) > 0f) return;
            CrewBlastImpact impact = __instance.gameObject.GetComponent<CrewBlastImpact>();
            if (impact == null) impact = __instance.gameObject.AddComponent<CrewBlastImpact>();
            // A repaired/rearmed hull may die again. The live->dead transition
            // identifies the new fatal impulse without retaining an old hit.
            impact.Blast = __state;
            impact.Vehicle = __instance;
            impact.Consumed = false;
            Transform seats = _seats == null ? null : _seats.GetValue(__instance) as Transform;
            int count = seats == null ? 0 : seats.childCount;
            impact.SeatPositions = new Vector3[Math.Max(1, count)];
            if (count == 0) impact.SeatPositions[0] = __instance.transform.position;
            else for (int i = 0; i < count; i++) impact.SeatPositions[i] = seats.GetChild(i).position;
        }

        internal static void Release(GameObject car, GameObject settlement)
        {
            if (car == null || settlement == null || !_master()) return;
            CrewBlastImpact impact = car.GetComponent<CrewBlastImpact>();
            if (impact == null || impact.Consumed || impact.Vehicle == null
                || Convert.ToSingle(_durability.GetValue(impact.Vehicle)) > 0f) return;
            Array men = Crew.Men(settlement);
            if (men == null || men.Length == 0) return;
            impact.Consumed = true;
            OrdnanceBlast.EnqueueCrew(men, impact.Blast.Point, impact.Blast.Radius,
                impact.Blast.Peak, impact.Blast.Native, impact.SeatPositions);
            UnityEngine.Object.Destroy(impact);
        }
    }
}
