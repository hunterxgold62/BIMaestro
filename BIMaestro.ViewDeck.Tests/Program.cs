using BIMaestro.ViewHover;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            Test("Implicit row: size and local values restored", ImplicitRow);
            Test("Explicit rows, spans and columns preserved", ExplicitRows);
            Test("Bound rows rejected without mutation", BoundRows);
            Test("Duplicate attachment rejected", Duplicate);
            Test("Repeated ON/OFF does not accumulate rows", Repeated);
            Test("Host changes preserved during detach", HostChanges);
            Test("New child shifted back, removed child not restored", ChangedChildren);
            Test("Styled row restored without a local override", StyledRow);
            Console.WriteLine(_passed + " ViewDeck tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Test(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Border Strip() => new Border { Height = 126 };

    private static void Layout(Grid grid)
    {
        grid.Measure(new Size(400, 300));
        grid.Arrange(new Rect(0, 0, 400, 300));
        grid.UpdateLayout();
    }

    private static void ImplicitRow()
    {
        var grid = new Grid();
        var native = new Border();
        grid.Children.Add(native);
        Layout(grid);
        Check(native.ActualHeight == 300, "Unexpected original size");
        var host = ViewDeckHost.Attach(grid, Strip());
        Layout(grid);
        Check(host.IsAttached && Grid.GetRow(native) == 1, "Not attached");
        Check(native.ActualHeight == 174, "Strip must reserve space, not overlay the view");
        host.Dispose();
        host.Dispose();
        Layout(grid);
        Check(grid.RowDefinitions.Count == 0 && grid.Children.Count == 1, "Extra objects left behind");
        Check(native.ActualHeight == 300, "Original size not restored");
        Check(native.ReadLocalValue(Grid.RowProperty) == DependencyProperty.UnsetValue, "Default row not restored");
    }

    private static void ExplicitRows()
    {
        var grid = new Grid();
        var row0 = new RowDefinition { Height = new GridLength(23) };
        var row1 = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        grid.RowDefinitions.Add(row0);
        grid.RowDefinitions.Add(row1);
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var native = new Border();
        Grid.SetRow(native, 0);
        Grid.SetRowSpan(native, 2);
        Grid.SetColumn(native, 1);
        grid.Children.Add(native);
        var strip = Strip();
        using (var host = ViewDeckHost.Attach(grid, strip))
        {
            Check(Grid.GetColumnSpan(strip) == 2, "Strip must span columns");
            Check(grid.RowDefinitions[1] == row0 && grid.RowDefinitions[2] == row1, "Rows replaced");
            Check(Grid.GetRowSpan(native) == 2 && Grid.GetColumn(native) == 1, "Native layout changed");
        }
        Check(grid.RowDefinitions.Count == 2 && grid.RowDefinitions[0] == row0, "Explicit rows not restored");
        Check((int)native.ReadLocalValue(Grid.RowProperty) == 0, "Explicit local row lost");
    }

    private static void BoundRows()
    {
        var grid = new Grid();
        var native = new Border();
        BindingOperations.SetBinding(native, Grid.RowProperty, new Binding { Source = 0 });
        grid.Children.Add(native);
        Check(ViewDeckHost.Attach(grid, Strip()) == null, "Bound row should be rejected");
        Check(grid.RowDefinitions.Count == 0 && grid.Children.Count == 1, "Rejected grid was mutated");
        Check(BindingOperations.IsDataBound(native, Grid.RowProperty), "Binding was removed");
    }

    private static void Duplicate()
    {
        var grid = new Grid();
        using (var host = ViewDeckHost.Attach(grid, Strip()))
        {
            Check(ViewDeckHost.Attach(grid, Strip()) == null, "Duplicate accepted");
            Check(grid.Children.Count == 1 && grid.RowDefinitions.Count == 2, "Duplicate left layout changes");
        }
    }

    private static void Repeated()
    {
        var grid = new Grid();
        grid.Children.Add(new Border());
        for (int index = 0; index < 30; index++)
        {
            using (var host = ViewDeckHost.Attach(grid, Strip()))
                Check(host != null, "Reattachment failed");
            Check(grid.RowDefinitions.Count == 0 && grid.Children.Count == 1, "Rows accumulated");
        }
    }

    private static void HostChanges()
    {
        var grid = new Grid();
        var native = new Border();
        grid.Children.Add(native);
        using (var host = ViewDeckHost.Attach(grid, Strip())) Grid.SetRow(native, 5);
        Check(Grid.GetRow(native) == 5, "An unrelated row edit was overwritten");
    }

    private static void ChangedChildren()
    {
        var grid = new Grid();
        var original = new Border();
        var added = new Border();
        grid.Children.Add(original);
        using (var host = ViewDeckHost.Attach(grid, Strip()))
        {
            grid.Children.Remove(original);
            Grid.SetRow(added, 1);
            grid.Children.Add(added);
        }
        Check(grid.Children.Count == 1 && grid.Children.Contains(added), "Removed native element resurrected");
        Check(Grid.GetRow(added) == 0, "New native child not shifted back");
    }

    private static void StyledRow()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var native = new Border();
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Grid.RowProperty, 1));
        native.Style = style;
        grid.Children.Add(native);
        using (var host = ViewDeckHost.Attach(grid, Strip()))
            Check(Grid.GetRow(native) == 2, "Styled row not shifted");
        Check(Grid.GetRow(native) == 1 && native.ReadLocalValue(Grid.RowProperty) == DependencyProperty.UnsetValue,
            "Style overridden after detach");
    }
}
