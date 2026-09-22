using System;
using System.Windows.Forms;

namespace PlayMakerTurboInstaller
{
    internal static class Program
    {
        // Without arguments the window opens. With a path it runs on the command line: install, --restore to
        // undo, or --patch-only to patch PlayMaker.dll without the runtime, which build.ps1 needs because the
        // runtime is compiled against the patched PlayMaker.dll.
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }

            string managed = GameLocator.ToManaged(args[0]) ?? args[0];
            try
            {
                if (Array.IndexOf(args, "--restore") >= 0)
                    Installer.Uninstall(managed, Console.WriteLine);
                else if (Array.IndexOf(args, "--patch-only") >= 0)
                    PatchEngine.Patch(managed, Console.WriteLine);
                else
                {
                    Console.WriteLine(Installer.BetaNotice);
                    Console.WriteLine();
                    Installer.Install(managed, Console.WriteLine);
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        }
    }
}
