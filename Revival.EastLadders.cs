// Next Day: Survival - Revival Toolkit
//
// EAST LADDERS: makes the ladders of the east content scenes climbable with
// the game's own ladder (Assembly-CSharp LadderObject). Acts only with
// [World] EastTile (EastWorld.On).
//
// How the game climbs (PlayerInteractingManager.SearchGameplayItems, an
// 11 u interaction ray): a collider tagged "Ladder" whose object carries a
// LadderObject starts PlayerMovementController.PlayerLadderClimb(ladder, up).
// A collider named "ClimbDownPoint" (also tagged "Ladder", a child of the
// ladder) climbs down. The climb is an animation plus teleports: to
// startPoint, then beupPoint (the top of the ladder), then the climb-over
// animation and endPoint (standing on top). The vanilla ladders
// (Ladder_1st/2st/4st_01 in level4/5/7) are built exactly so, on layer 17.
//
// A content bundle cannot carry the game's MonoBehaviour, so the building
// kit (unity/EastTile/Tools/mt_service.py, the boiler chimney B1c) exports a
// plain group
//
//   .../Ladders/<id>/Ladder          BoxCollider over the ladder
//   .../Ladders/<id>/ClimbDownPoint  BoxCollider at the top exit
//   .../Ladders/<id>/StartPoint      empty: foot of the ladder
//   .../Ladders/<id>/BeupPoint       empty: top of the climb, on the ladder
//   .../Ladders/<id>/EndPoint        empty: where the player stands after
//
// and this module, whenever the set of loaded scenes changes, wires every
// such group once: tag, layer, hierarchy as the vanilla prefab, the points
// turned to face the ladder, and a LadderObject with the three points.
// docs/ai/tasks/military-town-service.md section 2.
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class EastLadders
    {
        const int LadderLayer = 17;            // the vanilla ladders' layer
        static int _sceneCount = -1;
        static float _next;

        static void Log(string s) { RevivalPlugin.L.LogInfo("EastLadders: " + s); }

        internal static void Tick()
        {
            if (!EastWorld.On) return;
            try
            {
                // Q1 perf: the search for "Ladders" groups is a time-sliced
                // sweep; it used to read the name of every transform of every
                // east scene in one frame on each scene change.
                if (_sweep.Active) { _sweep.Step(1.5, _visit); return; }
                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 2f;
                if (SceneManager.sceneCount == _sceneCount) return;
                _sceneCount = SceneManager.sceneCount;
                _sweep.Begin("East");
                _sweep.Step(1.5, _visit);
            }
            catch (Exception ex) { _sweep.Cancel(); Log("scan failed: " + ex.Message); }
        }

        static readonly SceneSweep _sweep = new SceneSweep();
        static readonly SceneSweep.Visitor _visit = ScanVisit;

        static bool ScanVisit(Transform t, string name)
        {
            if (name != "Ladders") return true;
            string scene = t.gameObject.scene.name;
            for (int k = 0; k < t.childCount; k++) Wire(scene, t.GetChild(k));
            return true;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
        }

        static void Wire(string scene, Transform g)
        {
            Transform lad = g.Find("Ladder");
            if (lad == null)
            {
                // already wired: the ladder was re-parented under itself
                return;
            }
            Transform down = g.Find("ClimbDownPoint"), st = g.Find("StartPoint"),
                      be = g.Find("BeupPoint"), en = g.Find("EndPoint");
            if (down == null || st == null || be == null || en == null)
            {
                Log(scene + "/" + g.name + ": incomplete group (needs Ladder, ClimbDownPoint, StartPoint, BeupPoint, EndPoint).");
                return;
            }
            Type lt = Type.GetType("LadderObject, Assembly-CSharp");
            if (lt == null) { Log("LadderObject not found in Assembly-CSharp."); return; }
            if (lad.GetComponent<Collider>() == null || down.GetComponent<Collider>() == null)
            {
                Log(scene + "/" + g.name + ": Ladder or ClimbDownPoint has no collider.");
                return;
            }
            // The vanilla prefab: points under the ladder, the top points under ClimbDownPoint.
            down.SetParent(lad, true);
            st.SetParent(lad, true);
            be.SetParent(down, true);
            en.SetParent(down, true);
            st.rotation = Quaternion.LookRotation(Flat(lad.position - st.position));
            be.rotation = Quaternion.LookRotation(Flat(lad.position - be.position));
            en.rotation = Quaternion.LookRotation(Flat(en.position - be.position));
            lad.name = "Ladder_" + g.name;          // wired: Find("Ladder") no longer matches
            foreach (Transform x in new Transform[] { lad, down })
            {
                x.gameObject.layer = LadderLayer;
                x.gameObject.tag = "Ladder";
            }
            Component lo = lad.gameObject.AddComponent(lt);
            Set(lo, "startPoint", st);
            Set(lo, "beupPoint", be);
            Set(lo, "endPoint", en);
            // 0 = the four-storey climb animation, the longest the game has
            FieldInfo f = lt.GetField("ladderType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType.IsEnum) f.SetValue(lo, Enum.ToObject(f.FieldType, 0));
            Log(scene + "/" + g.name + ": ladder wired, climb " + (be.position.y - st.position.y).ToString("F1")
                + " u, top " + en.position.ToString("F1") + ".");
        }

        static void Set(Component c, string field, Transform t)
        {
            FieldInfo f = c.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) { Log("LadderObject has no field " + field + "."); return; }
            f.SetValue(c, t);
        }
    }
}
