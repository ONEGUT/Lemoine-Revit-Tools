using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

namespace LemoineTools.Framework
{
    // =========================================================================
    // SeedLibrary — the static, read-only starting point a project gets the
    // first time a library-backed tool is opened in it.
    //
    // Trade and legend libraries are per-project: what you build in one model
    // stays in that model. A brand-new project would therefore start empty,
    // which is right for a user's own work but wrong for an office standard
    // everyone should begin from. The seed closes that gap WITHOUT making the
    // library shared again — it is copied in once, then the project owns its
    // copy outright and edits never travel back or sideways.
    //
    // Two locations, checked in order:
    //   1. %AppData%\LemoineTools\Seed\<name>      — drop a file in, no rebuild
    //   2. <assembly folder>\Seed\<name>           — shipped with the plugin
    //
    // No seed file is a valid, supported state: the project simply starts blank.
    // =========================================================================
    public static class SeedLibrary
    {
        public const string AutoFiltersSeedFile = "AutoFiltersSeed.xml";
        public const string LegendSeedFile      = "LegendSeed.xml";

        /// <summary>User-writable seed folder. Wins over the shipped one.</summary>
        public static string UserSeedFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LemoineTools", "Seed");

        /// <summary>Seed folder deployed beside the add-in.</summary>
        public static string ShippedSeedFolder
        {
            get
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                return Path.Combine(asmDir, "Seed");
            }
        }

        /// <summary>
        /// Full path to a seed file, or null when neither location has one.
        /// Absence is normal — it means new projects start blank.
        /// </summary>
        public static string? Resolve(string fileName)
        {
            try
            {
                string user = Path.Combine(UserSeedFolder, fileName);
                if (File.Exists(user)) return user;

                string shipped = Path.Combine(ShippedSeedFolder, fileName);
                if (File.Exists(shipped)) return shipped;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SeedLibrary: resolve {fileName}", ex);
            }
            return null;
        }

        /// <summary>
        /// Deserializes a seed file into <typeparamref name="T"/>, or returns null when there
        /// is no seed or it cannot be read. A malformed seed is reported as an error rather
        /// than swallowed — the user put that file there deliberately, so silently starting
        /// blank would look like the seed feature simply not working.
        /// </summary>
        public static T? TryLoad<T>(string fileName, string rootElementName) where T : class
        {
            string? path = Resolve(fileName);
            if (path == null)
            {
                DiagnosticsLog.Info("SeedLibrary",
                    $"No {fileName} in {UserSeedFolder} or {ShippedSeedFolder} — new projects start blank.");
                return null;
            }

            try
            {
                var xs = new XmlSerializer(typeof(T), new XmlRootAttribute(rootElementName));
                using (var r = new StreamReader(path))
                {
                    var loaded = xs.Deserialize(r) as T;
                    DiagnosticsLog.Info("SeedLibrary",
                        loaded != null
                            ? $"Seeded from {path}."
                            : $"Seed {path} deserialized as null — starting blank.");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"SeedLibrary: read seed {path} (starting blank instead)", ex);
                return null;
            }
        }
    }
}
