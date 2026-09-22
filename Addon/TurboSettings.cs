using System;
using System.Collections.Generic;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace PlayMakerTurboAddon
{
    // PlayMaker Turbo's switches from PlayMakerTurbo.ini as checkboxes. The ini stays the source of truth, because
    // Turbo reads it when the game starts and people also edit it by hand: the checkboxes are set from it when the
    // settings load, and every change is written straight back to it.
    internal static class TurboSettings
    {
        // Key, label, default. Same order and defaults as the ini the installer writes.
        private static readonly string[] keys =
        {
            "IdleUpdateSkip", "LateUpdateSkip", "FixedUpdateSkip", "DelayedEventsEarlyOut", "GameObjectCache",
            "SkipNonUpdatingActions", "MousePickSingleCameraLookup", "SetGameVolumeSkipUnchanged", "PropertyDelegates",
            "CInputAxisInvertedBuilder", "ActiveLists", "FastEventRouting", "MousePickFrameCache", "ActiveFast"
        };

        private static readonly string[] labels =
        {
            "Skip Update of FSMs that finished and wait for nothing",
            "Skip LateUpdate when no active action uses it",
            "Skip FixedUpdate when no active action uses it",
            "Return from delayed events right away when there are none",
            "Cache the FSM owner's GameObject",
            "Do not call actions that have no OnUpdate",
            "Fetch Camera.main once per mouse pick instead of twice",
            "Set the game volume only when it changes",
            "GetProperty / SetProperty without reflection (no garbage)",
            "cInput stores inverted axis settings only when they change",
            "Tick only woken FSMs instead of walking all of them",
            "Send events to one GameObject without walking all FSMs",
            "Share mouse pick raycast in a frame (almost identical)",
            "Fsm.Active via isActiveAndEnabled (NOT identical)"
        };

        private static readonly bool[] defaults = { true, true, true, true, true, true, true, true, true, true, true, true, false, false };

        private static string iniPath;
        private static readonly Dictionary<string, SettingsCheckBox> boxes = new Dictionary<string, SettingsCheckBox>();

        public static void Add()
        {
            iniPath = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "PlayMakerTurbo.ini");
            Settings.AddHeader("PlayMaker Turbo");
            if (!File.Exists(iniPath))
            {
                Settings.AddText("PlayMaker Turbo is not installed (no PlayMakerTurbo.ini next to PlayMaker.dll).");
                return;
            }
            Settings.AddText("Saved to PlayMakerTurbo.ini. Changes apply after restarting the game. If the game misbehaves, turn options off one by one to find the cause.");
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                boxes[key] = Settings.AddCheckBox("turbo" + key, labels[i], defaults[i], () => Write(key));
            }
        }

        public static void LoadFromIni()
        {
            if (boxes.Count == 0)
                return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(iniPath);
            }
            catch (Exception e)
            {
                ModConsole.Error("PlayMaker Turbo Addon: cannot read " + iniPath + "\n" + e.GetFullMessage());
                return;
            }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                SettingsCheckBox box;
                if (eq > 0 && boxes.TryGetValue(line.Substring(0, eq).Trim(), out box))
                {
                    bool value = line.Substring(eq + 1).Trim() == "1";
                    if (box.GetValue() != value)
                        box.SetValue(value);
                }
            }
        }

        private static void Write(string key)
        {
            string entry = key + "=" + (boxes[key].GetValue() ? "1" : "0");
            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(iniPath));
                int at = lines.FindIndex(l => l.Trim().StartsWith(key + "=", StringComparison.Ordinal));
                if (at >= 0)
                {
                    if (lines[at].Trim() == entry)
                        return;
                    lines[at] = entry;
                }
                else
                {
                    lines.Add(entry);
                }
                File.WriteAllLines(iniPath, lines.ToArray());
            }
            catch (Exception e)
            {
                ModConsole.Error("PlayMaker Turbo Addon: cannot write " + iniPath + "\n" + e.GetFullMessage());
            }
        }
    }
}
