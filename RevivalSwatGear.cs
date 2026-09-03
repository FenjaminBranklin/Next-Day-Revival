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
// KNOWN LIMITATION, for in-game acceptance and documented on purpose: these
// items carry the SWAT look in the INVENTORY and on the GROUND (our mesh + our
// icon). The WORN appearance on the player's body is still the donor clothing's
// own skinned, bone-rigged mesh - replacing the on-body look means re-skinning
// our carved mesh to the character skeleton, which is a separate, larger piece
// of work (the same shape of caveat as "the surveillance drone is local-visual
// only"). Nothing here touches the worn model.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs. Player-facing strings go through Loc.T, whose
// Russian half is real Cyrillic - this file is therefore UTF-8 (no BOM) and is
// compiled with /codepage:65001 like the main file.

using System.Collections.Generic;

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
}
