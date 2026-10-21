using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BIMaestro.PropertiesAppearance;

namespace BIMaestro.PropertiesAppearance
{
    // Only the native boundary and unrelated editor/persistence types are simulated.
    // Build.ps1 compiles the production layout, ordering and preview sources unchanged.
    internal sealed class NativePropertiesGrid
    {
        internal sealed class Row
        {
            internal string Label, Value, Marker;
            internal long? Id;
            internal bool Editable;
            internal Row Copy() => (Row)MemberwiseClone();
        }
        internal IntPtr Window = new IntPtr(10), Core = new IntPtr(20);
        internal List<Row> Rows, Baseline;
        internal bool IsEditing, FreezeMapping, CorruptPayload;
        internal int Moves;
        internal uint RowCount => (uint)Rows.Count;
        internal static bool IsGroupMarker(string value) => (value ?? "").Trim() == "^" || (value ?? "").Trim() == "v";
        internal bool IsGroupHeader(uint row) => IsGroupMarker(Rows[(int)row - 1].Marker);
        internal string Value(uint row, uint col)
        {
            var item = Rows[(int)row - 1];
            return col == 1 ? item.Label : col == 2 ? item.Value : item.Marker;
        }
        internal bool CanStyle(uint row) => Rows[(int)row - 1].Editable;
        internal int FindParameterRow(long id)
        {
            var rows = FreezeMapping ? Baseline : Rows;
            int index = rows.FindIndex(r => r.Id == id);
            return index < 0 ? -1 : index + 1;
        }
        internal bool MoveRows(uint first, uint last, uint destination)
        {
            if (IsEditing) throw new Exception("An active editor must never receive MoveRows.");
            Moves++;
            int start = (int)first - 1, count = (int)(last - first + 1);
            var block = Rows.GetRange(start, count); Rows.RemoveRange(start, count);
            Rows.InsertRange((int)destination - 1, block);
            if (CorruptPayload) Rows[0].Value = "CORRUPTED";
            return true;
        }
        internal void Redraw() { }
    }
    internal sealed class PropertyTarget
    {
        internal string Key, NativeName, GroupKey;
        internal long? NativeParameterId;
    }
    internal sealed class AppearanceContext
    {
        internal string CategoryKey = "walls", TypeKey = "basic", Signature = "fixture/1";
        internal Dictionary<string, string> GroupMap = new Dictionary<string, string>();
        internal Dictionary<string, PropertyTarget> ParameterMap = new Dictionary<string, PropertyTarget>();
        internal List<PropertyTarget> Parameters = new List<PropertyTarget>();
    }
    internal sealed class AppearanceLayout
    {
        public List<string> Groups = new List<string>();
        public Dictionary<string, List<string>> Parameters = new Dictionary<string, List<string>>();
        public bool IsEmpty => Groups.Count == 0 && !Parameters.Any(p => p.Value.Count > 0);
    }
    internal sealed class AppearanceProfile
    {
        public string Key = "all";
        public AppearanceLayout Layout = new AppearanceLayout();
    }
    internal sealed class AppearancePreferences
    {
        public List<AppearanceProfile> Profiles = new List<AppearanceProfile>();
        public IEnumerable<AppearanceProfile> Matching(string category, string type) => new[] { "all", category, type }
            .Where(k => !string.IsNullOrEmpty(k)).Select(k => Profiles.FirstOrDefault(p => p.Key == k)).Where(p => p != null);
        public static string Normalize(string value)
        {
            var result = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormD))
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && !char.IsWhiteSpace(c))
                    result.Append(char.ToUpperInvariant(c));
            return result.ToString();
        }
    }
}

