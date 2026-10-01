// Native explosions use the same position registry and W health-owner path
// as mod bombs. Replace only person selection; retain vehicle/animal damage,
// forces, visuals and the existing explosion observers.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class NativeBlast
    {
        internal static bool Ready;
        static FieldInfo _damage, _radius, _damaged;
        static MethodInfo _getView;
        static PropertyInfo _mine;

        internal static void Install(Harmony harmony)
        {
            try
            {
                if (!OrdnanceBlast.Ready) throw new Exception("health-owner queue unavailable");
                Type explosion = RevivalPlugin.TypeByName("ExplosionObject");
                Type view = RevivalPlugin.TypeByName("PhotonView");
                _damage = AccessTools.Field(explosion, "ExplosionDamage");
                _radius = AccessTools.Field(explosion, "ExplodeDamageRadius");
                _damaged = AccessTools.Field(explosion, "damagedPlayers");
                _mine = view.GetProperty("isMine");
                _getView = AccessTools.Method(RevivalPlugin.TypeByName("Extensions"), "GetPhotonView",
                    new Type[] { typeof(GameObject) }, null);
                MethodInfo effect = AccessTools.Method(explosion, "ExplosionPhysicsEffect", Type.EmptyTypes, null);
                if (effect == null || _damage == null || _radius == null || _mine == null || _getView == null
                    || _damaged == null || !typeof(IList).IsAssignableFrom(_damaged.FieldType))
                    throw new Exception("native explosion bindings incomplete");
                harmony.Patch(effect, new HarmonyMethod(typeof(NativeBlast).GetMethod("BeforePhysics")),
                    null, new HarmonyMethod(typeof(NativeBlast).GetMethod("ReplacePersonTag")), null, null);
                Ready = true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("NativeBlast: " + ex.Message); }
        }

        public static void BeforePhysics(Component __instance)
        {
            if (!Ready) return;
            object view = _getView.Invoke(null, new object[] { __instance.gameObject });
            if (view == null || !(bool)_mine.GetValue(view, null)) return;
            float damage = (float)_damage.GetValue(__instance);
            float radius = (float)_radius.GetValue(__instance);
            if (damage <= 0f || radius <= 0f) return;
            // The native per-explosion dedup list owns this one-shot stamp.
            // Its own GameObject can never be a person/animal victim. This
            // avoids a second registry pass if Explode is called twice, with
            // no global object retention, cleanup tick or marker component.
            IList damaged = (IList)_damaged.GetValue(__instance);
            if (damaged.Contains(__instance.gameObject)) return;
            damaged.Add(__instance.gameObject);
            // Zero-damage bomb pictures never create a second person job.
            OrdnanceBlast.EnqueuePeople(__instance.transform.position, radius, damage, true);
        }

        public static IEnumerable<CodeInstruction> ReplacePersonTag(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            MethodInfo compare = typeof(Component).GetMethod("CompareTag", new Type[] { typeof(string) });
            MethodInfo replacement = typeof(NativeBlast).GetMethod("PersonTag");
            int matches = 0;
            for (int i = 1; i < code.Count; i++)
            {
                if (code[i - 1].opcode != OpCodes.Ldstr || (string)code[i - 1].operand != "RagdollBone"
                    || !Equals(code[i].operand, compare)) continue;
                // Same stack signature (Component, string) -> bool. Preserve
                // labels and exception blocks by editing the instruction in place.
                code[i].opcode = OpCodes.Call; code[i].operand = replacement; matches++;
            }
            if (matches != 1) throw new Exception("expected one RagdollBone explosion branch, found " + matches);
            return code;
        }

        public static bool PersonTag(Component collider, string tag)
        {
            // Both NPC and player health are queued once per registry victim;
            // their native bone branch must not also apply collider damage.
            return !Ready && collider.CompareTag(tag);
        }
    }
}
