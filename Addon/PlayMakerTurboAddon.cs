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
        public override string Description => "Removes the ~185 ms freeze when you first get into a car: force feedback is set up once while the game loads instead of once per car.";
        public override Game SupportedGames => Game.MyWinterCar;

        public override void ModSetup()
        {
            SetupFunction(Setup.OnLoad, Mod_OnLoad, "Setting up force feedback...");
        }

        private void Mod_OnLoad()
        {
            ForceFeedbackInit.Apply(HarmonyInstance.Create(ID));
        }
    }
}
