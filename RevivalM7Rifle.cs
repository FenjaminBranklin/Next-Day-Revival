// Next Day: Survival - Revival Toolkit
//
// The M7 (XM7) rifle in 6.8x51mm and its two magazines - the whole feature.
//
// A separate file on purpose, isolated for a clean parallel merge. build.ps1
// compiles every top-level *.cs beside RevivalPlugin.cs into the one DLL, so a
// new item family lives here instead of growing the main file. The ONLY seam in
// RevivalPlugin.cs is a single line in BuildItemTable:
//
//     M7Rifle.AddItems(Items);
//
// (added right after DroneGear.AddItems(Items);). Everything else is generic:
// ResourceHook, Registry and the loot tables all iterate RevivalPlugin.Items, so
// adding the three ItemDefs to that list is enough to register, spawn, loot and
// localize them. The firearm STATS (accuracy, damage, calibre, which magazines
// fit) come from the master server's weapons_db.xml - see mods/m7.json, whose
// entry must be folded into the combined mods/revival.json at integration time
// (modtool apply rebuilds weapons_db from one file on top of .orig).
//
// Balance intent (from the brief): the M7 is the most ACCURATE rifle at long
// range with MEDIUM stopping power. In weapons_db that is the tightest Spread of
// any conventional rifle with a long EffectiveRange, and a Damage well above the
// assault rifles yet far below the TAC-50 anti-materiel round. It feeds from a
// standard 20-round box (2058) or a 100-round drum (2059), both in 6.8x51mm.
//
// PLACEHOLDER ART, exactly like the drone-gear items: the rifle borrows the
// TAC-50 (sniper50) mesh/textures/icons and the magazines borrow the .50 ammo
// box. Dedicated generators (an m7_*.py mesh/texture/icon set, drum art) can
// replace these later without touching this table or the stats.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs. Player-facing strings go through Loc.T, whose
// Russian half is real Cyrillic - this file is therefore UTF-8 (no BOM) and is
// compiled with /codepage:65001 like the main file.

using System.Collections.Generic;

namespace NextDayRevival
{
    /// <summary>
    /// The M7 (XM7) rifle and its 6.8x51mm magazines. Item ids 1164 (rifle),
    /// 2058 (standard box magazine) and 2059 (100-round drum) are fresh: they
    /// fill the gap between the drone-gear items (2055-2057) and the vehicle
    /// modules (2060-2062), and items.tsv has nothing on them.
    ///
    /// The rifle clones donor 1010 (the SVD - the same donor the sniper50 mesh
    /// is built from), so its material and prefab components match the borrowed
    /// model. The magazines clone donor 2030 (a generic item box), the shape
    /// every ammunition item in this mod uses.
    /// </summary>
    public static class M7Rifle
    {
        // Fresh ids. Verified free against items.tsv and the source tree.
        public const int RifleId = 1164;   // M7 (XM7) rifle, weapon
        public const int MagId   = 2058;   // 6.8x51 standard box magazine (20)
        public const int DrumId  = 2059;   // 6.8x51 drum magazine (100)

        /// <summary>
        /// Add the three item definitions to the shared table. Called from
        /// RevivalPlugin.BuildItemTable - the one seam this feature needs.
        /// </summary>
        public static void AddItems(List<ItemDef> items)
        {
            // The M7 (XM7) rifle (1164). Placeholder art: the TAC-50 mesh.
            // The default loaded magazine is the drum (2059); the standard box
            // (2058) also fits - both are listed in the weapons_db Clips.
            items.Add(new ItemDef(
                RifleId, 1010, true,
                "M7 (XM7)", "M7 (XM7)",
                "Автоматическая винтовка M7 (XM7) под патрон 6,8x51 мм. Самая "
                + "точная винтовка на большой дистанции: плотная кучность и "
                + "пологая траектория. Останавливающее действие среднее - "
                + "заметно сильнее автоматного калибра, но это не крупнокалиберная "
                + "снайперская винтовка. Питается из стандартного магазина или "
                + "барабана на 100 патронов.",
                "The M7 (XM7) select-fire rifle in 6.8x51mm. The most accurate "
                + "rifle at long range: tight groups and a flat trajectory. "
                + "Medium stopping power - clearly harder-hitting than an assault "
                + "rifle round, but no anti-materiel calibre. Feeds from a "
                + "standard box magazine or a 100-round drum.",
                "sniper50.ndmesh", "sniper50_diffuse.png", "sniper50_normal.png",
                "sniper50_icon.png", "sniper50_weapon_icon.png",
                100, DrumId, 5.0f));

            // 6.8x51 standard box magazine (2058). Placeholder art: .50 box.
            items.Add(new ItemDef(
                MagId, 2030, false,
                "Магазин 6,8x51 (20)", "6.8x51 magazine (20)",
                "Коробчатый магазин на 20 патронов 6,8x51 мм для винтовки M7.",
                "A 20-round box magazine of 6.8x51mm for the M7 rifle.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png",
                "ammo50_icon.png", null,
                20, 0, 0.7f));

            // 6.8x51 drum magazine (2059). Placeholder art: .50 box.
            items.Add(new ItemDef(
                DrumId, 2030, false,
                "Барабан 6,8x51 (100)", "6.8x51 drum (100)",
                "Барабанный магазин на 100 патронов 6,8x51 мм для винтовки M7. "
                + "Вчетверо больше стандартного - и заметно тяжелее.",
                "A 100-round drum magazine of 6.8x51mm for the M7 rifle. Four "
                + "times a standard magazine - and noticeably heavier.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png",
                "ammo50_icon.png", null,
                100, 0, 2.8f));
        }
    }
}
