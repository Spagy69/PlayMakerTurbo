using Harmony;
using MSCLoader;

namespace PlayMakerTurboAddon
{
    public class PlayMakerTurboAddon : Mod
    {
        public override string ID => "PlayMakerTurboAddon";
        public override string Name => "PlayMaker Turbo Addon";
        public override string Author => "Spagy";
        public override string Version => "1.0";
        public override string Description => "Fixes in the game's own scripts: no ~185 ms freeze when you first get into a car, no GUI garbage from the closed key binding menu, IK of parked cars is not recomputed while nothing moves, cars do not recompute their mass on every physics step. Console: turboaddon";
        public override Game SupportedGames => Game.MyWinterCar;

        private SettingsCheckBox forceFeedback, keyBindingGui, parkedIK, centerOfMass;

        public override void ModSetup()
        {
            SetupFunction(Setup.ModSettings, Mod_ModSettings);
            SetupFunction(Setup.ModSettingsLoaded, Mod_ModSettingsLoaded);
            SetupFunction(Setup.OnLoad, Mod_OnLoad, "Setting up force feedback...");
        }

        private void Mod_ModSettings()
        {
            // Read once when the game loads, because a Harmony patch is not taken back while the game runs.
            Settings.AddHeader("Addon fixes");
            Settings.AddText("Each fix can be turned off here. Changes apply after restarting the game. Force feedback: no ~185 ms freeze when you first get into a car. Suspension IK: joints stay within 0.1 mm of the exact result.");
            forceFeedback = Settings.AddCheckBox("forceFeedback", "Set up force feedback once while loading", true);
            keyBindingGui = Settings.AddCheckBox("keyBindingGui", "Switch off the key binding menu GUI while the menu is closed", true);
            parkedIK = Settings.AddCheckBox("parkedIK", "Skip suspension IK while nothing moves", true);
            centerOfMass = Settings.AddCheckBox("centerOfMass", "Set a car's centre of mass only when it changes", true);
            TurboSettings.Add();
        }

        private void Mod_ModSettingsLoaded()
        {
            TurboSettings.LoadFromIni();
        }

        private void Mod_OnLoad()
        {
            HarmonyInstance harmony = HarmonyInstance.Create(ID);
            if (forceFeedback.GetValue())
                ForceFeedbackInit.Apply(harmony);
            if (keyBindingGui.GetValue())
                CInputGuiSwitch.Apply();
            if (parkedIK.GetValue())
                IKSkipSettled.Apply(harmony);
            if (centerOfMass.GetValue())
                CenterOfMassSkip.Apply(harmony);
            StatusCommand.AddOnce();
        }
    }
}
