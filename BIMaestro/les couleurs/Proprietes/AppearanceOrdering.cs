using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.PropertiesAppearance
{
    internal static class AppearanceOrdering
    {
        internal static AppearanceLayout Resolve(AppearancePreferences preferences, string category, string type)
        {
            var layout = new AppearanceLayout();
            foreach (var profile in preferences.Matching(category, type))
            {
                if (profile.Layout.Groups.Count > 0) layout.Groups = profile.Layout.Groups.ToList();
                foreach (var pair in profile.Layout.Parameters) if (pair.Value.Count > 0) layout.Parameters[pair.Key] = pair.Value.ToList();
                if(profile.Layout.CustomGroups!=null)layout.CustomGroups=profile.Layout.CustomGroups.Select(g=>g.Copy()).ToList();
            }
            return layout;
        }
        internal static List<T> Ranked<T>(IEnumerable<T> values, IEnumerable<string> order, Func<T, string> key)
        {
            var rank = order.Where(k => k != null).Distinct().Select((k, i) => new { k, i }).ToDictionary(x => x.k, x => x.i);
            return values.OrderBy(v => rank.TryGetValue(key(v) ?? "", out int i) ? i : int.MaxValue).ToList();
        }
        internal static List<PropertyPreviewRow> Arrange(IList<PropertyPreviewRow> original, AppearanceLayout layout)
        {
            var custom=layout.CustomGroups??new List<AppearanceCustomGroup>();
            var eligible=new HashSet<string>(original.Where(r=>!r.Header&&!r.ReadOnly&&r.ParameterKey!=null).Select(r=>r.ParameterKey));
            var ownership=custom.SelectMany(g=>g.Members.Where(eligible.Contains).Select(m=>new{Member=m,Group=g.Key})).GroupBy(x=>x.Member).ToDictionary(g=>g.Key,g=>g.First().Group);
            var prefix = original.TakeWhile(r => !r.Header).ToList();
            var groups = new List<List<PropertyPreviewRow>>();
            foreach (var row in original.Skip(prefix.Count))
            {
                if (row.Header) groups.Add(new List<PropertyPreviewRow>());
                if (groups.Count > 0 && (row.Header||row.ParameterKey==null||!ownership.ContainsKey(row.ParameterKey))) groups[groups.Count - 1].Add(row);
            }
            foreach(var definition in custom)
            {
                var block=new List<PropertyPreviewRow>{new PropertyPreviewRow{Header=true,GroupKey=definition.Key,Label=definition.Name}};
                foreach(var key in definition.Members)
                {
                    var row=original.SingleOrDefault(r=>!r.Header&&r.ParameterKey==key);
                    if(row!=null&&!row.ReadOnly)block.Add(new PropertyPreviewRow{GroupKey=definition.Key,ParameterKey=row.ParameterKey,Label=row.Label,Value=row.Value,ReadOnly=row.ReadOnly});
                }
                groups.Add(block);
            }
            foreach (var group in Ranked(groups, layout.Groups, g => g[0].GroupKey))
            {
                prefix.Add(group[0]);
                prefix.AddRange(layout.Parameters.TryGetValue(group[0].GroupKey ?? "", out var order)
                    ? Ranked(group.Skip(1), order, r => r.OrderKey) : group.Skip(1));
            }
            return prefix;
        }
    }
}
