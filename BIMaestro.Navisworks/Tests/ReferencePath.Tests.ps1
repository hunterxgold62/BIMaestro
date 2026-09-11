$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\Services\ReferencePath.cs') -Raw
$test = @'
namespace BIMaestro.Navisworks.Services
{
    public static class ReferencePathChecks
    {
        public static void Run()
        {
            Check(ReferencePath.Normalize(@"..\models\a.nwc", @"C:\project\coord\main.nwf") == @"C:\project\models\a.nwc");
            Check(ReferencePath.Normalize(@"\\server\share\a.nwc", null) == @"\\server\share\a.nwc");
            Check(ReferencePath.Normalize("https://example.org/a.nwc", null) == null);
            Check(ReferencePath.Normalize(null, null) == null);
            Check(ReferencePath.Normalize("a.nwc", null) == null);
            Check(ReferencePath.Normalize("file:///C:/models/a.nwc", null) == @"C:\models\a.nwc");
            Check(ReferencePath.Same(@"C:\MODELS\a.nwc", @"c:\models\A.nwc"));
            Check(!ReferencePath.Same(@"C:\one\a.nwc", @"C:\two\a.nwc"));
            Check(!ReferencePath.Same(null, null));
        }
        private static void Check(bool valid)
        {
            if (!valid) throw new System.Exception("ReferencePath check failed");
        }
    }
}
'@
Add-Type -TypeDefinition ($source + $test)
[BIMaestro.Navisworks.Services.ReferencePathChecks]::Run()
Write-Output '9 contrôles de résolution de chemins réussis.'
