// Pooled console-only ground contacts, budgeted LOS and explicit direct fire.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RadarGround
    {
        sealed class Track
        {
            internal readonly GepardGun.Contact Contact = new GepardGun.Contact();
            internal readonly float[] SightAt = new float[7];
            internal readonly bool[] Sight = new bool[7];
            internal float NextThink, NextSight, SampleAt;
            internal int Gun;
            internal string Faction;
        }
        sealed class Lane
        {
            internal GepardGun.Contact Contact;
            internal Vector3 From, Velocity, Previous, Point;
            internal float Tof, Started;
            internal int Segment;
            internal float WaitingAt = -100f;
        }
        static Track[] _tracks = MakeTracks(256);
        static readonly Lane[] _lanes = MakeLanes();
        static readonly List<GepardGun.Contact> _contacts = new List<GepardGun.Contact>(256);
        static readonly RaycastHit[] _hits = new RaycastHit[32];
        static Component[] _vehicles;
        static FieldInfo _durability, _passengers;
        static int _cursor, _scanCursor, _rayFrame = -1, _rays, _shotFrame = -1;
        static int _laneOwner = -1, _nextLane, _viewSide = -1;
        static Texture2D _icon;
        internal static int RaysThisFrame { get { Budget(); return _rays; } }
        internal static List<GepardGun.Contact> Contacts { get { return _contacts; } }

        static Track[] MakeTracks(int count)
        {
            // Kind 1 does not use the drone-owner Actor field: keep its pool slot there.
            Track[] tracks = new Track[count];
            for (int i = 0; i < count; i++)
            { tracks[i] = new Track(); tracks[i].Contact.Actor = i; }
            return tracks;
        }
        static Lane[] MakeLanes()
        {
            Lane[] lanes = new Lane[7];
            for (int i = 0; i < lanes.Length; i++) lanes[i] = new Lane();
            return lanes;
        }

        // Cold console Repaint only, like the radar's existing blip pool.
        // The initial pool covers ordinary scenes; a spawn high-water mark
        // expands here rather than allocating in Tick or gun control.
        internal static void Warm()
        {
            Component[] vehicles = VehicleScan.All();
            if (vehicles.Length > _tracks.Length)
            {
                Track[] next = MakeTracks(vehicles.Length * 2);
                Array.Copy(_tracks, next, _tracks.Length);
                _tracks = next;
                _contacts.Capacity = next.Length;
            }
            _vehicles = vehicles;
            if (_durability == null && vehicles.Length > 0)
            {
                Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
                _durability = FastField.Find(t, "Durability");
                _passengers = FastField.Find(t, "Passengers");
                // Compile the numeric getter outside recurring think paths.
                for (int i = 0; i < vehicles.Length; i++)
                    if (vehicles[i] != null && _durability != null)
                    { FastField.GetFloat(_durability, vehicles[i]); break; }
            }
        }

        // Shared registry snapshots allocate only on membership changes in the
        // registry itself. Startup/spawn warming never adds per-frame pools.
        internal static void Registry()
        {
            if (!RadarScope.InView && TowerRadar.OperatorActor < 0 && TowerRadar.AssignedKind != RadarGroundCore.Kind) return;
            int side = TowerRadar.OperatorActor >= 0 ? TowerRadar.PlayerSide(TowerRadar.OperatorActor) : TowerRadar.ControlSide;
            if (side != _viewSide)
            {
                _viewSide = side;
                for (int i = 0; i < _contacts.Count; i++) For(_contacts[i]).NextSight = 0f;
            }
            Component[] vehicles = VehicleScan.All();
            _vehicles = vehicles;
            if (_durability == null || vehicles.Length > _tracks.Length) Warm();
        }

        internal static Texture2D Icon()
        {
            if (_icon != null) return _icon;
            const int size = 64;
            Color[] pixels = new Color[size * size];
            Color ink = Color.white, outline = new Color(0.08f, 0.08f, 0.08f, 0.9f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - 31.5f) * 40f / size, dy = (y - 31.5f) * 40f / size;
                    if (RadarScopeCore.Ink(RadarGroundCore.Marker, dx, dy)) pixels[y * size + x] = ink;
                    else
                        for (int oy = -2; oy <= 2 && pixels[y * size + x].a == 0f; oy++)
                            for (int ox = -2; ox <= 2; ox++)
                                if (RadarScopeCore.Ink(RadarGroundCore.Marker, dx + ox, dy + oy))
                                { pixels[y * size + x] = outline; break; }
                }
            _icon = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _icon.name = "NDR_RadarGroundIcon";
            _icon.wrapMode = TextureWrapMode.Clamp; _icon.filterMode = FilterMode.Bilinear;
            _icon.SetPixels(pixels); _icon.Apply(false, true);
            return _icon;
        }

        static void Budget()
        {
            if (_rayFrame == Time.frameCount) return;
            _rayFrame = Time.frameCount; _rays = 0;
        }
        static bool Alive(GepardGun.Contact c)
        {
            if (c == null || c.Vehicle == null || c.Go == null || !c.Go.activeInHierarchy
                || _durability == null || FastField.GetFloat(_durability, c.Vehicle) <= 0f) return false;
            Vector3 p = c.Go.transform.position;
            return RadarGroundCore.Area(p.x, p.z);
        }
        static Track For(GepardGun.Contact c)
        {
            if (c == null || c.Kind != RadarGroundCore.Kind || c.Actor < 0 || c.Actor >= _tracks.Length) return null;
            Track t = _tracks[c.Actor];
            return object.ReferenceEquals(t.Contact, c) ? t : null;
        }
        internal static GepardGun.Contact Find(GameObject go)
        {
            if (go == null) return null;
            for (int i = 0; i < _contacts.Count; i++)
                if (_contacts[i].Go == go) return _contacts[i];
            return null;
        }
        internal static bool Allowed(GepardGun.Contact c, string side)
        {
            Track track = For(c);
            return Alive(c) && track != null && (track.Faction == null
                || AirDefencePolicy.Hostile(side, track.Faction));
        }
        internal static int Iff(GepardGun.Contact c, string side)
        {
            Track track = For(c);
            string faction = track == null ? null : track.Faction;
            return faction == null ? 0 : side != null && side == faction ? 1
                : AirDefencePolicy.Hostile(side, faction) ? -1 : 0;
        }
        static bool Eligible(Flak.Gun g, int side)
        {
            return g != null && g.Root != null && g.Muzzle != null && !g.Town && g.Info != null
                && TowerRadar.Follows(g.Info, side) && g.Info.CrewAlive > 0
                && !g.Info.PlayerManned && Time.time >= g.ClaimedUntil && AirDefenceDamage.Alive(g.Index);
        }
        static bool Reach(Flak.Gun g, GepardGun.Contact c)
        {
            Vector3 d = c.Pos - Flak.Mid(g);
            float range = g.ShortRange ? ShortRangeCore.RangeM * Flak.K
                : Mathf.Min(Flak.MaxFuze, Flak.GunRange(g));
            return RadarGroundCore.Reach(d.x, d.y, d.z, range,
                g.ShortRange ? ZuGroundCore.MinPitch : (Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value),
                (g.ShortRange ? ZuGroundCore.DeadM : Flak.ArmMetres) * Flak.K);
        }
        internal static bool CanEngage(Flak.Gun g, GepardGun.Contact c, int side)
        {
            Track t = For(c);
            return t != null && Eligible(g, side) && Alive(c) && Reach(g, c)
                && t.Sight[g.Index] && Time.time - t.SightAt[g.Index] <= 1.25f;
        }
        internal static bool Visible(GepardGun.Contact c)
        {
            if (!TowerRadar.Working || !Alive(c)) return false;
            Track t = For(c);
            if (t == null) return false;
            float now = Time.time;
            for (int i = 0; i < 7; i++)
            {
                if (!t.Sight[i] || now - t.SightAt[i] > 1.25f) continue;
                Flak.Gun g = Flak.ByIndex(i);
                if (Eligible(g, _viewSide < 0 ? TowerRadar.ControlSide : _viewSide) && Reach(g, c)) return true;
            }
            return false;
        }

        static bool Ordered(GepardGun.Contact c)
        {
            for (int i = 0; i < 7; i++)
            {
                Flak.Gun g = Flak.ByIndex(i);
                if (g != null && object.ReferenceEquals(g.RadarGroundTarget, c)) return true;
            }
            return false;
        }

        internal static void Tick()
        {
            FrameProf.S(FrameProf.S_RadarGroundT);
            try
            {
                Budget();
                if (!TowerRadar.Working || _vehicles == null
                    || (!RadarScope.InView && TowerRadar.OperatorActor < 0 && TowerRadar.AssignedKind != RadarGroundCore.Kind)) return;
                float now = Time.time;
                // Inspect only four registry members per frame. VehicleGameSystem
                // is the ground registry; helicopters use HelicopterGameSystem.
                for (int n = 0; n < RadarGroundCore.ThinkBudget && _vehicles.Length > 0; n++)
                {
                    if (_cursor >= _vehicles.Length) _cursor = 0;
                    Component vehicle = _vehicles[_cursor++];
                    if (vehicle == null || !vehicle.gameObject.activeInHierarchy) continue;
                    Vector3 p = vehicle.transform.position;
                    if (!RadarGroundCore.Area(p.x, p.z)) continue;
                    GepardGun.Contact c = Find(vehicle.gameObject);
                    Track t = c == null ? null : For(c);
                    if (t == null)
                    {
                        for (int i = 0; i < _tracks.Length; i++)
                            if (_tracks[i].Contact.Go == null) { t = _tracks[i]; break; }
                        if (t == null) continue; // Warm expands on the next Repaint.
                        c = t.Contact; _contacts.Remove(c); c.Go = vehicle.gameObject; c.Vehicle = vehicle;
                        c.Kind = RadarGroundCore.Kind; c.LocalCentre = Vector3.up * Flak.K;
                        c.Radius = Flak.K; c.Vel = Vector3.zero; c.VelInit = false;
                        // One native hull box is enough; no GetComponents array.
                        BoxCollider hull = vehicle.GetComponentInChildren<BoxCollider>();
                        if (hull != null)
                        {
                            c.LocalCentre = vehicle.transform.InverseTransformPoint(hull.bounds.center);
                            c.Radius = Mathf.Clamp(hull.bounds.extents.magnitude, 0.3f, 9f);
                        }
                        t.NextThink = t.NextSight = t.SampleAt = 0f; t.Gun = 0; t.Faction = null;
                        for (int i = 0; i < 7; i++) { t.Sight[i] = false; t.SightAt[i] = -100f; }
                        _contacts.Add(c);
                    }
                    if (now < t.NextThink) continue;
                    p = vehicle.transform.TransformPoint(c.LocalCentre);
                    float elapsed = now - t.SampleAt;
                    c.Vel = c.VelInit && elapsed > 0.01f ? (p - c.Pos) / elapsed : Vector3.zero;
                    if (c.Vel.sqrMagnitude > 4000000f) c.Vel = Vector3.zero;
                    c.Pos = p; c.VelInit = true; t.SampleAt = now;
                    GameObject[] passengers = _passengers == null ? null : _passengers.GetValue(vehicle) as GameObject[];
                    t.Faction = passengers != null && passengers.Length > 0 && passengers[0] != null
                        ? Fraktion.Spielerseite(passengers[0]) : null;
                    float distance = float.MaxValue;
                    for (int i = 0; i < 7; i++)
                    {
                        Flak.Gun g = Flak.ByIndex(i);
                        if (g != null && g.Root != null) distance = Mathf.Min(distance, (p - g.Root.position).sqrMagnitude);
                    }
                    bool selected = Ordered(c);
                    bool seen = false;
                    for (int i = 0; i < 7; i++) if (t.Sight[i]) { seen = true; break; }
                    t.NextThink = now + (selected ? 0.25f : seen ? RadarGroundCore.Interval(distance, false) : 4f);
                }
                // Round-robin LOS, two physics calls maximum across scan AND shots.
                // No sight: retry only every four seconds; a FIRE order retries at 4 Hz.
                for (int n = 0; n < RadarGroundCore.ThinkBudget; n++)
                {
                    if (_contacts.Count == 0) break;
                    if (_scanCursor >= _contacts.Count) _scanCursor = 0;
                    GepardGun.Contact c = _contacts[_scanCursor++];
                    Track t = For(c);
                    if (!Alive(c))
                    { _contacts.RemoveAt(--_scanCursor); c.Go = null; c.Vehicle = null; continue; }
                    if (now < t.NextSight) continue;
                    int gun = t.Gun;
                    Flak.Gun g = Flak.ByIndex(gun);
                    bool candidate = Eligible(g, _viewSide < 0 ? TowerRadar.ControlSide : _viewSide) && Reach(g, c);
                    if (candidate && _rays >= RadarGroundCore.RayBudget) { _scanCursor--; break; }
                    bool before = t.Sight[gun];
                    t.Sight[gun] = candidate && Clear(g, c, g.Muzzle.position, c.Pos);
                    if (!before && t.Sight[gun]) t.NextThink = 0f;
                    t.SightAt[gun] = now;
                    bool ordered = Ordered(c);
                    if (t.Sight[gun] && !ordered)
                    {
                        // One clear gun proves a marker. Recheck that gun only;
                        // inspect every following gun after explicit FIRE.
                        t.NextSight = now + RadarGroundCore.Interval((c.Pos - g.Root.position).sqrMagnitude, false);
                        continue;
                    }
                    if (++t.Gun >= 7)
                    {
                        t.Gun = 0;
                        bool seen = false;
                        for (int i = 0; i < 7; i++) if (t.Sight[i]) { seen = true; break; }
                        float distance = (c.Pos - TowerRadar.TowerBase).sqrMagnitude;
                        t.NextSight = now + (ordered ? 0.25f : seen ? RadarGroundCore.Interval(distance, false) : 4f);
                    }
                }
            }
            finally { FrameProf.E(FrameProf.S_RadarGroundT); }
        }

        // NonAlloc returns unsorted hits. Inspect every hit, ignore only this
        // gun/crew and the target, fail closed if the fixed buffer saturated.
        static bool Clear(Flak.Gun g, GepardGun.Contact c, Vector3 from, Vector3 to)
        {
            Budget();
            if (_rays >= RadarGroundCore.RayBudget) return false;
            _rays++;
            Vector3 d = to - from;
            float distance = d.magnitude;
            if (distance < 0.05f) return true;
            int count = Physics.RaycastNonAlloc(from, d / distance, _hits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count == _hits.Length) return false;
            Transform target = c.Go.transform;
            float targetAt = distance;
            for (int i = 0; i < count; i++)
                if (_hits[i].transform == target || _hits[i].transform.IsChildOf(target))
                    targetAt = Mathf.Min(targetAt, _hits[i].distance);
            for (int i = 0; i < count; i++)
            {
                Transform hit = _hits[i].transform;
                if (_hits[i].distance >= targetAt || hit == target || hit.IsChildOf(target)
                    || (g.Owner != null && hit.IsChildOf(g.Owner)) || FlakFire.Crewman(g, hit)) continue;
                return false;
            }
            return true;
        }

        // Three low-arc segments, resumed next frame when the shared budget is
        // exhausted. A gun waits rather than firing through unchecked cover.
        static bool ShotLane(Flak.Gun g, GepardGun.Contact c, Vector3 aim, float tof)
        {
            Budget();
            Lane lane = _lanes[g.Index];
            lane.WaitingAt = Time.time;
            if (_laneOwner >= 0 && Time.time - _lanes[_laneOwner].WaitingAt > 0.15f) _laneOwner = -1;
            if (_laneOwner < 0)
                for (int i = 0; i < 7; i++)
                {
                    int index = (_nextLane + i) % 7;
                    if (Time.time - _lanes[index].WaitingAt <= 0.1f) { _laneOwner = index; break; }
                }
            // Reserve one of the two calls for contact scans. Round-robin
            // waiting guns share the remaining call without starving a gun.
            if (_laneOwner != g.Index || _shotFrame == Time.frameCount) return false;
            if (lane.Contact != c || Time.time - lane.Started > 0.5f
                || (lane.Point - c.Pos).sqrMagnitude > 0.25f * Flak.K * Flak.K
                || (lane.From - g.Muzzle.position).sqrMagnitude > 0.25f * Flak.K * Flak.K)
            {
                lane.Contact = c; lane.Started = Time.time; lane.Segment = 1;
                lane.Point = c.Pos; lane.From = lane.Previous = g.Muzzle.position;
                lane.Tof = tof;
                float speed = g.ShortRange ? ShortRangeCore.SpeedM * Flak.K : Flak.Speed;
                lane.Velocity = (aim - lane.From).normalized * speed;
            }
            float gravity = g.ShortRange ? 9.81f * Flak.K : Flak.Gravity;
            if (lane.Segment <= 3 && _rays < RadarGroundCore.RayBudget)
            {
                float t = lane.Tof * lane.Segment / 3f;
                Vector3 point = lane.From + lane.Velocity * t - Vector3.up * (0.5f * gravity * t * t);
                _shotFrame = Time.frameCount;
                if (!Clear(g, c, lane.Previous, point))
                {
                    Track track = For(c); track.Sight[g.Index] = false; track.NextSight = 0f;
                    lane.Contact = null; lane.WaitingAt = -100f; _laneOwner = -1; _nextLane = (g.Index + 1) % 7;
                    return false;
                }
                lane.Previous = point; lane.Segment++;
            }
            if (lane.Segment <= 3) return false;
            lane.Contact = null; lane.WaitingAt = -100f; _laneOwner = -1; _nextLane = (g.Index + 1) % 7;
            return true;
        }

        internal static bool Control(Flak.Gun g, float dt, bool gunner, bool loader)
        {
            GepardGun.Contact c = g.RadarGroundTarget;
            if (c == null) return false;
            FrameProf.S(FrameProf.S_RadarGroundFire);
            try
            {
                g.Target = null; g.Cue = null;
                if (!object.ReferenceEquals(g.Assigned, c.Go) || !object.ReferenceEquals(g.RadarAssigned, c.Go))
                { g.RadarGroundTarget = null; g.Firing = g.Engaged = false; return false; }
                bool valid = TowerRadar.Working && CanEngage(g, c, TowerRadar.ControlSide)
                    && Allowed(c, Flak.OwnerFaction(g));
                if (!valid || g.Mode == FlakMode.HoldFire)
                {
                    g.Firing = g.Engaged = false; g.Held = 0f; g.ShortBurst = new ShortBurst();
                    Flak.Publish(g, false, false); return true;
                }
                float tof;
                Vector3 aim = g.ShortRange ? ShortRange.Intercept(g.Muzzle.position, c.Pos, c.Vel, out tof)
                    : Flak.Intercept(g.Muzzle.position, c.Pos, c.Vel, out tof);
                float yaw, pitch;
                Flak.Angles(g, aim - Flak.Mid(g), out yaw, out pitch);
                g.WantYaw = yaw; g.WantPitch = pitch; g.Aim = aim;
                g.FuzeRange = Mathf.Min((aim - g.Muzzle.position).magnitude + 100f * Flak.K,
                    g.ShortRange ? ShortRangeCore.RangeM * Flak.K : Flak.MaxFuze);
                g.Engaged = true; g.Held += dt; g.LastContact = Time.time;
                float min = g.ShortRange ? ZuGroundCore.MinPitch : (Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value);
                bool ready = pitch >= min && pitch <= 15f && g.Held >= 0.8f && !g.Reloading
                    && FlakAmmo.Ready(g) && Vector3.Angle(g.Cradle.forward, aim - g.Muzzle.position) < 0.75f;
                float now = Time.time;
                bool due = g.ShortRange ? now >= g.ShortBurst.Next && now >= g.ShortBurst.PauseUntil : now >= g.NextShot;
                bool shot = ready && due && (g.Rounds <= 0 || ShotLane(g, c, aim, tof));
                if (g.ShortRange)
                {
                    bool corrected;
                    if (!ready || !due || shot)
                        shot = ShortRangeCore.Shot(ref g.ShortBurst, now, ready && (!due || shot), out corrected);
                }
                g.Firing = shot;
                if (shot)
                {
                    bool merc = MercAA.Gun(g.Index) != null;
                    float reload = (g.ShortRange ? ShortRangeCore.ReloadSeconds : Flak.ReloadSeconds())
                        * MercAACore.ReloadScale(merc, gunner && loader);
                    if (g.Rounds <= 0) Flak.StartReload(g, reload);
                    else
                    {
                        Flak.Fire(g, aim, true, g.FuzeRange, null, false);
                        if (!g.ShortRange)
                            g.NextShot = Flak.NextShot(g.NextShot, now, dt,
                                MercAACore.CrewInterval(Flak.Interval(), Flak.CrewInterval(), merc,
                                    MercAA.Directed(g), gunner && loader));
                        if (g.Rounds <= 0) Flak.StartReload(g, reload);
                    }
                }
                Flak.Publish(g, false, false);
                return true;
            }
            finally { FrameProf.E(FrameProf.S_RadarGroundFire); }
        }
    }
}
