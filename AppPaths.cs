using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace GlassBar
{
    /// <summary>
    /// Turns the program file behind a window or pin into one stable path per app, so that a pin keeps working
    /// and matches its running windows:
    ///
    ///  - Apps that update with Squirrel (Discord, Slack, Teams classic...) live in a new "app-1.2.3" folder
    ///    after every update, and the old folder is deleted. Any "app-*" path maps to the newest one installed.
    ///  - Steam's windows belong to its web helper (bin\cef\...\steamwebhelper.exe), which cannot be launched
    ///    on its own. They map to steam.exe.
    /// </summary>
    internal static class AppPaths
    {
        private static readonly Regex SquirrelPath = new Regex(
            @"^(?<root>.+)\\app-(?<version>\d+(?:\.\d+)*)\\(?<rest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly ConcurrentDictionary<string, string> Cache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Canonical(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path ?? "";
            }

            // Re-check once the cached answer has disappeared (the app updated again during this session).
            if (Cache.TryGetValue(path, out string cached) && File.Exists(cached))
            {
                return cached;
            }

            string result = Resolve(path);
            Cache[path] = result;
            return result;
        }

        private static string Resolve(string path)
        {
            try
            {
                string steam = SteamFromWebHelper(path);
                if (steam != null)
                {
                    return steam;
                }

                string squirrel = NewestSquirrelVersion(path);
                if (squirrel != null)
                {
                    return squirrel;
                }
            }
            catch (Exception)
            {
                // Unreadable folder or odd path: keep it as it is.
            }

            return path;
        }

        private static string SteamFromWebHelper(string path)
        {
            if (!string.Equals(Path.GetFileName(path), "steamwebhelper.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int bin = path.IndexOf(@"\bin\cef\", StringComparison.OrdinalIgnoreCase);
            if (bin <= 0)
            {
                return null;
            }

            string steam = Path.Combine(path.Substring(0, bin), "steam.exe");
            return File.Exists(steam) ? steam : null;
        }

        private static string NewestSquirrelVersion(string path)
        {
            Match match = SquirrelPath.Match(path);
            if (!match.Success)
            {
                return null;
            }

            string root = match.Groups["root"].Value;
            string rest = match.Groups["rest"].Value;

            // Squirrel installs always have Update.exe next to the app-* folders.
            if (!File.Exists(Path.Combine(root, "Update.exe")))
            {
                return null;
            }

            string best = null;
            Version bestVersion = null;
            foreach (string dir in Directory.EnumerateDirectories(root, "app-*"))
            {
                if (!Version.TryParse(Path.GetFileName(dir).Substring(4), out Version version))
                {
                    continue;
                }

                string candidate = Path.Combine(dir, rest);
                if ((bestVersion == null || version > bestVersion) && File.Exists(candidate))
                {
                    best = candidate;
                    bestVersion = version;
                }
            }

            return best;
        }
    }
}
