using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using BIMaestro.Navisworks.Services;
using BIMaestro.VideoGames;
using Licensing;
using Point3D = System.Windows.Media.Media3D.Point3D;

namespace BIMaestro.Navisworks.UI
{
    internal sealed class PublishWebWindow : Form
    {
        private readonly Document _document;
        private readonly string _title;
        private readonly Model[] _models;
        private static readonly string[] ScopeLabels = { "Complet", "Extérieur uniquement", "Carcasse simplifiée", "Simplifier toute la maquette" };
        private readonly WebModelScope[] _scopes;
        private const int PublicationLimitMb = GameMepPublishClient.NavisworksLimitMb;
        private string _tileDirectory;
        private sealed class PreparedModel
        {
            public GameElementData[] Elements;
            public GameMepWebAsset[] Tiles;
            public Point3D Spawn;
            public double SpawnScore;
            public long TriangleCount;
            public long TransferBytes => Tiles.Sum(tile => tile.Size);
        }
        private readonly Dictionary<int, PreparedModel> _prepared = new Dictionary<int, PreparedModel>();
        private readonly System.Windows.Forms.Timer _selectionTimer = new System.Windows.Forms.Timer { Interval = 180 };
        private GameSceneData _referenceScene;
        private int _nextElementIndex;
        private int _nextTileNumber;
        private readonly NumericUpDown _targetWeight = new NumericUpDown { Minimum = 1, Maximum = PublicationLimitMb, Value = 100, Width = 58 };
        private readonly ComboBox _scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205, Enabled = false };
        private readonly ListView _modelList = new ListView
        {
            CheckBoxes = true, FullRowSelect = true, ShowItemToolTips = true, MultiSelect = false, HideSelection = false,
            View = System.Windows.Forms.View.Details, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Top
        };
        private readonly FlowLayoutPanel _scopeButtons = new FlowLayoutPanel { AutoSize = true };
        private readonly Button _prepare = new Button { Text = "Calculer le poids", AutoSize = true };
        private readonly Label _size = new Label { AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(550, 0) };
        private readonly TextBox _license = new TextBox { Dock = DockStyle.Fill, Text = Environment.UserName };
        private readonly Button _publish = new Button { Text = "Publier", AutoSize = true, Enabled = false };
        private readonly Button _export = new Button { Text = "Exporter les GLB", AutoSize = true, Enabled = false };
        private readonly Button _close = new Button { Text = "Fermer", AutoSize = true };
        private readonly TextBox _viewer = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly TextBox _editor = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private GameSceneData _scene;
        private GameMepWebPackageResult _package;
        private CancellationTokenSource _operation;
        private bool _canPublish;

