// Next Day: Survival - Revival Toolkit
//
// Power 5: sabotage points at the power sites (docs/ai/POWER.md "Power 5").
// The points are precomputed data (Revival.SabotageData.cs, rules in
// Revival.SabotageCore.cs); this file resolves their frames in the scene and
// connects C4 to them, on the master, once per chain (C4.Damage):
// - generator: a charge inside the bunker near the generator damages it
//   straight away, the bunker armour does not apply (PowerNet.Demolition);
//   such charges skip the armour hit of AirDefenceDamage.ReportBlast;
// - fuel tank: a charge on it destroys it (FuelDepot.Demolish);
// - command post: a charge on it wrecks the tower console, which turns the
//   command-room screens and the supply order terminal off until it is
//   repaired (PowerNet.Has, the existing AirDefenceDamage console repair);
// - radar mast and AA guns: a charge on it wrecks it (AirDefenceDamage.Wreck).
// Everything rides on the existing master-authoritative state and sync of
// those systems; nothing new goes over the wire.
// Anchor(...) gives each point's charge pose in the world for the mercs'
// "Demolish" order. The admin panel (EAST, "show sabotage points") draws them;
// nobody else sees them. No Update, no polling: work only on a C4 chain and
// in the admin overlay while it is on.
// C# 3.0, ASCII.

using System;
using UnityEngine;

namespace NextDayRevival
{
    internal static class Sabotage
    {
        const float K = SabotageCore.K;

        internal static readonly SabotagePoint[] Points = SabotageData.Points;

        /// <summary>Admin panel toggle: draw the points near the camera.</summary>
        internal static bool Show;

        static readonly string[] _tag = Tags();
        static GUIStyle _style;

        static string[] Tags()
        {
            string[] a = new string[SabotageData.Points.Length];
            for (int i = 0; i < a.Length; i++)
            {
                SabotagePoint p = SabotageData.Points[i];
                a[i] = "C4 " + p.Label + " x" + p.Charges.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            }
            return a;
        }

        // ------------------------------------------------------------ frames