internal static class Program
{
    private sealed class Fixture
    {
        internal NativePropertiesGrid Grid;
        internal AppearanceContext Context;
        internal AppearancePreferences Preferences;
        internal string Original;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Cells(NativePropertiesGrid grid) => string.Join("|", grid.Rows.Select(r => r.Label + ":" + r.Value + ":" + r.Marker));
    private static string Labels(NativePropertiesGrid grid) => string.Join("|", grid.Rows.Select(r => r.Label));
    private static Fixture Create()
    {
        var grid = new NativePropertiesGrid();
        grid.Rows = new List<NativePropertiesGrid.Row> {
            new NativePropertiesGrid.Row { Label="Constraints", Value="", Marker="^ " },
            new NativePropertiesGrid.Row { Label="  Offset", Value="1.200", Marker="Associate global parameter", Id=11, Editable=true },
            new NativePropertiesGrid.Row { Label="  Comment", Value="wall-A", Marker="", Id=12, Editable=true },
            new NativePropertiesGrid.Row { Label="", Value="", Marker="" },
            new NativePropertiesGrid.Row { Label="Phase", Value="", Marker="^ " },
            new NativePropertiesGrid.Row { Label="  Created", Value="New construction", Marker="", Id=21, Editable=true },
            new NativePropertiesGrid.Row { Label="  Demolished", Value="None", Marker="", Id=22, Editable=true }
        };
        grid.Baseline = grid.Rows.Select(r => r.Copy()).ToList();
        var context = new AppearanceContext();
        context.GroupMap[AppearancePreferences.Normalize("Constraints")] = "constraints";
        context.GroupMap[AppearancePreferences.Normalize("Phase")] = "phase";
        foreach (var row in grid.Rows.Where(r => r.Id.HasValue))
        {
            string group = row.Id < 20 ? "constraints" : "phase";
            var target = new PropertyTarget { Key="id:"+row.Id, NativeName=AppearancePreferences.Normalize(row.Label), GroupKey=group, NativeParameterId=row.Id };
            context.Parameters.Add(target); context.ParameterMap[group + "|" + target.NativeName] = target;
        }
        string comment = new PropertyPreviewRow { GroupKey="constraints", ParameterKey="id:12", Label="  Comment" }.OrderKey;
        var profile = new AppearanceProfile();
        profile.Layout.Groups.AddRange(new[] { "phase", "constraints" });
        profile.Layout.Parameters["constraints"] = new List<string> { comment };
        return new Fixture { Grid=grid, Context=context, Preferences=new AppearancePreferences { Profiles=new List<AppearanceProfile> { profile } }, Original=Cells(grid) };
    }
    private static void Apply(Fixture f, string version="2025") => PropertiesPaletteLayout.Apply(f.Grid, f.Context, f.Preferences, version, null);
    private static void Reset(Fixture f) => PropertiesPaletteLayout.Apply(f.Grid, f.Context, null, "2025", null);
    private static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    private static void OrderAndReset()
    {
        var f=Create(); Apply(f);
        Check(Labels(f.Grid)=="Phase|  Created|  Demolished|Constraints|  Comment||  Offset", "Combined group/parameter permutation incorrect; global marker may have become a header.");
        Check(f.Grid.Rows.Where(r=>r.Id.HasValue).All(r=>f.Grid.Baseline.Single(b=>b.Id==r.Id).Value==r.Value), "A parameter value changed during ordering.");
        Check(PropertiesPaletteLayout.HasApplied, "Applied order was not tracked.");
        Reset(f); Check(Cells(f.Grid)==f.Original, "Reset did not restore all original cells.");
        Check(!PropertiesPaletteLayout.HasApplied && !PropertiesPaletteLayout.RequiresNativeRebuild, "Reset retained applied/rebuild state.");
    }
    private static void MappingRollback()
    {
        var f=Create(); f.Grid.FreezeMapping=true;
        Check(Throws(()=>Apply(f)), "Incorrect semantic mapping was accepted.");
        Check(f.Grid.Moves>1 && Cells(f.Grid)==f.Original, "A mapping failure did not roll back to the complete baseline.");
        Check(!PropertiesPaletteLayout.HasApplied && !PropertiesPaletteLayout.RequiresNativeRebuild, "Successful rollback retained unsafe state.");
        int moves=f.Grid.Moves; Apply(f); Check(f.Grid.Moves==moves, "Failed selection was retried without a rebuild.");
    }
    private static void MissingIdentity()
    {
        var f=Create(); var target=f.Context.Parameters.Single(p=>p.NativeParameterId==12);
        f.Context.Parameters.Remove(target); f.Context.ParameterMap.Remove(target.GroupKey+"|"+target.NativeName);
        Apply(f); Check(f.Grid.Moves==0 && Cells(f.Grid)==f.Original, "An editable field lacking verified identity was moved.");
        Check(PropertiesPaletteLayout.Status.Contains("identité"), "Identity refusal was not explained.");
    }
    private static void Editing()
    {
        var f=Create(); f.Grid.IsEditing=true; Apply(f);
        Check(f.Grid.Moves==0 && Cells(f.Grid)==f.Original, "Ordering touched an active editor.");
        f.Grid.IsEditing=false; Apply(f); Check(PropertiesPaletteLayout.HasApplied, "Ordering did not resume after editing finished.");
    }
    private static void MissingIdentityAfterPartialMove()
    {
        var f=Create();
        var layout=f.Preferences.Profiles[0].Layout; layout.Groups.Clear();
        layout.Parameters["phase"]=new List<string> { new PropertyPreviewRow { GroupKey="phase", ParameterKey="id:22", Label="  Demolished" }.OrderKey };
        var target=f.Context.Parameters.Single(p=>p.NativeParameterId==22);
        f.Context.Parameters.Remove(target); f.Context.ParameterMap.Remove(target.GroupKey+"|"+target.NativeName);
        // The unresolved row uses the production fallback key, as the editor would.
        layout.Parameters["phase"]=new List<string> { new PropertyPreviewRow { GroupKey="phase", Label="  Demolished" }.OrderKey };
        Apply(f);
        Check(f.Grid.Moves>1 && Cells(f.Grid)==f.Original, "A later unverified field left an earlier successful movement applied.");
        Check(!PropertiesPaletteLayout.HasApplied && !PropertiesPaletteLayout.RequiresNativeRebuild, "Refusal rollback retained unsafe state.");
    }
    private static void Rebuild()
    {
        var f=Create(); Apply(f); int moves=f.Grid.Moves;
        f.Grid.Rows=f.Grid.Baseline.Select(r=>r.Copy()).ToList();
        Apply(f); Check(f.Grid.Moves>moves && f.Grid.Rows[0].Label=="Phase", "Native reconstruction did not reapply the saved order.");
        f.Grid.Rows=f.Grid.Baseline.Select(r=>r.Copy()).ToList(); f.Grid.Core=new IntPtr(21);
        Apply(f); Reset(f); Check(Cells(f.Grid)==f.Original, "New native grid instance retained stale permutation tokens.");
    }
    private static void CollapsedMarker()
    {
        var f=Create(); Apply(f); int moves=f.Grid.Moves;
        f.Grid.Rows.Single(r=>r.Label=="Constraints").Marker="v ";
        Apply(f); Check(f.Grid.Moves==moves, "Changing only the collapse marker changed the layout.");
        Reset(f); Check(Labels(f.Grid)==string.Join("|",f.Grid.Baseline.Select(r=>r.Label)), "Collapse state replaced the original order baseline.");
        Check(f.Grid.Rows.Single(r=>r.Label=="Constraints").Marker=="v ", "Reset changed the current collapse state.");
    }
    private static void NoIdentity()
    {
        var f=Create(); f.Context.Parameters.Clear(); f.Context.ParameterMap.Clear();
        Apply(f);
        Check(f.Grid.Moves==0 && !PropertiesPaletteLayout.HasApplied && PropertiesPaletteLayout.Status.Contains("attente"), "An empty semantic proof was accepted or not deferred.");
    }
    private static void TransientCapture()
    {
        // First capture sees a stale grid. It must be retried without Forget/rebuild.
        var f=Create(); var target=f.Context.Parameters.Single(p=>p.NativeParameterId==22);
        string name=target.NativeName; target.NativeName="UNSYNCHRONIZED";
        Apply(f); Check(f.Grid.Moves==0 && !PropertiesPaletteLayout.HasApplied, "A stale first capture was published.");
        target.NativeName=name; Apply(f);
        Check(PropertiesPaletteLayout.HasApplied, "A transient first capture poisoned the next synchronized attempt.");

        // A transient change of context must preserve the old verified baseline.
        var next=Create(); next.Context.Signature="fixture/2";
        next.Context.Parameters.Single(p=>p.NativeParameterId==22).NativeName="UNSYNCHRONIZED";
        Apply(next);
        Check(next.Grid.Moves==0 && PropertiesPaletteLayout.HasApplied, "A partially checked next selection erased the previous verified state.");
        next.Context.Parameters.Clear(); next.Context.ParameterMap.Clear();
        Apply(next);
        Check(next.Grid.Moves==0 && PropertiesPaletteLayout.HasApplied, "An empty next selection erased the previous verified state.");
        Reset(f); Check(Cells(f.Grid)==f.Original, "A transient next selection replaced the previous original-order baseline.");

        // No-identity first captures can recover when native identities appear.
        PropertiesPaletteLayout.Forget(); var delayed=Create();
        var targets=delayed.Context.Parameters.ToList(); delayed.Context.Parameters.Clear();
        Apply(delayed); delayed.Context.Parameters.AddRange(targets); Apply(delayed);
        Check(PropertiesPaletteLayout.HasApplied, "A temporary absence of native identities permanently suspended ordering.");
    }
    private static void CorruptedMove()
    {
        var f=Create(); f.Grid.CorruptPayload=true;
        Check(Throws(()=>Apply(f)) && PropertiesPaletteLayout.RequiresNativeRebuild, "Unexpected native contents did not require a native rebuild.");
    }
    private static void VersionRange()
    {
        foreach(string version in new[]{"2023","2024","2025","2026","2027"})
        {
            PropertiesPaletteLayout.Forget(); var f=Create(); Apply(f,version);
            Check(PropertiesPaletteLayout.HasApplied, "Installed version rejected by layout: "+version);
        }
        PropertiesPaletteLayout.Forget(); var unsupported=Create(); Apply(unsupported,"2028");
        Check(unsupported.Grid.Moves==0,"An unsupported version invoked native movement.");
    }
    private static void QualifiedWallVersions()
    {
        foreach(string version in new[]{"2023","2024","2025","2026","2027"})
        {
            PropertiesPaletteLayout.Forget(); var f=Create(); f.Context.CategoryKey="cat:-2000011";
            Apply(f,version);
            Check(PropertiesPaletteLayout.HasApplied && f.Grid.Moves>0, "A qualified wall version was blocked: "+version);
            Reset(f); Check(Cells(f.Grid)==f.Original, "A qualified wall could not restore its original order.");
        }
    }
    private static void UnqualifiedContexts()
    {
        foreach(var input in new (string Version,string Category)[]{
            ("2022","cat:-2000011"),("2028","cat:-2000011"),("2025","cat:-2000020"),("2025","all"),("2025",null)})
        {
            PropertiesPaletteLayout.Forget(); var f=Create(); f.Context.CategoryKey=input.Category;
            Apply(f,input.Version);
            Check(f.Grid.Moves==0 && !PropertiesPaletteLayout.HasApplied && Cells(f.Grid)==f.Original,
                "An unqualified version/category moved native rows: "+input.Version+"/"+input.Category);
        }
    }
    private static void QualificationAfterAppliedWall()
    {
        foreach(string category in new[]{"cat:-2000020","all",null})
        {
            PropertiesPaletteLayout.Forget(); var wall=Create(); wall.Context.CategoryKey="cat:-2000011"; Apply(wall);
            Check(PropertiesPaletteLayout.HasApplied, "The prerequisite qualified wall was not ordered.");
            var other=Create(); other.Context.CategoryKey=category; other.Context.Signature="different/category";
            // The fixture deliberately uses an all-profile with matching group names:
            // an old applied wall must not grant this new category native access.
            Apply(other);
            Check(other.Grid.Moves==0 && Cells(other.Grid)==other.Original, "HasApplied bypassed production category qualification.");
            Check(PropertiesPaletteLayout.HasApplied, "Refusing another category erased the previous verified wall baseline.");
            Reset(wall); Check(Cells(wall.Grid)==wall.Original, "Refusing another category poisoned wall restoration.");
        }
    }
    public static int Main()
    {
#if PRODUCTION_QUALIFICATION_TESTS
        var cases=new (string Name, Action Run)[] {
            ("production qualified walls 2023-2027 move and restore",QualifiedWallVersions),
            ("production unqualified versions and categories never move",UnqualifiedContexts),
            ("previously applied wall never bypasses production category qualification",QualificationAfterAppliedWall)
        };
#else
        var cases=new (string Name, Action Run)[] {
            ("group and parameter permutation, global marker, values and reset",OrderAndReset),
            ("semantic mapping failure rolls back and suspends retries",MappingRollback),
            ("editable field with no verified identity is refused",MissingIdentity),
            ("unverified field discovered later rolls back earlier movements",MissingIdentityAfterPartialMove),
            ("active editor postpones movement",Editing),
            ("native reconstruction and replacement instance",Rebuild),
            ("collapse marker preserves original baseline",CollapsedMarker),
            ("empty identity set is rejected",NoIdentity),
            ("transient capture is atomic and recovers on the next refresh",TransientCapture),
            ("unexpected native payload requires reconstruction",CorruptedMove),
            ("2023 through 2027 accepted, future version refused",VersionRange)
        };
#endif
        int failed=0;
        foreach(var test in cases)
        {
            PropertiesPaletteLayout.Forget();
            try { test.Run(); Console.WriteLine("PASS "+test.Name); }
            catch(Exception e) { failed++; Console.WriteLine("FAIL "+test.Name+": "+e.Message); }
        }
        Console.WriteLine($"{cases.Length-failed}/{cases.Length} passed; production layout sources, simulated native boundary.");
        return failed==0?0:1;
    }
}
