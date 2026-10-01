// Y P2: retained combat objects and their native engine cost, without scene scans.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static class CombatLoad
    {
        const float K = 2.8f;
        const int NpcSlots = 512, Slice = 12;
        sealed class Record
        {
            internal GameObject Go;
            internal Component Ai, View;
            internal NavMeshAgent Agent;
            internal int Id, Slot = -1, Quiet;
            internal float Born, DeadAt = -1f, Next;
            internal bool Frozen, Hidden, PendingRemoval, EffectsStopped;
            internal Animation[] Anim;
            internal Animator[] Mecanim;
            internal Renderer[] Render;
            internal Rigidbody[] Bodies;
            internal Collider[] Wheels;
            internal Behaviour[] Behaviours;
            internal AudioSource[] Audio;
            internal ParticleSystem[] Particles;
            internal bool[] AnimOn, MecanimOn, RenderOn;

            internal Record(GameObject go, Component ai)
            {
                Go = go; Ai = ai; Id = go.GetInstanceID(); Born = Time.unscaledTime;
                View = _viewType == null ? null : go.GetComponent(_viewType);
                if (ai != null)
                {
                    Agent = go.GetComponent<NavMeshAgent>();
                    if (Agent == null) Agent = go.GetComponentInChildren<NavMeshAgent>();
                }
                // Cold, once per spawned object. Never queried in the frame loop.
                Anim = go.GetComponentsInChildren<Animation>(true);
                Mecanim = go.GetComponentsInChildren<Animator>(true);
                Render = go.GetComponentsInChildren<Renderer>(true);
                Bodies = go.GetComponentsInChildren<Rigidbody>(true);
                Wheels = go.GetComponentsInChildren<Collider>(true);
                Behaviours = go.GetComponentsInChildren<Behaviour>(true);
                Audio = go.GetComponentsInChildren<AudioSource>(true);
                Particles = go.GetComponentsInChildren<ParticleSystem>(true);
                AnimOn = new bool[Anim.Length]; MecanimOn = new bool[Mecanim.Length];
                RenderOn = new bool[Render.Length];
            }
        }

        static readonly Record[] _npcs = new Record[NpcSlots];
        static readonly Dictionary<int, Record> _known = new Dictionary<int, Record>(NpcSlots);
        static readonly CombatLeaseBook _corpses = new CombatLeaseBook(CombatLoadPolicy.CorpseCap);
        static readonly CombatLeaseBook _wrecks = new CombatLeaseBook(CombatLoadPolicy.WreckCap);
        static readonly CombatLeaseBook _debris = new CombatLeaseBook(CombatLoadPolicy.DebrisCap);
        static readonly Record[] _dead = new Record[CombatLoadPolicy.CorpseCap];
        static readonly Record[] _wreck = new Record[CombatLoadPolicy.WreckCap];
        static readonly Record[] _scrap = new Record[CombatLoadPolicy.DebrisCap];
        static readonly Record[] _remoteWreck = new Record[CombatLoadPolicy.WreckCap];
        static readonly Vector3[] _players = new Vector3[64];
        static int _playerCount, _npcCursor, _leaseCursor, _removed, _remoteCursor;
        static Vector3 _eye;
        static bool _haveEye, _looked, _warned;
        static float _nextView;
        static Type _viewType, _rccType;
        static MethodInfo _alive, _mine;
        static Action<GameObject> _destroy;

        static void Look()
        {
            if (_looked) return;
            _looked = true;
            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            _alive = npc == null ? null : AccessTools.Method(npc, "IsAlive", null, null);
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            _rccType = RevivalPlugin.TypeByName("RCCCarControllerV2");
            _mine = _viewType == null ? null : AccessTools.PropertyGetter(_viewType, "isMine");
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            MethodInfo remove = photon == null ? null : AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
            if (remove != null) _destroy = (Action<GameObject>)Delegate.CreateDelegate(typeof(Action<GameObject>), remove);
        }

        // Called from the existing native Start hook only after the replicated
        // Crew spawn marker has been validated. Vanilla NPCs/players are excluded.
        internal static void RegisterNpc(Component ai)
        {
            if (ai == null) return;
            Look();
            int id = ai.gameObject.GetInstanceID();
            if (_known.ContainsKey(id)) return;
            for (int i = 0; i < _npcs.Length; i++)
            {
                if (_npcs[i] != null && _npcs[i].Go != null) continue;
                if (_npcs[i] != null) _known.Remove(_npcs[i].Id);
                Record r = new Record(ai.gameObject, ai);
                PhysicsDiet.RegisterNpc(ai, r.Bodies);
                _npcs[i] = r; _known[id] = r;
                return;
            }
            Warn("NPC tracker full; event admission must remain bounded");
        }

        internal static void Wreck(GameObject go)
        {
            if (go == null) return;
            Look();
            // Local presentations may be registered on a joined client, but
            // only the Photon owner is allowed to destroy a networked root.
            Retain(_wrecks, _wreck, go, CombatLoadPolicy.WreckSeconds);
        }

        internal static void Debris(GameObject go, float seconds)
        {
            if (go == null) return;
            Look();
            Retain(_debris, _scrap, go, Mathf.Min(seconds, 120f));
        }

        static void Retain(CombatLeaseBook book, Record[] rows, GameObject go, float seconds)
        {
            int id = go.GetInstanceID();
            for (int i = 0; i < rows.Length; i++) if (rows[i] != null && rows[i].Id == id) return;
            Record fresh = new Record(go, null);
            PhysicsDiet.Track(fresh.Bodies, book == _wrecks ? 5 : 6);
            fresh.DeadAt = Time.unscaledTime;
            if (fresh.View != null && !Mine(fresh))
            {
                // Replicas cannot evict the owner's authoritative lease. Their
                // physics/presentation still rest locally; the owner removes
                // the root for everyone. Ownership transfer is checked in Tick.
                for (int i = 0; i < _remoteWreck.Length; i++)
                    if (_remoteWreck[i] != null && _remoteWreck[i].Id == id) return;
                int remote = _remoteCursor;
                _remoteCursor = (_remoteCursor + 1) % _remoteWreck.Length;
                if (_remoteWreck[remote] != null && _remoteWreck[remote].Go != null) StopEffects(_remoteWreck[remote]);
                _remoteWreck[remote] = fresh;
                return;
            }
            int target = book.SlotFor(id);
            Record old = rows[target];
            if (old != null && old.Go != null && !Retire(old))
            {
                // Keep the failed victim's lease for retry, rather than losing
                // its only cleanup hook while accepting another wreck.
                Retire(fresh); StopEffects(fresh);
                Warn("wreck admission refused while a network retirement is pending");
                return;
            }
            int victim;
            int slot = book.Put(id, Time.unscaledTime, seconds, out victim);
            rows[slot] = fresh;
        }

        // Cached positions, in world units. Camera rather than body also covers
        // drones, guns and aircraft. Fail open before a viewer is available.
        internal static bool LocalNear(Vector3 at, float metres)
        {
            return !_haveEye || (at - _eye).sqrMagnitude <= metres * metres * K * K;
        }

        internal static bool AnyNear(Vector3 at, float metres)
        {
            float sq = metres * metres * K * K;
            if (LocalNear(at, metres)) return true;
            for (int i = 0; i < _playerCount; i++) if ((_players[i] - at).sqrMagnitude <= sq) return true;
            return false;
        }

        internal static NavMeshAgent Agent(Component ai)
        {
            Record r;
            return ai != null && _known.TryGetValue(ai.gameObject.GetInstanceID(), out r) ? r.Agent : null;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            Crew.PruneRoots();
            if (now >= _nextView)
            {
                _nextView = now + 0.5f;
                Camera c = CameraOwner.ViewCamera();
                _haveEye = c != null;
                if (_haveEye) _eye = c.transform.position;
                Component[] players = PlayerScan.All();
                _playerCount = 0;
                for (int i = 0; i < players.Length && _playerCount < _players.Length; i++)
                    if (players[i] != null) _players[_playerCount++] = players[i].transform.position;
            }
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 12500; // 0.08 ms cooperative slice
            for (int n = 0; n < Slice; n++)
            {
                int i = _npcCursor++;
                if (_npcCursor == _npcs.Length) _npcCursor = 0;
                Record r = _npcs[i];
                if (r != null)
                {
                    if (r.Go == null) { _known.Remove(r.Id); _npcs[i] = null; }
                    else if (now >= r.Next) { r.Next = now + 0.5f; Npc(r, now); }
                }
                // One lease per iteration; all TTL work is bounded, too.
                int lease = _leaseCursor++;
                int all = _dead.Length + _wreck.Length + _scrap.Length + _remoteWreck.Length;
                if (_leaseCursor == all) _leaseCursor = 0;
                if (lease < _dead.Length) Lease(_corpses, _dead, lease, now);
                else if (lease < _dead.Length + _wreck.Length) Lease(_wrecks, _wreck, lease - _dead.Length, now);
                else if (lease < all - _remoteWreck.Length) Lease(_debris, _scrap, lease - _dead.Length - _wreck.Length, now);
                else RemoteWreck(lease - all + _remoteWreck.Length, now);
                if (Stopwatch.GetTimestamp() >= until) break;
            }
            FireEffect.TickBlasts();
        }

        static void RemoteWreck(int slot, float now)
        {
            Record r = _remoteWreck[slot];
            if (r == null) return;
            if (r.Go == null) { _remoteWreck[slot] = null; return; }
            if (Mine(r))
            {
                Retain(_wrecks, _wreck, r.Go, Mathf.Max(0f, CombatLoadPolicy.WreckSeconds - (now - r.Born)));
                _remoteWreck[slot] = null;
                return;
            }
            if (now < r.Next) return;
            r.Next = now + 0.5f;
            Rest(r, now, false);
            if (now - r.DeadAt >= 45f || !LocalNear(r.Go.transform.position, 1200f)) StopEffects(r);
        }

        static void Npc(Record r, float now)
        {
            if (r.PendingRemoval) { Retire(r); return; }
            if (_alive == null || r.Ai == null) return; // do not guess death
            if (r.DeadAt < 0f && !FastCall.Bool(_alive, r.Ai)) r.DeadAt = now;
            if (r.DeadAt < 0f)
            {
                // Pause posing without Animation.Stop (which produces a bind
                // pose). Hit colliders and AI authority are never switched off.
                SetHidden(r, !LocalNear(r.Go.transform.position, r.Hidden ? 900f : 1000f));
                return;
            }
            if (r.Slot < 0 && Mine(r))
            {
                int victim;
                int slot = _corpses.Put(r.Id, r.DeadAt, CombatLoadPolicy.CorpseSeconds, out victim);
                Record old = _dead[slot];
                if (victim != 0 && old != null) { old.PendingRemoval = true; old.Slot = -1; Retire(old); }
                _dead[slot] = r; r.Slot = slot;
            }
            SetHidden(r, !LocalNear(r.Go.transform.position, r.Hidden ? 900f : 1000f));
            Rest(r, now, true);
            if (now - r.DeadAt >= 30f) StopEffects(r);
        }

        static void Lease(CombatLeaseBook book, Record[] rows, int slot, float now)
        {
            Record r = rows[slot];
            if (r == null) return;
            if (r.Go == null) { book.Drop(slot); rows[slot] = null; return; }
            if (book.Due(slot, now))
            {
                if (Retire(r)) { book.Drop(slot); rows[slot] = null; }
                // Failed network removal stays tracked and retries next slice.
                return;
            }
            if (r.Ai == null && now >= r.Next)
            {
                r.Next = now + 0.5f;
                Rest(r, now, false); // never freeze a still-falling aircraft
                if (now - r.DeadAt >= 45f || !LocalNear(r.Go.transform.position, 1200f)) StopEffects(r);
            }
        }

        static void Rest(Record r, float now, bool force)
        {
            if (r.Frozen) return;
            bool quiet = true;
            for (int i = 0; i < r.Bodies.Length; i++)
            {
                Rigidbody b = r.Bodies[i];
                if (b != null && !b.isKinematic && (b.velocity.sqrMagnitude > 0.25f || b.angularVelocity.sqrMagnitude > 0.25f))
                { quiet = false; break; }
            }
            r.Quiet = quiet ? r.Quiet + 1 : 0;
            float age = now - r.DeadAt;
            if (!(force ? CombatLoadPolicy.Rest(age, quiet, r.Quiet) : age >= CombatLoadPolicy.RestSeconds && quiet && r.Quiet >= 2)) return;
            r.Frozen = true;
            for (int i = 0; i < r.Wheels.Length; i++) if (r.Wheels[i] != null && r.Wheels[i].GetType().Name == "WheelCollider") r.Wheels[i].enabled = false;
            if (r.Ai == null && _rccType != null)
                for (int i = 0; i < r.Behaviours.Length; i++)
                    if (r.Behaviours[i] != null && _rccType.IsInstanceOfType(r.Behaviours[i])) r.Behaviours[i].enabled = false;
            for (int i = 0; i < r.Bodies.Length; i++)
            {
                Rigidbody b = r.Bodies[i];
                if (b == null || b.isKinematic) continue;
                b.velocity = Vector3.zero; b.angularVelocity = Vector3.zero;
                b.Sleep(); b.isKinematic = true;
                // Keep detectCollisions/colliders: the corpse remains lootable.
            }
            for (int i = 0; i < r.Anim.Length; i++) if (r.Anim[i] != null) r.Anim[i].enabled = false;
            for (int i = 0; i < r.Mecanim.Length; i++) if (r.Mecanim[i] != null) r.Mecanim[i].enabled = false;
        }

        static void SetHidden(Record r, bool hide)
        {
            if (r.Hidden == hide) return;
            r.Hidden = hide;
            for (int i = 0; i < r.Render.Length; i++)
                if (r.Render[i] != null) { if (hide) r.RenderOn[i] = r.Render[i].enabled; r.Render[i].enabled = !hide && r.RenderOn[i]; }
            for (int i = 0; i < r.Anim.Length; i++)
                if (r.Anim[i] != null) { if (hide) r.AnimOn[i] = r.Anim[i].enabled; r.Anim[i].enabled = !hide && !r.Frozen && r.AnimOn[i]; }
            for (int i = 0; i < r.Mecanim.Length; i++)
                if (r.Mecanim[i] != null) { if (hide) r.MecanimOn[i] = r.Mecanim[i].enabled; r.Mecanim[i].enabled = !hide && !r.Frozen && r.MecanimOn[i]; }
        }

        static void StopEffects(Record r)
        {
            if (r.EffectsStopped) return;
            r.EffectsStopped = true;
            for (int i = 0; i < r.Audio.Length; i++) if (r.Audio[i] != null) { r.Audio[i].Stop(); r.Audio[i].enabled = false; }
            for (int i = 0; i < r.Particles.Length; i++) if (r.Particles[i] != null)
                r.Particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        static bool Mine(Record r) { return r.View != null && _mine != null && FastCall.Bool(_mine, r.View); }

        static bool Retire(Record r)
        {
            if (r.Go == null) return true;
            // No local Destroy fallback for a Photon object owned by somebody
            // else, or when the network API fails. Ownership is read live.
            if (r.View != null && (!Mine(r) || _destroy == null)) return false;
            try
            {
                if (r.View != null) _destroy(r.Go); else UnityEngine.Object.Destroy(r.Go);
                _removed++;
                return true;
            }
            catch (Exception) { Warn("network retirement failed; retained for retry"); return false; }
        }

        static void Warn(string reason)
        {
            if (_warned) return;
            _warned = true;
            RevivalPlugin.L.LogWarning("CombatLoad: " + reason);
        }

        static void Counts(Record[] rows, ref int bodies, ref int colliders, ref int particles, ref int audio, ref int frozen)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                Record r = rows[i];
                if (r == null || r.Go == null) continue;
                // Lazy ragdolls and native destruction leave null slots in the
                // cached arrays. Count existing actors rather than array capacity.
                int live = 0;
                for (int j = 0; j < r.Bodies.Length; j++) if (r.Bodies[j] != null) live++;
                bodies += live; colliders += r.Wheels.Length;
                particles += r.Particles.Length; audio += r.Audio.Length;
                if (r.Frozen) frozen += live;
            }
        }

        static string _line;
        static int _lineBodies, _lineColliders, _lineParticles, _lineAudio, _lineFrozen;
        static int _lineCorpses, _lineWrecks, _lineDebris, _lineRemoved;

        internal static string StatusLine()
        {
            // F6 refresh/log only. Cached component counts for our retained
            // roots, not an allocating scan of every object in the world.
            int bodies = 0, colliders = 0, particles = 0, audio = 0, frozen = 0;
            Counts(_npcs, ref bodies, ref colliders, ref particles, ref audio, ref frozen);
            Counts(_wreck, ref bodies, ref colliders, ref particles, ref audio, ref frozen);
            Counts(_scrap, ref bodies, ref colliders, ref particles, ref audio, ref frozen);
            Counts(_remoteWreck, ref bodies, ref colliders, ref particles, ref audio, ref frozen);
            if (_line != null && bodies == _lineBodies && colliders == _lineColliders
                && particles == _lineParticles && audio == _lineAudio && frozen == _lineFrozen
                && _corpses.Count == _lineCorpses && _wrecks.Count == _lineWrecks
                && _debris.Count == _lineDebris && _removed == _lineRemoved) return _line;
            _lineBodies = bodies; _lineColliders = colliders; _lineParticles = particles;
            _lineAudio = audio; _lineFrozen = frozen; _lineCorpses = _corpses.Count;
            _lineWrecks = _wrecks.Count; _lineDebris = _debris.Count; _lineRemoved = _removed;
            _line = "combat retained: bodies " + _lineCorpses.ToString() + "/64, wrecks " + _lineWrecks.ToString()
                + "/12, debris " + _lineDebris.ToString() + "/32, retired " + _removed.ToString()
                + " | tracked RB " + bodies.ToString() + " (rest " + frozen.ToString() + "), col " + colliders.ToString()
                + ", PS " + particles.ToString() + ", audio " + audio.ToString();
            return _line;
        }

        internal static void Shutdown()
        {
            for (int i = 0; i < _npcs.Length; i++)
                if (_npcs[i] != null && _npcs[i].Go != null && _npcs[i].DeadAt < 0f) SetHidden(_npcs[i], false);
        }
    }
}
