using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BIMaestro.Navisworks.Services;

namespace BIMaestro.Navisworks.UI
{
    internal sealed class ModifyLinksWindow : Form
    {
        private readonly LinkService service;
        private readonly DataGridView grid = new DataGridView();
        private readonly Button replace = new Button();
        private readonly Button apply = new Button();
        private readonly Button reset = new Button();
        private List<ModelReference> references;
        private bool busy;

        public ModifyLinksWindow(LinkService service)
        {
            this.service = service;
            Text = "BIMaestro — Modifier les liens — " + LinkService.DiagnosticVersion;
            ClientSize = new Size(1150, 480);
            MinimumSize = new Size(1050, 350);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text =
                "Préparez les nouveaux chemins, puis cliquez sur Appliquer et recharger le NWF.\r\n" +
                "Votre travail sera enregistré et sauvegardé avant la réouverture. Après vérification du résultat, enregistrez avec Ctrl+S." }, 0, 0);
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AutoGenerateColumns = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fichier", DataPropertyName = "File", Width = 190 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Chemin actuel", DataPropertyName = "Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nouveau chemin préparé", DataPropertyName = "ReplacementPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "État", DataPropertyName = "Status", Width = 140 });
            grid.SelectionChanged += (s, e) => UpdateButtons();
            layout.Controls.Add(grid, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            var close = new Button { Text = "Fermer", AutoSize = true, DialogResult = DialogResult.Cancel };
            replace.Text = "Choisir le nouveau fichier…";
            replace.AutoSize = true;
            replace.Click += ReplaceClick;
            actions.Controls.Add(close);
            apply.Text = "Appliquer et recharger le NWF";
            apply.AutoSize = true;
            apply.Click += ApplyClick;
            actions.Controls.Add(apply);
            reset.Text = "Annuler ce changement";
            reset.AutoSize = true;
            reset.Click += (s, e) =>
            {
                var selected = grid.CurrentRow?.DataBoundItem as ModelReference;
                if (selected != null) selected.ReplacementPath = null;
                grid.Refresh();
                UpdateButtons();
            };
            actions.Controls.Add(reset);
            actions.Controls.Add(replace);
            layout.Controls.Add(actions, 0, 2);
            Controls.Add(layout);
            CancelButton = close;
            FormClosing += (s, e) =>
            {
                if (busy) { e.Cancel = true; return; }
                if (references != null && references.Any(r => !string.IsNullOrWhiteSpace(r.ReplacementPath)))
                    e.Cancel = MessageBox.Show(this, "Fermer et abandonner les changements préparés ?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes;
            };
            Reload();
        }

        private void Reload()
        {
            references = service.GetReferences();
            grid.DataSource = references;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var selected = grid.CurrentRow?.DataBoundItem as ModelReference;
            replace.Enabled = !busy && selected?.AbsolutePath != null;
            reset.Enabled = !busy && !string.IsNullOrWhiteSpace(selected?.ReplacementPath);
            apply.Enabled = !busy && references != null && references.Any(r => !string.IsNullOrWhiteSpace(r.ReplacementPath));
        }

        private void ApplyClick(object sender, EventArgs e)
        {
            if (busy || references == null) return;
            var count = references.Count(r => !string.IsNullOrWhiteSpace(r.ReplacementPath));
            if (count == 0) return;
            if (MessageBox.Show(this,
                "Appliquer " + count + " changement(s) de chemin ?\r\n\r\n" +
                "Votre travail sera enregistré si nécessaire, puis le NWF sera rouvert. " +
                "Deux sauvegardes temporaires seront créées à côté du NWF, puis supprimées si le remplacement réussit.\r\n" +
                "Après vérification du résultat, Ctrl+S enregistrera les nouveaux liens.",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            busy = true;
            UseWaitCursor = true;
            UpdateButtons();
            try
            {
                var cleanupResult = service.ApplyAndReload(references);
                Reload();
                MessageBox.Show(this, "NWF rechargé : les nouveaux chemins ont été vérifiés.\r\n" +
                    "Contrôlez la maquette puis enregistrez avec Ctrl+S.\r\n\r\n" + cleanupResult,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowDiagnostic(ex);
                // Keep the plan visible for correction/retry. Service revalidates it before saving.
            }
            finally
            {
                busy = false;
                UseWaitCursor = false;
                UpdateButtons();
            }
        }

        private void ShowDiagnostic(Exception error)
        {
            using (var dialog = new Form
            {
                Text = "BIMaestro — " + LinkService.DiagnosticVersion,
                Size = new Size(900, 620), MinimumSize = new Size(640, 400),
                StartPosition = FormStartPosition.CenterParent, Font = Font,
                AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var report = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                    WordWrap = false, Dock = DockStyle.Fill,
                    Text = error.Message + "\r\n\r\n" + service.DiagnosticReport
                };
                var actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, Height = 48,
                    FlowDirection = FlowDirection.RightToLeft
                };
                var close = new Button { Text = "Fermer", AutoSize = true, DialogResult = DialogResult.OK };
                var copy = new Button { Text = "Copier le diagnostic", AutoSize = true };
                copy.Click += (sender, args) =>
                {
                    try { Clipboard.SetText(report.Text); }
                    catch (Exception)
                    {
                        report.Focus();
                        report.SelectAll();
                        MessageBox.Show(dialog, "Le presse-papiers est indisponible. Le texte est sélectionné ; utilisez Ctrl+C.");
                    }
                };
                actions.Controls.Add(close);
                actions.Controls.Add(copy);
                dialog.Controls.Add(report);
                dialog.Controls.Add(actions);
                dialog.CancelButton = close;
                dialog.ShowDialog(this);
            }
        }

        private void ReplaceClick(object sender, EventArgs e)
        {
            var selected = grid.CurrentRow?.DataBoundItem as ModelReference;
            if (selected == null) return;
            using (var picker = new OpenFileDialog
            {
                Title = "Nouveau fichier pour " + selected.File,
                Filter = "Maquettes Navisworks (*.nwc;*.nwd)|*.nwc;*.nwd|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true, Multiselect = false, RestoreDirectory = true
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    service.ValidateReplacement(selected, picker.FileName);
                    selected.ReplacementPath = picker.FileName;
                    grid.Refresh();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    UpdateButtons();
                }
            }
        }
    }
}
