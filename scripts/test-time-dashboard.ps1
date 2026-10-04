# Runs the actual PDF writer without Revit or WPF (PowerShell 7 C# compiler).
param([string]$OutputFolder)
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../BIMaestro/app et excel/temps revit/TimeSeriesDashboardWindow.xaml.cs') -Raw -Encoding UTF8
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
  if(System.Text.RegularExpressions.Regex.Matches(pdf,@"\(Maquette / famille\)").Count!=pages) throw new Exception("Table headers were not repeated on every detail page");
  if(pdf.IndexOf("Total :")>pdf.IndexOf("Maquette / famille")) throw new Exception("Table headers appeared before the summary");
  var match=System.Text.RegularExpressions.Regex.Match(pdf,@"startxref\n(\d+)");
  int offset=int.Parse(match.Groups[1].Value);
  if(!pdf.Substring(offset).StartsWith("xref")) throw new Exception("Incorrect byte offset");
  foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pdf,@"/Length (\d+) >>\nstream\n([\s\S]*?)endstream"))
   if(Encoding.GetEncoding(1252).GetByteCount(m.Groups[2].Value)!=int.Parse(m.Groups[1].Value)) throw new Exception("Incorrect stream length");
  Console.WriteLine("PDF totals, live hours, accents, escaping, "+pages+" pages and byte offsets passed: "+path);
  var sample = new List<TimeSeriesDashboardWindow.Total> {
   new TimeSeriesDashboardWindow.Total { Name="Snowdon Towers Sample Architectural", Path=@"C:\Program Files\Autodesk\Revit 2024\Samples\Snowdon Towers Sample Architectural.rvt", Hours=61.0/60, Days=2, Last=new DateTime(2026,10,5), Versions="2024" },
   new TimeSeriesDashboardWindow.Total { Name="BIM_Chaise_Confort_Parametrique", Path=@"C:\Users\lemer\AppData\Local\BIMaestro\Codex\Families\BIM_Chaise_Confort_Parametrique_20261004_190006_00cbda37\BIM_Chaise_Confort_Parametrique.rfa", Hours=.3, Days=1, Last=new DateTime(2026,10,4), Versions="2024" },
   new TimeSeriesDashboardWindow.Total { Name="BIM_Batman_The_Dark_Knight_1900", Path=@"C:\Users\lemer\AppData\Local\BIMaestro\Codex\Families\BIM_Batman_The_Dark_Knight_1900_20261004_232703_0391fba2\BIM_Batman_The_Dark_Knight_1900.rfa", Hours=.05, Days=1, Last=new DateTime(2026,10,4), Versions="2024" }
  };
  var sampleRows=sample.Select(x => new TimeSeriesDashboardWindow.Entry { Hours=x.Hours }).ToList();
  sampleRows[0].Hours-=1.0/60;
  sampleRows.Add(new TimeSeriesDashboardWindow.Entry { Hours=1.0/60, Live=true });
  TimePdfReport.Write(Path.Combine(folder,"time-report-sample.pdf"),new DateTime(2026,9,29),new DateTime(2026,10,5),sampleRows,sample,"Type : Maquettes et familles · Revit : Toutes", "0.059 0.318 0.196");
  var huge=new TimeSeriesDashboardWindow.Total { Name=new string('W',1800)+" NOM_FIN", Path=@"C:\"+new string('W',4000)+"_CHEMIN_FIN.rvt", Hours=2, Days=1, Last=new DateTime(2026,10,5), Versions="2024" };
  TimePdfReport.Write(Path.Combine(folder,"time-report-long.pdf"),new DateTime(2026,9,29),new DateTime(2026,10,5),rows.Take(1).ToList(),new List<TimeSeriesDashboardWindow.Total> { huge },"Filtres : "+new string('W',6000)+" FILTRE_FIN", "0.059 0.318 0.196");
  TimePdfReport.Write(Path.Combine(folder,"time-report-empty.pdf"),new DateTime(2026,9,29),new DateTime(2026,10,5),new List<TimeSeriesDashboardWindow.Entry>(),new List<TimeSeriesDashboardWindow.Total>(),"Type : Toutes", "0.059 0.318 0.196");
  Console.WriteLine("Representative, long-content and empty PDFs generated: "+folder);
 }
}
'@
Add-Type -TypeDefinition ($stub + $writer) -Language CSharp
$testFolder = if ($OutputFolder) { [IO.Path]::GetFullPath($OutputFolder) } else { Join-Path ([System.IO.Path]::GetTempPath()) ('BIMaestro-time-tests-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $testFolder -Force | Out-Null
[BIMaestro.Dashboard.ReportChecks]::Run($testFolder)
