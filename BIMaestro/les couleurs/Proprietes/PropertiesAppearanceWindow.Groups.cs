using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed partial class PropertiesAppearanceEditor
    {
        private readonly TextBox _groupName=new TextBox {Text=UiLanguage.T("Mes paramètres", "My parameters"),MinWidth=180,Height=36,Padding=new Thickness(7,3,7,3),Margin=new Thickness(0,0,7,0),MaxLength=80};
        private readonly TextBox _groupRenameName=new TextBox {Height=36,Padding=new Thickness(7,3,7,3),Margin=new Thickness(0,0,0,7),MaxLength=80};
        private readonly StackPanel _groupTools=new StackPanel {Margin=new Thickness(0,12,0,12)};
        private sealed class GroupDestination {internal string Key,Name;public override string ToString()=>Name;}
        // Appearance tutorial hooks: "context-refreshed", "group-created", "parameter-grouped:<label>".
        internal event Action<string> TutorialAction;
        internal Expander GroupExpander { get; private set; }
        internal bool HasPreviewParameter(params string[] labels)=>_context?.PreviewRows?.Any(r=>!r.Header&&!r.ReadOnly&&
            labels.Any(l=>string.Equals((r.Label??"").Trim(),l,StringComparison.OrdinalIgnoreCase)))==true;
        private List<AppearanceCustomGroup> EditableGroups()
        {
            if(_profile==null)throw new InvalidOperationException(UiLanguage.T("Choisissez une portée.", "Choose a scope."));
            var matching=new AppearancePreferences{Profiles=_drafts.Values.ToList()}.Matching(_context.CategoryKey,_context.TypeKey).ToList();
            int current=matching.FindIndex(p=>p.Key==_profile.Key);int owner=matching.FindLastIndex(p=>p.Layout.CustomGroups!=null);
            if(owner>current)throw new InvalidOperationException(UiLanguage.T("Une portée plus précise définit déjà les bandeaux de cette sélection. Choisissez cette portée dans « Appliquer à… » pour les modifier.", "A more specific scope already defines the group headers for this selection. Choose that scope in “Apply to…” to modify them."));
            return _profile.Layout.CustomGroups??(_profile.Layout.CustomGroups=(DraftLayout.CustomGroups??new List<AppearanceCustomGroup>()).Select(g=>g.Copy()).ToList());
        }
        private string ValidGroupName(string name,string exceptKey=null)
        {
            name=(name??"").Trim();
            if(name.Length==0||name.Length>80||name.Any(char.IsControl))throw new InvalidOperationException(UiLanguage.T("Saisissez un nom de groupe de 1 à 80 caractères.", "Enter a group name of 1 to 80 characters."));
            var normalized=AppearancePreferences.Normalize(name);
            if(_context.PreviewRows.Any(r=>r.Header&&AppearancePreferences.Normalize(r.Label)==normalized)||(DraftLayout.CustomGroups??new List<AppearanceCustomGroup>()).Any(g=>g.Key!=exceptKey&&AppearancePreferences.Normalize(g.Name)==normalized))throw new InvalidOperationException(UiLanguage.T("Ce nom de groupe existe déjà dans cet aperçu.", "This group name already exists in this preview."));
            return name;
        }
        private void CreateCustomGroup()
        {
            if(_context.CategoryKey==null)throw new InvalidOperationException(UiLanguage.T("Sélectionnez d’abord un élément ou une vue dans Revit.", "First select an element or a view in Revit."));
            string name=ValidGroupName(_groupName.Text);
            if((DraftLayout.CustomGroups?.Count??0)>=20)throw new InvalidOperationException(UiLanguage.T("Maximum : 20 groupes personnalisés.", "Maximum: 20 custom groups."));
            var definition=new AppearanceCustomGroup{Name=name};EditableGroups().Add(definition);
            var keys=AppearanceOrdering.Arrange(_context.PreviewRows,DraftLayout).Where(r=>r.Header).Select(r=>r.GroupKey).Where(k=>k!=definition.Key).ToList();keys.Insert(0,definition.Key);_profile.Layout.Groups=keys;
            _selected=new PropertyPreviewRow{Header=true,GroupKey=definition.Key,Label=name};
            _search.Text="";
            _loading=true;_enabled.IsChecked=true;_loading=false;Changed();LoadColors();RefreshGroupTools();
            _previewScroll?.ScrollToVerticalOffset(0);
            Notify(UiLanguage.T("Groupe créé dans l’aperçu. Glissez les paramètres sur son bandeau, puis enregistrez vos réglages.", "Group created in the preview. Drag parameters onto its header, then save your settings."));
            TutorialAction?.Invoke("group-created");
        }
        private void RenameCustomGroup()
        {
            string key=_selected?.Header==true?_selected.GroupKey:null;
            if(key==null||!key.StartsWith("custom:",StringComparison.Ordinal))return;
            string name=ValidGroupName(_groupRenameName.Text,key);var group=EditableGroups().Single(g=>g.Key==key);group.Name=name;_selected.Label=name;
            foreach(var rule in _profile.Rules.Where(r=>r.Kind=="group"&&r.TargetKey==key))rule.Label=name;
            Changed();LoadColors();RefreshGroupTools();Notify(UiLanguage.T("Bandeau renommé dans l’aperçu. Enregistrez pour conserver ce nom.", "Header renamed in the preview. Save to keep this name."));
        }
        private void DeleteCustomGroup()
        {
            string key=_selected?.Header==true?_selected.GroupKey:null;
            if(key==null||!key.StartsWith("custom:",StringComparison.Ordinal))return;
            EditableGroups().RemoveAll(g=>g.Key==key);_profile.Layout.Groups.RemoveAll(k=>k==key);_profile.Layout.Parameters.Remove(key);_profile.Rules.RemoveAll(r=>r.Kind=="group"&&r.TargetKey==key);
            _collapsedGroups.Remove(key);
            _selected=_context.PreviewRows.FirstOrDefault(r=>r.Header&&!r.ReadOnly);Changed();LoadColors();RefreshGroupTools();
            Notify(UiLanguage.T("Groupe retiré de l’aperçu. Ses paramètres retrouvent leurs groupes Revit d’origine ; aucune définition n’est supprimée.", "Group removed from the preview. Its parameters return to their original Revit groups; no definition is deleted."));
        }
        private void AssignToGroup(PropertyPreviewRow row,string destination)
        {
            if(row==null||row.Header||row.ParameterKey==null||row.ReadOnly)throw new InvalidOperationException(UiLanguage.T("Choisissez un paramètre modifiable et reconnu.", "Choose an editable, recognized parameter."));
            var groups=EditableGroups();
            if(destination!=null&&!groups.Any(g=>g.Key==destination))throw new InvalidOperationException(UiLanguage.T("Groupe de destination introuvable.", "Destination group not found."));
            if(destination!=null&&groups.Single(g=>g.Key==destination).Members.Count>=256&&!groups.Single(g=>g.Key==destination).Members.Contains(row.ParameterKey))throw new InvalidOperationException(UiLanguage.T("Maximum : 256 paramètres dans un groupe.", "Maximum: 256 parameters in a group."));
            foreach(var group in groups)group.Members.RemoveAll(k=>k==row.ParameterKey);
            if(destination!=null)groups.Single(g=>g.Key==destination).Members.Add(row.ParameterKey);
            var original=_context.PreviewRows.Single(r=>!r.Header&&r.ParameterKey==row.ParameterKey);
            _selected=new PropertyPreviewRow{ParameterKey=original.ParameterKey,GroupKey=destination??original.GroupKey,Label=original.Label,Value=original.Value,ReadOnly=original.ReadOnly};
            _loading=true;_enabled.IsChecked=true;_loading=false;Changed();LoadColors();RefreshGroupTools();
            if(destination!=null)TutorialAction?.Invoke("parameter-grouped:"+original.Label);
        }
        private void RefreshGroupTools()
        {
            _groupTools.Children.Clear();
            bool custom=_selected?.Header==true&&(_selected.GroupKey??"").StartsWith("custom:",StringComparison.Ordinal);
            if(custom)
            {
                _groupRenameName.Text=_selected.Label;_groupTools.Children.Add(Label(UiLanguage.T("Nom du bandeau", "Header name"),12,true));_groupTools.Children.Add(_groupRenameName);
                var actions=new StackPanel();
                actions.Children.Add(MakeButton(UiLanguage.T("Renommer", "Rename"),RenameCustomGroup));actions.Children.Add(MakeButton(UiLanguage.T("Supprimer le groupe", "Delete group"),DeleteCustomGroup));_groupTools.Children.Add(actions);
                _groupTools.Children.Add(Label(UiLanguage.T("Déposez les paramètres sur ce bandeau. Les champs grisés restent dans leur groupe Revit.", "Drop parameters onto this header. Grayed-out fields stay in their Revit group."),12,false,true));
            }
            else if(_selected!=null&&!_selected.Header&&!_selected.ReadOnly&&_selected.ParameterKey!=null)
            {
                _groupTools.Children.Add(Label(UiLanguage.T("Placer ce paramètre dans…", "Place this parameter in…"),12,true));
                var destinations=new List<GroupDestination>{new GroupDestination{Name=UiLanguage.T("Son groupe Revit d’origine", "Its original Revit group")}};
                destinations.AddRange((DraftLayout.CustomGroups??new List<AppearanceCustomGroup>()).Select(g=>new GroupDestination{Key=g.Key,Name=g.Name}));
                var combo=new ComboBox{ItemsSource=destinations,SelectedIndex=0,Margin=new Thickness(0,0,0,7)};
                combo.SelectedItem=destinations.FirstOrDefault(d=>d.Key==_selected.GroupKey)??destinations[0];_groupTools.Children.Add(combo);
                _groupTools.Children.Add(MakeButton(UiLanguage.T("Déplacer le paramètre", "Move parameter"),()=>{var row=_selected;AssignToGroup(row,((GroupDestination)combo.SelectedItem).Key);}));
            }
        }
    }
}
