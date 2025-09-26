using System;
using System.Collections.Generic;
using System.ComponentModel;           // ← pour CancelEventArgs (OnClosing)
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Famille.Orbit3D
{
    public partial class Family3DPreviewWindow : Window
    {
        private bool _isRealisticLighting = true; // (Réaliste ↔ Uniforme)

        public Family3DPreviewWindow()
        {
            InitializeComponent();

            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Pendant le chargement on reste au-dessus (désactivé en fin de LoadMeshes)
            Topmost = true;

            // Raccourcis : Échap pour fermer (= Hide), L pour basculer l'éclairage
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) { SafeHide(); return; }
                if (e.Key == System.Windows.Input.Key.L)
                {
                    _isRealisticLighting = !_isRealisticLighting;
                    SetLightingMode(_isRealisticLighting);
                }
            };

            // Head-light qui suit la caméra
            view.CameraChanged += (s, e) => UpdateHeadlightDirection();

            // Démarrage en mode réaliste
            Loaded += (s, e) =>
            {
                SetLightingMode(true);
                UpdateHeadlightDirection();
            };

            // IMPORTANT : ne jamais détruire, on cache
            Closing += Family3DPreviewWindow_Closing;
        }

        /// <summary>Attache la fenêtre à la fenêtre Revit (pour rester devant).</summary>
        public void AttachToRevit(IntPtr mainHwnd)
        {
            new WindowInteropHelper(this) { Owner = mainHwnd };
        }

        /// <summary>Affiche/masque l’overlay de chargement/erreur.</summary>
        public void ShowBusy(bool isBusy, string message = null)
        {
            Overlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            if (message != null) OverlayText.Text = message;
        }

        /// <summary>Efface le contenu 3D (avant de recharger une autre famille).</summary>
        public void ClearScene()
        {
            try
            {
                _root.Content = null;      // libère le Model3DGroup
                ShowBusy(false, null);     // cache l’overlay
            }
            catch { /* ignore */ }
        }

        /// <summary>Charge les maillages dans la scène (matériaux diffus, sans reflet).</summary>
        public void LoadMeshes(IList<MeshData> meshes)
        {
            var group = new Model3DGroup();

            foreach (var m in meshes)
            {
                // Évite le "pancake" tout blanc si la couleur est quasi blanche
                Color c = m.DiffuseColor;
                bool nearWhite = (c.R > 245 && c.G > 245 && c.B > 245);
                if (nearWhite) c = Color.FromRgb(0xE0, 0xE0, 0xE0);

                // Matériau diffus (pas de spéculaire)
                var brush = new SolidColorBrush(c) { Opacity = m.Opacity };
                try { brush.Freeze(); } catch { }
                var mat = new DiffuseMaterial(brush);
                try { mat.Freeze(); } catch { }

                var geo = new MeshGeometry3D
                {
                    Positions = m.Positions,
                    TriangleIndices = m.Indices,
                    Normals = (m.Normals?.Count == m.Positions.Count) ? m.Normals : null
                };
                try { geo.Freeze(); } catch { }

                var gm = new GeometryModel3D(geo, mat) { BackMaterial = mat };
                try { gm.Freeze(); } catch { }

                group.Children.Add(gm);
            }

            try { group.Freeze(); } catch { }
            _root.Content = group;

            // Repères visibles
            view.ShowCoordinateSystem = true;
            view.ShowViewCube = true;

            view.ZoomExtents();
            ShowBusy(false, null);

            // Éclairage par défaut : réaliste + head-light
            _isRealisticLighting = true;
            SetLightingMode(true);
            UpdateHeadlightDirection();

            // Redonne la main à l’utilisateur
            Topmost = false;
            Activate();
            Focus();
        }

        // ---------- Eclairage ----------

        private void SetLightingMode(bool realistic)
        {
            var children = view.Children;

            // s'assurer que les nœuds existent (déclarés dans le XAML)
            if (!children.Contains(RealisticLightsNode)) children.Insert(0, RealisticLightsNode);
            if (!children.Contains(AmbientLightsNode)) children.Insert(0, AmbientLightsNode);
            if (!children.Contains(HeadlightNode)) children.Insert(0, HeadlightNode);

            if (realistic)
            {
                // Garder DefaultLights + Headlight ; retirer Ambient
                if (children.Contains(AmbientLightsNode)) children.Remove(AmbientLightsNode);
                if (!children.Contains(RealisticLightsNode)) children.Insert(0, RealisticLightsNode);
                if (!children.Contains(HeadlightNode)) children.Insert(0, HeadlightNode);
            }
            else
            {
                // Garder Ambient seul ; retirer DefaultLights + Headlight
                if (children.Contains(RealisticLightsNode)) children.Remove(RealisticLightsNode);
                if (children.Contains(HeadlightNode)) children.Remove(HeadlightNode);
                if (!children.Contains(AmbientLightsNode)) children.Insert(0, AmbientLightsNode);
            }
        }

        private void UpdateHeadlightDirection()
        {
            if (Headlight is DirectionalLight dl && view.Camera is ProjectionCamera cam)
            {
                var dir = cam.LookDirection; // vers la scène
                if (dir.LengthSquared > 1e-12)
                {
                    dir.Normalize();
                    dl.Direction = dir; // inverser en "-dir" si besoin
                }
            }
        }

        // ---------- Fermeture/Hide ----------

        private void Family3DPreviewWindow_Closing(object? sender, CancelEventArgs e)
        {
            // On NE ferme PAS la fenêtre → on la cache pour réutilisation
            e.Cancel = true;
            SafeHide();
        }

        private void SafeHide()
        {
            try
            {
                // détache les anims en cours pour éviter des callbacks post-hide
                Overlay.BeginAnimation(OpacityProperty, null);
                view.BeginAnimation(OpacityProperty, null);
            }
            catch { }

            try
            {
                ClearScene();
                Hide();
            }
            catch { }
        }
    }
}
