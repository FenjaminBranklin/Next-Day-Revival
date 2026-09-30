// W merc core fix (docs/ai/tasks/w-merc-core-fix.md): the mercs of my group
// mates on my map, like squad members. The owner's own client draws his mercs
// as green numbered squares (B3b, MercUi.DrawMap); every member of his game
// group (PlayerGroupManager) sees them as blue squares with the owner's name.
//
// Nothing new goes over the network: the mercs are the owner's own Photon
// NPCs, so every client already has them and their positions. The spawn key
// "merc/<owner actor>/<id>" (Crew.GroundKey) names the owner; the group is
// the game's own - the local PlayerGroupManager's currentGroupPlayers plus
// any player whose GroupID equals mine.
//
// Cost: only while the map is open (GameUi.State 8), one scan per second over
// the shared NpcScan / PlayerScan arrays. Each NPC's key is read once
// (cached by instance id), each owner's label built once. The per-second scan
// boxes the two GroupID reads (reflection) and nothing else; the draw reads
// the cached list. Runs inside the F6 slot Mercs.Tick.
// C# 3.0, ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercMates
    {
        internal struct Seen
        {
            internal Component Ai;
            internal string Label;
        }

        /// <summary>My group mates' living mercs (only while the map is open).</summary>
        internal static readonly List<Seen> List = new List<Seen>();

        const int KeyReads = 64;
        static float _next, _nextFlush;
        static bool _looked, _warned;
        static Type _pgmType;
        static FieldInfo _fGroupId, _fPlayers;
        static readonly List<int> _mates = new List<int>();
        static readonly Dictionary<int, int> _ownerOf = new Dictionary<int, int>();      // NPC id -> owner actor, -1 not a merc
        static readonly Dictionary<int, Component> _pgmOf = new Dictionary<int, Component>(); // player go id -> PlayerGroupManager
        static readonly Dictionary<int, string> _labelOf = new Dictionary<int, string>(); // owner actor -> map label

        internal static void Tick(float now)
        {
            if (GameUi.State != 8) { if (List.Count > 0) List.Clear(); return; }
            if (now < _next) return;
            _next = now + 1f;
            if (now >= _nextFlush)
            {
                // Players come and go, names change: the per-player caches are
                // rebuilt now and then; the NPC key cache only when it is big.
                _nextFlush = now + 30f;
                _pgmOf.Clear(); _labelOf.Clear();
                if (_ownerOf.Count > 4096) _ownerOf.Clear();
            }
            try { Scan(); }
            catch (Exception ex)
            {
                List.Clear();
                _next = now + 10f;
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("Mercs: group mates' mercs - " + ex.Message); }
            }
        }

        static bool Look()
        {
            if (_looked) return _fGroupId != null;
            _looked = true;
            _pgmType = RevivalPlugin.TypeByName("PlayerGroupManager");
            if (_pgmType == null) return false;
            _fGroupId = AccessTools.Field(_pgmType, "GroupID");
            _fPlayers = AccessTools.Field(_pgmType, "currentGroupPlayers");
            if (_fGroupId == null)
                RevivalPlugin.L.LogWarning("Mercs: PlayerGroupManager.GroupID not found - mates' mercs stay off the map.");
            return _fGroupId != null;
        }

        static Component Pgm(GameObject go)
        {
            int id = go.GetInstanceID();
            Component c;
            if (_pgmOf.TryGetValue(id, out c)) return c;
            c = go.GetComponent(_pgmType);
            if (c == null) c = go.GetComponentInChildren(_pgmType);
            if (c == null) c = go.GetComponentInParent(_pgmType);
            _pgmOf[id] = c;
            return c;
        }

        static int GroupOf(Component pgm)
        {
            object v = pgm == null ? null : _fGroupId.GetValue(pgm);
            return v is int ? (int)v : 0;
        }

        static void AddMate(GameObject go, int mine)
        {
            if (go == null) return;
            int a = Mercs.ActorOf(go);
            if (a >= 0 && a != mine && !_mates.Contains(a)) _mates.Add(a);
        }

        static void Scan()
        {
            List.Clear();
            _mates.Clear();
            GameObject me = MapTools.LocalPlayer();
            if (me == null || !Look()) return;
            Component mine = Pgm(me);
            int group = GroupOf(mine);
            if (group == 0) return;
            int self = Mercs.LocalActor;
            IList members = _fPlayers == null ? null : _fPlayers.GetValue(mine) as IList;
            if (members != null)
                for (int i = 0; i < members.Count; i++)
                {
                    object o = members[i];
                    GameObject go = o as GameObject;
                    if (go == null) { Component c = o as Component; if (c != null) go = c.gameObject; }
                    AddMate(go, self);
                }
            Component[] players = PlayerScan.All();
            for (int i = 0; i < players.Length; i++)
            {
                Component p = players[i];
                if (p == null) continue;
                GameObject go = p.gameObject;
                if (go == me) continue;
                Component other = Pgm(go);
                if (other != null && GroupOf(other) == group) AddMate(go, self);
            }
            if (_mates.Count == 0) return;
            Component[] npcs = NpcScan.All();
            int reads = 0;
            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs[i];
                if (ai == null) continue;
                int id = ai.GetInstanceID(), owner;
                if (!_ownerOf.TryGetValue(id, out owner))
                {
                    // The spawn key is a reflection read: at most KeyReads new
                    // NPCs a scan, the rest on the next one (no spike on the
                    // first map open in a crowded town).
                    if (reads >= KeyReads) continue;
                    reads++;
                    owner = OwnerFromKey(Crew.GroundKey(ai));
                    _ownerOf[id] = owner;
                }
                if (owner < 0 || !_mates.Contains(owner) || !NpcWar.MercAlive(ai)) continue;
                Seen s;
                s.Ai = ai;
                s.Label = LabelOf(owner);
                List.Add(s);
            }
        }

        /// <summary>"merc/7/12" -> 7; anything else -1.</summary>
        internal static int OwnerFromKey(string key)
        {
            if (key == null || !key.StartsWith(Mercs.KeyPrefix, StringComparison.Ordinal)) return -1;
            int start = Mercs.KeyPrefix.Length, actor = 0, i = start;
            for (; i < key.Length && key[i] >= '0' && key[i] <= '9'; i++) actor = actor * 10 + (key[i] - '0');
            return i > start && i < key.Length && key[i] == '/' ? actor : -1;
        }

        static string LabelOf(int owner)
        {
            string label;
            if (_labelOf.TryGetValue(owner, out label)) return label;
            GameObject go = Mercs.PlayerByActor(owner);
            string name = go == null ? null : Mercs.Clean(Mercs.NameOf(go));
            if (string.IsNullOrEmpty(name)) name = "#" + owner;
            if (name.Length > 12) name = name.Substring(0, 12);
            label = name;
            _labelOf[owner] = label;
            return label;
        }
    }
}
