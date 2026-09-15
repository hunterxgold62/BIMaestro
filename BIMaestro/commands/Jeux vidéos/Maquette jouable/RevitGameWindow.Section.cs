using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;

namespace BIMaestro.VideoGames
{
    public partial class RevitGameWindow
    {
        private readonly GameSectionVolume _sectionVolume = new GameSectionVolume();
        private readonly Dictionary<MeshGeometryModel3D, CrossSectionMeshGeometryModel3D> _sectionMeshes =
            new Dictionary<MeshGeometryModel3D, CrossSectionMeshGeometryModel3D>();
        private readonly Dictionary<LineGeometryModel3D, (LineGeometry3D Source, LineGeometry3D Clipped)>
            _sectionLines = new Dictionary<LineGeometryModel3D, (LineGeometry3D, LineGeometry3D)>();
        private double _lastSectionUpdate = double.MinValue;
        private bool _pickingSectionFace, _bindingSections;
        private int _sectionSerial;
        private GameSectionPlane? SelectedSection => SectionList.SelectedItem as GameSectionPlane;

        private void SectionButton_Click(object sender, RoutedEventArgs e)
        {
            SectionControls.Visibility = SectionControls.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (SectionControls.Visibility != Visibility.Visible) CancelSectionPick();
            else if (_sectionVolume.Planes.Count == 0) SectionAdd_Click(sender, e);
        }
        private void SectionAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_sectionVolume.Planes.Count >= GameSectionVolume.MaximumPlanes)
            { ShowToast("8 coupes maximum : supprimez une coupe avant d'en ajouter une."); return; }
            _pickingSectionFace = true;
            SectionHint.Text = "Cliquez une face du bâtiment · Échap pour annuler";
            GameViewport.Cursor = Cursors.Cross;
            GameViewport.Focus();
        }
        private void CancelSectionPick()
        {
            _pickingSectionFace = false;
            GameViewport.Cursor = Cursors.Arrow;
            SectionHint.Text = "Ctrl + molette : déplacer la coupe sélectionnée";
        }
        private void AddSectionAt(Point screenPoint)
        {
            if (!_readyToPlay || !TryCreateSelectionRay(screenPoint, out var origin, out var direction)) return;
            double length = 10000;
            if (!_sectionVolume.ClipRay(ref origin, direction, ref length)) return;
            var hit = _selectionIndex.FindNearest(origin, direction, length);
            if (hit?.Normal == null || hit.Normal.Value.LengthSquared < 1e-12)
            { ShowToast("Cliquez sur une face visible du bâtiment."); return; }
            var normal = hit.Normal.Value;
            normal.Normalize();
            if (Vector3D.DotProduct(normal, direction) < 0) normal.Negate();
            var section = new GameSectionPlane
            {
                Name = "Coupe " + ++_sectionSerial, Enabled = true, FaceNormal = normal, Anchor = hit.Position
            };
            _sectionVolume.Planes.Add(section);
            CancelSectionPick(); BindSections(section); ApplySection();
        }
        private void BindSections(GameSectionPlane? selected = null)
        {
            _bindingSections = true;
            SectionList.ItemsSource = null;
            SectionList.ItemsSource = _sectionVolume.Planes;
            SectionList.SelectedItem = selected ?? _sectionVolume.Planes.LastOrDefault();
            _bindingSections = false;
            LoadSelectedSection();
        }
        private void SectionSelectionChanged(object sender, SelectionChangedEventArgs e)
        { if (!_bindingSections) LoadSelectedSection(); }
        private void LoadSelectedSection()
        {
            if (SectionPosition == null || SectionEnabled == null || SectionInverted == null) return;
            _bindingSections = true;
            var section = SelectedSection;
            SectionPosition.IsEnabled = SectionEnabled.IsEnabled = SectionInverted.IsEnabled = section != null;
            var b = _scene.Bounds;
            double extent = b.IsEmpty ? 10 : Math.Sqrt(b.SizeX*b.SizeX + b.SizeY*b.SizeY + b.SizeZ*b.SizeZ) * .3048;
            SectionPosition.Maximum = Math.Max(1, extent);
            SectionPosition.Minimum = -SectionPosition.Maximum;
            SectionPosition.Value = (section?.Offset ?? 0) * .3048;
            SectionEnabled.IsChecked = section?.Enabled == true;
            SectionInverted.IsChecked = section?.Inverted == true;
            SectionPositionText.Text = $"Décalage : {SectionPosition.Value:0.00} m";
            _bindingSections = false;
        }
        private void SectionChanged(object sender, RoutedEventArgs e)
        {
            if (_bindingSections || SectionList == null || SelectedSection == null) return;
            SelectedSection.Enabled = SectionEnabled.IsChecked == true;
            SelectedSection.Inverted = SectionInverted.IsChecked == true;
            ApplySection();
        }
        private void SectionPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_bindingSections || SectionList == null || SelectedSection == null) return;
            SelectedSection.Offset = SectionPosition.Value / .3048;
            SectionPositionText.Text = $"Décalage : {SectionPosition.Value:0.00} m";
            ApplySection();
        }
        private void SectionDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSection != null) _sectionVolume.Planes.Remove(SelectedSection);
            BindSections(); ApplySection();
        }
        private void SectionReset_Click(object sender, RoutedEventArgs e)
        {
            CancelSectionPick(); _sectionVolume.Planes.Clear(); BindSections(); ApplySection();
        }
        private void ApplySection()
        {
            var planes = _sectionVolume.Planes.Where(p => p.Enabled).ToArray();
            var values = planes.Select(p => new SharpDX.Plane(
                new SharpDX.Vector3((float)p.Normal.X, (float)p.Normal.Y, (float)p.Normal.Z), (float)p.PlaneD)).ToArray();
            // Keep the original rendering technique untouched when there are no cuts.
            foreach (var original in GameViewport.Items.OfType<MeshGeometryModel3D>()
                .Where(m => !(m is CrossSectionMeshGeometryModel3D)).ToArray())
            {
                if (!_sectionMeshes.TryGetValue(original, out var cut) && planes.Length > 0)
                {
                    cut = new CrossSectionMeshGeometryModel3D
                    {
                        Geometry = original.Geometry, Material = original.Material, Transform = original.Transform,
                        IsTransparent = original.IsTransparent, CullMode = original.CullMode,
                        EnableViewFrustumCheck = original.EnableViewFrustumCheck, IsHitTestVisible = false,
                        IsThrowingShadow = false, CrossSectionColor = Color.FromRgb(100, 120, 115)
                    };
                    _sectionMeshes[original] = cut; GameViewport.Items.Add(cut);
                }
                original.IsRendering = planes.Length == 0;
                if (cut == null) continue;
                cut.IsRendering = planes.Length > 0;
                cut.EnablePlane1 = values.Length > 0; if (values.Length > 0) cut.Plane1 = values[0];
                cut.EnablePlane2 = values.Length > 1; if (values.Length > 1) cut.Plane2 = values[1];
                cut.EnablePlane3 = values.Length > 2; if (values.Length > 2) cut.Plane3 = values[2];
                cut.EnablePlane4 = values.Length > 3; if (values.Length > 3) cut.Plane4 = values[3];
                cut.EnablePlane5 = values.Length > 4; if (values.Length > 4) cut.Plane5 = values[4];
                cut.EnablePlane6 = values.Length > 5; if (values.Length > 5) cut.Plane6 = values[5];
                cut.EnablePlane7 = values.Length > 6; if (values.Length > 6) cut.Plane7 = values[6];
                cut.EnablePlane8 = values.Length > 7; if (values.Length > 7) cut.Plane8 = values[7];
            }
            ClearHoveredElement(); HideSelectedBounds();
            _lastSectionUpdate = double.MinValue;
            UpdateSectionLines(_frameClock.Elapsed.TotalSeconds);
            GameViewport.InvalidateRender();
        }

        private void UpdateSectionLines(double now)
        {
            if (!_sectionVolume.Enabled)
            {
                foreach (var pair in _sectionLines)
                    if (ReferenceEquals(pair.Key.Geometry, pair.Value.Clipped)) pair.Key.Geometry = pair.Value.Source;
                _sectionLines.Clear();
                return;
            }
            if (now - _lastSectionUpdate < 1.0 / 30) return;
            _lastSectionUpdate = now;
            foreach (var model in GameViewport.Items.OfType<LineGeometryModel3D>())
            {
                if (!(model.Geometry is LineGeometry3D geometry)) continue;
                if (!_sectionLines.TryGetValue(model, out var entry) || !ReferenceEquals(geometry, entry.Clipped))
                {
                    entry = (geometry, new LineGeometry3D
                    {
                        Positions = new Vector3Collection(), Indices = new IntCollection(), Colors = new Color4Collection(), IsDynamic = true
                    });
                    _sectionLines[model] = entry;
                }
                if (!model.IsRendering) continue;
                var source = entry.Source;
                var clipped = entry.Clipped;
                clipped.Positions.Clear(); clipped.Indices.Clear(); clipped.Colors.Clear();
                if (source.Positions == null || source.Indices == null) continue;
                for (int i = 0; i + 1 < source.Indices.Count; i += 2)
                {
                    int a = source.Indices[i], b = source.Indices[i + 1];
                    var va = source.Positions[a]; var vb = source.Positions[b];
                    var start = new Point3D(va.X, va.Y, va.Z); var end = new Point3D(vb.X, vb.Y, vb.Z);
                    if (!_sectionVolume.ClipSegment(ref start, ref end)) continue;
                    int index = clipped.Positions.Count;
                    clipped.Positions.Add(new SharpDX.Vector3((float)start.X, (float)start.Y, (float)start.Z));
                    clipped.Positions.Add(new SharpDX.Vector3((float)end.X, (float)end.Y, (float)end.Z));
                    clipped.Indices.Add(index); clipped.Indices.Add(index + 1);
                    if (source.Colors != null && source.Colors.Count == source.Positions.Count)
                    {
                        clipped.Colors.Add(source.Colors[a]); clipped.Colors.Add(source.Colors[b]);
                    }
                }
                clipped.UpdateVertices(); clipped.UpdateTriangles(); clipped.UpdateColors(); clipped.UpdateBounds();
                model.Geometry = clipped;
            }
        }
    }
}
