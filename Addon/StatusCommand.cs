using MSCLoader;

namespace PlayMakerTurboAddon
{
    internal class StatusCommand : ConsoleCommand
    {
        private static bool added;

        public override string Name => "turboaddon";
        public override string Help => "Shows what PlayMaker Turbo Addon is doing right now.";

        public static void AddOnce()
        {
            if (added)
                return;
            ConsoleCommand.Add(new StatusCommand());
            added = true;
        }

        public override void Run(string[] args)
        {
            ModConsole.Print(ForceFeedbackInit.Summary());
            ModConsole.Print(CInputGuiSwitch.Summary());
            ModConsole.Print(IKSkipSettled.Summary());
            ModConsole.Print(CenterOfMassSkip.Summary());
        }
    }
}
