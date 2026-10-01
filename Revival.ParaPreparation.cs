// Warning-time native NPC preparation. All Unity work stays on the main thread.
using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class Crew
    {
        static int _preparedSpawnIndex = -1;
        // Native IL: both builders iterate their arrays. A one-element window
        // preserves native weapons, customization and Photon spawning, without
        // rebuilding previously prepared men. Full arrays are restored in finally.
        internal sealed class PreparedSquad
        {
            internal GameObject Root;
            internal Array Men;
            internal int Count;
            Component _settlement, _point;
            Array _points, _onePoint, _oneMan;
            string _faction, _key;
            int _stage;

            internal PreparedSquad(Vector3 home, float yaw, int total, string faction, string key)
            {
                _faction = faction; _key = key;
                GameObject anchor = new GameObject("NDR_ParaPrepareAnchor");
                try
                {
                    anchor.transform.position = home;
                    anchor.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    _groundPositions = new Vector3[0];
                    _quietSquad = true;
                    Root = Absetzen(anchor, null, 0, "helicopter", faction, null, true);
                }
                finally
                {
                    _groundPositions = null; _quietSquad = false;
                    UnityEngine.Object.Destroy(anchor);
                }
                if (Root == null) throw new InvalidOperationException("Empty paradrop settlement failed");
                _settlement = Root.GetComponent(RevivalPlugin.TypeByName("NPC_Settlement"));
                Type pointType = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                Men = Array.CreateInstance(RevivalPlugin.TypeByName("NPC_AI2"), total);
                _points = Array.CreateInstance(pointType, total);
                _onePoint = Array.CreateInstance(pointType, 1);
                _oneMan = Array.CreateInstance(Men.GetType().GetElementType(), 1);
                Restore();
            }

            void Restore()
            {
                Set(_settlement, "_npcSpawnPoints", _points);
                Set(_settlement, "NpcAI", Men);
                for (int i = 0; i < _crewRoots.Count; i++)
                    if (ReferenceEquals(_crewRoots[i].Root, Root)) { _crewRoots[i].Men = Men; break; }
            }

            internal void LandingHome(Vector3 home)
            {
                // All NPCs are bound to the aircraft before this control root
                // moves. Native waypoints must live at the DZ, not underground.
                Root.transform.position = home;
            }

            internal void LandingPoint(int index, Vector3 point)
            {
                Component spawn = _points.GetValue(index) as Component;
                if (spawn != null) spawn.transform.position = point;
            }

            // Three frames per man: point construction, native spawn, native
            // setup/arming. Only spawn must immediately hide/hold the new object.
            internal bool Step(Vector3 hidden, float yaw)
            {
                if (Count >= Men.Length) return true;
                if (_stage == 0)
                {
                    GameObject go = new GameObject("Crew" + Count + "_crew");
                    go.transform.SetParent(Root.transform, true);
                    go.transform.position = hidden;
                    go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    Type type = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                    _point = go.AddComponent(type);
                    Listen(_point, 0);
                    Abschreiben(_point, VorlagePunkt(type));
                    Component military = VorlageMilitaer(type);
                    Abschreiben(_point, military);
                    int lawCount = RevivalPlugin.CfgPatrolCrewLawCount == null ? 0
                        : Mathf.Clamp(RevivalPlugin.CfgPatrolCrewLawCount.Value, 0, Men.Length);
                    Punkt(_point, military != null, UsableWeapon(Count < lawCount ? LAW_ID : MG42_ID, MG42_ID), null);
                    _points.SetValue(_point, Count);
                    _stage = 1;
                    return false;
                }
                _onePoint.SetValue(_point, 0);
                _oneMan.SetValue(Men.GetValue(Count), 0);
                Set(_settlement, "_npcSpawnPoints", _onePoint);
                Set(_settlement, "NpcAI", _oneMan);
                try
                {
                    if (_stage == 1)
                    {
                        RegisterAppearance(_point, null);
                        _spawningSettlement = _settlement; _spawningFraction = _faction;
                        _spawningCar = Root.transform; _spawningCount = 1; _groundKey = _key;
                        _preparedSpawnIndex = Count;
                        AccessTools.Method(_settlement.GetType(), "InitSpawnNpc", null, null)
                            .Invoke(_settlement, new object[] { true });
                        Component ai = _oneMan.GetValue(0) as Component;
                        if (ai == null) throw new InvalidOperationException("Native prepared NPC missing");
                        ParaPose.Hold(ai);
                        _stage = 2;
                    }
                    else
                    {
                        Invoke(_settlement, "InitSetupNpc");
                        ArmNativeWeapons(_settlement);
                        Component ai = _oneMan.GetValue(0) as Component;
                        SetNumber(ai, "_myIndexInSettlement", Count);
                        AccessTools.Method(ai.GetType(), "SetGodMode", new Type[] { typeof(bool) }, null)
                            .Invoke(ai, new object[] { false });
                        Count++; _stage = 0;
                    }
                }
                finally
                {
                    // Setup may advance Count; keep the result in its original slot.
                    Men.SetValue(_oneMan.GetValue(0), _stage == 0 ? Count - 1 : Count);
                    _spawningSettlement = null; _spawningCar = null; _groundKey = null; _preparedSpawnIndex = -1;
                    _appearance.Clear();
                    Restore();
                }
                return Count >= Men.Length;
            }

            internal void Cancel()
            {
                if (Men != null)
                    for (int i = 0; i < Men.Length; i++)
                    {
                        ParaPose p = ParaPose.Hold(Men.GetValue(i) as Component);
                        if (p != null && ParaPose.MasterClient()) p.DestroyAboard();
                    }
                if (Root != null) { Forget(Root); UnityEngine.Object.Destroy(Root); }
                Root = null;
            }
        }
    }

    internal static partial class AirEvents
    {
        sealed class PreparedDrop
        {
            internal Raid Raid;
            internal Sortie Sortie;
            internal FlightPath Path;
            internal Stick Stick;
            internal Crew.PreparedSquad Squad;
            internal Vector3 First, Hidden;
            internal float Interval, BeginAt, Expires;
            internal int Total, Stage, GroundIndex;
            internal GameObject Plane;
            internal int Boarded;
            internal bool Finished, Failed, Used, Launched;

            internal void Cancel()
            {
                Failed = Finished = true;
                if (!Used && Squad != null) Squad.Cancel();
            }
        }

        static readonly List<PreparedDrop> _preparations = new List<PreparedDrop>();
        static int _prepareCursor;

        static void QueuePreparation(Raid raid, Sortie sortie)
        {
            PreparedDrop p = new PreparedDrop();
            p.Raid = raid; p.Sortie = sortie; p.Total = Mathf.Clamp(sortie.W.Load, 1, 12);
            // Do not retain hidden NPCs throughout an arbitrarily delayed wave.
            p.BeginAt = Mathf.Max(Time.time, sortie.At - WarnSeconds);
            p.Expires = sortie.At + Eta(Over(raid, sortie.W, sortie.Index), raid.Heading, sortie.W, 0f, raid.SpeedFactor) + 120f;
            sortie.Prepared = p;
            _preparations.Add(p);
        }

        static PreparedDrop QueueSupportPreparation(Raid raid, FlightPath path, Vector3 first)
        {
            PreparedDrop p = new PreparedDrop();
            p.Raid = raid; p.Path = path; p.First = first; p.Total = 8;
            p.Interval = 200f / 7f / path.Speed;
            p.Expires = Time.time + path.Project(first) / path.Speed + 120f;
            _preparations.Add(p);
            return p;
        }

        static void CancelPreparations(Raid raid)
        {
            for (int i = _preparations.Count - 1; i >= 0; i--)
                if (raid == null || ReferenceEquals(_preparations[i].Raid, raid))
                {
                    _preparations[i].Cancel();
                    _preparations.RemoveAt(i);
                }
        }

        static int ReservedParatroopers()
        {
            int n = 0;
            for (int i = 0; i < _preparations.Count; i++)
            {
                PreparedDrop p = _preparations[i];
                if (!p.Used && !p.Failed && p.Stage > 0) n += p.Total;
            }
            return n;
        }

        // One preparation stage per rendered frame, shared by ALL raids. No
        // catch-up burst after a slow frame. A newly joined peer warms poses too.
        static void TickPreparation()
        {
            if (_preparations.Count == 0 && !ParaPose.HasPrepared) return;
            FrameProf.S(FrameProf.S_ParaPrepareT);
            try
            {
                if (!Master()) CancelPreparations(null);
                bool work = false;
                for (int visits = 0; visits < _preparations.Count; visits++)
                {
                    if (_prepareCursor >= _preparations.Count) _prepareCursor = 0;
                    PreparedDrop p = _preparations[_prepareCursor++];
                    if (p.Used || p.Failed)
                    {
                        _preparations.RemoveAt(--_prepareCursor);
                        visits--;
                        continue;
                    }
                    bool invalid = !p.Raid.E.Here || (p.Stick != null && p.Stick.Support
                        && p.Stick.SupportGeneration != TowerSupport.WorldGeneration);
                    if (p.Launched && (p.Plane == null || PlayerAn2.Down(p.Plane))) invalid = true;
                    if (invalid || Time.time > p.Expires) { p.Cancel(); continue; }
                    if (p.Finished)
                    {
                        // Seat markers and bindings are cold work too. Board
                        // one hidden man per frame during the aircraft approach.
                        if (p.Plane != null && p.Boarded < p.Total)
                        {
                            p.Stick.Men[p.Boarded].Body.Aboard(p.Plane, p.Boarded);
                            p.Stick.Men[p.Boarded].Boarded = true;
                            p.Boarded++;
                            if (p.Boarded == p.Total)
                            {
                                Vector3 home = Vector3.zero;
                                for (int k = 0; k < p.Total; k++) home += p.Stick.Men[k].Ground;
                                p.Squad.LandingHome(home / p.Total);
                                for (int k = 0; k < p.Total; k++) p.Squad.LandingPoint(k, p.Stick.Men[k].Ground);
                            }
                            work = true;
                            break;
                        }
                        continue;
                    }
                    if (Time.time < p.BeginAt) continue;
                    try { work = PrepareStep(p); }
                    catch (Exception ex)
                    {
                        p.Cancel();
                        RevivalPlugin.L.LogWarning("AirEvents: preparation cancelled: " + ex.Message);
                    }
                    break;
                }
                // Pose discovery/canopy creation is a separate heavy stage;
                // never combine it with a native spawn/setup in one frame.
                if (!work) ParaPose.TickPrepared();
            }
            finally { FrameProf.E(FrameProf.S_ParaPrepareT); }
        }

        static bool PrepareStep(PreparedDrop p)
        {
            if (p.Stage == 0)
            {
                if (!CombatLoadPolicy.CanDrop(NpcWar.ParatrooperPopulation() + PendingParatroopers(), p.Total)
                    || !CombatLoadPolicy.CanSpawnEvent(NpcWar.EventPopulation(), p.Total))
                {
                    p.Cancel();
                    RevivalPlugin.L.LogInfo("AirEvents: prepared stick stays aboard (population cap).");
                    return false;
                }
                ParaPose.Install();
                if (p.Path == null)
                {
                    float heading = p.Sortie.W.OwnDrop ? p.Sortie.W.Direction : p.Raid.Heading;
                    Vector2 d = Dir(heading);
                    Vector3 forward = new Vector3(d.x, 0f, d.y), right = new Vector3(d.y, 0f, -d.x);
                    Vector3 centre = Over(p.Raid, p.Sortie.W, p.Sortie.Index) + right * Offset(p.Sortie.Index, p.Sortie.W.Count, Mathf.Min(p.Raid.E.Width, 200f));
                    Vector2 from, to;
                    FlightPath.AcrossMap(centre, Mathf.Repeat(heading + 180f, 360f), out from, out to);
                    p.Path = FlightPath.BeginStraight(from, to, TransportAltitudeM * K, NpcAircraft.Speed(TransportKmh / 3.6f * K, p.Raid.SpeedFactor));
                    p.Path.ExtendApproach(p.Path.Project(centre), MercAACore.ApproachUnits(JumpSpread, K));
                    float spread = RetakeTactics.DropSpread(p.Sortie.W.CloseDrop, p.Total, JumpSpread, K);
                    p.First = centre - forward * (spread * 0.5f + (p.Sortie.W.OwnDrop ? 25f * K : 0f));
                    if (p.Sortie.W.OwnDrop) p.Path.ExtendApproach(p.Path.Project(p.First), RetakeTactics.ApproachSeconds * p.Path.Speed);
                    p.Interval = spread / Mathf.Max(1, p.Total - 1) / p.Path.Speed;
                }
                p.Stick = new Stick(); p.Stick.R = p.Raid; p.Stick.Heading = p.Raid.Heading;
                p.Stick.Support = p.Raid.E.Name == "tower-support";
                p.Stick.SupportGeneration = TowerSupport.WorldGeneration;
                p.Hidden = p.Raid.Drop + Vector3.down * 2000f;
                p.Stage = 1;
                return true;
            }
            if (p.Stage == 1)
            {
                if (!p.Path.ProfileReady) { p.Path.ProfileStep(); return true; }
                // One terrain/all-collider ground query per frame during warning.
                Vector3 from = p.Path.At(p.Path.Project(p.First));
                Vector3 v = p.Path.Velocity(p.Path.Project(p.First));
                Jumper j = new Jumper();
                j.From = from + v * (p.GroundIndex * p.Interval) + v.normalized * (25f * K) + Vector3.down * (15f * K);
                j.Ground = Ground(new Vector3(j.From.x, 0f, j.From.z));
                p.Stick.Men.Add(j);
                if (++p.GroundIndex >= p.Total) p.Stage = 2;
                return true;
            }
            if (p.Stage == 2)
            {
                double expiry = ParaPose.Clock() + Mathf.Max(120f, p.Expires - Time.time);
                string key = "para/" + expiry.ToString("R", CultureInfo.InvariantCulture);
                p.Squad = new Crew.PreparedSquad(p.Hidden, p.Raid.Heading, p.Total, p.Raid.E.Faction, key);
                p.Stick.Settlement = p.Squad.Root; p.Stick.Npcs = p.Squad.Men;
                p.Stage = 3;
                return true;
            }
            if (p.Stage == 3)
            {
                if (p.Squad.Step(p.Hidden, p.Raid.Heading)) p.Stage = 4;
                return true;
            }
            bool ready = true;
            for (int i = 0; i < p.Total; i++)
            {
                Jumper j = p.Stick.Men[i];
                if (j.Body == null) j.Body = ParaPose.Hold(p.Squad.Men.GetValue(i) as Component);
                if (j.Body == null || j.Body.Gone) { p.Cancel(); return false; }
                if (!j.Body.Ready) ready = false;
            }
            p.Finished = ready;
            return false;
        }
    }
}
