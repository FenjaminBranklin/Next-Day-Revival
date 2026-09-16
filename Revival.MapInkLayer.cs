using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // Native UITexture widgets share the map's NGUI clipping shaders, including
    // nested panels, clipSoftness, panel alpha and camera/UI scaling. No IMGUI
    // approximation of the edge fade and no cloned marker scripts/colliders.
    internal sealed class MapInkLayer : MonoBehaviour
    {
        static readonly Dictionary<string, MapInkLayer> Layers =
            new Dictionary<string, MapInkLayer>();
        static Type TextureType;
        static PropertyInfo MainTexture, ColorProperty, Width, Height, Depth, Corners, UvRect;
        readonly List<Component> Widgets = new List<Component>();
        Component Source;
        Vector3 BottomLeft, TopRight;
        int Used, LastFrame, DrawDepth;
        float SourceAlpha;

        internal static MapInkLayer Begin(string name, Component source)
        {
            if (TextureType == null)
            {
                TextureType = RevivalPlugin.TypeByName("UITexture");
                if (TextureType == null) return null;
                MainTexture = AccessTools.Property(TextureType, "mainTexture");
                ColorProperty = AccessTools.Property(TextureType, "color");
                Width = AccessTools.Property(TextureType, "width");
                Height = AccessTools.Property(TextureType, "height");
                Depth = AccessTools.Property(TextureType, "depth");
                Corners = AccessTools.Property(TextureType, "localCorners");
                UvRect = AccessTools.Property(TextureType, "uvRect");
            }
            if (source == null || MainTexture == null || ColorProperty == null
                || Width == null || Height == null || Depth == null || Corners == null || UvRect == null)
                return null;

            MapInkLayer layer;
            if (!Layers.TryGetValue(name, out layer) || layer == null)
            {
                GameObject go = new GameObject("NDR map ink " + name);
                go.hideFlags = HideFlags.HideAndDontSave;
                layer = go.AddComponent<MapInkLayer>();
                Layers[name] = layer;
            }
            layer.Source = source;
            layer.Used = 0;
            layer.LastFrame = Time.frameCount;
            layer.DrawDepth = (int)Depth.GetValue(source, null) + 1;
            layer.SourceAlpha = ((Color)ColorProperty.GetValue(source, null)).a;
            Vector3[] corners = (Vector3[])Corners.GetValue(source, null);
            layer.BottomLeft = corners[0];
            layer.TopRight = corners[2];
            layer.Follow();
            return layer;
        }

        internal static void Hide(string name)
        {
            MapInkLayer layer;
            if (!Layers.TryGetValue(name, out layer) || layer == null) return;
            layer.Used = 0;
            layer.End();
        }

        void Follow()
        {
            // A sibling is essential: MapTools and native mouse picking call
            // CalculateAbsoluteWidgetBounds on the map texture. Children outside
            // its bounds would enlarge that rectangle and corrupt all coordinates.
            Transform t = Source.transform;
            transform.SetParent(t.parent, false);
            transform.localPosition = t.localPosition;
            transform.localRotation = t.localRotation;
            transform.localScale = t.localScale;
            gameObject.layer = Source.gameObject.layer;
        }

        internal void Draw(Rect artwork, Texture stamp, Color tint)
        {
            if (stamp == null || artwork.width <= 0f || artwork.height <= 0f) return;
            // NGUI clips the viewport. Crop only at the artwork's own boundary,
            // which may lie inside that viewport when the map is zoomed out.
            float left = Mathf.Max(0f, artwork.x), top = Mathf.Max(0f, artwork.y);
            float right = Mathf.Min(1024f, artwork.x + artwork.width);
            float bottom = Mathf.Min(1024f, artwork.y + artwork.height);
            if (right <= left || bottom <= top) return;
            Rect uv = new Rect((left - artwork.x) / artwork.width,
                1f - (bottom - artwork.y) / artwork.height,
                (right - left) / artwork.width, (bottom - top) / artwork.height);
            artwork = new Rect(left, top, right - left, bottom - top);
            Component widget;
            if (Used == Widgets.Count)
            {
                GameObject go = new GameObject("ink");
                go.SetActive(false);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                widget = go.AddComponent(TextureType);
                // UIWidget's native default pivot is Center (4). An even fixed
                // size plus transform scaling preserves fractional artwork pixels.
                Width.SetValue(widget, 100, null);
                Height.SetValue(widget, 100, null);
                Widgets.Add(widget);
            }
            else widget = Widgets[Used];
            Used++;
            float w = TopRight.x - BottomLeft.x, h = TopRight.y - BottomLeft.y;
            widget.transform.localPosition = new Vector3(
                BottomLeft.x + (artwork.x + artwork.width * .5f) * w / 1024f,
                TopRight.y - (artwork.y + artwork.height * .5f) * h / 1024f, 0f);
            widget.transform.localScale = new Vector3(
                artwork.width * w / 102400f, artwork.height * h / 102400f, 1f);
            MainTexture.SetValue(widget, stamp, null);
            UvRect.SetValue(widget, uv, null);
            tint.a *= SourceAlpha;
            ColorProperty.SetValue(widget, tint, null);
            Depth.SetValue(widget, DrawDepth, null);
            if (!widget.gameObject.activeSelf) widget.gameObject.SetActive(true);
        }

        internal void End()
        {
            for (int i = Used; i < Widgets.Count; i++)
                if (Widgets[i] != null && Widgets[i].gameObject.activeSelf)
                    Widgets[i].gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (Source == null) { Destroy(gameObject); return; }
            Follow();
            // OnGUI submissions stop when the map closes or a feature is disabled.
            // Hide pooled widgets; retain the pool across ordinary map reopening.
            Behaviour behaviour = Source as Behaviour;
            if (!Source.gameObject.activeInHierarchy || (behaviour != null && !behaviour.enabled)
                || Time.frameCount - LastFrame > 1)
            { Used = 0; End(); }
        }

        internal static Texture SettlementStamp()
        {
            // TargetArea and TargetAreaRed reference 87x87 textures with exactly
            // the same alpha mask (AreaMarkerCut / TargetAreaMarkerCut). Use the
            // neutral one so the settlement keeps its orange faction colour.
            GameObject prefab = Resources.Load("GUI/HUD_MapUI/MarkerPrefabs/TargetArea") as GameObject;
            if (prefab == null) return null;
            Type type = RevivalPlugin.TypeByName("UITexture");
            Component widget = type == null ? null : prefab.GetComponent(type);
            PropertyInfo texture = type == null ? null : AccessTools.Property(type, "mainTexture");
            return widget == null || texture == null ? null : texture.GetValue(widget, null) as Texture;
        }
    }
}
