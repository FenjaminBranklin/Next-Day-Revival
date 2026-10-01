// Z H0c: offline-only recipes use the existing native flight/drop/blast paths.
using System;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class AirEvents
    {
        internal static void ScenarioLaunch(ScenarioAirSpec spec)
        {
            if (!OfflineStart.Active || !ScenarioRun.Measuring || !Master())
                throw new InvalidOperationException("Scenario air requires offline master.");
            Vector3 target = ScenarioRun.Position(spec.Target), drop = ScenarioRun.Position(spec.Drop);
            if (Safe(target) || (spec.Mode == "raid" && spec.Transports > 0 && Safe(drop)))
                throw new ArgumentException("Scenario air target/drop is in a safe zone.");
            if (spec.Mode == "an2-bomb")
            {
                // Existing TowerMission is 150 m / 190 km/h, six FAB-50 bombs.
                // Its selected target remains the JSON pin; no invented detonation.
                if (!TowerMission(false, target, spec.Faction)) throw new Exception("An-2 bomb mission refused.");
                return;
            }
            Event e = new Event(); e.Name = "scenario-" + spec.Id; e.Scene = MapScene.Current;
            e.X = target.x; e.Z = target.z; e.Heading = (float)spec.Heading; e.Edge = "drawn";
            e.Length = (float)spec.Length; e.Width = (float)spec.Width;
            e.DropX = drop.x; e.DropZ = drop.z; e.AttackX = target.x; e.AttackZ = target.z;
            e.Faction = spec.Faction; e.AltitudeM = 550f; e.PatrolMinutes = 10;
            if (spec.Bombers > 0)
            { Wave w = new Wave(); w.Bomber = true; w.Count = spec.Bombers; w.Load = 20; e.Waves.Add(w); }
            if (spec.Transports > 0)
            { Wave w = new Wave(); w.Count = spec.Transports; w.Load = spec.Load; w.Delay = 8f; e.Waves.Add(w); }
            Launch(e, true);
            if (!Flying(e)) throw new Exception("Scenario raid did not enter the native event queue.");
        }
    }
}
