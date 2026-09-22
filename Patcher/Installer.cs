using System;
using System.Diagnostics;
using System.IO;

namespace PlayMakerTurboInstaller
{
    // Puts the files the patch needs next to PlayMaker.dll: the patched assembly itself (PatchEngine),
    // the runtime PlayMakerTurbo.dll shipped with this installer, and PlayMakerTurbo.ini with the switches.
    internal static class Installer
    {
        public const string RuntimeName = "PlayMakerTurbo.dll";
        public const string SettingsName = "PlayMakerTurbo.ini";

        public const string SaveFolder = @"%USERPROFILE%\AppData\LocalLow\Amistech\My Winter Car";

        public const string BetaNotice =
            "PlayMaker Turbo is beta software. It patches PlayMaker.dll, and it may crash the game, break " +
            "game logic or damage your save.\r\n\r\n" +
            "Before you play, copy your save folder somewhere safe:\r\n" + SaveFolder + "\r\n\r\n" +
            "The software is provided as is, without warranty of any kind. You use it at your own risk. " +
            "It is not affiliated with Amistech Games or Hutong Games.";

        public static bool GameRunning()
        {
            return Process.GetProcessesByName("mywintercar").Length > 0;
        }

        public static void Install(string managed, Action<string> log)
        {
            string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RuntimeName);
            if (!File.Exists(runtime))
                throw new FileNotFoundException(RuntimeName + " is missing next to the installer. Unpack the whole release into one folder.");

            PatchEngine.Patch(managed, log);
            File.Copy(runtime, Path.Combine(managed, RuntimeName), true);
            log(RuntimeName + " copied.");

            string settings = Path.Combine(managed, SettingsName);
            if (File.Exists(settings))
            {
                log(SettingsName + " left as it was, your settings are kept.");
            }
            else
            {
                File.WriteAllText(settings, DefaultSettings, System.Text.Encoding.ASCII);
                log(SettingsName + " written with default settings.");
            }
        }

        public static void Uninstall(string managed, Action<string> log)
        {
            PatchEngine.Restore(managed, log);
        }

        public static string Status(string managed)
        {
            string dll = Path.Combine(managed, "PlayMaker.dll");
            if (!File.Exists(dll))
                return "no PlayMaker.dll in this folder.";
            if (!PatchEngine.IsPatched(dll))
                return "game is untouched.";
            return File.Exists(Path.Combine(managed, RuntimeName))
                ? "installed."
                : "PlayMaker.dll is patched but " + RuntimeName + " is missing. The game will not start like this, press Install.";
        }

        private const string DefaultSettings = @"# PlayMaker Turbo (beta). If the game misbehaves, set entries to 0 one by one to find the cause.
# Every optimization can be turned off (0) or on (1). Read when the game starts.
# Everything except ActiveFast and MousePickFrameCache gives the same result as stock PlayMaker.

# Skip Fsm.Update for FSMs whose state has finished and that wait for nothing
IdleUpdateSkip=1
# Skip Fsm.LateUpdate when no active action overrides OnLateUpdate
LateUpdateSkip=1
# Skip Fsm.FixedUpdate when no active action overrides OnFixedUpdate
FixedUpdateSkip=1
# Return from UpdateDelayedEvents right away when there are no delayed events
DelayedEventsEarlyOut=1
# Cache the owner's GameObject instead of asking the engine for it
GameObjectCache=1
# Do not call actions that have no OnUpdate (the base method is empty)
SkipNonUpdatingActions=1
# Fetch Camera.main once per DoMousePick instead of twice
MousePickSingleCameraLookup=1
# SetGameVolume writes AudioListener.volume only when it differs
SetGameVolumeSkipUnchanged=1
# GetProperty/SetProperty through typed delegates instead of reflection (no allocations)
PropertyDelegates=1
# cInput stores the inverted axis settings only when they changed
CInputAxisInvertedBuilder=1
# Fsm.Active through isActiveAndEnabled. LEAVE OFF: not identical to the original.
ActiveFast=0

# --- ALMOST identical (off by default) ---
# Share the mouse pick raycast within a frame per layer mask. Differs from the original only when the
# camera or a collider moves between two picks inside one frame. Saves about 190 raycasts per frame.
MousePickFrameCache=0
";
    }
}
