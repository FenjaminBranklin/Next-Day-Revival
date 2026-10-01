// Revival.AirPicturePolicy.cs - the pure rules of the tower radar's air
// picture (task W-Tower1, docs/ai/tasks/w-tower1-airpicture.md): who sees it,
// friend or foe, bearing, and when an aircraft reaches you. No Unity types,
// so research/air_picture_check.py compiles this very file with the .NET 3.5
// compiler and runs its scenarios offline.
//
// World frame: x east, z north, bearings in degrees clockwise from north.
// C# 3.0 (csc from .NET 3.5). ASCII only.

using System;

namespace NextDayRevival
{
    internal static class AirPicturePolicy
    {
        /// <summary>Does this viewer get the picture? The radar and its
        /// console work, the viewer is of the holder's side, and the radar is
        /// manned by that side: the holder's NPC operator alive, or a player of
        /// the holder's side at the console.</summary>
        internal static bool Held(bool working, int holder, int mySide, bool npcOperator, int operatorSide)
        {
            if (!working || holder < 0 || mySide != holder) return false;
            return npcOperator || operatorSide == holder;
        }

        /// <summary>Strategic raid lead; quick admin tests retain their short lead.</summary>
        internal static float WarningLead(bool quick, float configured, float normal, float quickSeconds)
        {
            if (quick) return quickSeconds;
            return configured > normal && !float.IsInfinity(configured) ? configured : normal;
        }

        /// <summary>1 friend, -1 foe, 0 unknown, as the viewer's side sees the
        /// pilot. An NPC raid aircraft is always a foe; an empty aircraft or an
        /// unreadable pilot is unknown and never raises a warning.</summary>
        internal static int Iff(string mine, string pilot, bool npcRaid)
        {
            if (npcRaid) return -1;
            if (string.IsNullOrEmpty(pilot) || string.IsNullOrEmpty(mine)) return 0;
            if (pilot == mine) return 1;
            return AirDefencePolicy.Hostile(mine, pilot) ? -1 : 0;
        }

        /// <summary>Bearing 0..360 of a vector (x east, z north).</summary>
        internal static float Bearing(float dx, float dz)
        {
            double b = Math.Atan2(dx, dz) * 180.0 / Math.PI;
            if (b < 0.0) b += 360.0;
            if (b >= 360.0) b -= 360.0;
            return (float)b;
        }

        static readonly string[] Compass8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        internal static string Compass(float bearing)
        {
            int i = (int)Math.Floor(bearing / 45.0 + 0.5);
            i %= 8;
            if (i < 0) i += 8;
            return Compass8[i];
        }

        /// <summary>An aircraft at r (relative to the protected point) flying
        /// v (per second, horizontal): will it pass within <paramref name="radius"/>,
        /// closest inside <paramref name="horizon"/> seconds? eta = seconds until
        /// it is closest ("over you"; 0 = there now, or still inside and
        /// leaving), miss = that closest horizontal distance. An aircraft flying
        /// away outside the circle, or passing wide, is no threat.</summary>
        internal static bool Inbound(float rx, float rz, float vx, float vz, float radius, float horizon,
                                     out float eta, out float miss)
        {
            float r2 = rx * rx + rz * rz;
            float v2 = vx * vx + vz * vz;
            eta = 0f;
            miss = (float)Math.Sqrt(r2);
            float t = v2 < 1e-4f ? 0f : -(rx * vx + rz * vz) / v2;   // time of closest approach
            if (t <= 0f) return r2 <= radius * radius;                // hovering or leaving
            float cx = rx + vx * t, cz = rz + vz * t;
            float m2 = cx * cx + cz * cz;
            miss = (float)Math.Sqrt(m2);
            if (m2 > radius * radius) return false;                  // passes wide
            eta = t;
            return eta <= horizon;
        }

        /// <summary>An announced raid (N11 warning): it flies a straight line
        /// through the target (tx, tz), coming from the unit direction (fx, fz)
        /// (target -> entry side), over the target <paramref name="etaTarget"/>
        /// seconds from now at <paramref name="speed"/> units/s. For a viewer at
        /// (px, pz): seconds until the raid passes closest, how far it passes,
        /// and the bearing from the viewer to where it comes from now.</summary>
        internal static float RaidEta(float tx, float tz, float fx, float fz, float etaTarget, float speed,
                                      float px, float pz, out float miss, out float bearing)
        {
            float qx = px - tx, qz = pz - tz;
            // along the flight direction (-f): positive = past the target
            float along = -(qx * fx + qz * fz);
            float side = qx * fz - qz * fx;
            miss = Math.Abs(side);
            float eta = etaTarget + (speed > 0.1f ? along / speed : 0f);
            // where the leading aircraft is now (it may still be off the map)
            float back = etaTarget * speed;
            bearing = Bearing(tx + fx * back - px, tz + fz * back - pz);
            return eta;
        }

        /// <summary>Type code of a contact: 0 helicopter, 1 An-2, 2 Tu-95,
        /// 3 FPV drone, 4 recon drone, 5 unknown.</summary>
        internal static int Type(int kind, bool tu95)
        {
            switch (kind)
            {
                case 0: return 0;
                case 6: return tu95 ? 2 : 1;
                case 2: return 3;
                case 3: return 4;
                default: return 5;
            }
        }

        static readonly string[] TypeNames = { "Mi-8", "An-2", "Tu-95", "FPV drone", "Recon drone", "Aircraft" };

        internal static string TypeName(int type)
        {
            return type >= 0 && type < TypeNames.Length ? TypeNames[type] : TypeNames[5];
        }

        static readonly string[] IffNames = { "FOE", "UNKNOWN", "FRIEND" };

        internal static string IffName(int iff) { return IffNames[iff < 0 ? 0 : iff > 0 ? 2 : 1]; }

        /// <summary>A key that changes only when the hover text would: the
        /// label is rebuilt then, not every snapshot.</summary>
        internal static int DetailKey(int id, int type, int iff, float heightM, float kmh, float heading)
        {
            int h = (int)Math.Round(heightM / 25.0);
            int s = (int)Math.Round(kmh / 10.0);
            int d = (int)Math.Round(heading / 10.0) % 36;
            return ((((id * 8 + type) * 4 + iff + 1) * 4096 + (h & 4095)) * 128 + (s & 127)) * 37 + d;
        }
    }
}
