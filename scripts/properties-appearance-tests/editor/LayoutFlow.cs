using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using BIMaestro.PropertiesAppearance;

internal static class LayoutFlow
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    internal static void Run(AppearanceContext context)
    {
        var document=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"HostAppearance.xaml"));
        XNamespace w="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var source=document.Descendants(w+"TabItem").Single(t=>(string)t.Attribute(x+"Name")=="PropertiesTab");
        source.SetAttributeValue(XNamespace.Xmlns+"x",x.NamespaceName);
        var tab=(TabItem)XamlReader.Parse(source.ToString());
        var host=HomeFlow.Children<ContentControl>(tab).Single(c=>c.Name=="PropertiesEditorHost");
        var editor=new PropertiesAppearanceEditor(context);host.Content=editor;
        var tabs=new TabControl();tabs.Items.Add(tab);tabs.SelectedItem=tab;
        var rows=context.PreviewRows.ToArray();
        try
        {
            for(int i=0;i<80;i++)context.PreviewRows.Add(new PropertyPreviewRow{GroupKey="g",ParameterKey="long:"+i,Label="Un paramètre avec un intitulé très long pour vérifier les petites fenêtres "+i,Value="Une valeur de propriété très longue qui doit rester dans l’aperçu"});
            editor.GetType().GetMethod("RenderRows",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);
            foreach(var size in new[]{new Size(1000,520),new Size(1168,660),new Size(1440,720)})
            {
                tabs.Measure(size);tabs.Arrange(new Rect(size));tabs.UpdateLayout();
                var selection=(TextBlock)editor.GetType().GetField("_selection",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(editor);
                var bounds=selection.TransformToAncestor(tabs).TransformBounds(new Rect(selection.RenderSize));
                Check(editor.ActualWidth<=size.Width&&bounds.Left>=0&&bounds.Right<=size.Width&&bounds.Bottom<=size.Height,"property settings remain visible in the real tab host at "+size.Width+"×"+size.Height);
                var scroll=(ScrollViewer)editor.GetType().GetField("_previewScroll",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(editor);
                Check(scroll.ScrollableHeight>0&&scroll.ScrollableWidth==0,"long property lists scroll vertically within the preview at "+size.Width+"×"+size.Height);
            }
            HomeFlow.Render(tabs,"proprietes-fenetre-compacte",1000,560);
            var home=new BIMaestro.Home.HomeAppearanceEditor();
            Check(HomeFlow.Children<TextBlock>(home).All(t=>!t.Text.Contains("Proposition")&&!t.Text.Contains("2023")&&!t.Text.Contains("2024")&&!t.Text.Contains("2025")&&!t.Text.Contains("2026")),"home choices omit year and proposition labels");
        }
        finally { context.PreviewRows.Clear();context.PreviewRows.AddRange(rows);host.Content=null;editor.Close(); }
    }
}
