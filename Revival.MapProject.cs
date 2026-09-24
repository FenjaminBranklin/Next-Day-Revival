// Revival.MapProject.cs - world point -> the open map, for everything the
// plugin draws over it (MapTools.WorldToGui: settlement ring, helipad ring,
// mortar and arty marks, patrol route hover/labels) and the one correction
// for the map PICTURE (docs/ai/tasks/east-map-markers.md).
//
// Two things only held for the vanilla window:
//
// - The texture's own scale is 1. MAP_SIZE is the texture's size in its
//   PARENT's units (MapUIManager.InitMapSize: localScale x localSize), and
//   the game's markers are placed with it in that parent's frame
//   (MapUIDynamicForms/Markers). WorldToGui instead handed a point in
//   MAP_SIZE units to the TEXTURE's TransformPoint, which applies the
//   texture's scale a second time. The east window
//   (Revival.EastMapPanel.cs) draws the texture at one screen pixel per
//   unit, scale 1/unit (0.78 at 1640x927): every plugin overlay sat 22 %
//   closer to the map centre than the painted place.
// - The vanilla picture is ~0.5 % larger than the terrain and ~20 m
//   north-east of it (REVERSE_ENGINEERING.md 33.1), so Patrol, the
//   settlement ring and the helipads moved world-true points onto it. The
//   east artwork (research/east_map.py) is registered EXACTLY on the world
//   rectangle - that correction must not be applied to it.
//
// Off (not the east world) both functions compute exactly what the callers
// computed before, bit for bit.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only.

using UnityEngine;

namespace NextDayRevival
{
    internal static class MapProject
    {
        // The vanilla picture's registration error, measured at 1024 px
        // (REVERSE_ENGINEERING.md 33.1): 0.5 % larger, +2 px east, 4 px north.
        const float PictureFit = 1024f;
        const float PictureScale = 1.005f;
        const float PictureShiftX = 2f;
        const float PictureShiftY = -4f;

        /// <summary>A world point in the map texture's OWN local space (what
        /// its TransformPoint takes; centre = 0). world = WORLD_SIZE, map =
        /// MAP_SIZE, both as MapUIManager holds them.</summary>
        internal static Vector3 TextureLocal(Vector3 point, Transform texture, Vector2 world, Vector2 map)
        {
            if (!EastWorld.Extends)
                return new Vector3(point.x / world.x * map.x, point.z / world.y * map.y, 0f);
            // The east world's rectangle (x -2500..7500, z -2500..2500) as the
            // fraction the game's markers use (EastWorld.NormalizePrefix), times
            // the texture's size in its own units.
            Rect w = EastWorld.Extended;
            Vector3 s = texture.localScale;
            float fx = (point.x - w.xMin) / w.width - 0.5f;
            float fz = (point.z - w.yMin) / w.height - 0.5f;
            return new Vector3(fx * map.x / s.x, fz * map.y / s.y, 0f);
        }

        /// <summary>World point -> GUI point (IMGUI: origin top left) over the
        /// open map. The body of MapTools.WorldToGui.</summary>
        internal static bool ToGui(Vector3 point, Component texture, Camera cam,
                                   Vector2 world, Vector2 map, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (texture == null || cam == null || world.x <= 0f || world.y <= 0f)
                return false;
            Transform t = texture.transform;
            Vector3 screen = cam.WorldToScreenPoint(t.TransformPoint(TextureLocal(point, t, world, map)));
            if (screen.z < 0f) return false;
            gui = new Vector2(screen.x, Screen.height - screen.y);
            return true;
        }

        /// <summary>A projected (world-true) GUI point moved onto the map
        /// PICTURE: the vanilla picture's registration error applied, scaled
        /// to the texture's GUI rectangle <paramref name="full"/>. The east
        /// artwork is registered exactly: unchanged there.</summary>
        internal static Vector2 OnPicture(Vector2 g, Rect full)
        {
            if (EastWorld.Extends || full.width < 1f) return g;
            float factor = full.width / PictureFit;
            float cx = full.x + full.width * 0.5f;
            float cy = full.y + full.height * 0.5f;
            return new Vector2((g.x - cx) * PictureScale + cx + PictureShiftX * factor,
                               (g.y - cy) * PictureScale + cy + PictureShiftY * factor);
        }
    }
}
