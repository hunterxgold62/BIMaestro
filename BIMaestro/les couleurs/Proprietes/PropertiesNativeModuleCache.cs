using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BIMaestro.PropertiesAppearance;

namespace BIMaestro.PropertiesAppearance
{
    // Retain qualified immutable native bindings, never mutable palette state.
    internal static class PropertiesNativeModuleCache
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, ProcessModule> Modules = new Dictionary<string, ProcessModule>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<(string Name, string Hash, IntPtr Base)> Qualifications = new HashSet<(string, string, IntPtr)>();
        private static readonly Dictionary<(IntPtr Base, string Name, Type Type), Delegate> Exports = new Dictionary<(IntPtr, string, Type), Delegate>();

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        internal static ProcessModule Loaded(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A loaded module name is required.", nameof(name));
            lock (Sync)
            {
                IntPtr current = GetModuleHandle(name);
                if (current != IntPtr.Zero && Modules.TryGetValue(name, out var cached) && cached.BaseAddress == current) return cached;
                Modules.Remove(name);
                if (current == IntPtr.Zero) throw new InvalidOperationException("Required native module is not loaded: " + name);
                long started = PropertiesAppearanceMetrics.Begin();
                try
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        var module = process.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase));
                        if (GetModuleHandle(name) != module.BaseAddress) throw new InvalidOperationException("Native module changed during discovery: " + name);
                        Modules.Add(name, module);
                        return module;
                    }
                }
                finally { PropertiesAppearanceMetrics.End("group.module-discovery", started); }
            }
        }

        internal static ProcessModule Qualified(string name, string expectedHash)
        {
            if (expectedHash == null || expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("An exact SHA256 digest is required.", nameof(expectedHash));
            lock (Sync)
            {
                var module = Loaded(name);
                var key = (name.ToUpperInvariant(), expectedHash.ToUpperInvariant(), module.BaseAddress);
                if (Qualifications.Contains(key)) return module;
                long started = PropertiesAppearanceMetrics.Begin();
                try
                {
                    string actual;
                    using (var stream = File.OpenRead(module.FileName))
                    using (var sha = SHA256.Create())
                        actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Native module SHA256 qualification failed: " + name + " (" + actual + ")");
                    if (GetModuleHandle(name) != module.BaseAddress) throw new InvalidOperationException("Native module changed during qualification: " + name);
                    Qualifications.Add(key);
                    return module;
                }
                finally { PropertiesAppearanceMetrics.End("group.module-qualification", started); }
            }
        }

        internal static T Export<T>(IntPtr module, string name) where T : Delegate
        {
            if (module == IntPtr.Zero) throw new ArgumentException("A loaded native module is required.", nameof(module));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("An exact native export name is required.", nameof(name));
            lock (Sync)
            {
                // Resolve modules through Loaded/Qualified first, so cached pointers cannot bypass the live-handle check.
                var owner = Modules.FirstOrDefault(pair => pair.Value.BaseAddress == module);
                if (owner.Key == null || GetModuleHandle(owner.Key) != module)
                    throw new InvalidOperationException("Native export owner is absent or changed: " + name);
                var key = (module, name, typeof(T));
                if (Exports.TryGetValue(key, out var cached)) return (T)cached;
                IntPtr address = GetProcAddress(module, name);
                if (address == IntPtr.Zero) throw new MissingMethodException("Required native export is unavailable: " + name);
                var function = Marshal.GetDelegateForFunctionPointer<T>(address);
                Exports.Add(key, function);
                return function;
            }
        }
    }
}
