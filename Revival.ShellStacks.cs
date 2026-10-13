// Shell stacks use native ammo quantities and RPCs. No frame hook.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace NextDayRevival
{
    internal static class ShellStacks
    {
        internal const int Maximum = 28;
        internal const float RoundWeight = 9f;

        internal static float Weight(float original, int id, int rounds)
        { return id == TowerDeliveryCore.ShellId ? RoundWeight * Math.Max(1, rounds) : original; }

        // Manual gun supply is an interaction, not an Update path. Vanilla
        // RemoveItem removes whole slots, so consume one round before syncing.
        internal static bool TakeRound(object inventory)
        {
            object data = AccessTools.Field(inventory.GetType(), "_backpackData").GetValue(inventory);
            Array ids = AccessTools.Field(data.GetType(), "ItemID").GetValue(data) as Array;
            Array rounds = AccessTools.Field(data.GetType(), "ItemBullets").GetValue(data) as Array;
            MethodInfo readId = Cast(ids.GetType().GetElementType(), typeof(int));
            MethodInfo readRounds = Cast(rounds.GetType().GetElementType(), typeof(int));
            for (int i = 0; i < ids.Length; i++)
            {
                if ((int)readId.Invoke(null, new object[] { ids.GetValue(i) }) != TowerDeliveryCore.ShellId) continue;
                int count = (int)readRounds.Invoke(null, new object[] { rounds.GetValue(i) });
                if (count <= 1)
                    AccessTools.Method(inventory.GetType(), "RemoveItem", new Type[] { typeof(int), typeof(int) }, null)
                        .Invoke(inventory, new object[] { TowerDeliveryCore.ShellId, 1 });
                else
                {
                    rounds.SetValue(Cast(typeof(int), rounds.GetType().GetElementType()).Invoke(null, new object[] { count - 1 }), i);
                    AccessTools.Method(inventory.GetType(), "OnChangedInventoryData", new Type[] { typeof(bool) }, null)
                        .Invoke(inventory, new object[] { true });
                }
                return true;
            }
            return false;
        }

        internal static void Install(Harmony harmony)
        {
            Type container = RevivalPlugin.TypeByName("ItemsContainer");
            if (container == null) return;
            int patched = 0;
            // Discover only methods reading native slot weights. This happens
            // once at startup, and covers backpack, storage, containers and UI.
            foreach (Type type in container.Assembly.GetTypes())
            {
                string name = type.Name;
                if (name != "ItemsContainer" && name != "PlayerInventoryManager"
                    && name != "PlayerInventoryUISystem" && name != "PlayerCraftingSystem"
                    && name != "StorageObject" && name != "VehicleInventoryManager"
                    && name != "HUD_MarketplaceUI" && name != "QuestManager"
                    && name != "<CheckNotPreloadedItems>c__Iterator1"
                    && name != "<ConvertContainerDataToBackpackData>c__Iterator0") continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.ContainsGenericParameters || method.IsAbstract) continue;
                    MethodBody body = method.GetMethodBody();
                    if (body == null) continue;
                    byte[] il = body.GetILAsByteArray();
                    bool found = false;
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x7b) continue; // ldfld
                        FieldInfo field;
                        try { field = method.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); }
                        catch { continue; }
                        if (SlotWeight(field)) { found = true; break; }
                    }
                    if (!found) continue;
                    harmony.Patch(method, null, null, new HarmonyMethod(
                        typeof(ShellStacks).GetMethod("Weights", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                    patched++;
                }
            }
            RevivalPlugin.L.LogInfo("Shell stack weight readers patched: " + patched);
        }

        static bool SlotWeight(FieldInfo field)
        {
            return field != null && field.Name == "ItemWeight" && field.FieldType.IsArray
                && AccessTools.Field(field.DeclaringType, "ItemID") != null
                && AccessTools.Field(field.DeclaringType, "ItemBullets") != null;
        }

        static MethodInfo Cast(Type from, Type to)
        {
            foreach (MethodInfo method in from.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                ParameterInfo[] p = method.GetParameters();
                if (method.Name == "op_Implicit" && method.ReturnType == to
                    && p.Length == 1 && p[0].ParameterType == from) return method;
            }
            foreach (MethodInfo method in to.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                ParameterInfo[] p = method.GetParameters();
                if (method.Name == "op_Implicit" && method.ReturnType == to
                    && p.Length == 1 && p[0].ParameterType == from) return method;
            }
            throw new InvalidOperationException("Shell stack native numeric conversion missing");
        }

        static IEnumerable<CodeInstruction> Weights(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(input);
            for (int i = 0; i + 3 < code.Count; i++)
            {
                FieldInfo weight = code[i].operand as FieldInfo;
                if (code[i].opcode != OpCodes.Ldfld || !SlotWeight(weight)
                    || code[i + 2].opcode != OpCodes.Ldelema || code[i + 3].opcode != OpCodes.Ldobj) continue;
                // Native slot reads use one ldloc/ldarg for the array index.
                string indexOp = code[i + 1].opcode.Name;
                if (!indexOp.StartsWith("ldloc") && !indexOp.StartsWith("ldarg")) continue;
                FieldInfo ids = AccessTools.Field(weight.DeclaringType, "ItemID");
                FieldInfo bullets = AccessTools.Field(weight.DeclaringType, "ItemBullets");
                Type number = weight.FieldType.GetElementType();
                LocalBuilder data = generator.DeclareLocal(weight.DeclaringType);
                List<CodeInstruction> replacement = new List<CodeInstruction>();
                CodeInstruction first = new CodeInstruction(OpCodes.Stloc, data);
                first.labels.AddRange(code[i].labels);
                first.blocks.AddRange(code[i].blocks);
                replacement.Add(first);
                replacement.Add(new CodeInstruction(OpCodes.Ldloc, data));
                replacement.Add(new CodeInstruction(OpCodes.Ldfld, weight));
                replacement.Add(new CodeInstruction(code[i + 1].opcode, code[i + 1].operand));
                replacement.Add(new CodeInstruction(OpCodes.Ldelema, number));
                replacement.Add(new CodeInstruction(OpCodes.Ldobj, number));
                replacement.Add(new CodeInstruction(OpCodes.Call, Cast(number, typeof(float))));
                foreach (FieldInfo field in new FieldInfo[] { ids, bullets })
                {
                    Type element = field.FieldType.GetElementType();
                    replacement.Add(new CodeInstruction(OpCodes.Ldloc, data));
                    replacement.Add(new CodeInstruction(OpCodes.Ldfld, field));
                    replacement.Add(new CodeInstruction(code[i + 1].opcode, code[i + 1].operand));
                    replacement.Add(new CodeInstruction(OpCodes.Ldelema, element));
                    replacement.Add(new CodeInstruction(OpCodes.Ldobj, element));
                    replacement.Add(new CodeInstruction(OpCodes.Call, Cast(element, typeof(int))));
                }
                replacement.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShellStacks), "Weight", null, null)));
                replacement.Add(new CodeInstruction(OpCodes.Call, Cast(typeof(float), number)));
                code.RemoveRange(i, 4);
                code.InsertRange(i, replacement);
                i += replacement.Count - 1;
            }
            return code;
        }
    }
}
