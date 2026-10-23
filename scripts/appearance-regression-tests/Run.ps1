$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out = Join-Path $repo 'tmp/appearance-regression-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$source = Get-Content -LiteralPath (Join-Path $repo 'BIMaestro/les couleurs/Changement auto des couleurs/ColoringStateManager.cs') -Raw
function Get-Method([string]$name) {
    $start = $source.IndexOf('        private static void ' + $name + '(')
    if ($start -lt 0) { $start = $source.IndexOf('        public static void ' + $name + '(') }
    if ($start -lt 0) { throw "Production method absent: $name" }
    $open = $source.IndexOf('{', $start)
    $depth = 1
    $end = $open + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw "Incomplete production method: $name" }
    return $source.Substring($start, $end - $start).Replace('private static void', 'internal static void').Replace('public static void', 'internal static void')
}
$methods = @('SetTrackedValue', 'RestoreTrackedValues') | ForEach-Object { Get-Method $_ }
$trackerStart = $source.IndexOf('    internal sealed class AppearanceValueTracker')
$trackerEnd = $source.IndexOf('    // -----------------------------------------------------------------------', $trackerStart)
if ($trackerStart -lt 0 -or $trackerEnd -lt 0) { throw 'Production tracker absent' }
$tracker = $source.Substring($trackerStart, $trackerEnd - $trackerStart)
$wrapper = @'
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Data;
'@
$wrapper += $tracker + "`r`ninternal static class BrowserVisuals { private static readonly AppearanceValueTracker Values = new AppearanceValueTracker();`r`n"
[IO.File]::WriteAllText((Join-Path $out 'BrowserVisuals.cs'), $wrapper + ($methods -join "`r`n") + "`r`n}")
$ribbon = @'
using System;
internal static class RibbonVisuals {
 internal static readonly AppearanceValueTracker PanelValues = new AppearanceValueTracker();
 internal static readonly AppearanceValueTracker TabValues = new AppearanceValueTracker();
 internal static readonly AppearanceValueTracker Values = new AppearanceValueTracker();
'@
$resetMethods = @('ResetPapanoelColoring','ResetTabItemColoring','ResetPartialColoring') | ForEach-Object { Get-Method $_ }
[IO.File]::WriteAllText((Join-Path $out 'RibbonVisuals.cs'), $ribbon + ($resetMethods -join "`r`n") + "`r`n}")
$hover = @'
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace BIMaestro.ViewHover {
 internal static class ViewHoverPreviewPreferences { internal static bool Enabled = true; }
}
internal static class BrowserHover {
 internal static bool _viewHoverPollPending;
 internal static long _viewHoverPollGeneration, _activeViewGeneration;
 internal static object _chromiumBrowser = new object();
 internal static FrameworkElement _projectBrowserRoot = new Border();
 internal static List<Action<object>> Callbacks = new List<Action<object>>();
 internal static List<object> Applied = new List<object>();
 private static bool IsAttachedToWindow(FrameworkElement root) => true;
 private static void HideViewHoverPopup() { }
 private static void ApplyViewHoverResult(object value) => Applied.Add(value);
 private static bool EvaluateBrowserScriptAsync(object browser, string script, Action<object> callback) {
  Callbacks.Add(callback); return true;
 }
'@
[IO.File]::WriteAllText((Join-Path $out 'BrowserHover.cs'), $hover + (Get-Method 'PollViewHoverPreview') + "`r`n}")
$framework = 'C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
$refs = @('mscorlib.dll','System.dll','System.Core.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$json = Join-Path $repo 'packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.dll'
$sources = @('PropertiesPaletteRenderer.cs','AppearancePreferences.cs','PropertiesAppearanceMetrics.cs') | ForEach-Object { Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_" }
$sources += Join-Path $out 'BrowserVisuals.cs'
$sources += Join-Path $out 'BrowserHover.cs'
$sources += Join-Path $out 'RibbonVisuals.cs'
$sources += Join-Path $PSScriptRoot 'Verify.cs'
$exe = Join-Path $out 'Verify.exe'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:latest /target:exe "/out:$exe" "/reference:$json" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Regression test compilation failed' }
Copy-Item -LiteralPath $json -Destination $out -Force
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Appearance regressions failed' }
