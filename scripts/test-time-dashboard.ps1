# Runs the actual PDF writer without Revit or WPF. Windows PowerShell / .NET Framework.
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../BIMaestro/app et excel/temps revit/TimeSeriesDashboardWindow.xaml.cs') -Raw
$marker = $source.IndexOf('    internal static class TimePdfReport')
if ($marker -lt 0) { throw 'PDF writer missing' }
$writer = $source.Substring($marker).Replace('using var output = new MemoryStream();', 'var output = new MemoryStream();')
$stub = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
namespace BIMaestro.Dashboard {
internal class TimeSeriesDashboardWindow {
 internal class Entry { public double Hours; public bool Live; }
 internal class Total { public string Name, Path, Versions; public double Hours; public int Days; public DateTime Last; public string Duration { get { return TimeSeriesDashboardWindow.Duration(Hours); } } }
 internal static string Duration(double hours) { long m=(long)Math.Round(hours*60, MidpointRounding.AwayFromZero); return (m/60)+" h "+(m%60).ToString("00"); }
 }
public static class ReportChecks {
 public static void Run(string folder) {
  var rows = Enumerable.Range(0,80).Select(i => new TimeSeriesDashboardWindow.Entry { Hours=2, Live=i==79 }).ToList();
  var totals = Enumerable.Range(0,80).Select(i => new TimeSeriesDashboardWindow.Total { Name="Maquette été ("+i+")", Path=@"C:\Projets\École\Maquette-"+i+".rvt", Hours=2, Days=1, Last=new DateTime(2026,10,2), Versions="2023" }).ToList();
  string path=System.IO.Path.Combine(folder,"time-report-pagination.pdf");
  TimePdfReport.Write(path,new DateTime(2026,9,26),new DateTime(2026,10,2),rows,totals,"Paramètres : École", "0.059 0.318 0.196");
  string pdf=Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(path));
  if(!pdf.StartsWith("%PDF-1.4") || !pdf.EndsWith("%%EOF\n")) throw new Exception("Invalid PDF envelope");
  if(!pdf.Contains("Total : 160 h 00") || !pdf.Contains("Dont sessions ouvertes : 2 h 00")) throw new Exception("Totals or live hours incorrect");
  if(!pdf.Contains("Maquette été \\(79\\)")) throw new Exception("Last row missing or text escaping incorrect");
  int pages=System.Text.RegularExpressions.Regex.Matches(pdf,@"/Type /Page /Parent").Count;
  if(pages<2) throw new Exception("Pagination missing");
  var match=System.Text.RegularExpressions.Regex.Match(pdf,@"startxref\n(\d+)");
  int offset=int.Parse(match.Groups[1].Value);
  if(!pdf.Substring(offset).StartsWith("xref")) throw new Exception("Incorrect byte offset");
  foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pdf,@"/Length (\d+) >>\nstream\n([\s\S]*?)endstream"))
   if(Encoding.GetEncoding(1252).GetByteCount(m.Groups[2].Value)!=int.Parse(m.Groups[1].Value)) throw new Exception("Incorrect stream length");
  Console.WriteLine("PDF totals, live hours, accents, escaping, "+pages+" pages and byte offsets passed: "+path);
 }
}
'@
Add-Type -TypeDefinition ($stub + $writer) -Language CSharp
$testFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('BIMaestro-time-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder | Out-Null
[BIMaestro.Dashboard.ReportChecks]::Run($testFolder)
