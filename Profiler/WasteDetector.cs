using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MWCFsmProfiler
{
    // Counts per-frame writes that would not change anything: the target already holds the value. Those
    // everyFrame actions are the cheapest optimizations to find (the FSM Controls on Systems/OptionsDB was one).
    // Each check reads the action's own fields before OnUpdate runs, compiled against the game's types so it
    // needs no reflection and allocates nothing. A type missing after a game update is skipped.
    internal static class WasteDetector
    {
        public static int PatchedTypes;

        public static void Patch(HarmonyInstance harmony)
        {
            Try(harmony, () => typeof(SetFloatValue), "SetFloat");
            Try(harmony, () => typeof(SetIntValue), "SetInt");
            Try(harmony, () => typeof(SetBoolValue), "SetBool");
            Try(harmony, () => typeof(SetStringValue), "SetString");
            Try(harmony, () => typeof(SetVector3Value), "SetVector3");
            Try(harmony, () => typeof(SetColorValue), "SetColor");
            Try(harmony, () => typeof(FloatClamp), "ClampFloat");
            Try(harmony, () => typeof(IntClamp), "ClampInt");
            Try(harmony, () => typeof(ActivateGameObject), "Activate");
            Try(harmony, () => typeof(SetGameVolume), "GameVolume");
            Try(harmony, () => typeof(SetAudioVolume), "AudioVolume");
            Try(harmony, () => typeof(SetAudioPitch), "AudioPitch");
            Try(harmony, () => typeof(SetLightIntensity), "LightIntensity");
            Try(harmony, () => typeof(CInputSetAxisInverted), "AxisInverted");
        }

        private static void Try(HarmonyInstance harmony, Func<Type> type, string check)
        {
            try
            {
                MethodInfo method = type().GetMethod("OnUpdate", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method == null)
                    return;
                // Runs before the timing prefix, so the check's own cost is not booked to the action.
                HarmonyMethod prefix = new HarmonyMethod(typeof(WasteDetector).GetMethod(check, BindingFlags.Public | BindingFlags.Static)) { prioritiy = Priority.First };
                harmony.Patch(method, prefix, null, null);
                PatchedTypes++;
            }
            catch (Exception e)
            {
                MSCLoader.ModConsole.Print($"FSM Profiler: write check '{check}' skipped: {(e.InnerException ?? e).Message}");
            }
        }

        private static void Count(FsmStateAction action, bool same)
        {
            ActionEntry entry = ActionTimings.Get(action);
            entry.WasteChecked++;
            if (same)
                entry.WasteSame++;
        }

        public static void SetFloat(SetFloatValue __instance)
        {
            if (Probe.Recording && !__instance.floatVariable.IsNone)
                Count(__instance, __instance.floatVariable.Value == __instance.floatValue.Value);
        }

        public static void SetInt(SetIntValue __instance)
        {
            if (Probe.Recording && !__instance.intVariable.IsNone)
                Count(__instance, __instance.intVariable.Value == __instance.intValue.Value);
        }

        public static void SetBool(SetBoolValue __instance)
        {
            if (Probe.Recording && !__instance.boolVariable.IsNone)
                Count(__instance, __instance.boolVariable.Value == __instance.boolValue.Value);
        }

        public static void SetString(SetStringValue __instance)
        {
            if (Probe.Recording && !__instance.stringVariable.IsNone)
                Count(__instance, string.Equals(__instance.stringVariable.Value, __instance.stringValue.Value));
        }

        public static void SetVector3(SetVector3Value __instance)
        {
            if (Probe.Recording && !__instance.vector3Variable.IsNone)
                Count(__instance, __instance.vector3Variable.Value == __instance.vector3Value.Value);
        }

        public static void SetColor(SetColorValue __instance)
        {
            if (Probe.Recording && !__instance.colorVariable.IsNone)
                Count(__instance, __instance.colorVariable.Value == __instance.color.Value);
        }

        public static void ClampFloat(FloatClamp __instance)
        {
            if (!Probe.Recording || __instance.floatVariable.IsNone)
                return;
            float v = __instance.floatVariable.Value;
            Count(__instance, v >= __instance.minValue.Value && v <= __instance.maxValue.Value);
        }

        public static void ClampInt(IntClamp __instance)
        {
            if (!Probe.Recording || __instance.intVariable.IsNone)
                return;
            int v = __instance.intVariable.Value;
            Count(__instance, v >= __instance.minValue.Value && v <= __instance.maxValue.Value);
        }

        public static void Activate(ActivateGameObject __instance)
        {
            if (!Probe.Recording || __instance.Fsm == null)
                return;
            GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
            if (go != null)
                Count(__instance, go.activeSelf == __instance.activate.Value);
        }

        public static void GameVolume(SetGameVolume __instance)
        {
            if (Probe.Recording)
                Count(__instance, AudioListener.volume == __instance.volume.Value);
        }

        public static void AudioVolume(SetAudioVolume __instance)
        {
            AudioSource audio = Audio(__instance, __instance.gameObject);
            if (audio != null && !__instance.volume.IsNone)
                Count(__instance, audio.volume == __instance.volume.Value);
        }

        public static void AudioPitch(SetAudioPitch __instance)
        {
            AudioSource audio = Audio(__instance, __instance.gameObject);
            if (audio != null && !__instance.pitch.IsNone)
                Count(__instance, audio.pitch == __instance.pitch.Value);
        }

        public static void LightIntensity(SetLightIntensity __instance)
        {
            if (!Probe.Recording || __instance.Fsm == null)
                return;
            GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
            Light light = go != null ? go.GetComponent<Light>() : null;
            if (light != null)
                Count(__instance, light.intensity == __instance.lightIntensity.Value);
        }

        public static void AxisInverted(CInputSetAxisInverted __instance)
        {
            if (Probe.Recording && !__instance.axisName.IsNone)
                Count(__instance, cInput.AxisInverted(__instance.axisName.Value) == __instance.axisInverted.Value);
        }

        private static AudioSource Audio(FsmStateAction action, FsmOwnerDefault target)
        {
            if (!Probe.Recording || action.Fsm == null)
                return null;
            GameObject go = action.Fsm.GetOwnerDefaultTarget(target);
            return go != null ? go.GetComponent<AudioSource>() : null;
        }
    }
}
