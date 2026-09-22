using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PlayMakerTurboInstaller
{
    // Finds My Winter Car by asking Steam where its libraries are. Steam keeps the client path in the registry
    // and the list of library folders in steamapps\libraryfolders.vdf, which is a text file with "path" entries.
    internal static class GameLocator
    {
        private const string GameFolder = "My Winter Car";
        private const string DataFolder = "mywintercar_Data";

        // Returns the Managed folder of the game, or null when nothing was found.
        public static string FindManaged()
        {
            foreach (string library in Libraries())
            {
                string managed = Path.Combine(library, Path.Combine("steamapps", Path.Combine("common", Path.Combine(GameFolder, Path.Combine(DataFolder, "Managed")))));
                if (Directory.Exists(managed) && File.Exists(Path.Combine(managed, "PlayMaker.dll")))
                    return managed;
            }
            return null;
        }

        // Accepts the game folder, the _Data folder or the Managed folder itself.
        public static string ToManaged(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return null;
            folder = folder.TrimEnd(Path.DirectorySeparatorChar);

            string[] candidates =
            {
                folder,
                Path.Combine(folder, "Managed"),
                Path.Combine(folder, Path.Combine(DataFolder, "Managed")),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "PlayMaker.dll")))
                    return candidate;
            }
            return null;
        }

        private static IEnumerable<string> Libraries()
        {
            string steam = SteamPath();
            if (steam == null)
                yield break;

            yield return steam;
            string vdf = Path.Combine(steam, Path.Combine("steamapps", "libraryfolders.vdf"));
            if (!File.Exists(vdf))
                yield break;

            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                yield return match.Groups[1].Value.Replace("\\\\", "\\");
        }

        private static string SteamPath()
        {
            foreach (string key in new[] { @"Software\Valve\Steam", @"Software\Wow6432Node\Valve\Steam" })
            {
                foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using (RegistryKey sub = root.OpenSubKey(key))
                    {
                        string path = sub == null ? null : sub.GetValue("SteamPath") as string ?? sub.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                            return path.Replace('/', '\\');
                    }
                }
            }
            return null;
        }
    }
}
