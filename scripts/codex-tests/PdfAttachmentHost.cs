using System;
using System.Reflection;

internal static class PdfAttachmentHost
{
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            var method = assembly.GetType("BIMaestro.Codex.PdfAttachmentProbe", true).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
            return (int)method.Invoke(null, new object[] { new[] { args[1] } });
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
