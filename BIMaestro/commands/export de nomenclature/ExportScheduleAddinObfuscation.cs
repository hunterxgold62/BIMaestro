// Visualisation/ComUtils.cs
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Visualisation
{
    // Empêche la plupart des obfuscateurs de renommer/stripper cette classe et ses membres
    [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
    public static class ComUtils
    {
        // Empêche l'inlining pour que l'obfuscateur ne fusionne pas la méthode
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Release(object obj)
        {
            if (obj == null) return;
            try
            {
                // Libération complète des RCW associés à l'objet COM
                while (Marshal.ReleaseComObject(obj) > 0) { }
            }
            catch { /* intentionally ignored */ }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FinalRelease(object obj)
        {
            if (obj == null) return;
            try
            {
                Marshal.FinalReleaseComObject(obj);
            }
            catch { /* intentionally ignored */ }
        }
    }
}