        public PublishWebWindow(Document document)
        {
            _document = document;
            _title = Path.GetFileNameWithoutExtension(document.FileName);
            _models = document.Models.ToArray();
            _scopes = new WebModelScope[_models.Length];
            Text = "BIMaestro — Publier sur le web";
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel { AutoSize = true, Padding = new Padding(14), ColumnCount = 1, MinimumSize = new System.Drawing.Size(570, 0) };
            layout.Controls.Add(new Label { AutoSize = true, Text = _title });
            layout.Controls.Add(new Label { AutoSize = true, Text = "Maquettes à exporter" });
            _modelList.Columns.Add("Fichier", 240);
            _modelList.Columns.Add("GLB compressé estimé", 135, HorizontalAlignment.Right);
            _modelList.Columns.Add("Périmètre", 145);
            _modelList.Height = (_modelList.Font.Height + 6) * (1 + Math.Max(3, Math.Min(9, _models.Length))) + 4;
            var fileNames = _models.Select(model => Path.GetFileName(model.FileName)).ToArray();
            for (int i = 0; i < _models.Length; i++)
            {
                string file = fileNames[i];
                if (string.IsNullOrWhiteSpace(file)) file = _models[i].RootItem.DisplayName ?? "Maquette";
                string path = _models[i].FileName;
                string label = fileNames.Count(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) > 1
                    ? $"{i + 1}. {file} — {Path.GetDirectoryName(path)}"
                    : file;
                var row = new ListViewItem(label) { Checked = true, ToolTipText = path };
                row.SubItems.Add("—");
                row.SubItems.Add("Complet");
                _modelList.Items.Add(row);
            }
            layout.Controls.Add(_modelList);
            var mode = new FlowLayoutPanel { AutoSize = true };
            mode.Controls.Add(new Label { AutoSize = true, Text = "Périmètre de la maquette surlignée" });
            _scope.Items.AddRange(ScopeLabels);
            mode.Controls.Add(_scope);
            mode.Controls.Add(new Label { AutoSize = true, Text = "Poids cible (Mo)" });
            mode.Controls.Add(_targetWeight);
            layout.Controls.Add(mode);
            var checkAll = new Button { Text = "Tout cocher", AutoSize = true };
            var uncheckAll = new Button { Text = "Tout décocher", AutoSize = true };
            checkAll.Click += (_, __) => SetAllModels(true);
            uncheckAll.Click += (_, __) => SetAllModels(false);
            _scopeButtons.Controls.AddRange(new Control[] { checkAll, uncheckAll, _prepare });
            layout.Controls.Add(_scopeButtons);
            layout.Controls.Add(new Label { AutoSize = true, Text = "Objets visibles des maquettes cochées. Les plans de coupe sont ignorés." });
            layout.Controls.Add(_size);
            layout.Controls.Add(new Label { AutoSize = true, Text = "Estimations par fichier hors aperçu et données communes." });
            layout.Controls.Add(new Label { AutoSize = true, Text = "Identifiant de licence BIMaestro (le même que dans Revit)" });
            layout.Controls.Add(_license);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _publish, _export, _close });
            layout.Controls.Add(buttons);
            layout.Controls.Add(_status);
            layout.Controls.Add(LinkRow("Consultation", _viewer));
            layout.Controls.Add(LinkRow("Édition", _editor));
            Controls.Add(layout);

            SelectionChanged();
            _selectionTimer.Tick += async (_, __) =>
            {
                _selectionTimer.Stop();
                await RunAsync(AssembleSelectionAsync);
            };
            _modelList.ItemChecked += (_, __) => SelectionChanged();
            _modelList.SelectedIndexChanged += (_, __) => ShowScope();
            _scope.SelectedIndexChanged += (_, __) =>
            {
                if (!_scope.Enabled || _scope.SelectedIndex < 0 || _modelList.SelectedIndices.Count == 0) return;
                int index = _modelList.SelectedIndices[0];
                var scope = (WebModelScope)_scope.SelectedIndex;
                if (_scopes[index] == scope) return;
                _scopes[index] = scope;
                _modelList.Items[index].SubItems[2].Text = ScopeLabels[(int)scope];
                InvalidateModel(index);
                SelectionChanged();
            };
            _targetWeight.ValueChanged += (_, __) =>
            {
                foreach (int index in _prepared.Keys.Where(i => _scopes[i] == WebModelScope.SimplifiedShell ||
                    _scopes[i] == WebModelScope.SimplifiedModel).ToArray()) InvalidateModel(index);
                SelectionChanged();
            };
            _prepare.Click += async (_, __) => await RunAsync(PrepareAsync);
            _license.TextChanged += (_, __) => LoadLinks();
            _publish.Click += async (_, __) => await RunAsync(PublishAsync);
            _export.Click += async (_, __) => await SaveGlbsAsync();
            _close.Click += (_, __) => { if (_operation != null) _operation.Cancel(); else Close(); };
        }

        private void ShowScope()
        {
            int index = _modelList.SelectedIndices.Count == 0 ? -1 : _modelList.SelectedIndices[0];
            _scope.Enabled = false;
            _scope.SelectedIndex = index >= 0 ? (int)_scopes[index] : -1;
            _scope.Enabled = index >= 0 && _operation == null;
        }

        private void SetAllModels(bool isChecked)
        {
            for (int i = 0; i < _models.Length; i++)
                _modelList.Items[i].Checked = isChecked;
        }

        private void SelectionChanged()
        {
            _selectionTimer.Stop();
            _scene = null;
            _package = null;
            _canPublish = false;
            _publish.Enabled = _export.Enabled = false;
            var selected = _modelList.CheckedIndices.Cast<int>().ToArray();
            int missing = selected.Count(index => !_prepared.ContainsKey(index));
            _prepare.Enabled = missing > 0;
            _size.Text = string.Empty;
            LoadLinks();
            if (selected.Length == 0) _status.Text = "Cochez au moins une maquette.";
            else if (missing > 0)
            {
                long bytes = selected.Where(_prepared.ContainsKey).Sum(index => _prepared[index].TransferBytes);
                if (bytes > 0) _size.Text = $"Zones déjà calculées : {FormatEstimate(bytes)}";
                _status.Text = $"{missing} maquette(s) à calculer sur {selected.Length}.";
            }
            else
            {
                _status.Text = "Mise à jour de la sélection…";
                _selectionTimer.Start();
            }
        }

        private void InvalidateModel(int index)
        {
            if (_prepared.TryGetValue(index, out var model))
            {
                _prepared.Remove(index);
                foreach (var tile in model.Tiles)
                {
                    try { tile.DeleteFile(); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            _modelList.Items[index].SubItems[1].Text = "—";
            if (_prepared.Count != 0) return;
            DeleteTileDirectory();
            _referenceScene = null;
            _nextElementIndex = _nextTileNumber = 0;
        }

        private void DeleteTileDirectory()
        {
            if (_tileDirectory == null) return;
            try { Directory.Delete(_tileDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _tileDirectory = null;
        }

        private static Control LinkRow(string label, TextBox link)
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Top };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            row.Controls.Add(link);
            var copy = new Button { Text = "Copier", AutoSize = true, Enabled = false };
            var open = new Button { Text = "Ouvrir", AutoSize = true, Enabled = false };
            link.TextChanged += (_, __) => copy.Enabled = open.Enabled = link.TextLength > 0;
            copy.Click += (_, __) => Clipboard.SetText(link.Text);
            open.Click += (_, __) => Process.Start(new ProcessStartInfo(link.Text) { UseShellExecute = true });
            row.Controls.Add(copy);
            row.Controls.Add(open);
            return row;
        }

        private async Task PrepareAsync(CancellationToken cancellation)
        {
            var selectedIndices = _modelList.CheckedIndices.Cast<int>().Where(index => !_prepared.ContainsKey(index)).ToArray();
            if (selectedIndices.Length == 0)
            {
                await AssembleSelectionAsync(cancellation);
                return;
            }
            var selected = selectedIndices.Select(index => _models[index]).ToArray();
            var scopes = selectedIndices.Select(index => _scopes[index]).ToArray();
            var exterior = scopes.Select(scope => scope == WebModelScope.Exterior || scope == WebModelScope.SimplifiedShell).ToArray();
            bool adjustDetail = scopes.Contains(WebModelScope.SimplifiedShell) || scopes.Contains(WebModelScope.SimplifiedModel);
            int targetWeight = Decimal.ToInt32(_targetWeight.Value);
            long cachedBytes = _modelList.CheckedIndices.Cast<int>().Where(_prepared.ContainsKey).Sum(index => _prepared[index].TransferBytes);
            _status.Text = "Extraction de la géométrie…";
            await Task.Delay(1, cancellation);
            IProgress<string> extraction = new Progress<string>(message => _status.Text = message);
            var (scene, modelIndexByElement) = await NavisworksWebScene.CaptureAsync(_document, _title, selected, scopes, extraction, cancellation, _referenceScene?.SourceOrigin);
            _status.Text = "Préparation et compression des GLB…";
            IProgress<int> packing = new Progress<int>(tiles => _status.Text = tiles == 0
                ? "Préparation et compression des GLB…" : $"Compression des GLB… {tiles:N0} zones préparées");
            _tileDirectory ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BIMaestro-Web-" + Guid.NewGuid().ToString("N"))).FullName;
            string batchDirectory = Directory.CreateDirectory(Path.Combine(_tileDirectory, Guid.NewGuid().ToString("N"))).FullName;
            bool stored = false;
            try
            {
                var (models, removedParts, adjustedShellDetail, simplifiedTubes) = await Task.Run(() =>
                {
                    var filtered = ExteriorVisibility.Filter(scene, modelIndexByElement, exterior, extraction, cancellation);
                    GameMepWebPackageResult BuildPackage()
                    {
                        packing.Report(0);
                        foreach (string file in Directory.EnumerateFiles(batchDirectory, "tile-*.glb.gz")) File.Delete(file);
                        var progressInterval = Stopwatch.StartNew();
                        GameMepWebPackage.PrepareStaticAssets(scene, cancellation, reportTileCount: tiles =>
                        {
                            if (progressInterval.ElapsedMilliseconds < 250) return;
                            packing.Report(tiles);
                            progressInterval.Restart();
                        }, tileDirectory: batchDirectory, modelIndex: mesh => modelIndexByElement[mesh.ElementIndices[0]],
                            elementIndexOffset: _nextElementIndex, firstTileNumber: _nextTileNumber);
                        return adjustDetail ? GameMepWebPackage.Build(scene, _title) : null;
                    }

                    bool FitsTarget(GameMepWebPackageResult candidate) =>
                        GameMepPublishClient.FitsPublication(candidate, targetWeight) &&
                        cachedBytes + candidate.Bytes.LongLength + candidate.Assets.Sum(tile => tile.Size) <= targetWeight * GameMepPublishClient.Megabyte;
                    var candidate = scene.Meshes.Count > 0 ? BuildPackage() : null;
                    bool adjusted = false;
                    if (adjustDetail && candidate != null && !FitsTarget(candidate))
                    {
                        var reducibleMeshes = scene.Meshes.Select((mesh, index) =>
                            (Mesh: mesh, Index: index, Scope: scopes[modelIndexByElement[mesh.ElementIndices[0]]]))
                            .Where(entry => entry.Scope == WebModelScope.SimplifiedShell || entry.Scope == WebModelScope.SimplifiedModel)
                            .Where(entry => entry.Scope != WebModelScope.SimplifiedModel ||
                                !SimplifiedShellMesh.IsInteriorGeometryProtected(scene.Elements[entry.Mesh.ElementIndices[0]]))
                            .Select(entry => (entry.Mesh, entry.Index, entry.Scope,
                                Detailed: SimplifiedShellMesh.HasVariedSurfaceNormals(entry.Mesh, cancellation)))
                            .Where(entry => entry.Scope != WebModelScope.SimplifiedModel || !entry.Detailed)
                            .ToArray();
                        var lastAttempt = Enumerable.Repeat(SimplifiedShellMesh.InitialSubdivisions, reducibleMeshes.Length).ToArray();
                        foreach (var step in SimplifiedShellMesh.BudgetSteps())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            bool reduced = false;
                            for (int i = 0; i < reducibleMeshes.Length; i++)
                            {
                                var entry = reducibleMeshes[i];
                                int subdivisions = entry.Detailed ? step.Detailed : step.Flat;
                                if (lastAttempt[i] == subdivisions) continue;
                                lastAttempt[i] = subdivisions;
                                var mesh = entry.Scope == WebModelScope.SimplifiedModel
                                    ? SimplifiedShellMesh.ReduceInterior(entry.Mesh, subdivisions, cancellation)
                                    : SimplifiedShellMesh.Reduce(entry.Mesh, subdivisions, cancellation);
                                if (mesh.Indices.Count >= scene.Meshes[entry.Index].Indices.Count) continue;
                                scene.Meshes[entry.Index] = mesh;
                                reduced = true;
                            }
                            if (!reduced) continue;
                            adjusted = true;
                            scene.OriginalRenderTriangleCount = scene.Meshes.Sum(mesh => mesh.Indices.Count / 3);
                            candidate = BuildPackage();
                            if (FitsTarget(candidate)) break;
                        }
                    }
                    int tubes = 0;
                    if (scopes.Contains(WebModelScope.SimplifiedModel) && candidate != null && !FitsTarget(candidate))
                    {
                        extraction.Report("Recherche de tronçons droits à alléger…");
                        for (int i = 0; i < scene.Meshes.Count; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var mesh = scene.Meshes[i];
                            int element = mesh.ElementIndices[0];
                            if (scopes[modelIndexByElement[element]] != WebModelScope.SimplifiedModel) continue;
                            string category = scene.Elements[element].Category;
                            if (category == "Murs" || category == "Sols") continue;
                            var reduced = StraightTubeMesh.Simplify(mesh, cancellation);
                            if (ReferenceEquals(mesh, reduced)) continue;
                            scene.Meshes[i] = reduced;
                            tubes++;
                        }
                        if (tubes > 0)
                        {
                            scene.OriginalRenderTriangleCount = scene.Meshes.Sum(mesh => mesh.Indices.Count / 3);
                            candidate = BuildPackage();
                        }
                    }
                    var spawns = NavisworksWebScene.FindSpawns(scene, modelIndexByElement, selected.Length, cancellation);
                    var tilesByModel = scene.WebTiles.ToLookup(tile => tile.ModelIndex);
                    var trianglesByModel = scene.Meshes.ToLookup(mesh => modelIndexByElement[mesh.ElementIndices[0]], mesh => mesh.Indices.Count / 3);
                    var elementsByModel = scene.Elements.ToLookup(element => modelIndexByElement[element.WebElementIndex]);
                    foreach (var element in scene.Elements) element.WebElementIndex = checked(element.WebElementIndex + _nextElementIndex);
                    var prepared = Enumerable.Range(0, selected.Length).Select(i => new PreparedModel
                    {
                        Elements = elementsByModel[i].ToArray(),
                        Tiles = tilesByModel[i].ToArray(),
                        Spawn = spawns[i].Point,
                        SpawnScore = spawns[i].Score,
                        TriangleCount = trianglesByModel[i].Sum(count => (long)count)
                    }).ToArray();
                    return (prepared, filtered.Before - filtered.After, adjusted, tubes);
                }, cancellation);

                cancellation.ThrowIfCancellationRequested();
                scene.Meshes.Clear();
                if (_referenceScene == null && scene.WebTiles.Count > 0)
                {
                    _referenceScene = scene.CopyForWebExport(Array.Empty<GameElementData>());
                    _referenceScene.WebTiles.Clear();
                    _referenceScene.WebOverviewGlb = Array.Empty<byte>();
                    _referenceScene.WebPropertiesJson = "[]";
                }
                _nextElementIndex = checked(_nextElementIndex + scene.Elements.Count);
                _nextTileNumber = checked(_nextTileNumber + scene.WebTiles.Count);
                for (int i = 0; i < selectedIndices.Length; i++)
                {
                    _prepared.Add(selectedIndices[i], models[i]);
                    _modelList.Items[selectedIndices[i]].SubItems[1].Text = FormatEstimate(models[i].TransferBytes);
                }
                stored = true;
                await AssembleSelectionAsync(cancellation);
                if (removedParts > 0) _status.Text += $" {removedParts:N0} pièces masquées écartées de l’export.";
                if (adjustedShellDetail) _status.Text += " Surfaces planes secondaires réduites.";
                if (simplifiedTubes > 0) _status.Text += $" {simplifiedTubes:N0} tronçons droits allégés.";
            }
            finally
            {
                if (!stored)
                {
                    try { Directory.Delete(batchDirectory, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private async Task AssembleSelectionAsync(CancellationToken cancellation)
        {
            var selected = _modelList.CheckedIndices.Cast<int>().ToArray();
            if (selected.Length == 0 || selected.Any(index => !_prepared.ContainsKey(index))) return;
            var models = selected.Select(index => _prepared[index]).Where(model => model.Tiles.Length > 0).ToArray();
            if (models.Length == 0)
            {
                _status.Text = "Aucune géométrie 3D visible dans les maquettes sélectionnées.";
                return;
            }

            _status.Text = "Mise à jour de l’index et de l’aperçu…";
            var (scene, package) = await Task.Run(() =>
            {
                var subset = _referenceScene.CopyForWebExport(models.SelectMany(model => model.Elements));
                foreach (var tile in models.SelectMany(model => model.Tiles)) subset.WebTiles.Add(tile);
                subset.SpawnFootPosition = models.OrderBy(model => model.SpawnScore).First().Spawn;
                subset.OriginalRenderTriangleCount = checked((int)models.Sum(model => model.TriangleCount));
                subset.VisibleElementCount = subset.Elements.Count;
                cancellation.ThrowIfCancellationRequested();
                GameMepWebPackage.SetWebMetadata(subset);
                cancellation.ThrowIfCancellationRequested();
                return (subset, GameMepWebPackage.Build(subset, _title));
            }, cancellation);

            cancellation.ThrowIfCancellationRequested();
            _scene = scene;
            _package = package;
            long geometryBytes = scene.WebTiles.Sum(tile => tile.DecodedBytes) + scene.WebOverviewGlb.LongLength;
            long transferBytes = package.Bytes.LongLength + package.Assets.Sum(asset => asset.Size);
            _canPublish = GameMepPublishClient.FitsPublication(package, PublicationLimitMb);
            _size.Text = $"Géométrie GLB : {geometryBytes / 1048576d:F1} Mo · Transfert : {transferBytes / 1048576d:F1} Mo / 2 Go";
            _status.Text = _canPublish
                ? $"Prêt à publier en {package.Assets.Count:N0} zones."
                : package.Bytes.LongLength > GameMepPublishClient.MaxAssetBytes
                    ? "L’index des données dépasse 48 Mo. Décochez des maquettes."
                    : "Limite de publication dépassée. Décochez des maquettes.";
            int targetWeight = Decimal.ToInt32(_targetWeight.Value);
            if (selected.Any(i => _scopes[i] == WebModelScope.SimplifiedShell || _scopes[i] == WebModelScope.SimplifiedModel) &&
                transferBytes > targetWeight * GameMepPublishClient.Megabyte)
                _status.Text += $" Cible de {targetWeight} Mo non atteinte.";
            LoadLinks();
        }

        private static string FormatEstimate(double bytes)
        {
            double megabytes = bytes / 1048576d;
            return megabytes > 0 && megabytes < 0.05 ? "< 0,1 Mo" : $"≈ {megabytes:F1} Mo";
        }

        private async Task PublishAsync(CancellationToken cancellation)
        {
            string user = _license.Text.Trim();
            if (user.Length == 0)
                throw new InvalidOperationException("Saisissez votre identifiant de licence BIMaestro.");
            _status.Text = "Validation de la licence…";
            string jwt = await Task.Run(() => NetSupport.ValidateLicense(user, NetSupport.ComputeMachineId()), cancellation);
            cancellation.ThrowIfCancellationRequested();
            SetPublicationKey(user);
            var client = new GameMepPublishClient(() => jwt, PublicationLimitMb);
            var progress = new Progress<GameMepPublishProgress>(value =>
                _status.Text = $"{value.Message} {value.Percentage:P0}");
            var share = await client.PublishAsync(_scene, _title, progress, cancellation, _package);
            ShowLinks(share);
            _status.Text = $"Révision {share.Revision} publiée.";
        }

        private void SetPublicationKey(string user)
        {
            _scene.MepGraph.ScenarioModelKey = _scene.SourceDocumentId + ":" + NavisworksWebScene.HashKey(user);
        }

        private void LoadLinks()
        {
            if (_scene == null || _license.Text.Trim().Length == 0)
            {
                ShowLinks(new GameMepShareState());
                return;
            }
            SetPublicationKey(_license.Text.Trim());
            ShowLinks(GameMepPublishClient.Load(_scene.MepGraph));
        }

        private void ShowLinks(GameMepShareState share)
        {
            bool available = share.Revision > 0 && share.ExpiresAtUtc > DateTime.UtcNow;
            _viewer.Text = available ? share.ViewerUrl : string.Empty;
            _editor.Text = available ? share.EditorUrl : string.Empty;
        }

        private async Task SaveGlbsAsync()
        {
            using (var dialog = new SaveFileDialog { Filter = "Archive GLB (*.zip)|*.zip", FileName = _title + "-GLB.zip" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await RunAsync(async cancellation =>
                {
                    string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await Task.Run(() => GameMepWebPackage.WriteLocalTilesZip(_scene, temporary, _title, cancellation), cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        if (File.Exists(dialog.FileName)) File.Replace(temporary, dialog.FileName, null);
                        else File.Move(temporary, dialog.FileName);
                        _status.Text = "Archive GLB exportée.";
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                });
            }
        }

        private async Task RunAsync(Func<CancellationToken, Task> action)
        {
            _selectionTimer.Stop();
            _operation = new CancellationTokenSource();
            _modelList.Enabled = _scopeButtons.Enabled = _scope.Enabled = _targetWeight.Enabled = false;
            _publish.Enabled = _export.Enabled = _license.Enabled = false;
            _close.Text = "Annuler";
            try { await action(_operation.Token); }
            catch (OperationCanceledException) { _status.Text = "Opération annulée."; }
            catch (Exception exception) { _status.Text = exception.Message; }
            finally
            {
                _operation.Dispose();
                _operation = null;
                _publish.Enabled = _canPublish;
                _export.Enabled = _package != null;
                _license.Enabled = true;
                _modelList.Enabled = _scopeButtons.Enabled = true;
                ShowScope();
                _targetWeight.Enabled = true;
                _prepare.Enabled = _package == null && _modelList.CheckedIndices.Count > 0;
                _close.Text = "Fermer";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_operation != null)
            {
                _operation.Cancel();
                e.Cancel = true;
            }
            base.OnFormClosing(e);
            if (!e.Cancel)
            {
                _selectionTimer.Dispose();
                DeleteTileDirectory();
            }
        }
    }
}
