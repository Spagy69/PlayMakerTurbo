using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Harmony;
using MSCLoader;

namespace PlayMakerTurboAddon
{
    // ForceFeedback.Start (one component per car) calls InitialiseForceFeedback, which makes UnityForceFeedback.dll
    // create DirectInput and enumerate every game controller in the system: about 185 ms, the freeze when you first
    // sit in a car. The plugin keeps one device for the whole process and frees it only on quit, yet every car sets
    // it up again. Here it is set up once while the game loads; after that a car's InitialiseForceFeedback only
    // marks the car as running, which is the state the original leaves behind.
    internal static class ForceFeedbackInit
    {
        private static bool patched;
        private static bool initialised;
        private static FieldInfo enabledField;

        [DllImport("user32")]
        private static extern int GetForegroundWindow();

        [DllImport("user32")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // Same imports and signatures as ForceFeedback, so both talk to the same loaded plugin.
        [DllImport("UnityForceFeedback")]
        private static extern int InitDirectInput(int HWND);

        [DllImport("UnityForceFeedback")]
        private static extern void Aquire();

        [DllImport("UnityForceFeedback")]
        private static extern bool StartEffect();

        public static void Apply(HarmonyInstance harmony)
        {
            if (!patched)
            {
                MethodInfo method = AccessTools.Method(typeof(ForceFeedback), "InitialiseForceFeedback");
                enabledField = AccessTools.Field(typeof(ForceFeedback), "forceFeedbackEnabled");
                if (method == null || enabledField == null)
                {
                    ModConsole.Error("PlayMaker Turbo Addon: ForceFeedback.InitialiseForceFeedback or forceFeedbackEnabled is missing, the game has changed. The force feedback fix is off.");
                    return;
                }
                harmony.Patch(method, new HarmonyMethod(typeof(ForceFeedbackInit).GetMethod("Prefix")), new HarmonyMethod(typeof(ForceFeedbackInit).GetMethod("Postfix")), null);
                patched = true;
            }
            if (!initialised)
                InitialiseNow();
        }

        // The original binds DirectInput to the foreground window. If the player switched away while the game was
        // loading, that would be some other program's window, so the first car sets it up as it always did.
        private static void InitialiseNow()
        {
            int window = GetForegroundWindow();
            uint processId;
            GetWindowThreadProcessId(new IntPtr(window), out processId);
            if (processId != (uint)Process.GetCurrentProcess().Id)
            {
                ModConsole.Print("PlayMaker Turbo Addon: the game window was not in front while loading, force feedback will be set up by the first car you drive.");
                return;
            }

            InitDirectInput(window);
            Aquire();
            StartEffect();
            initialised = true;
        }

        public static string Summary()
        {
            return "Force feedback: " + (!patched ? "off" : initialised ? "set up once, cars reuse it" : "not set up yet, the first car will do it");
        }

        public static bool Prefix(ForceFeedback __instance)
        {
            if (!initialised)
                return true;
            enabledField.SetValue(__instance, true);
            return false;
        }

        public static void Postfix()
        {
            initialised = true;
        }
    }
}
