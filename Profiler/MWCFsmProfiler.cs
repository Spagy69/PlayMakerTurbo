using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MWCFsmProfiler
{
    public class MWCFsmProfiler : Mod
    {
        public override string ID => "MWCFsmProfiler";
        public override string Name => "FSM Profiler";
        public override string Author => "Spagy";
        public override string Version => "2.0";
        public override string Description => "Measures where every millisecond of a frame goes: FSMs, states, actions, events, scripts, mods, physics, rendering, GC. F9 full, F10 light, F11 trace.";
        public override Game SupportedGames => Game.MyWinterCar;

        private enum RecordMode { None, Light, Full, Trace, Benchmark }

        // PlayMakerTurbo options that can be switched at runtime (checked in PlayMakerTurbo/Runtime).
        // ActiveFast is not identical to stock PlayMaker, so it is off unless asked for.
        private static readonly string[] abOptions =
        {
            "IdleUpdateSkip", "LateUpdateSkip", "FixedUpdateSkip", "DelayedEventsEarlyOut", "GameObjectCache",
            "SkipNonUpdatingActions", "MousePickSingleCameraLookup", "MousePickFrameCache", "PropertyDelegates",
            "SetGameVolumeSkipUnchanged", "CInputAxisInvertedBuilder", "ActiveLists", "FastEventRouting", "ActiveFast"
        };

        private SettingsKeybind fullKey, lightKey, traceKey, overlayKey, benchKey;
        private SettingsDropDownList benchKind;
        private SettingsCheckBox freezeTraffic;
        private SettingsSliderInt blockSeconds, minPairs, maxPairs;
        private readonly Dictionary<string, SettingsCheckBox> abChecks = new Dictionary<string, SettingsCheckBox>();
        private string lastBenchOption;
        private SettingsCheckBox measureMemory, timeEnterExit, spikeCapture, wasteDetector;
        private SettingsSliderInt traceFrames;
        private SettingsSlider spikeMultiplier;

        private RecordMode mode = RecordMode.None;
        private bool patched;
        private bool patchFailed;
        private long heapAtStart;

        public override void ModSetup()
        {
            SetupFunction(Setup.ModSettings, Mod_ModSettings);
            SetupFunction(Setup.Update, Mod_Update);
        }

        private void Mod_ModSettings()
        {
            fullKey = Keybind.Add("toggle", "Full recording: times everything (slows the game)", KeyCode.F9);
            lightKey = Keybind.Add("light", "Light recording: no patches, clean FPS / frame times", KeyCode.F10);
            traceKey = Keybind.Add("trace", "Trace: every call of the next N frames (ui.perfetto.dev)", KeyCode.F11);
            overlayKey = Keybind.Add("overlay", "Show / hide the live overlay", KeyCode.F8);

            Settings.AddText("Reports are saved to Mods\\Config\\Mod Settings\\MWCFsmProfiler, one folder per recording.");
            Settings.AddButton("Open reports folder", () => System.Diagnostics.Process.Start(ModLoader.GetModSettingsFolder(this)));
            Settings.AddHeader("What a full recording measures");
            Settings.AddText("The OnEnter/OnExit timing and write counting settings apply only before the first F9 of a game session.");
            measureMemory = Settings.AddCheckBox("measureMemory", "Allocations per call (adds overhead to every timed call)", true);
            timeEnterExit = Settings.AddCheckBox("timeEnterExit", "Time action OnEnter/OnExit too", true);
            wasteDetector = Settings.AddCheckBox("wasteDetector", "Count writes that do not change anything", true);
            spikeCapture = Settings.AddCheckBox("spikeCapture", "Keep every call of spike frames", true);
            spikeMultiplier = Settings.AddSlider("spikeMultiplier", "Spike = frame longer than this many times the recent median", 2f, 10f, 3f, null, 1);
            traceFrames = Settings.AddSlider("traceFrames", "Trace length (frames)", 60, 1000, 300);

            benchKey = Keybind.Add("benchmark", "Benchmark: start / abort (stand on foot)", KeyCode.F12);
            Settings.AddHeader("Benchmark (F12)");
            Settings.AddText("Locks the player and view, stops game time and weather, removes traffic, warms up 5 s, then measures. Use a COPY of your save and do not save afterwards.");
            benchKind = Settings.AddDropDownList("benchKind", "What to measure", new[] { "A/B: each checked Turbo option off vs on", "A/A: method check (switches nothing)", "Baseline session (compare after a restart)" }, 0);
            freezeTraffic = Settings.AddCheckBox("freezeTraffic", "Remove traffic, NPC cars and the train (less noise)", true);
            blockSeconds = Settings.AddSlider("blockSeconds", "Block length (s)", 5, 30, 10);
            minPairs = Settings.AddSlider("minPairs", "Minimum off/on pairs per option", 4, 20, 8);
            maxPairs = Settings.AddSlider("maxPairs", "Maximum off/on pairs per option", 8, 40, 20);
            Settings.AddButton("Save benchmark spot here", () => Benchmark.SaveSpot(SpotFile()));
            Settings.AddButton("Teleport to benchmark spot", () => Benchmark.TeleportToSpot(SpotFile()));
            Settings.AddButton("List FSMs that advance game time", () => ModConsole.Print(Benchmark.DescribeTimeWriters()));
            Settings.AddText("Options to test in A/B:");
            foreach (string option in abOptions)
                abChecks[option] = Settings.AddCheckBox("ab_" + option, option, option != "ActiveFast");
        }

        private string SpotFile() => Path.Combine(ModLoader.GetModSettingsFolder(this), "benchmark_spot.txt");

        private void Mod_Update()
        {
            if (overlayKey.GetKeybindDown())
                Overlay.Toggle();

            if (mode == RecordMode.Benchmark)
            {
                UpdateBenchmark();
                return;
            }

            if (mode == RecordMode.None)
            {
                if (benchKey.GetKeybindDown())
                    StartBenchmark();
                else if (fullKey.GetKeybindDown())
                    StartRecording(RecordMode.Full);
                else if (lightKey.GetKeybindDown())
                    StartRecording(RecordMode.Light);
                else if (traceKey.GetKeybindDown())
                    StartRecording(RecordMode.Trace);
            }
            else if (fullKey.GetKeybindDown() || lightKey.GetKeybindDown() || traceKey.GetKeybindDown()
                || (mode == RecordMode.Trace && SpanRecorder.TraceDone))
            {
                StopRecording();
            }
        }

        private void StartRecording(RecordMode newMode)
        {
            // Patching is deferred to the first full recording so the mod costs nothing while it just sits
            // in the Mods folder. Once patched, a light recording is no longer clean, so it says so.
            bool needsPatches = newMode != RecordMode.Light;
            if (needsPatches && !patched && !ApplyPatches())
                return;

            Probe.MarkMainThread();
            Probe.MeasureMemory = measureMemory.GetValue();
            Timeline.SpikeMultiplier = spikeMultiplier.GetValue();

            if (newMode == RecordMode.Trace)
                SpanRecorder.StartTrace(traceFrames.GetValue());
            else if (newMode == RecordMode.Full && spikeCapture.GetValue())
                SpanRecorder.StartFrameCapture();

            // Calibrated with the span recorder already on: recording a span is part of every probe's cost.
            if (needsPatches)
                Calibration.Run();
            SpanRecorder.ClearBuffers();
            FsmTimings.Reset();
            ActionTimings.Reset();
            EventFlow.Reset();
            ScriptTimings.Reset();
            Probe.Reset();
            if (newMode == RecordMode.Full && measureMemory.GetValue())
                Probe.MeasureBackground();

            heapAtStart = GC.GetTotalMemory(false);
            FramePhases.Begin();
            TurboCounters.Start();
            Probe.Recording = needsPatches;
            mode = newMode;

            string what = newMode == RecordMode.Trace ? $"tracing the next {traceFrames.GetValue()} frames" : "recording, press the key again to stop";
            ModConsole.Print($"<color=orange>FSM Profiler:</color> {newMode} {what}.");
        }

        private void StopRecording()
        {
            Probe.Recording = false;
            SpanRecorder.Stop();
            FramePhases.End();
            RecordMode stopped = mode;
            mode = RecordMode.None;

            if (Timeline.Count == 0)
                return;

            string modeText = stopped == RecordMode.Full ? "FULL (all timings; overhead is measured and subtracted, FPS is lower than without profiler)"
                : stopped == RecordMode.Trace ? "TRACE (every call of " + Timeline.Count + " frames, see trace.json)"
                : patched ? "LIGHT, but timing patches from an earlier F9 are still in place (cheap when idle); restart the game for clean numbers"
                : "LIGHT (no patches: FPS, frame times and GC are clean; timing tables are empty)";

            ReportData report = ReportData.Build(modeText, heapAtStart);
            report.TurboText = TurboCounters.Stop(report.Frames);
            report.RenderText = RenderSettingsDump.Build();

            string folder = Path.Combine(ModLoader.GetModSettingsFolder(this), $"{DateTime.Now:yyyyMMdd_HHmmss}_{stopped.ToString().ToLowerInvariant()}");
            Directory.CreateDirectory(folder);
            try
            {
                Exporter.WriteAll(folder, report, stopped == RecordMode.Trace);
                ModConsole.Print($"<color=orange>FSM Profiler:</color> {report.Frames} frames, {FsmTimings.Entries.Count} FSMs, {report.SpikeList.Count} spikes kept. Saved to {folder}");
            }
            catch (Exception e)
            {
                ModConsole.Error($"FSM Profiler: writing the report failed.\n{e}");
            }
            if (stopped == RecordMode.Trace)
                SpanRecorder.Release();
        }

        private void StartBenchmark()
        {
            AbRunner.Kind kind = (AbRunner.Kind)benchKind.GetSelectedItemIndex();
            List<string> options = new List<string>();
            if (kind == AbRunner.Kind.AB)
            {
                if (!TurboCounters.Installed)
                {
                    ModConsole.Print("FSM Profiler: PlayMakerTurbo is not installed, there is nothing to switch. Use the baseline benchmark instead.");
                    return;
                }
                List<string> available = TurboCounters.OptionNames();
                foreach (string option in abOptions)
                {
                    if (abChecks[option].GetValue() && available.Contains(option))
                        options.Add(option);
                }
                if (options.Count == 0)
                {
                    ModConsole.Print("FSM Profiler: no options checked for the A/B benchmark (Mod Settings).");
                    return;
                }
            }

            string blocker = Benchmark.Lock(freezeTraffic.GetValue());
            if (blocker != null)
            {
                ModConsole.Print("FSM Profiler: cannot start the benchmark, " + blocker + ".");
                return;
            }
            foreach (string line in Benchmark.Log)
                ModConsole.Print("  benchmark " + line);

            AbRunner.BlockSeconds = blockSeconds.GetValue();
            AbRunner.MinPairs = minPairs.GetValue();
            AbRunner.MaxPairs = Math.Max(minPairs.GetValue(), maxPairs.GetValue());
            FsmTimings.Reset();
            ActionTimings.Reset();
            EventFlow.Reset();
            ScriptTimings.Reset();
            // No timing probes during a benchmark: the frame times must be what a player gets.
            Probe.Recording = false;
            Probe.Reset();
            heapAtStart = GC.GetTotalMemory(false);
            FramePhases.Begin();
            AbRunner.Start(kind, options);
            mode = RecordMode.Benchmark;
            lastBenchOption = null;

            float perOption = AbRunner.MinPairs * 2 * (AbRunner.BlockSeconds + AbRunner.SettleSeconds);
            float seconds = AbRunner.WarmupSeconds + (kind == AbRunner.Kind.Baseline
                ? AbRunner.BaselineBlocks * (AbRunner.BlockSeconds + AbRunner.SettleSeconds)
                : perOption * AbRunner.OptionCount);
            ModConsole.Print($"<color=orange>FSM Profiler:</color> benchmark started, at least {seconds / 60f:F0} min. Do not touch anything. F12 or Esc aborts.");
        }

        private void UpdateBenchmark()
        {
            if (benchKey.GetKeybindDown() || Input.GetKeyDown(KeyCode.Escape))
            {
                AbRunner.Abort();
                Benchmark.Unlock();
                FramePhases.End();
                mode = RecordMode.None;
                ModConsole.Print("FSM Profiler: benchmark aborted, everything restored, no report.");
                return;
            }

            if (AbRunner.Active && AbRunner.CurrentOption != lastBenchOption)
            {
                lastBenchOption = AbRunner.CurrentOption;
                ModConsole.Print($"FSM Profiler: benchmark measuring {lastBenchOption}");
            }
            if (!AbRunner.Finished)
                return;

            // Checked before unlocking: afterwards the clock runs again.
            string clock = Benchmark.ClockCheck();
            Benchmark.Unlock();
            FramePhases.End();
            mode = RecordMode.None;

            string folderRoot = ModLoader.GetModSettingsFolder(this);
            if (AbRunner.RunKind == AbRunner.Kind.Baseline)
                AbRunner.CompareSessions(folderRoot, Benchmark.ConditionsLine());

            ReportData report = ReportData.Build("BENCHMARK (" + AbRunner.RunKind + "; no timing patches running, frame times are clean)", heapAtStart);
            report.TurboText = TurboCounters.Installed ? "PlayMakerTurbo settings: " + TurboCounters.SettingsLine() : "PlayMakerTurbo: not installed";
            report.RenderText = RenderSettingsDump.Build();
            report.BenchmarkText = AbRunner.Text() + clock + "\n";
            report.Notes.Add(clock);
            foreach (string line in Benchmark.Log)
                report.Notes.Add("frozen for the benchmark: " + line);
            string description = AbRunner.RunKind == AbRunner.Kind.AB ? "Each option switched off (A) and on (B) in ABBA/BAAB blocks; gain = off minus on."
                : AbRunner.RunKind == AbRunner.Kind.AA ? "A/A check: nothing is switched; the result must be 'in noise'."
                : "Baseline with the current settings, compared with the previous baseline session.";
            report.Benchmark = j => AbRunner.Json(j, description);

            string folder = Path.Combine(folderRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_benchmark");
            Directory.CreateDirectory(folder);
            try
            {
                Exporter.WriteAll(folder, report, false);
                ModConsole.Print("<color=orange>FSM Profiler:</color> benchmark done, everything restored. Report: " + folder);
                ModConsole.Print(report.BenchmarkText);
            }
            catch (Exception e)
            {
                ModConsole.Error($"FSM Profiler: writing the benchmark report failed.\n{e}");
            }
        }

        private bool ApplyPatches()
        {
            if (patchFailed)
                return false;

            try
            {
                HarmonyInstance harmony = HarmonyInstance.Create(ID);
                HarmonyMethod prefix = Hook("Prefix");
                harmony.Patch(AccessTools.Method(typeof(Fsm), "Update"), prefix, Hook("UpdatePostfix"), null);
                harmony.Patch(AccessTools.Method(typeof(Fsm), "LateUpdate"), prefix, Hook("LateUpdatePostfix"), null);
                harmony.Patch(AccessTools.Method(typeof(Fsm), "FixedUpdate"), prefix, Hook("FixedUpdatePostfix"), null);
                ActionTimings.TimeEnterExit = timeEnterExit.GetValue();
                ActionTimings.Patch(harmony);
                EventFlow.Patch(harmony);
                ScriptTimings.Patch(harmony);
                Calibration.Patch(harmony);
                if (wasteDetector.GetValue())
                    WasteDetector.Patch(harmony);
                ModConsole.Print($"<color=orange>FSM Profiler:</color> timing {ActionTimings.PatchedMethods} action methods, {ScriptTimings.PatchedMethods} script/mod methods ({ScriptTimings.PatchedCoroutines} coroutines), {WasteDetector.PatchedTypes} write checks.");
                patched = true;
                return true;
            }
            catch (Exception e)
            {
                patchFailed = true;
                ModConsole.Error($"FSM Profiler: patching PlayMaker failed, profiler disabled.\n{e}");
                return false;
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(FsmTimings).GetMethod(name, BindingFlags.Public | BindingFlags.Static));
        }
    }
}
