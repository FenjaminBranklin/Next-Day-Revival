// Bounded NPC drone fire. Cached native access avoids reflection boxing/arrays
// on the steady think/fire path. Initialization happens at flight reset.
using System;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class DroneNpcEngagement
    {
        internal delegate bool Damage(Vector3 shooter);
        delegate bool BoolRead(object o);
        delegate bool EnemyRead(object o, object pilot);
        delegate Component GunRead(object o);
        delegate int IntRead(object o);
        delegate float FloatRead(object o);
        delegate Vector3 MuzzleRead(object o);
        delegate void FireCall(object o, Vector3 aim, bool network);
        delegate void VoidCall(object o);

        struct Slot
        {
            internal Component Ai;
            internal float NextThink, ReloadAt, SeenAt;
            internal DroneNpcAimCore.Contact Contact;
            internal byte Flags;
            internal bool Watched;
        }
        struct Watch { internal Component Ai; internal int Slot; }
        const int Slots = 1024, VisitsPerFrame = 8, RaycastsPerFrame = 3;
        readonly Slot[] _slots = new Slot[Slots];
        readonly Watch[] _watch = new Watch[64];
        readonly Damage _damage;
        BoolRead _initialized, _sensor, _alive, _bullets, _busy;
        EnemyRead _enemy;
        GunRead _gun;
        IntRead _weapon;
        FloatRead _delay;
        MuzzleRead _muzzle;
        FireCall _fire;
        VoidCall _reload;
        bool _bound, _positionValid, _hadMembers;
        Vector3 _previous;
        float _previousTime, _nextVolley;
        int _cursor, _shots, _watchCount, _watchCursor;
        static int _rayFrame = -1, _rays;
        internal int LastInRange, LastActive, LastLocated;

        internal DroneNpcEngagement(Damage damage) { _damage = damage; }

        internal void Bind(FieldInfo gun, FieldInfo initialized, FieldInfo sensor,
            FieldInfo weapon, FieldInfo delay, MethodInfo alive, MethodInfo enemy,
            MethodInfo bullets, MethodInfo busy, MethodInfo muzzle, MethodInfo fire,
            MethodInfo reload)
        {
            if (_bound) return;
            _gun = (GunRead)Read(gun, typeof(Component), typeof(GunRead));
            _initialized = initialized == null || initialized.FieldType != typeof(bool)
                ? Always : (BoolRead)Read(initialized, typeof(bool), typeof(BoolRead));
            _sensor = sensor == null || sensor.FieldType != typeof(bool)
                ? Always : (BoolRead)Read(sensor, typeof(bool), typeof(BoolRead));
            _weapon = weapon == null ? Rifle : (IntRead)Read(weapon, typeof(int), typeof(IntRead));
            _delay = delay == null || delay.FieldType != typeof(float)
                ? NoDelay : (FloatRead)Read(delay, typeof(float), typeof(FloatRead));
            _alive = (BoolRead)Call(alive, typeof(BoolRead));
            _enemy = (EnemyRead)Call(enemy, typeof(EnemyRead));
            _bullets = (BoolRead)Call(bullets, typeof(BoolRead));
            _busy = (BoolRead)Call(busy, typeof(BoolRead));
            _muzzle = (MuzzleRead)Call(muzzle, typeof(MuzzleRead));
            _fire = (FireCall)Call(fire, typeof(FireCall));
            _reload = reload == null ? null : (VoidCall)Call(reload, typeof(VoidCall));
            _bound = true;
        }

        internal void Reset()
        {
            Array.Clear(_slots, 0, _slots.Length);
            Array.Clear(_watch, 0, _watch.Length);
            _cursor = _shots = 0;
            _watchCount = _watchCursor = 0;
            _positionValid = false;
            _hadMembers = false;
            _nextVolley = 0f;
            LastInRange = LastActive = LastLocated = 0;
        }

        internal void Tick(object pilot, Vector3 drone, float rangeM, float accuracy,
            float seconds, int maxShooters)
        {
            if (!_bound || pilot == null) return;
            FrameProf.S(FrameProf.S_DroneNpcFire);
            try { Step(pilot, drone, rangeM, accuracy, seconds, maxShooters); }
            finally { FrameProf.E(FrameProf.S_DroneNpcFire); }
        }

        void Step(object pilot, Vector3 drone, float rangeM, float accuracy, float seconds, int maxShooters)
        {
            float now = Time.time;
            float dt = now - _previousTime;
            float speed = _positionValid && dt > 0f
                ? (drone - _previous).magnitude / (dt * DroneNpcAimCore.Metre) : 0f;
            _previous = drone; _previousTime = now; _positionValid = true;
            if (now >= _nextVolley)
            {
                _nextVolley = now + seconds * UnityEngine.Random.Range(0.8f, 1.2f);
                _shots = 0;
            }
            if (_rayFrame != Time.frameCount) { _rayFrame = Time.frameCount; _rays = 0; }
            Component[] all = NpcScan.All();
            int count = all.Length;
            if (count == 0)
            {
                if (_hadMembers)
                {
                    Array.Clear(_slots, 0, _slots.Length);
                    Array.Clear(_watch, 0, _watch.Length);
                    _watchCount = _watchCursor = 0;
                    LastInRange = LastActive = LastLocated = 0;
                    _hadMembers = false;
                }
                return;
            }
            _hadMembers = true;
            float rangeU = rangeM * DroneNpcAimCore.Metre;
            // Reserve half the visits for known local hostiles. Distant NPCs
            // in the registry must not reduce a nearby man's think cadence.
            int priority = Mathf.Min(4, _watchCount);
            int visits = priority + Mathf.Min(VisitsPerFrame - priority, count);
            for (int visit = 0; visit < visits; visit++)
            {
                Component ai;
                int index, watchAt = -1;
                if (visit < priority && _watchCount > 0)
                {
                    if (_watchCursor >= _watchCount) _watchCursor = 0;
                    watchAt = _watchCursor++;
                    ai = _watch[watchAt].Ai; index = _watch[watchAt].Slot;
                    if (!ReferenceEquals(_slots[index].Ai, ai) || !_slots[index].Watched)
                    { RemoveWatch(watchAt); continue; }
                }
                else
                {
                    if (_cursor >= count) _cursor = 0;
                    ai = all[_cursor++];
                    if (ai == null) continue;
                    index = FindSlot(ai, now);
                }
                if (index < 0) continue;
                Slot s = _slots[index];
                if (ai == null || !ai.gameObject.activeInHierarchy)
                {
                    s.Flags = 0; s.Watched = false; Store(index, s);
                    if (watchAt >= 0) RemoveWatch(watchAt);
                    continue;
                }
                if (now < s.NextThink) continue;
                float units = (drone - ai.transform.position).magnitude;
                float metres = units / DroneNpcAimCore.Metre;
                float interval = DroneNpcAimCore.ThinkSeconds(metres, s.Contact.Heard);
                if (s.NextThink == 0f) s.NextThink = now + interval + (index % 15) / 60f;
                else s.NextThink += interval;
                if (s.NextThink <= now) s.NextThink = now + interval;
                s.SeenAt = now;
                s.Flags = 0;
                if (units < 2f || units > rangeU)
                {
                    s.Contact = new DroneNpcAimCore.Contact();
                    s.Watched = false;
                    Store(index, s); continue;
                }
                s.Flags = 1;
                if (!_initialized(ai) || !_sensor(ai) || !_alive(ai)) { s.Watched = false; Store(index, s); continue; }
                s.Flags = 3;
                if (!_enemy(ai, pilot)) { s.Watched = false; Store(index, s); continue; }
                if (!s.Watched && _watchCount < _watch.Length)
                {
                    _watch[_watchCount].Ai = ai; _watch[_watchCount++].Slot = index;
                    s.Watched = true;
                }
                if (!DroneNpcAimCore.Notice(ref s.Contact, now, metres, UnityEngine.Random.value))
                { Store(index, s); continue; }
                Component gun = _gun(ai);
                if (gun == null) { Store(index, s); continue; }
                Vector3 muzzle = _muzzle(gun);
                if (muzzle == Vector3.zero) muzzle = ai.transform.position + Vector3.up * 1.4f;
                Vector3 to = drone - muzzle;
                float distance = to.magnitude;
                if (distance < 2f || distance > rangeU) { Store(index, s); continue; }
                bool visible;
                if (!Visible(ai, muzzle, to / distance, distance, out visible))
                {
                    // Budget exhaustion is not loss of LOS. Retry at the next
                    // scheduled think, without repeating native state checks.
                    Store(index, s); continue;
                }
                bool ready = DroneNpcAimCore.Track(ref s.Contact, now, metres, visible);
                if (s.Contact.Located && visible) s.Flags = 7;
                if (!ready)
                { Store(index, s); continue; }
                if (_shots >= maxShooters)
                { Store(index, s); continue; }
                if (!_bullets(gun))
                {
                    if (_reload != null && now >= s.ReloadAt)
                    { s.ReloadAt = now + 5f; _reload(ai); }
                    Store(index, s); continue;
                }
                if (_busy(gun) || _delay(gun) >= now) { Store(index, s); continue; }
                float chance = DroneNpcAimCore.Chance(MercWeaponReach.Kind(_weapon(ai)),
                    distance / DroneNpcAimCore.Metre, speed, accuracy);
                bool hit = UnityEngine.Random.value < chance;
                Vector3 aim = drone;
                if (!hit)
                {
                    Vector3 dir = to / distance;
                    Vector3 side = Vector3.Cross(dir, Vector3.up);
                    if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                    side.Normalize();
                    Vector3 up = Vector3.Cross(side, dir).normalized;
                    float spread = Mathf.Max(2.5f, distance * 0.08f);
                    float x = UnityEngine.Random.Range(-spread, spread);
                    float y = UnityEngine.Random.Range(-spread, spread);
                    if (Mathf.Abs(x) + Mathf.Abs(y) < spread * 0.5f) x += x < 0f ? -spread : spread;
                    aim += side * x + up * y;
                }
                _fire(gun, aim, true);
                _shots++;
                s.Contact.NextShot = now + seconds;
                Store(index, s);
                if (hit && !_damage(ai.transform.position)) return;
            }
            // A small group is visited in full each frame. Rotate its start so
            // a shooter whose own colliders spend the budget cannot starve it.
            if (count > 0 && count <= VisitsPerFrame) _cursor = (_cursor + 1) % count;
            if (_watchCount > 0) _watchCursor = (_watchCursor + 1) % _watchCount;
        }

        int FindSlot(Component ai, float now)
        {
            int start = ai.GetInstanceID() & (Slots - 1);
            for (int probe = 0; probe < 16; probe++)
            {
                int index = (start + probe) & (Slots - 1);
                Component old = _slots[index].Ai;
                if (old == ai) return index;
                if (old == null || now - _slots[index].SeenAt > 30f)
                {
                    Slot empty = new Slot(); empty.Ai = ai; Store(index, empty); return index;
                }
            }
            return -1;
        }

        void RemoveWatch(int index)
        {
            _watch[index] = _watch[--_watchCount];
            _watch[_watchCount] = new Watch();
            if (_watchCursor > index) _watchCursor = index;
        }

        void Store(int index, Slot value)
        {
            byte old = _slots[index].Flags;
            LastInRange += (value.Flags & 1) - (old & 1);
            LastActive += ((value.Flags >> 1) & 1) - ((old >> 1) & 1);
            LastLocated += ((value.Flags >> 2) & 1) - ((old >> 2) & 1);
            _slots[index] = value;
        }

        static bool Visible(Component ai, Vector3 from, Vector3 dir, float distance, out bool visible)
        {
            visible = false;
            if (_rays >= RaycastsPerFrame) return false;
            from += dir * 0.35f;
            float left = distance - 1f;
            while (left > 0.1f && _rays < RaycastsPerFrame)
            {
                _rays++;
                Vector3 point;
                GameObject block = Turret.RaycastObject(from, dir, left, out point);
                if (block == null) { visible = true; return true; }
                if (!block.transform.IsChildOf(ai.transform) && !ai.transform.IsChildOf(block.transform)) return true;
                float step = Vector3.Distance(from, point) + 0.35f;
                left -= step; from = point + dir * 0.35f;
            }
            if (left <= 0.1f) { visible = true; return true; }
            return false;
        }

        static bool Always(object o) { return true; }
        static int Rifle(object o) { return 0; }
        static float NoDelay(object o) { return -1f; }

        static Delegate Read(FieldInfo field, Type result, Type kind)
        {
            DynamicMethod method = new DynamicMethod("DroneRead_" + field.Name, result,
                new Type[] { typeof(object) }, typeof(DroneNpcEngagement), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (result == typeof(int) && field.FieldType != typeof(int))
            {
                if (field.FieldType.IsEnum) il.Emit(OpCodes.Conv_I4);
                else
                {
                    // Some game item IDs use a value wrapper. Resolve its
                    // typed conversion at launch rather than box it in think.
                    MethodInfo conversion = null;
                    MethodInfo[] methods = field.FieldType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        if (methods[i].Name != "op_Implicit" || methods[i].ReturnType != typeof(int)) continue;
                        ParameterInfo[] args = methods[i].GetParameters();
                        if (args.Length == 1 && args[0].ParameterType == field.FieldType) { conversion = methods[i]; break; }
                    }
                    if (conversion == null) throw new InvalidOperationException("Drone weapon ID has no int conversion");
                    il.Emit(OpCodes.Call, conversion);
                }
            }
            else if (!result.IsAssignableFrom(field.FieldType))
                throw new InvalidOperationException("Drone native field type mismatch: " + field.Name);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(kind);
        }

        static Delegate Call(MethodInfo target, Type kind)
        {
            MethodInfo invoke = kind.GetMethod("Invoke");
            ParameterInfo[] parameters = invoke.GetParameters();
            Type[] types = new Type[parameters.Length];
            for (int i = 0; i < types.Length; i++) types[i] = parameters[i].ParameterType;
            DynamicMethod method = new DynamicMethod("DroneCall_" + target.Name, invoke.ReturnType,
                types, typeof(DroneNpcEngagement), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, target.DeclaringType);
            ParameterInfo[] args = target.GetParameters();
            if (args.Length != types.Length - 1) throw new InvalidOperationException("Drone native method signature mismatch");
            for (int i = 0; i < args.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, (short)(i + 1));
                if (types[i + 1] == typeof(object)) il.Emit(OpCodes.Castclass, args[i].ParameterType);
            }
            il.Emit(OpCodes.Callvirt, target);
            // Original reflection callers discard any FireTo/reload result.
            if (invoke.ReturnType == typeof(void) && target.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
            else if (invoke.ReturnType != target.ReturnType) throw new InvalidOperationException("Drone native method return mismatch");
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(kind);
        }
    }
}
