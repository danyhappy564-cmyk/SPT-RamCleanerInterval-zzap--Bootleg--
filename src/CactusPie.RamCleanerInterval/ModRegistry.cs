using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Maps loaded assemblies to "mods" (index + display name) for the per-mod profiler and the per-mod object
    /// counter, and produces the mod list fingerprint the performance history compares between raids.
    /// A mod = an assembly loaded from BepInEx\plugins. Its name is the BepInEx plugin name when the assembly
    /// holds a plugin, the assembly name otherwise; SPT's own plugins are grouped as "SPT".
    /// </summary>
    internal static class ModRegistry
    {
        public const int MaxMods = 512;

        private static readonly Dictionary<Assembly, int> s_index = new Dictionary<Assembly, int>();
        private static readonly List<string> s_names = new List<string>();
        private static bool s_built;

        public static int Count => s_names.Count;

        public static string Name(int index)
        {
            return index >= 0 && index < s_names.Count ? s_names[index] : "?";
        }

        /// <summary>Mod index of an assembly, or -1 when it is the game, Unity, BepInEx, Harmony or this plugin.</summary>
        public static int IndexOf(Assembly assembly)
        {
            Build();
            return assembly != null && s_index.TryGetValue(assembly, out int index) ? index : -1;
        }

        public static void Build()
        {
            if (s_built)
            {
                return;
            }

            s_built = true;
            var pluginNames = new Dictionary<Assembly, string>();
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                Assembly assembly = info?.Instance != null ? info.Instance.GetType().Assembly : null;
                if (assembly != null && !pluginNames.ContainsKey(assembly))
                {
                    pluginNames[assembly] = info.Metadata.Name;
                }
            }

            Assembly self = typeof(ModRegistry).Assembly;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == self || assembly.IsDynamic)
                {
                    continue;
                }

                string location;
                try
                {
                    location = assembly.Location;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(location) ||
                    location.IndexOf(Path.DirectorySeparatorChar + "plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0 ||
                    location.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string assemblyName = assembly.GetName().Name;
                if (assemblyName.IndexOf("ModProfiler", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue; // another profiler: its wrappers would be measured twice
                }

                string name = location.IndexOf(Path.DirectorySeparatorChar + "spt" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                              assemblyName.StartsWith("spt-", StringComparison.OrdinalIgnoreCase)
                    ? "SPT"
                    : pluginNames.TryGetValue(assembly, out string pluginName) ? pluginName : assemblyName;

                if (!byName.TryGetValue(name, out int index))
                {
                    if (s_names.Count >= MaxMods)
                    {
                        continue;
                    }

                    index = s_names.Count;
                    s_names.Add(name);
                    byName[name] = index;
                }

                s_index[assembly] = index;
            }
        }

        /// <summary>
        /// "name@version@stamp" for every loaded plugin. The stamp is the DLL's write time, so a rebuilt fork with
        /// an unchanged version number still shows up as changed.
        /// </summary>
        public static List<string> Fingerprint()
        {
            var list = new List<string>();
            foreach (var info in Chainloader.PluginInfos.Values.OrderBy(i => i.Metadata.GUID, StringComparer.Ordinal))
            {
                string stamp = "0";
                try
                {
                    if (!string.IsNullOrEmpty(info.Location) && File.Exists(info.Location))
                    {
                        stamp = File.GetLastWriteTimeUtc(info.Location).ToString("yyyyMMddHHmm");
                    }
                }
                catch (Exception)
                {
                    // keep "0"
                }

                list.Add($"{Clean(info.Metadata.Name)}@{info.Metadata.Version}@{stamp}");
            }

            return list;
        }

        private static string Clean(string value)
        {
            return (value ?? "?").Replace("@", "_").Replace(";", "_").Replace("|", "_");
        }
    }
}
