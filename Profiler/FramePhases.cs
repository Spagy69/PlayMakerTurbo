using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace MWCFsmProfiler
{
    // Frame boundary and the engine phases between scripts, using Unity's fixed points in the frame:
    //  - WaitForFixedUpdate resumes right after the physics simulation of a fixed step, so the time from the
    //    last FixedUpdate script of that step to it is the physics step (+ OnTrigger/OnCollision callbacks).
    //  - WaitForEndOfFrame resumes after cameras rendered and OnGUI ran, so the time from the last LateUpdate
    //    script to it is main-thread rendering work (culling, shadows, draw submission) + OnGUI.
    // Both need the script timestamps from a full recording; a light recording only gets the frame boundary.
    internal class FramePhases : MonoBehaviour
    {
        private static FramePhases instance;
        private static readonly WaitForFixedUpdate waitFixed = new WaitForFixedUpdate();
        private static readonly WaitForEndOfFrame waitEnd = new WaitForEndOfFrame();

        public static int FixedSteps;
        public static float FixedDeltaTime;

        public static void Begin()
        {
            FixedSteps = 0;
            FixedDeltaTime = Time.fixedDeltaTime;
            Timeline.Begin();
            GameObject go = new GameObject("MWCFsmProfiler_Phases");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<FramePhases>();
        }

        public static void End()
        {
            if (instance != null)
                Destroy(instance.gameObject);
            instance = null;
        }

        private void Start()
        {
            StartCoroutine(AfterPhysics());
            StartCoroutine(AfterRendering());
            StartCoroutine(AfterUpdate());
        }

        // "yield return null" resumes after every Update script and before animation and LateUpdate, which splits
        // allocations between the two in the gap table.
        private IEnumerator AfterUpdate()
        {
            while (true)
            {
                yield return null;
                if (Probe.Recording && Probe.MeasureMemory)
                    Probe.Marker(Timeline.MarkerAfterUpdate, System.GC.GetTotalMemory(false));
            }
        }

        private void Update()
        {
            Timeline.Boundary();
        }

        private IEnumerator AfterPhysics()
        {
            while (true)
            {
                yield return waitFixed;
                FixedSteps++;
                bool open = ScriptTimings.PhysicsWindow;
                ScriptTimings.PhysicsWindow = false;
                long start = ScriptTimings.LastFixedEnd;
                if (start == 0L || !open)
                    continue;
                long elapsed = Stopwatch.GetTimestamp() - start;
                // Trigger/collision callbacks run inside this window and are timed on their own.
                Timeline.CurPhysics += System.Math.Max(0L, elapsed - ScriptTimings.InPhysicsTicks);
                long memoryNow = System.GC.GetTotalMemory(false);
                Probe.Marker(Timeline.MarkerPhysics, memoryNow);
                long bytes = memoryNow - Timeline.PhysicsMemoryStart;
                if (bytes > 0L)
                {
                    Timeline.CurPhysicsBytes += bytes;
                    Timeline.CurPhysicsUntimed += System.Math.Max(0L, bytes - ScriptTimings.InPhysicsBytes);
                }
                if (SpanRecorder.Active)
                    SpanRecorder.Add(start, elapsed, SpanRecorder.NamePhysics, 0);
            }
        }

        private IEnumerator AfterRendering()
        {
            while (true)
            {
                yield return waitEnd;
                bool open = ScriptTimings.RenderWindow;
                ScriptTimings.RenderWindow = false;
                long start = ScriptTimings.LastLateEnd;
                if (start == 0L || !open)
                    continue;
                long elapsed = Stopwatch.GetTimestamp() - start;
                // OnGUI and render callbacks run inside this window and are timed on their own.
                Timeline.CurRender += System.Math.Max(0L, elapsed - ScriptTimings.InRenderTicks);
                long memoryNow = System.GC.GetTotalMemory(false);
                Probe.Marker(Timeline.MarkerRender, memoryNow);
                long bytes = memoryNow - Timeline.RenderMemoryStart;
                if (bytes > 0L)
                {
                    Timeline.CurRenderBytes += bytes;
                    Timeline.CurRenderUntimed += System.Math.Max(0L, bytes - ScriptTimings.InRenderBytes);
                }
                if (SpanRecorder.Active)
                    SpanRecorder.Add(start, elapsed, SpanRecorder.NameRender, 0);
            }
        }
    }
}
