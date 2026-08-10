using Licensing;
using BIMaestro.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Famille
{
    public sealed class FamilySearchMetadata
    {
        public int SchemaVersion { get; set; } = 1;
        public string Description { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
        public string Source { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public static class FamilySearchMetadataService
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaxKeywords = 40;
        public const int MaxKeywordLength = 80;
        public const int MaxDescriptionLength = 500;

        public static string GetMetadataPath(string familyPath)
            => string.IsNullOrWhiteSpace(familyPath) ? null : familyPath + ".search.json";

        public static FamilySearchMetadata Load(string familyPath)
        {
            var path = GetMetadataPath(familyPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return CreateEmpty();

            try
            {
                var value = JsonConvert.DeserializeObject<FamilySearchMetadata>(File.ReadAllText(path, Encoding.UTF8));
                return Normalize(value);
            }
            catch
            {
                // Une métadonnée invalide ne doit jamais empêcher la navigation/recherche.
                return CreateEmpty();
            }
        }

        public static DateTime? GetLastWriteUtc(string familyPath)
        {
            try
            {
                var path = GetMetadataPath(familyPath);
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : (DateTime?)null;
            }
            catch { return null; }
        }

        public static bool TrySave(
            string familyPath,
            FamilySearchMetadata metadata,
            DateTime? expectedLastWriteUtc,
            out string error,
            out DateTime? newLastWriteUtc)
        {
            error = null;
            newLastWriteUtc = null;
            string target = GetMetadataPath(familyPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                error = UiLanguage.T("Le chemin de la famille est invalide.", "The family path is invalid.");
                return false;
            }

            string temp = target + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                bool exists = File.Exists(target);
                DateTime? actualWriteUtc = exists ? File.GetLastWriteTimeUtc(target) : (DateTime?)null;
                if (actualWriteUtc != expectedLastWriteUtc)
                {
                    error = UiLanguage.T("Les mots-clés ont été modifiés par un autre utilisateur. Fermez puis rouvrez l’éditeur avant de réessayer.", "The keywords were modified by another user. Close and reopen the editor before trying again.");
                    return false;
                }

                var clean = Normalize(metadata);
                clean.SchemaVersion = CurrentSchemaVersion;
                clean.UpdatedUtc = DateTime.UtcNow;
                clean.UpdatedBy = string.IsNullOrWhiteSpace(clean.UpdatedBy)
                    ? Environment.UserName
                    : clean.UpdatedBy.Trim();

                string json = JsonConvert.SerializeObject(clean, Formatting.Indented);
                File.WriteAllText(temp, json, new UTF8Encoding(false));

                DateTime? writeBeforeCommit = File.Exists(target)
                    ? File.GetLastWriteTimeUtc(target)
                    : (DateTime?)null;
                if (writeBeforeCommit != expectedLastWriteUtc)
                {
                    error = UiLanguage.T("Les mots-clés ont été modifiés pendant l’enregistrement. Fermez puis rouvrez l’éditeur avant de réessayer.", "The keywords were modified while saving. Close and reopen the editor before trying again.");
                    return false;
                }

                // File.Replace est atomique sur les volumes qui le prennent en charge.
                // Le repli Copy conserve la compatibilité avec certains partages réseau.
                if (exists)
                {
                    try { File.Replace(temp, target, null); }
                    catch (PlatformNotSupportedException) { File.Copy(temp, target, true); File.Delete(temp); }
                    catch (IOException) { File.Copy(temp, target, true); File.Delete(temp); }
                    catch (UnauthorizedAccessException) { File.Copy(temp, target, true); File.Delete(temp); }
                }
                else
                {
                    File.Move(temp, target);
                }

                newLastWriteUtc = File.GetLastWriteTimeUtc(target);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = UiLanguage.T("La bibliothèque est en lecture seule ou vous n’avez pas l’autorisation d’y enregistrer les mots-clés.", "The library is read-only or you do not have permission to save keywords there.");
                return false;
            }
            catch (Exception ex)
            {
                error = UiLanguage.T("Impossible d’enregistrer les mots-clés : ", "Unable to save keywords: ") + ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public static FamilySearchMetadata Normalize(FamilySearchMetadata value)
        {
            value = value ?? CreateEmpty();
            value.Description = (value.Description ?? string.Empty).Trim();
            if (value.Description.Length > MaxDescriptionLength)
                value.Description = value.Description.Substring(0, MaxDescriptionLength);

            value.Keywords = (value.Keywords ?? new List<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Select(k => k.Length > MaxKeywordLength ? k.Substring(0, MaxKeywordLength) : k)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxKeywords)
                .ToList();
            return value;
        }

        public static List<string> ParseKeywords(string text)
            => Normalize(new FamilySearchMetadata
            {
                Keywords = (text ?? string.Empty)
                    .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList()
            }).Keywords;

        private static FamilySearchMetadata CreateEmpty()
            => new FamilySearchMetadata { SchemaVersion = CurrentSchemaVersion };
    }

    internal static class FamilySearchAiService
    {
        private const string Model = "gpt-4o-mini";

        public static Task<FamilySearchMetadata> SuggestAsync(string name, string folder, string category)
        {
            return Task.Run(() =>
            {
                string jwt = global::BIMaestroApp.LicenseJwt;
                if (string.IsNullOrWhiteSpace(jwt))
                    throw new InvalidOperationException(UiLanguage.T("Aucune licence IA active n’est disponible.", "No active AI license is available."));

                string prompt = UiLanguage.T(
                    "Tu aides à indexer une bibliothèque de familles Revit en français. À partir du nom, du dossier et de la catégorie, propose une description courte et 6 à 15 mots-clés utiles. Ajoute des synonymes et concepts que l’utilisateur pourrait naturellement saisir, sans inventer une fonction technique non déductible. Réponds uniquement avec un objet JSON valide de forme {\"description\":\"...\",\"keywords\":[\"...\"]}.\nNom=" + (name ?? string.Empty) + "\nDossier=" + (folder ?? string.Empty) + "\nCatégorie Revit=" + (category ?? string.Empty),
                    "You are helping index a Revit family library in English. Based on the name, folder, and category, suggest a short description and 6 to 15 useful keywords. Add synonyms and concepts a user might naturally enter, without inventing a technical purpose that cannot be inferred. Reply only with a valid JSON object shaped as {\"description\":\"...\",\"keywords\":[\"...\"]}.\nName=" + (name ?? string.Empty) + "\nFolder=" + (folder ?? string.Empty) + "\nRevit category=" + (category ?? string.Empty));

                JObject raw = AiClient.SendOpenAI(jwt, Model, prompt);
                string content = raw["choices"]?[0]?["message"]?["content"]?.ToString();
                JObject parsed = ParseObject(content);
                var result = new FamilySearchMetadata
                {
                    Description = parsed["description"]?.ToString(),
                    Keywords = parsed["keywords"]?.Values<string>().ToList() ?? new List<string>(),
                    Source = "ai-reviewed"
                };
                result = FamilySearchMetadataService.Normalize(result);
                if (result.Keywords.Count == 0)
                    throw new InvalidOperationException(UiLanguage.T("L’IA n’a proposé aucun mot-clé exploitable.", "AI did not suggest any usable keywords."));
                return result;
            });
        }

        private static JObject ParseObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException(UiLanguage.T("La réponse IA est vide.", "The AI response is empty."));

            string text = content.Trim();
            int first = text.IndexOf('{');
            int last = text.LastIndexOf('}');
            if (first < 0 || last <= first)
                throw new InvalidOperationException(UiLanguage.T("La réponse IA ne contient pas de JSON valide.", "The AI response does not contain valid JSON."));

            try { return JObject.Parse(text.Substring(first, last - first + 1)); }
            catch (Exception ex) { throw new InvalidOperationException(UiLanguage.T("La réponse IA est illisible : ", "The AI response could not be read: ") + ex.Message); }
        }
    }

    internal sealed class FamilySearchMetadataWindow : Window
    {
        private readonly FamilyItem _family;
        private readonly TextBox _descriptionBox;
        private readonly TextBox _keywordsBox;
        private readonly Button _aiButton;
        private DateTime? _expectedLastWriteUtc;
        private bool _usedAi;

        public FamilySearchMetadata SavedMetadata { get; private set; }

        public FamilySearchMetadataWindow(FamilyItem family)
        {
            _family = family ?? throw new ArgumentNullException(nameof(family));
            Title = UiLanguage.T("Mots-clés de recherche — ", "Search Keywords — ") + family.Name;
            Width = 620;
            Height = 510;
            MinWidth = 520;
            MinHeight = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI");
            Background = Brushes.White;

            var metadata = FamilySearchMetadataService.Load(family.Path);
            _expectedLastWriteUtc = FamilySearchMetadataService.GetLastWriteUtc(family.Path);

            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                Text = UiLanguage.T("Ajoutez les termes qu’une personne pourrait utiliser pour retrouver cette famille, sans changer son nom.", "Add terms someone might use to find this family without changing its name."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            var fields = new StackPanel();
            fields.Children.Add(Label(UiLanguage.T("Description recherchable", "Searchable Description")));
            _descriptionBox = new TextBox
            {
                Text = metadata.Description ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 92,
                MaxLength = FamilySearchMetadataService.MaxDescriptionLength,
                Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            fields.Children.Add(_descriptionBox);
            fields.Children.Add(Label(UiLanguage.T("Mots-clés (séparés par une virgule ou un retour à la ligne)", "Keywords (separated by a comma or line break)"), new Thickness(0, 16, 0, 6)));
            _keywordsBox = new TextBox
            {
                Text = string.Join(", ", metadata.Keywords),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 125,
                Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            fields.Children.Add(_keywordsBox);
            Grid.SetRow(fields, 1);
            root.Children.Add(fields);

            var hint = new TextBlock
            {
                Text = UiLanguage.T("La description participe aussi à la recherche. Exemple de mots-clés : humain, personnage, ouvrier, travailleur, chantier", "The description is also included in search. Example keywords: human, person, worker, laborer, construction site"),
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(hint, 2);
            root.Children.Add(hint);

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            _aiButton = Button(UiLanguage.T("Proposer avec l’IA", "Suggest with AI"), 145);
            _aiButton.Click += SuggestWithAi_Click;
            DockPanel.SetDock(_aiButton, Dock.Left);
            buttons.Children.Add(_aiButton);

            var cancel = Button(UiLanguage.T("Annuler", "Cancel"), 90);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += (s, e) => { DialogResult = false; Close(); };
            DockPanel.SetDock(cancel, Dock.Right);
            buttons.Children.Add(cancel);

            var save = Button(UiLanguage.T("Enregistrer", "Save"), 110);
            save.Margin = new Thickness(8, 0, 0, 0);
            save.FontWeight = FontWeights.SemiBold;
            save.Click += Save_Click;
            DockPanel.SetDock(save, Dock.Right);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
        }

        private async void SuggestWithAi_Click(object sender, RoutedEventArgs e)
        {
            _aiButton.IsEnabled = false;
            string original = _aiButton.Content?.ToString();
            _aiButton.Content = UiLanguage.T("Génération…", "Generating...");
            try
            {
                var suggestion = await FamilySearchAiService.SuggestAsync(
                    _family.Name,
                    System.IO.Path.GetDirectoryName(_family.Path),
                    _family.Category);
                _descriptionBox.Text = suggestion.Description ?? string.Empty;
                _keywordsBox.Text = string.Join(", ", suggestion.Keywords);
                _usedAi = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    UiLanguage.T("La proposition IA n’est pas disponible. Vous pouvez continuer à saisir les mots-clés manuellement.\n\n", "The AI suggestion is unavailable. You can continue entering keywords manually.\n\n") + ex.Message,
                    UiLanguage.T("Mots-clés de recherche", "Search Keywords"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _aiButton.Content = original;
                _aiButton.IsEnabled = true;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var metadata = new FamilySearchMetadata
            {
                Description = _descriptionBox.Text,
                Keywords = FamilySearchMetadataService.ParseKeywords(_keywordsBox.Text),
                Source = _usedAi ? "ai-reviewed" : "manual",
                UpdatedBy = Environment.UserName
            };

            if (!FamilySearchMetadataService.TrySave(
                _family.Path, metadata, _expectedLastWriteUtc, out string error, out DateTime? newWriteUtc))
            {
                MessageBox.Show(this, error, UiLanguage.T("Mots-clés de recherche", "Search Keywords"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _expectedLastWriteUtc = newWriteUtc;
            SavedMetadata = FamilySearchMetadataService.Normalize(metadata);
            DialogResult = true;
            Close();
        }

        private static TextBlock Label(string text, Thickness? margin = null)
            => new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Margin = margin ?? new Thickness(0, 0, 0, 6)
            };

        private static Button Button(string text, double width)
            => new Button { Content = text, Width = width, Height = 34, Padding = new Thickness(10, 0, 10, 0) };
    }
}
