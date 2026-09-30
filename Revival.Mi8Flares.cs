// W AA6: master-owned, finite Mi-8 countermeasures. Event 191 phases 5..8.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static class Mi8Flares
    {
        sealed class Pack
        {
            internal GameObject Go;
            internal int View, Pilot;
            internal float PilotUntil, AutoAt;
            internal bool Npc;
            internal Vector3 Point;
            internal FlareSupply Supply;
        }
        const int Capacity = 64, Visuals = 24;
        static readonly Pack[] Packs = MakePacks();
        static readonly List<GameObject> Troops = new List<GameObject>();
        static readonly GameObject[] Bodies = new GameObject[Visuals];
        static readonly Vector3[] Starts = new Vector3[Visuals];
        static readonly Vector3[] Velocities = new Vector3[Visuals];
        static readonly float[] Born = new float[Visuals];
        static int _visual;
        static int _activeBodies;
        static bool _auto;
        static float _syncAt;
        static int _syncTries;
        static readonly string[] HudRu = { "C: ловушки 0/6", "C: ловушки 1/6", "C: ловушки 2/6", "C: ловушки 3/6", "C: ловушки 4/6", "C: ловушки 5/6", "C: ловушки 6/6" };
        static readonly string[] HudEn = { "C: flares 0/6", "C: flares 1/6", "C: flares 2/6", "C: flares 3/6", "C: flares 4/6", "C: flares 5/6", "C: flares 6/6" };

        internal static string Hud(GameObject go)
        {
            int n = Mathf.Clamp(Remaining(go), 0, AirDefenceCore.FlareBursts);
            return Loc.T(HudRu[n], HudEn[n]);
        }

        static Pack[] MakePacks()
        {
            Pack[] p = new Pack[Capacity];
            for (int i = 0; i < p.Length; i++) p[i] = new Pack();
            return p;
        }

        static Pack Find(int view, bool create)
        {
            if (view <= 0) return null;
            Pack free = null;
            for (int i = 0; i < Packs.Length; i++)
            {
                Pack p = Packs[i];
                if (p.View == view && p.Go != null) return p;
                if (p.Go == null && free == null) free = p;
            }
            if (!create || free == null) return null;
            GameObject go = PlayerHeli.MissileTarget(view);
            bool npc = false;
            if (go == null)
            {
                Troops.Clear(); RevivalTroopInsertion.TroopHelis(Troops);
                for (int i = 0; i < Troops.Count; i++)
                    if (Troops[i] != null && PlayerAn2.View(Troops[i]) == view)
                    { go = Troops[i]; npc = true; break; }
            }
            if (go == null) return null;
            free.Go = go; free.View = view; free.Npc = npc;
            free.Pilot = 0; free.PilotUntil = 0; free.AutoAt = 0;
            free.Supply = new FlareSupply(); free.Supply.Remaining = AirDefenceCore.FlareBursts;
            return free;
        }

        // The existing aboard heartbeat identifies the pilot, not passengers.
        internal static void Pilot(int view, int actor, bool aboard, bool pilot)
        {
            Pack p = Find(view, aboard && pilot);
            if (p == null) return;
            if (aboard && pilot) { p.Pilot = actor; p.PilotUntil = Time.time + 3f; }
            else if (p.Pilot == actor) { p.Pilot = 0; p.PilotUntil = 0f; }
        }

        internal static int Remaining(GameObject go)
        {
            for (int i = 0; i < Packs.Length; i++)
                if (go != null && Packs[i].Go == go) return Packs[i].Supply.Remaining;
            return AirDefenceCore.FlareBursts;
        }

        internal static void Request(GameObject go)
        {
            if (go == null) return;
            int view = PlayerAn2.View(go);
            if (MercAA.Authority) RequestFrom(view, Mercs.LocalActor);
            else Stinger.CountermeasurePacket(5, view, 0, Vector3.zero, Vector3.zero);
        }

        internal static void RequestFrom(int view, int actor)
        {
            if (!MercAA.Authority) return;
            Pack p = Find(view, true);
            if (p == null || p.Npc) return;
            bool local = actor == Mercs.LocalActor && PlayerHeli.FlownMachine == p.Go;
            if (!local && (p.Pilot != actor || p.PilotUntil <= Time.time)) return;
            GameObject pilot = Crocodile.PlayerByActor(actor);
            if (!Crocodile.PlayerUp(pilot)
                || (pilot.transform.position - PlayerHeli.CabinSeatWorld(p.Go, -1)).sqrMagnitude > 100f) return;
            Dispense(p);
        }

        internal static void Threat(int view)
        {
            if (!MercAA.Authority) return;
            Pack p = Find(view, true);
            if (p != null && p.Npc && p.AutoAt <= 0f && p.Supply.Remaining > 0)
            { p.AutoAt = Mathf.Max(Time.time + 0.45f, p.Supply.Next); _auto = true; }
        }

        static void Dispense(Pack p)
        {
            if (!AirDefenceCore.Dispense(ref p.Supply, Time.time)) return;
            p.Point = p.Go.transform.position - p.Go.transform.forward * 5.6f;
            Show(p);
            Stinger.CountermeasurePacket(6, p.View, p.Supply.Sequence, p.Point,
                new Vector3(p.Supply.Remaining, AirDefenceCore.FlareLife, AirDefenceCore.FlareCooldown));
        }

        internal static void Apply(int view, int sequence, Vector3 point, Vector3 state)
        {
            Pack p = Find(view, true);
            if (p == null || sequence <= p.Supply.Sequence || sequence > AirDefenceCore.FlareBursts
                || state.x < 0f || state.x > AirDefenceCore.FlareBursts
                || (int)state.x != AirDefenceCore.FlareBursts - sequence) return;
            p.Supply.Sequence = sequence; p.Supply.Remaining = (int)state.x;
            p.Supply.Until = Time.time + Mathf.Clamp(state.y, 0f, AirDefenceCore.FlareLife);
            p.Supply.Next = Time.time + Mathf.Clamp(state.z, 0f, AirDefenceCore.FlareCooldown);
            p.Point = point;
            if (state.y > 0f) Show(p);
        }

        internal static void Snapshot()
        {
            if (!MercAA.Authority) return;
            for (int i = 0; i < Packs.Length; i++)
            {
                Pack p = Packs[i];
                if (p.Go == null || p.Supply.Sequence <= 0) continue;
                Stinger.CountermeasurePacket(6, p.View, p.Supply.Sequence, p.Point,
                    new Vector3(p.Supply.Remaining, Mathf.Max(0f, p.Supply.Until - Time.time),
                        Mathf.Max(0f, p.Supply.Next - Time.time)));
            }
        }

        internal static bool Decoy(GameObject go, Vector3 missile, Vector3 forward, out Vector3 point)
        {
            point = Vector3.zero;
            for (int i = 0; i < Packs.Length; i++)
            {
                Pack p = Packs[i];
                if (p.Go != go || go == null) continue;
                point = p.Point;
                Vector3 delta = point - missile;
                return AirDefenceCore.Decoy(Time.time, p.Supply.Until, delta.sqrMagnitude,
                    Vector3.Dot(forward, delta.normalized));
            }
            return false;
        }

        // Check on the master too: a delayed client's hit cannot override a flare.
        internal static bool Protects(GameObject go)
        {
            for (int i = 0; i < Packs.Length; i++)
                if (Packs[i].Go == go && go != null && Time.time < Packs[i].Supply.Until) return true;
            return false;
        }

        internal static void Tick()
        {
            if (!_auto && _activeBodies == 0 && _syncTries >= 4) return;
            FrameProf.S(FrameProf.S_Mi8FlaresT);
            try
            {
                // Finite retries after room entry: carriers may instantiate later.
                if (_syncTries < 4 && MapTools.LocalPlayer() != null && Time.time >= _syncAt)
                {
                    _syncTries++; _syncAt = Time.time + 2f;
                    if (!MercAA.Authority) Stinger.CountermeasurePacket(7, 0, 0, Vector3.zero, Vector3.zero);
                }
                if (_auto)
                {
                    _auto = false;
                    for (int i = 0; i < Packs.Length; i++)
                    {
                        Pack p = Packs[i];
                        if (p.Go == null) { p.AutoAt = 0f; continue; }
                        if (p.AutoAt <= 0f) continue;
                        if (Time.time >= p.AutoAt)
                        { p.AutoAt = 0f; if (p.Npc && MercAA.Authority) Dispense(p); }
                        else _auto = true;
                    }
                }
                if (_activeBodies == 0) return;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    if (Bodies[i] == null || !Bodies[i].activeSelf) continue;
                    float age = Time.time - Born[i];
                    if (age >= AirDefenceCore.FlareLife) { Bodies[i].SetActive(false); _activeBodies--; continue; }
                    Bodies[i].transform.position = Starts[i] + Velocities[i] * age
                        + Vector3.down * (4.905f * 2.8f * age * age);
                }
            }
            finally { FrameProf.E(FrameProf.S_Mi8FlaresT); }
        }

        static void Show(Pack p)
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null || (me.transform.position - p.Point).sqrMagnitude > 2000f * 2000f) return;
            if (Bodies[0] == null)
            {
                Material mat = new Material(Shader.Find("Unlit/Color"));
                mat.color = new Color(1f, 0.75f, 0.25f, 1f);
                for (int i = 0; i < Bodies.Length; i++)
                {
                    GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "NDR_Mi8Flare"; UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                    go.GetComponent<Renderer>().sharedMaterial = mat;
                    go.transform.localScale = Vector3.one * 0.65f;
                    go.SetActive(false); Bodies[i] = go;
                }
            }
            for (int side = -1; side <= 1; side += 2)
            {
                int i = _visual++ % Bodies.Length;
                if (!Bodies[i].activeSelf) _activeBodies++;
                Starts[i] = p.Point; Born[i] = Time.time;
                Velocities[i] = p.Go.transform.right * (side * 22.4f) + Vector3.up * 5.6f;
                Bodies[i].transform.position = Starts[i]; Bodies[i].SetActive(true);
            }
        }

        internal static void Clear()
        {
            _auto = false; _activeBodies = 0;
            _syncTries = 0; _syncAt = Time.time + 1f;
            for (int i = 0; i < Packs.Length; i++) { Packs[i].Go = null; Packs[i].AutoAt = 0f; }
            for (int i = 0; i < Bodies.Length; i++) if (Bodies[i] != null) Bodies[i].SetActive(false);
        }
    }
}
