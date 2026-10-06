param([string]$Configuration = 'Debug', [switch]$WithApplication, [string]$PreviewFolder, [string]$BinPath)
$ErrorActionPreference = 'Stop'
$bin = if ($BinPath) { [IO.Path]::GetFullPath($BinPath) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../BIMaestro/bin/$Configuration")) }
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
    if ($PreviewFolder) {
        New-Item -ItemType Directory -Path $PreviewFolder -Force | Out-Null
        $flags = [Reflection.BindingFlags]'Instance,NonPublic'
        $type = $window.GetType()
        $entryType = $type.GetNestedType('Entry', [Reflection.BindingFlags]::NonPublic)
        $all = $type.GetField('_all', $flags).GetValue($window)
        $all.Clear()
        $choiceType = $type.GetNestedType('ModelChoice', [Reflection.BindingFlags]::NonPublic)
        $choices = $type.GetField('_choices', $flags).GetValue($window)
        $choices.Clear()
        $names = @('Snowdon Towers Sample Architectural', 'BIMaestro_Apprentissage_2024_20261004_105929', 'CML_Table ronde + chaise', 'BIM_Chaise_Confort_Parametrique', 'BIMaestro_Apprentissage_2024_20261004_215024', 'Projet architecture', 'Famille mobilier - bureau', 'Maquette structure', 'Amenagement interieur', 'Famille luminaire')
        for ($i = 0; $i -lt $names.Count; $i++) {
            $entry = [Activator]::CreateInstance($entryType)
            $entry.Id = "document-$i"
            $entry.Name = $names[$i]
            $entry.Path = "C:\Projets\$($names[$i]).rvt"
            $entry.Version = '2024'
            $entry.Kind = 'RVT'
            $entry.When = [DateTime]::Today
            $entry.Hours = @(0.94, 0.62, 0.49, 0.35, 0.3, 0.24, 0.15, 0.12, 0.08, 0.05)[$i]
            $all.Add($entry)
            $choice = [Activator]::CreateInstance($choiceType, $true)
            $choice.Id = $entry.Id
            $choice.Name = $entry.Name
            $choice.Path = $entry.Path
            $choice.Selected = $true
            $choices.Add($choice)
        }
        [void]$type.GetMethod('Refresh', $flags).Invoke($window, $null)
        $root = $window.Content
        $window.Content = $null
        $content = [Windows.Controls.Border]::new()
        $content.Resources = $window.Resources
        $content.Background = $window.FindResource('App.Background')
        $content.Child = $root
        function Save-Preview([string]$name) {
            $content.UpdateLayout()
            [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
            $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$content.ActualWidth, [int]$content.ActualHeight, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
            $bitmap.Render($content)
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $stream = [IO.File]::Create((Join-Path $PreviewFolder $name))
            try { $encoder.Save($stream) } finally { $stream.Dispose() }
        }
        foreach ($size in @(@(1180, 780), @(960, 640), @(1500, 960))) {
            $content.Width = $size[0]
            $content.Height = $size[1]
            $content.Measure([Windows.Size]::new($content.Width, $content.Height))
            $content.Arrange([Windows.Rect]::new(0, 0, $content.Width, $content.Height))
            $content.UpdateLayout()
            [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
            $table = $window.FindName('OverviewTable')
            $plot = $window.FindName('OverviewPlot')
            if ($table.ActualHeight -lt 200 -or $plot.ActualHeight -lt 200) { throw "Chart or document list was squeezed at $($size -join 'x'): table=$($table.ActualHeight), plot=$($plot.ActualHeight)." }
            if ($plot.Model.IsLegendVisible) { throw 'Overview legend must not consume chart space.' }
            foreach ($tab in @(0, 1)) {
                $window.FindName('Tabs').SelectedIndex = $tab
                Save-Preview "dashboard-$($size -join 'x')-tab$tab.png"
            }
            $window.FindName('Tabs').SelectedIndex = 0
        }
        [void]$type.GetMethod('OpenAnalysis', $flags).Invoke($window, [object[]]@('document-0'))
        if ($window.FindName('DetailTable').Items.Count -ne 1 -or $window.FindName('DetailSummary').Text -ne '0 h 56') { throw 'Analyze did not isolate the selected document.' }
        $newEntry = [Activator]::CreateInstance($entryType)
        $newEntry.Id = 'new-document'
        $newEntry.Name = 'New document during analysis'
        $newEntry.Path = 'C:\Projets\New.rvt'
        $newEntry.When = [DateTime]::Today
        $newEntry.Hours = .5
        $all.Add($newEntry)
        [void]$type.GetMethod('RebuildChoices', $flags).Invoke($window, $null)
        $newChoices = $type.GetField('_choices', $flags).GetValue($window)
        if (($newChoices | Where-Object Id -eq 'new-document').Selected) { throw 'Refresh added a new document to a targeted selection.' }
        [void]$all.Remove($newEntry)
        [void]$type.GetMethod('RebuildChoices', $flags).Invoke($window, $null)
        $window.FindName('Search').Text = 'no matching document'
        if ($window.FindName('DetailTable').Items.Count -ne 0 -or $window.FindName('PdfButton').IsEnabled) { throw 'Empty filtered selection did not disable export.' }
        if ($window.FindName('DetailDocumentsEmpty').Visibility -ne 'Visible') { throw 'Empty detail table has no guidance.' }
        $window.FindName('From').SelectedDate = [DateTime]::Today.AddDays(1)
        $window.FindName('To').SelectedDate = [DateTime]::Today.AddDays(2)
        if ($window.FindName('Picker').Items.Count -ne 0) { throw 'Picker includes documents outside the selected dates.' }
        $window.FindName('From').SelectedDate = [DateTime]::Today.AddDays(3)
        if ($window.FindName('PdfButton').IsEnabled -or $window.FindName('DetailDocumentsEmpty').Visibility -ne 'Visible') { throw 'Invalid dates allowed export or hid guidance.' }
        [void]$type.GetMethod('ResetFilters_Click', $flags).Invoke($window, [object[]]@($null, $null))
        [void]$type.GetMethod('ClearSelection_Click', $flags).Invoke($window, [object[]]@($null, $null))
        if ($window.FindName('DetailTable').Items.Count -ne 0 -or $window.FindName('PdfButton').IsEnabled) { throw 'Clear selection did not clear totals.' }
        [void]$type.GetMethod('SelectAll_Click', $flags).Invoke($window, [object[]]@($null, $null))
        if ($window.FindName('DetailTable').Items.Count -ne 10) { throw 'Select all did not restore the visible documents.' }
        $all[0].Parameters = ([char]0xC9) + 'cole'
        $window.FindName('ParameterSearch').Text = 'ecole'
        if ($window.FindName('DetailTable').Items.Count -ne 1) { throw 'Parameter search is not accent insensitive.' }
        [void]$type.GetMethod('ResetFilters_Click', $flags).Invoke($window, [object[]]@($null, $null))
        $all[9].Kind = 'RFA'
        $all[9].Version = '2023'
        $window.FindName('Kind').SelectedIndex = 2
        if ($window.FindName('DetailTable').Items.Count -ne 1) { throw 'Family filter did not isolate family activity.' }
        $window.FindName('Kind').SelectedIndex = 0
        $window.FindName('Version').ItemsSource = @('Toutes', '2023', '2024')
        $window.FindName('Version').SelectedIndex = 1
        if ($window.FindName('DetailTable').Items.Count -ne 1) { throw 'Revit version filter did not isolate the selected version.' }
        [void]$type.GetMethod('ResetFilters_Click', $flags).Invoke($window, [object[]]@($null, $null))
        $dateMethod = $type.GetMethod('TryDate', [Reflection.BindingFlags]'Static,NonPublic')
        foreach ($dateText in @('04/10/2026', '2026-10-04')) {
            $arguments = [object[]]@($dateText, '10:30:00', [DateTime]::MinValue)
            $parsed = $dateMethod.Invoke($null, $arguments)
            if (!$parsed -or $arguments[2] -ne [DateTime]::new(2026, 10, 4, 10, 30, 0)) { throw "Ambiguous date was interpreted incorrectly: $dateText" }
        }
        $window.FindName('From').SelectedDate = [DateTime]::new(2000, 1, 1)
        if ($window.FindName('DetailPlot').Model.Series[0].Items.Count -gt 60) { throw 'Long range chart has too many buckets.' }
        $longHours = ($window.FindName('DetailPlot').Model.Series[0].Items | Measure-Object -Property Y1 -Sum).Sum
        if ([Math]::Abs($longHours - ($all | Measure-Object -Property Hours -Sum).Sum) -gt .000001) { throw 'Long range chart lost hours.' }
        [void]$type.GetMethod('ResetFilters_Click', $flags).Invoke($window, [object[]]@($null, $null))
        $window.FindName('Tabs').SelectedIndex = 0
        $content.Width = 1180
        $content.Height = 780
        $content.Measure([Windows.Size]::new(1180, 780))
        $content.Arrange([Windows.Rect]::new(0, 0, 1180, 780))
        $window.FindName('Tabs').SelectedIndex = 1
        $window.FindName('DetailChartExpansion').IsExpanded = $true
        Save-Preview 'dashboard-detail-chart.png'
        if ($window.FindName('DetailPlot').ActualHeight -lt 200) { throw 'Expanded detail chart was squeezed.' }
        $window.FindName('DetailChartExpansion').IsExpanded = $false
        $window.FindName('Tabs').SelectedIndex = 0
        for ($i = 0; $i -lt $all.Count; $i++) { $all[$i].When = [DateTime]::Today.AddDays(-($i % 7)) }
        $type.GetField('_days', $flags).SetValue($window, 15)
        [void]$type.GetMethod('Refresh', $flags).Invoke($window, $null)
        $chartHours = ($window.FindName('OverviewPlot').Model.Series[0].Items | Measure-Object -Property Y1 -Sum).Sum
        $allHours = ($all | Measure-Object -Property Hours -Sum).Sum
        if ([Math]::Abs($chartHours - $allHours) -gt 0.000001) { throw 'Daily bars did not preserve the total hours.' }
        Save-Preview 'dashboard-15-days.png'
        $all.Clear()
        [void]$type.GetMethod('Refresh', $flags).Invoke($window, $null)
        if ($window.FindName('OverviewDocumentsEmpty').Visibility -ne 'Visible') { throw 'Empty overview message missing.' }
        Save-Preview 'dashboard-empty.png'
        Write-Output "Populated dashboard layout and previews passed: $PreviewFolder"
    }
    $window.Close()
    Write-Output "Dashboard constructor, window theme, layout and charts passed (Application.Current present: $([Windows.Application]::Current -ne $null))."
} catch {
    Write-Output $_.Exception.ToString()
    throw
}
