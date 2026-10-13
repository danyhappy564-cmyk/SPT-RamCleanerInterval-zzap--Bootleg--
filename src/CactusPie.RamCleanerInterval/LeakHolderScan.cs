using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Names the mod that keeps the last raid alive (2026-10-14: a SAIN sim kept +0.5 to +1.1 GB of managed memory per raid
    /// after the full GC, and "some mod is holding on" was all this plugin could say).
    /// Back in the menu every raid object (players, bots, corpses, the game world) has been destroyed. A destroyed Unity object
    /// that a static field still points to keeps its whole C# side alive: a Player drags its profile, inventory and
    /// controllers along. So: walk the static fields of every mod DLL and count destroyed Unity objects reachable from each -
    /// the value itself, collection elements / dictionary keys and values, event subscribers, and one field deep inside the
    /// first elements of a collection (a (Player, float) tuple, an info class holding a bot).
    /// Runs in the menu right after the after-raid GC, only when the kept memory is over the suspect line. Bounded: 50k
    /// elements per collection, 64 elements looked into one level deeper, 4 s in total.
    /// Limit: something held deeper than that, or held by the game itself, is not found - "none found" does not clear a mod.
    /// </summary>
    internal static class LeakHolderScan
    {
        private const int MaxElements = 50000;
        private const int MaxDeepElements = 64;
        private const long MaxMs = 4000;

        internal sealed class Holder
        {
            public string Mod;
            public string Field;
            public int Count;
            public string Sample;
        }

        internal sealed class Result
        {
            public readonly List<Holder> Holders = new List<Holder>();
            public int Fields;
            public long Ms;
            public bool Partial;
        }

        public static Result Run()
        {
            var watch = Stopwatch.StartNew();
            var result = new Result();
            Assembly self = typeof(LeakHolderScan).Assembly;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                int mod = assembly == self ? -1 : ModRegistry.IndexOf(assembly);
                if (mod < 0)
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (watch.ElapsedMilliseconds > MaxMs)
                    {
                        result.Partial = true;
                        break;
                    }

                    ScanType(type, ModRegistry.Name(mod), result);
                }

                if (result.Partial)
                {
                    break;
                }
            }

            result.Holders.Sort((a, b) => b.Count.CompareTo(a.Count));
            result.Ms = watch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>One line for the log: "SAIN Foo._list x12 (Player); ...".</summary>
        public static string Describe(Result result, int top)
        {
            string list = result.Holders.Count == 0
                ? "none found"
                : string.Join("; ", result.Holders.Take(top).Select(h => $"{h.Mod}: {h.Field} x{h.Count} ({h.Sample})"));
            return $"{list} [{result.Holders.Count} fields, {result.Fields} static fields scanned in {result.Ms} ms{(result.Partial ? ", stopped at the time limit" : string.Empty)}]";
        }

        /// <summary>Mods holding destroyed raid objects, most first: "SAIN 2, Foo 1" (fields per mod).</summary>
        public static string Mods(Result result, int top)
        {
            return string.Join(", ", result.Holders.GroupBy(h => h.Mod)
                .OrderByDescending(g => g.Sum(h => h.Count))
                .Take(top)
                .Select(g => g.Key));
        }

        private static void ScanType(Type type, string mod, Result result)
        {
            if (type.ContainsGenericParameters)
            {
                return; // static fields of an open generic type can't be read
            }

            FieldInfo[] statics;
            try
            {
                statics = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                return;
            }

            foreach (FieldInfo field in statics)
            {
                Type fieldType = field.FieldType;
                if (field.IsLiteral || fieldType.IsPrimitive || fieldType.IsEnum || fieldType == typeof(string) || fieldType.IsPointer)
                {
                    continue;
                }

                object value;
                try
                {
                    value = field.GetValue(null);
                }
                catch (Exception)
                {
                    continue; // a type initializer that throws, a field the runtime refuses
                }

                if (value == null)
                {
                    continue;
                }

                result.Fields++;
                string sample = null;
                int dead = CountDead(value, 0, ref sample);
                if (dead > 0)
                {
                    result.Holders.Add(new Holder { Mod = mod, Field = $"{type.FullName}.{field.Name}", Count = dead, Sample = sample });
                }
            }
        }

        private static int CountDead(object value, int depth, ref string sample)
        {
            if (value is UnityEngine.Object unityObject)
            {
                // Unity's == says null for a destroyed object whose C# object is still referenced
                if (unityObject == null)
                {
                    sample = sample ?? value.GetType().Name;
                    return 1;
                }

                return 0;
            }

            if (value is Delegate del)
            {
                return Subscribers(del, depth, ref sample);
            }

            if (value is IDictionary dictionary)
            {
                int dead = 0;
                int n = 0;
                try
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (++n > MaxElements)
                        {
                            break;
                        }

                        dead += Element(entry.Key, depth, n, ref sample) + Element(entry.Value, depth, n, ref sample);
                    }
                }
                catch (Exception)
                {
                    // changed while enumerating: keep what was counted
                }

                return dead;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int dead = 0;
                int n = 0;
                try
                {
                    foreach (object element in enumerable)
                    {
                        if (++n > MaxElements)
                        {
                            break;
                        }

                        dead += Element(element, depth, n, ref sample);
                    }
                }
                catch (Exception)
                {
                }

                return dead;
            }

            return depth == 0 ? 0 : Fields(value, ref sample);
        }

        private static int Subscribers(Delegate del, int depth, ref string sample)
        {
            Delegate[] list;
            try
            {
                list = del.GetInvocationList();
            }
            catch (Exception)
            {
                return 0;
            }

            int dead = 0;
            bool first = sample == null;
            foreach (Delegate d in list)
            {
                if (d.Target == null)
                {
                    continue;
                }

                int found = d.Target is UnityEngine.Object ? CountDead(d.Target, depth + 1, ref sample) : (depth == 0 ? Fields(d.Target, ref sample) : 0);
                if (found > 0)
                {
                    if (first)
                    {
                        sample = $"{sample ?? "?"} via event -> {d.Method?.DeclaringType?.Name}.{d.Method?.Name}";
                        first = false;
                    }

                    dead += found;
                }
            }

            return dead;
        }

        private static int Element(object element, int depth, int index, ref string sample)
        {
            if (element == null)
            {
                return 0;
            }

            if (element is UnityEngine.Object || element is Delegate)
            {
                return CountDead(element, depth + 1, ref sample);
            }

            Type type = element.GetType();
            if (depth > 0 || index > MaxDeepElements || type.IsPrimitive || type.IsEnum || element is string)
            {
                return 0;
            }

            return Fields(element, ref sample);
        }

        /// <summary>One level into a plain object or struct: an instance field that is a destroyed Unity object.</summary>
        private static int Fields(object value, ref string sample)
        {
            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string)
            {
                return 0;
            }

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception)
            {
                return 0;
            }

            foreach (FieldInfo field in fields)
            {
                if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(object))
                {
                    continue;
                }

                object inner;
                try
                {
                    inner = field.GetValue(value);
                }
                catch (Exception)
                {
                    continue;
                }

                if (inner is UnityEngine.Object unityObject && unityObject == null)
                {
                    sample = sample ?? $"{type.Name}.{field.Name} -> {inner.GetType().Name}";
                    return 1;
                }
            }

            return 0;
        }
    }
}
