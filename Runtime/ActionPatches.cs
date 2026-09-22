using System;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace PlayMakerTurbo
{
    // Harmony patches for the game's actions (Assembly-CSharp) and cInput. Applied at runtime rather than by
    // editing the files, so a game update does not need a re-patch; a target that changed is skipped and logged.
    internal static class ActionPatches
    {
        private static Func<bool[]> getInvertAxis;
        private static Func<int> getAxisLength;
        private static Func<bool> getUsePlayerPrefs;
        private static Action<string> setExAxisInverted;
        private static readonly StringBuilder axisInvertedText = new StringBuilder(1024);
        private static bool[] lastInverted = new bool[0];
        private static int lastCount = -1;
        private static string lastText;
        private const string AxisInvertedKey = "cInput_axInv";

        public static void Apply()
        {
            HarmonyInstance harmony = HarmonyInstance.Create("PlayMakerTurbo");
            if (Core.SetGameVolumeSkipUnchanged)
                TryPatch(harmony, typeof(SetGameVolume), "OnUpdate", "SetGameVolumeOnUpdate");
            if (Core.CInputAxisInvertedBuilder && ResolveCInputFields())
                TryPatch(harmony, typeof(cInput), "_SaveAxInverted", "SaveAxInverted");
        }

        private static void TryPatch(HarmonyInstance harmony, Type type, string method, string prefix)
        {
            try
            {
                MethodInfo original = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (original == null)
                {
                    Debug.LogWarning($"PlayMakerTurbo: {type.Name}.{method} not found, patch skipped.");
                    return;
                }
                harmony.Patch(original, new HarmonyMethod(typeof(ActionPatches).GetMethod(prefix, BindingFlags.Public | BindingFlags.Static)), null, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"PlayMakerTurbo: patching {type.Name}.{method} failed, skipped.\n{e}");
            }
        }

        // SetGameVolume.OnUpdate sets AudioListener.volume every frame; setting the value it already has is a no-op.
        public static bool SetGameVolumeOnUpdate(SetGameVolume __instance)
        {
            float volume = __instance.volume.Value;
            if (AudioListener.volume != volume)
                AudioListener.volume = volume;
            return false;
        }

        private static bool ResolveCInputFields()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo invertAxis = typeof(cInput).GetField("_invertAxis", flags);
            FieldInfo axisLength = typeof(cInput).GetField("_axisLength", flags);
            FieldInfo usePlayerPrefs = typeof(cInput).GetField("_usePlayerPrefs", flags);
            FieldInfo exAxisInverted = typeof(cInput).GetField("_exAxisInverted", flags);
            if (invertAxis == null || axisLength == null || usePlayerPrefs == null || exAxisInverted == null)
            {
                Debug.LogWarning("PlayMakerTurbo: cInput fields changed, _SaveAxInverted patch skipped.");
                return false;
            }

            // FieldInfo.GetValue boxes int/bool on every call; emitted accessors read the fields directly.
            getInvertAxis = StaticField.Getter<bool[]>(invertAxis);
            getAxisLength = StaticField.Getter<int>(axisLength);
            getUsePlayerPrefs = StaticField.Getter<bool>(usePlayerPrefs);
            setExAxisInverted = StaticField.Setter<string>(exAxisInverted);
            return true;
        }

        // cInput._SaveAxInverted, which CInputSetAxisInverted calls every frame. The original rebuilds the string
        // with `text = text + bool + "*"` per axis and writes it to PlayerPrefs (the registry on Windows, ~40 us).
        public static bool SaveAxInverted()
        {
            bool[] inverted = getInvertAxis();
            int count = getAxisLength() + 1;
            bool usePrefs = getUsePlayerPrefs();

            // Same axis states as the last call: the text would be identical, and the key still holds it
            // (only cInput writes this key; HasKey catches a DeleteKey/DeleteAll in between).
            if (lastText != null && count == lastCount && SameAsLast(inverted, count) && (!usePrefs || PlayerPrefs.HasKey(AxisInvertedKey)))
            {
                setExAxisInverted(lastText);
                return false;
            }

            axisInvertedText.Length = 0;
            for (int i = 0; i < count; i++)
                axisInvertedText.Append(inverted[i] ? bool.TrueString : bool.FalseString).Append('*');
            string text = axisInvertedText.ToString();

            // Writing the value the key already holds changes nothing, so only write when it differs.
            if (usePrefs && (!PlayerPrefs.HasKey(AxisInvertedKey) || PlayerPrefs.GetString(AxisInvertedKey) != text))
                PlayerPrefs.SetString(AxisInvertedKey, text);
            setExAxisInverted(text);

            // Only remembered when the key now holds this text, so the shortcut above stays exact.
            lastText = usePrefs ? text : null;
            lastCount = count;
            if (lastInverted.Length < count)
                lastInverted = new bool[count];
            Array.Copy(inverted, lastInverted, count);
            return false;
        }

        private static bool SameAsLast(bool[] inverted, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (inverted[i] != lastInverted[i])
                    return false;
            }
            return true;
        }
    }
}
