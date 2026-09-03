// Next Day: Survival - Revival Toolkit
//
// The SWAT uniform gear - helmet, body armour, trousers and backpack - as four
// new inventory items. The whole feature, one file, isolated for a clean
// parallel merge exactly like RevivalM7Rifle.cs. build.ps1 compiles every
// top-level *.cs beside RevivalPlugin.cs into the one DLL, so a new item family
// lives here instead of growing the main file. The ONLY seam in
// RevivalPlugin.cs is a single line in BuildItemTable:
//
//     SwatGear.AddItems(Items);
//
// (added right after M7Rifle.AddItems(Items);). Everything else is generic:
// ResourceHook, Registry and the loot tables all iterate RevivalPlugin.Items, so
// adding the four ItemDefs to that list is enough to register, spawn, loot and
// localize them.
//
// DONORS. Each piece clones an existing GAME clothing item - the same ids the
// plugin already equips in EquipGear/UkbGear (so they are known-good, wearable
// gear, not guesses): the UKB helmet 4017 (slot 0), body armour 4316 (slot 2),
// trousers 4509 (slot 6) and the backpack 6019. A clone inherits its donor's
// item category and equip behaviour, so a clone of the helmet IS a helmet that
// drops into the head slot - only the visible mesh, textures and icon are ours.
//
// ART. Real, from assets/src/swat.glb (a posed SWAT operator, imported through
// the glTF path). The helmet is the model's own clean detailed headgear mesh
// (NVG monocle, visor, straps); the body pieces are CARVED out of the single
// posed body mesh by height/depth in swat_build.py - top, bottom and backpack.
// swat_build.py is the generator (make_assets.py GROUPS carries a "swat" entry);
// each piece ships diffuse/normal/metal/rough + a 300px icon.
//
// WORN-ON-BODY MODEL. A clothing item's worn mesh is NOT loaded from Resources
// by id; it is a pre-authored child GameObject baked into the character prefab,
// tagged with a `CheckItemID` (ItemID + ItemIDs[]) or `ItemCustomMaterialsManager`
// list of the item ids it covers. `PlayerInventoryManager.ShowCharacterMesh`
// (the single sink for head/body/legs/backpack, local and remote-RPC alike -
// CONFIRMED from IL, research/ilq.py) walks those children and SetActive()s the
// one whose list contains the equipped id. Our fresh clone ids (4090/4390/4590/
// 6090) are in NO such list, so before the fix equipping a piece activated
// nothing and the body part went COMPLETELY INVISIBLE (the equip-fix that moved
// the ids into the clothing band let the piece equip, but the id still matched no
// worn mesh). SwatWornMeshHook is a Harmony prefix on ShowCharacterMesh that
// remaps our clone id to its DONOR id (WornDonorId) for the itemId argument, so
// the donor's REAL, skeleton-skinned worn mesh (helmet 4017, vest 4316, trousers
// 4509, backpack 6019) activates. Visible, correctly rigged, not cropped.
//
// KNOWN LIMITATION, documented on purpose: the worn body look is therefore the
// donor clothing's model, NOT a black-SWAT skin - the carved swat_*.ndmesh has no
// bone weights and cannot be worn-rendered. A true SWAT worn skin means rigging
// our mesh to the character skeleton, which is separate, larger work. The SWAT
// identity stays on the INVENTORY icon, the ground drop and the item name.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs. Player-facing strings go through Loc.T, whose
// Russian half is real Cyrillic - this file is therefore UTF-8 (no BOM) and is
// compiled with /codepage:65001 like the main file.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The four SWAT gear items. The ids MUST lie in the same numeric band as
    /// their clothing donor, because the game derives an item's equip category
    /// from its id ALONE - not from any per-item data.
    /// `ItemSlotUI.LoadSlotItemCategoryData` calls
    /// `ItemDataManager.GetItemCatData(ItemID)`, which is a hard-coded id-RANGE
    /// switch (CONFIRMED from IL, research/ilq.py): 4000-4100 -> Clothes/Head/
    /// Special, 4300-4400 -> Clothes/Body/Jackets, 4500-4600 -> Clothes/Legs/
    /// Pants, 6000-7000 -> Backpacks; and, fatally, 2000-3000 -> Ammunation.
    /// The first cut of these items used 2065-2068, so the inventory UI read
    /// them as AMMO and `ItemSlotUI.OnDragZoneEquipment` refused to wear them -
    /// the "in the inventory but nothing equips" bug. The one-click admin loadout
    /// still worked because it FORCES the slot via AddGearItemFromValues(slot,..)
    /// instead of asking the category. Fresh ids in the donor's own band make the
    /// clone equip exactly like the donor. All four verified free against
    /// items.tsv and the plugin id space.
    /// </summary>
    public static class SwatGear
    {
        // Ids in the donor's clothing band so GetItemCatData returns the right
        // equip category (see the class summary - this is the equip fix).
        public const int HelmetId   = 4090;   // SWAT helmet    (donor 4017, Head/Special 4000-4100, slot 0)
        public const int ArmourId   = 4390;   // SWAT body armour (donor 4316, Body/Jackets 4300-4400, slot 2)
        public const int TrousersId = 4590;   // SWAT trousers  (donor 4509, Legs/Pants 4500-4600, slot 6)
        public const int BackpackId = 6090;   // SWAT backpack  (donor 6019, Backpacks 6000-7000)

        // The vacated first-cut ids. The gear shipped here (2065-2068, the ammo
        // band) before the equip fix moved it into the donor bands above. Kept
        // only so an old save can be migrated - see MoveLegacyId.
        public const int OldHelmetId   = 2065;
        public const int OldArmourId   = 2066;
        public const int OldTrousersId = 2067;
        public const int OldBackpackId = 2068;

        /// <summary>
        /// Map a pre-equip-fix inventory id to its current donor-band id. A
        /// character saved while the gear still lived at 2065-2068 carries those
        /// ids; the game can no longer resolve them (they spawn null and the
        /// piece is dropped), so the backpack-load prefix rewrites them through
        /// this. Returns everything else unchanged. The ids are provably dead
        /// (they resolve to nothing now), so nothing else is caught.
        /// </summary>
        public static int MoveLegacyId(int id)
        {
            switch (id)
            {
                case OldHelmetId:   return HelmetId;    // 2065 -> 4090
                case OldArmourId:   return ArmourId;    // 2066 -> 4390
                case OldTrousersId: return TrousersId;  // 2067 -> 4590
                case OldBackpackId: return BackpackId;  // 2068 -> 6090
                default:            return id;
            }
        }

        // The clothing donor each clone borrows. These are the same second
        // argument passed to ItemDef below (the "Spende"), pulled out as named
        // constants so the worn-mesh hook can reach them without re-deriving.
        public const int HelmetDonor   = 4017;   // UKB helmet    (Head/Special)
        public const int ArmourDonor   = 4316;   // UKB body armour (Body/Jackets)
        public const int TrousersDonor = 4509;   // UKB trousers  (Legs/Pants)
        public const int BackpackDonor = 6019;   // UKB backpack  (Backpacks)

        /// <summary>
        /// Map a SWAT clone id to the DONOR clothing id whose worn character mesh
        /// should show on the body. Everything else passes through unchanged, so
        /// this is safe to call on every id ShowCharacterMesh sees.
        ///
        /// WHY: a clothing item's worn mesh is a pre-authored child of the
        /// character prefab, tagged (CheckItemID / ItemCustomMaterialsManager)
        /// with the ids it covers. Our fresh clone ids are in no such list, so
        /// without this remap the equipped piece activates NO worn mesh and the
        /// body part is invisible. Remapping to the donor id activates the donor's
        /// real, skeleton-skinned worn mesh. See SwatWornMeshHook.
        /// </summary>
        public static int WornDonorId(int id)
        {
            switch (id)
            {
                case HelmetId:   return HelmetDonor;    // 4090 -> 4017
                case ArmourId:   return ArmourDonor;    // 4390 -> 4316
                case TrousersId: return TrousersDonor;  // 4590 -> 4509
                case BackpackId: return BackpackDonor;  // 6090 -> 6019
                default:         return id;
            }
        }

        /// <summary>
        /// Patch PlayerInventoryManager.ShowCharacterMesh so an equipped SWAT
        /// piece shows its donor's worn model instead of nothing. Called once
        /// from RevivalPlugin.Awake beside the other *.Install(_harmony) hooks.
        /// Fails soft: a missing method only means the worn look is unfixed, the
        /// items still equip and carry their inventory art.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerInventoryManager");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("SWAT worn mesh: PlayerInventoryManager "
                        + "not found - SWAT gear will be invisible when worn.");
                    return;
                }
                // ShowCharacterMesh(Transform meshesParent, bool show, int itemId,
                //                   int defaultId, bool setDefault) - static. Pin
                // the exact overload by its argument types.
                MethodInfo m = AccessTools.Method(t, "ShowCharacterMesh",
                    new Type[] { typeof(Transform), typeof(bool), typeof(int),
                                 typeof(int), typeof(bool) }, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("SWAT worn mesh: ShowCharacterMesh "
                        + "not found - SWAT gear will be invisible when worn.");
                    return;
                }
                harmony.Patch(m,
                    new HarmonyMethod(typeof(SwatWornMeshHook).GetMethod("Prefix")),
                    null, null, null, null);
                RevivalPlugin.L.LogInfo("SWAT worn mesh: ShowCharacterMesh patched "
                    + "(clone ids -> donor worn mesh).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("SWAT worn mesh not hooked: " + ex);
            }
        }

        /// <summary>
        /// Add the four item definitions to the shared table. Called from
        /// RevivalPlugin.BuildItemTable - the one seam this feature needs.
        /// </summary>
        public static void AddItems(List<ItemDef> items)
        {
            // SWAT helmet (4090). The model's own detailed headgear: hard black
            // tactical shell with an NVG monocle mount, a flip visor and straps.
            items.Add(new ItemDef(
                HelmetId, 4017, false,
                "Шлем SWAT", "SWAT helmet",
                "Тяжелый штурмовой шлем группы SWAT: жесткая композитная скорлупа, "
                + "крепление для ночного монокуляра и откидное забрало. Надежная "
                + "защита головы.",
                "A heavy SWAT assault helmet: a rigid composite shell, a mount for "
                + "a night-vision monocular and a flip-down visor. Solid head "
                + "protection.",
                "swat_helmet.ndmesh", "swat_helmet_diffuse.png",
                "swat_helmet_normal.png", "swat_helmet_icon.png", null,
                0, 0, 1.6f));

            // SWAT body armour (4390). The carved torso: a black plate carrier /
            // tactical vest over the operator's uniform.
            items.Add(new ItemDef(
                ArmourId, 4316, false,
                "Бронежилет SWAT", "SWAT body armour",
                "Черный бронежилет с плитоносцем и подсумками поверх формы "
                + "оператора SWAT. Прикрывает корпус от осколков и пуль.",
                "A black SWAT plate carrier with pouches over the operator's "
                + "uniform. Shields the torso from fragments and rounds.",
                "swat_top.ndmesh", "swat_top_diffuse.png",
                "swat_top_normal.png", "swat_top_icon.png", null,
                0, 0, 6.0f));

            // SWAT trousers (4590). The carved lower body: black tactical
            // trousers and boots.
            items.Add(new ItemDef(
                TrousersId, 4509, false,
                "Штаны SWAT", "SWAT trousers",
                "Черные тактические штаны с наколенниками и высокие ботинки "
                + "оператора SWAT.",
                "Black SWAT tactical trousers with knee pads and high boots.",
                "swat_bottom.ndmesh", "swat_bottom_diffuse.png",
                "swat_bottom_normal.png", "swat_bottom_icon.png", null,
                0, 0, 2.0f));

            // SWAT backpack (6090). The carved back load: a black assault pack.
            items.Add(new ItemDef(
                BackpackId, 6019, false,
                "Рюкзак SWAT", "SWAT backpack",
                "Черный штурмовой рюкзак группы SWAT. Дополнительное место для "
                + "снаряжения.",
                "A black SWAT assault backpack. Extra room for equipment.",
                "swat_backpack.ndmesh", "swat_backpack_diffuse.png",
                "swat_backpack_normal.png", "swat_backpack_icon.png", null,
                0, 0, 2.5f));
        }
    }

    /// <summary>
    /// Makes the SWAT gear visible on the body. Harmony prefix on the game's
    /// `PlayerInventoryManager.ShowCharacterMesh(Transform, bool, int itemId,
    /// int defaultId, bool)` - the one method that toggles every worn clothing
    /// child (head, body, legs, feet, backpack; the local path and the one the
    /// NetworkBodyGear/HeadGear/Legs/BackpackMeshChange RPC drives on remote
    /// clients). It resolves the mesh from `itemId` by walking the character's
    /// pre-authored gear children and SetActive()ing the one whose CheckItemID /
    /// ItemCustomMaterialsManager list contains that id.
    ///
    /// Our four clone ids are in no such list, so unpatched they light up nothing
    /// and the piece is invisible. Rewriting `itemId` to the donor id (WornDonorId)
    /// before the original runs makes the donor's real worn mesh activate - and,
    /// for the material-manager meshes, ChangeMeshMaterialFromItemID then gets the
    /// donor id and applies the donor material, so no null material either. Only
    /// the four clone ids are touched; every other id passes straight through.
    /// </summary>
    public static class SwatWornMeshHook
    {
        public static void Prefix(ref int itemId)
        {
            itemId = SwatGear.WornDonorId(itemId);
        }
    }
}