        /// <summary>The point's frame in the world: origin and yaw. False while
        /// the thing it sits on is not in the scene.</summary>
        internal static bool Frame(SabotagePoint p, out Vector3 origin, out float yaw)
        {
            origin = Vector3.zero; yaw = 0f;
            try
            {
                switch (p.Frame)
                {
                    case SabotageCore.Bunker:
                        return PowerNet.BunkerFrame(p.Site, out origin, out yaw);
                    case SabotageCore.Tank:
                        return FuelDepot.TankBase(p.Index, out origin);
                    case SabotageCore.Gun:
                    {
                        Transform root = AirDefenceDamage.Root(p.Index);
                        int pit = FlakPositionsCore.Position(p.Index);
                        if (root == null || pit < 0) return false;
                        origin = root.position; yaw = FlakPositionsCore.Heading(pit);
                        return true;
                    }
                    case SabotageCore.Cab:
                    case SabotageCore.Tower:
                    {
                        if (!TowerRadar.On || !TowerRadar.Built) return false;
                        origin = TowerRadar.TowerPoint(Vector3.zero);
                        if (p.Frame == SabotageCore.Cab) origin.y = TowerRadar.CabFloorY;
                        yaw = TowerRadar.TowerYaw;
                        return true;
                    }
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Sabotage: frame of " + p.Key + ": " + ex.Message); }
            return false;
        }

        /// <summary>For the mercs' Demolish order: where a charge goes on point
        /// i (world), the face normal, and the charge's rotation as C4.Orient
        /// builds it (long axis x normal; the C4 mesh frame is applied there).</summary>
        internal static bool Anchor(int i, out Vector3 pos, out Vector3 normal, out Quaternion rot)
        {
            pos = normal = Vector3.zero; rot = Quaternion.identity;
            if (i < 0 || i >= Points.Length) return false;
            SabotagePoint p = Points[i];
            Vector3 o; float yaw;
            if (!Frame(p, out o, out yaw)) return false;
            float dx, dy, dz;
            SabotageCore.ToWorld(yaw, p.AX, p.AY, p.AZ, out dx, out dy, out dz);
            pos = o + new Vector3(dx, dy, dz);
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            normal = turn * new Vector3(p.NX, p.NY, p.NZ);
            rot = Quaternion.LookRotation(turn * new Vector3(p.TX, p.TY, p.TZ), normal);
            return true;
        }

        // ---------------------------------------------------------------- C4

        /// <summary>Master, once per C4 chain (C4.Damage), before the generic
        /// blast reports. at[0..count) are the charges that went off; full/zero
        /// the C4 object share radii (units). inHouse[k] is set for a charge
        /// inside a generator bunker: its blast must not cost bunker armour.</summary>
        internal static void C4Chain(Vector3[] at, int count, float full, float zero, bool[] inHouse)
        {
            for (int k = 0; k < count; k++) inHouse[k] = PowerNet.InsideHouse(at[k]);
            for (int i = 0; i < Points.Length; i++)
            {
                SabotagePoint p = Points[i];
                Vector3 o; float yaw;
                if (!Frame(p, out o, out yaw)) continue;
                float sum = 0f;
                for (int k = 0; k < count; k++)
                {
                    // The bunker shields its generator: only charges inside count.
                    if (p.Kind == SabotageCore.Generator && !inHouse[k]) continue;
                    Vector3 d = at[k] - o;
                    float lx, ly, lz;
                    SabotageCore.ToLocal(yaw, d.x, d.y, d.z, out lx, out ly, out lz);
                    sum += C4Core.ChargeShare(SabotageCore.BoxDistance(p, lx, ly, lz) * K, full, zero);
                }
                if (sum <= 0f) continue;
                try { Hit(p, sum); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("Sabotage: " + p.Key + ": " + ex.Message); }
            }
        }

        static void Hit(SabotagePoint p, float shares)
        {
            bool down = SabotageCore.Demolished(shares, p.Charges);
            switch (p.Kind)
            {
                case SabotageCore.Generator:
                    // Damaged right away by its share; p.Charges destroy it.
                    PowerNet.Demolition(p.Site, shares, p.Charges);
                    return;
                case SabotageCore.FuelTank:
                    if (down && FuelDepot.Demolish(p.Index)) Log(p, shares);
                    return;
                case SabotageCore.CommandPost:
                    if (down && AirDefenceDamage.Wreck(AirDefenceDamage.Console)) Log(p, shares);
                    return;
                case SabotageCore.RadarMast:
                    if (down && AirDefenceDamage.Wreck(AirDefenceDamage.Radar)) Log(p, shares);
                    return;
                case SabotageCore.AaGun:
                    if (down && AirDefenceDamage.Wreck(p.Index)) Log(p, shares);
                    return;
            }
        }

        static void Log(SabotagePoint p, float shares)
        {
            RevivalPlugin.L.LogInfo("Sabotage: C4 destroyed " + p.Key + " (" + shares.ToString("0.00",
                System.Globalization.CultureInfo.InvariantCulture) + " of " + p.Charges + " charges).");
        }

        // ------------------------------------------------------ command post

        /// <summary>True while the site declares a command post and it is
        /// wrecked (the tower console's AirDefenceDamage state, repaired the
        /// usual way): its screens and supply order terminal are off.</summary>
        internal static bool PostDown(string site)
        {
            if (!TowerRadar.On || !TowerRadar.Built || TowerRadar.ConsoleAlive) return false;
            for (int i = 0; i < Points.Length; i++)
                if (Points[i].Kind == SabotageCore.CommandPost && Points[i].Site == site) return true;
            return false;
        }

        // ------------------------------------------------------------ admin

        /// <summary>Admin overlay (OnGUI, only while the admin panel is open and
        /// the toggle is on): each point's anchor and the way its face looks.</summary>
        internal static void Draw()
        {
            if (!Show || !Admin.IsOpen || Event.current.type != EventType.Repaint) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_style == null) { _style = new GUIStyle(GUI.skin.label); _style.fontSize = 12; }
            Color was = GUI.color;
            Vector3 eye = cam.transform.position;
            for (int i = 0; i < Points.Length; i++)
            {
                Vector3 pos, normal; Quaternion rot;
                if (!Anchor(i, out pos, out normal, out rot)) continue;
                if ((pos - eye).sqrMagnitude > 250f * 250f) continue;
                GUI.color = Color.red;
                Mark(cam, pos, _tag[i], 180f);
                GUI.color = Color.yellow;
                Mark(cam, pos + normal * (0.6f * K), "+", 18f);
            }
            GUI.color = was;
        }

        static void Mark(Camera cam, Vector3 world, string text, float width)
        {
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f) return;
            VanillaUi.Label(new Rect(s.x - 6f, Screen.height - s.y - 9f, width, 18f), text, _style);
        }

        /// <summary>Admin panel note: how many points, how many resolve now.</summary>
        internal static string Report()
        {
            int live = 0;
            for (int i = 0; i < Points.Length; i++)
            {
                Vector3 o; float yaw;
                if (Frame(Points[i], out o, out yaw)) live++;
            }
            return "Sabotage points: " + Points.Length + " declared, " + live + " in the scene.";
        }
    }
}
