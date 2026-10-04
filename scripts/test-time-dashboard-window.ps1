param([string]$Configuration = 'Debug', [switch]$WithApplication)
$ErrorActionPreference = 'Stop'
$bin = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../BIMaestro/bin/$Configuration"))
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$revitVersion = if ($Configuration -eq 'Release2024') { '2024' } else { '2023' }
[void][Reflection.Assembly]::LoadFrom("C:/Program Files/Autodesk/Revit $revitVersion/RevitAPI.dll")
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'BIMaestro.dll'))
if ($WithApplication) { $app = [Windows.Application]::new() }
try {
    $window = [Activator]::CreateInstance($assembly.GetType('BIMaestro.Dashboard.TimeSeriesDashboardWindow'), [object[]]@($null))
    $window.Measure([Windows.Size]::new(1180, 820))
    $window.Arrange([Windows.Rect]::new(0, 0, 1180, 820))
    if ($null -eq $window.FindName('OverviewPlot').Model) { throw 'Overview chart was not initialized.' }
    if ($null -eq $window.FindName('DetailPlot').Model) { throw 'Detail chart was not initialized.' }
    $surface = $window.FindResource('Surface').Color
    $plotColor = $window.FindName('OverviewPlot').Model.Background
    if ($surface.R -ne $plotColor.R -or $surface.G -ne $plotColor.G -or $surface.B -ne $plotColor.B) { throw 'Chart did not use the window theme.' }
    $window.Close()
    Write-Output "Dashboard constructor, window theme, layout and charts passed (Application.Current present: $([Windows.Application]::Current -ne $null))."
} catch {
    Write-Output $_.Exception.ToString()
    throw
}
