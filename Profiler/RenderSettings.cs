using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace MWCFsmProfiler
{
    // Snapshot of what the engine is working on when the recording stops: render settings plus live physics,
    // rendering, lights, particles and audio, per root object (map area / vehicle). Rendering and physics are the
    // largest parts of the frame the profiler cannot time directly, and this shows what feeds them.
    internal static class RenderSettingsDump
    {
        private class RootCounts
        {
            public string Name;
            public int Awake, Sleeping, Colliders, Renderers, Visible, Casters, Lights, ShadowLights, Particles, Audio;
            public int Weight => Visible * 2 + Awake * 5 + ShadowLights * 20 + Particles * 3;
        }

        public static string Build()
        {
            StringBuilder sb = new StringBuilder("== Render settings ==\n");
            sb.AppendLine($"Quality level: {QualitySettings.names[QualitySettings.GetQualityLevel()]}, vSync {QualitySettings.vSyncCount}, AA {QualitySettings.antiAliasing}, targetFrameRate {Application.targetFrameRate}");
            sb.AppendLine($"Shadows: distance {QualitySettings.shadowDistance}, cascades {QualitySettings.shadowCascades}, projection {QualitySettings.shadowProjection}, pixel lights {QualitySettings.pixelLightCount}");
            sb.AppendLine($"LOD bias {QualitySettings.lodBias}, max LOD level {QualitySettings.maximumLODLevel}, texture limit {QualitySettings.masterTextureLimit}");

            foreach (Camera camera in Camera.allCameras)
            {
                int culled = 0;
                foreach (float d in camera.layerCullDistances)
                {
                    if (d > 0f)
                        culled++;
                }
                sb.AppendLine($"Camera {camera.name}: far clip {camera.farClipPlane}, path {camera.actualRenderingPath}, depth {camera.depth}, layers with own cull distance {culled}{(camera == Camera.main ? " (main)" : "")}");
            }

            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null)
                sb.AppendLine($"Terrain: detail distance {terrain.detailObjectDistance}, detail density {terrain.detailObjectDensity}, tree distance {terrain.treeDistance}, billboard start {terrain.treeBillboardDistance}, basemap distance {terrain.basemapDistance}, pixel error {terrain.heightmapPixelError}");

            Dictionary<Transform, RootCounts> roots = new Dictionary<Transform, RootCounts>();
            RootCounts total = new RootCounts { Name = "TOTAL" };
            foreach (Rigidbody rb in Object.FindObjectsOfType<Rigidbody>())
            {
                bool sleeping = rb.IsSleeping();
                Count(roots, rb.transform, total, c => { if (sleeping) c.Sleeping++; else c.Awake++; });
            }
            foreach (Collider col in Object.FindObjectsOfType<Collider>())
            {
                if (col.enabled)
                    Count(roots, col.transform, total, c => c.Colliders++);
            }
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled)
                    continue;
                bool visible = r.isVisible, casts = r.shadowCastingMode != ShadowCastingMode.Off;
                Count(roots, r.transform, total, c => { c.Renderers++; if (visible) c.Visible++; if (casts) c.Casters++; });
            }
            foreach (Light l in Object.FindObjectsOfType<Light>())
            {
                if (!l.enabled)
                    continue;
                bool shadows = l.shadows != LightShadows.None;
                Count(roots, l.transform, total, c => { c.Lights++; if (shadows) c.ShadowLights++; });
            }
            foreach (ParticleSystem p in Object.FindObjectsOfType<ParticleSystem>())
            {
                if (p.isPlaying)
                    Count(roots, p.transform, total, c => c.Particles++);
            }
            foreach (AudioSource a in Object.FindObjectsOfType<AudioSource>())
            {
                if (a.isPlaying)
                    Count(roots, a.transform, total, c => c.Audio++);
            }

            sb.AppendLine();
            sb.AppendLine("== Live scene when the recording stopped (by root object, heaviest first) ==");
            sb.AppendLine("rigidbodies awake/sleeping, colliders, renderers enabled/visible now/shadow casters, lights/with shadows, particle systems playing, audio playing");
            List<RootCounts> list = new List<RootCounts>(roots.Values);
            list.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            Line(sb, total);
            for (int i = 0; i < list.Count && i < 25; i++)
                Line(sb, list[i]);
            return sb.ToString();
        }

        private static void Count(Dictionary<Transform, RootCounts> roots, Transform t, RootCounts total, System.Action<RootCounts> add)
        {
            Transform root = t.root;
            RootCounts c;
            if (!roots.TryGetValue(root, out c))
            {
                c = new RootCounts { Name = root.name };
                roots.Add(root, c);
            }
            add(c);
            add(total);
        }

        private static void Line(StringBuilder sb, RootCounts c)
        {
            sb.AppendLine($"{c.Name,-32} rb {c.Awake,4}/{c.Sleeping,-5} col {c.Colliders,5}  rend {c.Renderers,5}/{c.Visible,-5}/{c.Casters,-5} light {c.Lights,3}/{c.ShadowLights,-3} part {c.Particles,3}  audio {c.Audio,3}");
        }
    }
}
