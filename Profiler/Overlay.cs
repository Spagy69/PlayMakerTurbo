using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace MWCFsmProfiler
{
    // Small live panel. It keeps its own frame times, so it works without a recording; during a full recording it
    // also lists the FSMs with the most self time since the last refresh. Text is rebuilt twice a second only,
    // so the overlay itself does not allocate every frame.
    internal class Overlay : MonoBehaviour
    {
        private static Overlay instance;

        private readonly float[] frames = new float[300];
        private readonly float[] scratch = new float[300];
        private int count, next;
        private float refreshAt;
        private int gcAtStart;
        private float startTime;
        private string text = "";
        private GUIStyle style;

        public static void Toggle()
        {
            if (instance != null)
            {
                Destroy(instance.gameObject);
                instance = null;
                return;
            }
            GameObject go = new GameObject("MWCFsmProfiler_Overlay");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Overlay>();
        }

        private void Start()
        {
            gcAtStart = GC.CollectionCount(0);
            startTime = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            frames[next] = Time.unscaledDeltaTime * 1000f;
            next = (next + 1) % frames.Length;
            if (count < frames.Length)
                count++;

            if (Time.realtimeSinceStartup >= refreshAt)
            {
                refreshAt = Time.realtimeSinceStartup + 0.5f;
                Refresh();
            }
        }

        private void Refresh()
        {
            Array.Copy(frames, scratch, count);
            Array.Sort(scratch, 0, count, FloatComparer.Instance);
            float sum = 0f;
            for (int i = 0; i < count; i++)
                sum += scratch[i];
            float avg = count > 0 ? sum / count : 0f;
            float minutes = Mathf.Max(1f / 60f, (Time.realtimeSinceStartup - startTime) / 60f);

            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("FPS {0:F0}   avg {1:F1} ms   p50 {2:F1}   p99 {3:F1}   max {4:F1}\n",
                avg > 0 ? 1000f / avg : 0f, avg, scratch[count / 2], scratch[Math.Min(count - 1, (int)(count * 0.99f))], scratch[count - 1]);
            sb.AppendFormat("GC {0:F1}/min   heap {1:F0} MB\n", (GC.CollectionCount(0) - gcAtStart) / minutes, GC.GetTotalMemory(false) / 1048576f);

            if (Probe.Recording)
            {
                List<FsmEntry> top = new List<FsmEntry>();
                foreach (FsmEntry e in FsmTimings.Entries.Values)
                    top.Add(e);
                top.Sort((a, b) => (b.Total.Self - b.Total.OverlayMark).CompareTo(a.Total.Self - a.Total.OverlayMark));
                sb.Append("Top FSMs (self ms in the last 0.5 s):\n");
                for (int i = 0; i < top.Count && i < 5; i++)
                    sb.AppendFormat("  {0,6:F2}  {1}\n", (top[i].Total.Self - top[i].Total.OverlayMark) * 1000.0 / Stopwatch.Frequency, top[i].Label);
                foreach (FsmEntry e in top)
                    e.Total.OverlayMark = e.Total.Self;
            }
            else
            {
                sb.Append("F9 full recording adds the top FSMs here.");
            }
            text = sb.ToString();
        }

        private void OnGUI()
        {
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = false };
                style.normal.textColor = Color.white;
            }
            GUI.Box(new Rect(10, 10, 560, 150), text, style);
        }
    }
}
