using System;
using System.Collections.Generic;

namespace BIMaestro.ViewTemplates
{
    internal sealed class ViewTemplatePackage
    {
        public const int CurrentFormatVersion = 1;

        public string Format { get; set; } = "BIMaestro.ViewTemplate";
        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
        public string RevitVersion { get; set; }
        public string SourceDocument { get; set; }
        public string SourceView { get; set; }
        public string SourceTemplate { get; set; }
        public string SuggestedName { get; set; }
        public string ViewType { get; set; }
        public bool IncludesFilters { get; set; }
        public List<ParameterSnapshot> Parameters { get; set; } = new List<ParameterSnapshot>();
        public List<ParameterReference> NonControlledTemplateParameters { get; set; } = new List<ParameterReference>();
        public List<CategorySnapshot> Categories { get; set; } = new List<CategorySnapshot>();
        public List<WorksetSnapshot> Worksets { get; set; } = new List<WorksetSnapshot>();
        public List<ViewFilterSnapshot> Filters { get; set; } = new List<ViewFilterSnapshot>();
    }

    internal sealed class ParameterReference
    {
        public long? BuiltInId { get; set; }
        public string SharedGuid { get; set; }
        public string Name { get; set; }
    }

    internal sealed class ParameterSnapshot
    {
        public ParameterReference Parameter { get; set; }
        public string StorageType { get; set; }
        public bool HasValue { get; set; }
        public int? IntegerValue { get; set; }
        public double? DoubleValue { get; set; }
        public string StringValue { get; set; }
        public ElementReference ElementValue { get; set; }
    }

    internal sealed class ElementReference
    {
        public long? BuiltInId { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public string Auxiliary { get; set; }
    }

    internal sealed class CategoryReference
    {
        public long? BuiltInId { get; set; }
        public string Path { get; set; }
    }

    internal sealed class CategorySnapshot
    {
        public CategoryReference Category { get; set; }
        public bool Hidden { get; set; }
        public GraphicOverrideSnapshot Overrides { get; set; }
    }

    internal sealed class WorksetSnapshot
    {
        public string Name { get; set; }
        public string Visibility { get; set; }
    }

    internal sealed class ViewFilterSnapshot
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public GraphicOverrideSnapshot Overrides { get; set; }
        public List<CategoryReference> Categories { get; set; } = new List<CategoryReference>();
        public FilterNodeSnapshot RuleTree { get; set; }
        public List<string> SelectedElementUniqueIds { get; set; } = new List<string>();
    }

    internal sealed class FilterNodeSnapshot
    {
        public string Kind { get; set; }
        public bool Inverted { get; set; }
        public List<FilterNodeSnapshot> Children { get; set; } = new List<FilterNodeSnapshot>();
        public List<FilterRuleSnapshot> Rules { get; set; } = new List<FilterRuleSnapshot>();
    }

    internal sealed class FilterRuleSnapshot
    {
        public string Kind { get; set; }
        public string Evaluator { get; set; }
        public ParameterReference Parameter { get; set; }
        public string StringValue { get; set; }
        public int? IntegerValue { get; set; }
        public double? DoubleValue { get; set; }
        public double? Epsilon { get; set; }
        public ElementReference ElementValue { get; set; }
        public FilterRuleSnapshot InnerRule { get; set; }
        public string UnsupportedType { get; set; }
    }

    internal sealed class GraphicOverrideSnapshot
    {
        public ColorSnapshot ProjectionLineColor { get; set; }
        public ElementReference ProjectionLinePattern { get; set; }
        public int ProjectionLineWeight { get; set; }
        public ColorSnapshot CutLineColor { get; set; }
        public ElementReference CutLinePattern { get; set; }
        public int CutLineWeight { get; set; }
        public ColorSnapshot SurfaceForegroundPatternColor { get; set; }
        public ElementReference SurfaceForegroundPattern { get; set; }
        public bool SurfaceForegroundPatternVisible { get; set; }
        public ColorSnapshot SurfaceBackgroundPatternColor { get; set; }
        public ElementReference SurfaceBackgroundPattern { get; set; }
        public bool SurfaceBackgroundPatternVisible { get; set; }
        public ColorSnapshot CutForegroundPatternColor { get; set; }
        public ElementReference CutForegroundPattern { get; set; }
        public bool CutForegroundPatternVisible { get; set; }
        public ColorSnapshot CutBackgroundPatternColor { get; set; }
        public ElementReference CutBackgroundPattern { get; set; }
        public bool CutBackgroundPatternVisible { get; set; }
        public int Transparency { get; set; }
        public bool Halftone { get; set; }
        public string DetailLevel { get; set; }
    }

    internal sealed class ColorSnapshot
    {
        public byte Red { get; set; }
        public byte Green { get; set; }
        public byte Blue { get; set; }
    }

    internal sealed class ViewTemplateImportReport
    {
        public string TargetName { get; set; }
        public bool CreatedTemplate { get; set; }
        public int ParametersApplied { get; set; }
        public int CategoriesApplied { get; set; }
        public int WorksetsApplied { get; set; }
        public int FiltersApplied { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }
}
