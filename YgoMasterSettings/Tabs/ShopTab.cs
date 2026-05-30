using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using YgoMasterSettings.Tabs.Shop;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs
{
    // Tab raiz do editor de Shop. Carrega 3 arquivos relacionados uma
    // vez e compartilha as instâncias entre as 6 sub-tabs:
    //   - Shop.json              → ShopData          (Packs/Structures/Accessories/Globals)
    //   - ShopPackOdds.json      → ShopOddsData      (Pack Odds)
    //   - ShopPackOddsVisuals.json → ShopVisualsData (Visuals)
    //
    // Save é unificado: 1 botão grava os 3 arquivos sequencialmente
    // (cada um com seu próprio backup em _bkp/). Dirty tracking é
    // global — qualquer sub-tab marca dirty e o Save grava tudo.
    class ShopTab : UserControl
    {
        readonly ShopData        _shop;
        readonly ShopOddsData    _odds;
        readonly ShopVisualsData _visuals;
        readonly string          _dataDir;

        Button _btnSave;
        Label  _lblDirty, _saveStatus;
        ProgressBar _saveProgress;
        Panel  _progressPanel;
        bool   _dirty;
        bool   _saving;

        public ShopTab()
        {
            _dataDir = Program.DataDir;
            _shop    = new ShopData       (Path.Combine(_dataDir, "Shop.json"));
            _odds    = new ShopOddsData   (Path.Combine(_dataDir, "ShopPackOdds.json"));
            _visuals = new ShopVisualsData(Path.Combine(_dataDir, "ShopPackOddsVisuals.json"));
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildUi();
        }

        // Sub-tabs chamam isso quando user editar algo (qualquer um dos 3 arquivos)
        public void MarkDirty()
        {
            _dirty = true;
            UpdateDirtyLabel();
        }

        void BuildUi()
        {
            SuspendLayout();

            Label header = new Label
            {
                Text = "Editor de Shop — Packs / Structures / Accessories / Globals / " +
                       "Pack Odds / Visuals. Save unificado grava os 3 arquivos JSON.",
                AutoSize = false, Dock = DockStyle.Top, Height = 32,
                ForeColor = Theme.FgMuted, Padding = new Padding(4),
            };

            // Bottom: save bar + progress
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 68 };
            Panel saveBar = new Panel
            {
                Dock = DockStyle.Top, Height = 40,
                Padding = new Padding(8, 4, 8, 4),
            };
            _btnSave = new Button { Text = "Save Shop (3 arquivos)", Width = 220, Height = 32,
                Dock = DockStyle.Right,
                BackColor = SystemColors.Highlight, ForeColor = SystemColors.HighlightText,
                FlatStyle = FlatStyle.Flat, Enabled = false,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            _btnSave.Click += OnSaveClick;
            _lblDirty = new Label { AutoSize = false, Dock = DockStyle.Fill,
                Padding = new Padding(0, 8, 12, 0),
                ForeColor = Theme.FgDanger,
                Font = new Font(Font, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleLeft };
            saveBar.Controls.Add(_btnSave);
            saveBar.Controls.Add(_lblDirty);

            _progressPanel = new Panel { Dock = DockStyle.Top, Height = 26,
                Padding = new Padding(8, 2, 8, 2), Visible = false };
            _saveProgress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
            };
            _saveStatus = new Label
            {
                Text = "", Dock = DockStyle.Right, Width = 280,
                Padding = new Padding(8, 4, 0, 0),
                ForeColor = Theme.FgAccent,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _progressPanel.Controls.Add(_saveProgress);
            _progressPanel.Controls.Add(_saveStatus);
            bottom.Controls.Add(_progressPanel);
            bottom.Controls.Add(saveBar);

            // Inner TabControl com as 6 sub-tabs
            TabControl inner = new TabControl { Dock = DockStyle.Fill };
            inner.TabPages.Add(MakePage("Packs",       new ShopPacksSubTab      (this, _shop, _dataDir)));
            inner.TabPages.Add(MakePage("Structures",  new ShopStructuresSubTab (this, _shop, _dataDir)));
            inner.TabPages.Add(MakePage("Accessories", new ShopAccessoriesSubTab(this, _shop, _dataDir)));
            inner.TabPages.Add(MakePage("Pack Odds",   new ShopOddsSubTab       (this, _odds)));
            inner.TabPages.Add(MakePage("Visuals",     new ShopVisualsSubTab    (this, _visuals)));
            inner.TabPages.Add(MakePage("Globals",     new ShopGlobalsSubTab    (this, _shop)));

            Controls.Add(inner);
            Controls.Add(bottom);
            Controls.Add(header);
            ResumeLayout(performLayout: true);
        }

        static TabPage MakePage(string title, UserControl body)
        {
            TabPage p = new TabPage(title) { Padding = new Padding(6) };
            body.Dock = DockStyle.Fill;
            p.Controls.Add(body);
            return p;
        }

        void UpdateDirtyLabel()
        {
            _btnSave.Enabled = _dirty && !_saving;
            _lblDirty.Text = _dirty ? "* mudanças não salvas" : "";
        }

        // Save async: salva os 3 arquivos sequencialmente. Cada um com
        // seu próprio backup. Reporta fase pra ProgressBar.
        void OnSaveClick(object sender, EventArgs e)
        {
            if (_saving || !_dirty) return;
            BeginSaveUi();
            System.Threading.Tasks.Task.Run<SaveResult>(() =>
            {
                SaveResult r = new SaveResult();
                try
                {
                    ReportProgress("Gravando Shop.json…");
                    r.ShopBackup    = _shop.Save();
                    ReportProgress("Gravando ShopPackOdds.json…");
                    r.OddsBackup    = _odds.Save();
                    ReportProgress("Gravando ShopPackOddsVisuals.json…");
                    r.VisualsBackup = _visuals.Save();
                }
                catch (Exception ex) { r.Error = ex; }
                return r;
            }).ContinueWith(OnSaveCompleted,
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        class SaveResult
        {
            public string ShopBackup;
            public string OddsBackup;
            public string VisualsBackup;
            public Exception Error;
        }

        void ReportProgress(string phase)
        {
            if (_saveStatus.InvokeRequired)
            {
                _saveStatus.BeginInvoke(new Action<string>(ReportProgress), phase);
                return;
            }
            _saveStatus.Text = phase;
        }

        void BeginSaveUi()
        {
            _saving = true;
            _btnSave.Enabled = false;
            _saveStatus.Text = "Iniciando…";
            _progressPanel.Visible = true;
        }
        void EndSaveUi()
        {
            _saving = false;
            _progressPanel.Visible = false;
            _saveStatus.Text = "";
            UpdateDirtyLabel();
        }

        void OnSaveCompleted(System.Threading.Tasks.Task<SaveResult> t)
        {
            EndSaveUi();
            SaveResult r = t.Result;
            if (r.Error != null)
            {
                MessageBox.Show("Falha ao salvar:\n" + r.Error.Message,
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _dirty = false;
            UpdateDirtyLabel();
            List<string> backups = new List<string>();
            if (!string.IsNullOrEmpty(r.ShopBackup))    backups.Add(Path.GetFileName(r.ShopBackup));
            if (!string.IsNullOrEmpty(r.OddsBackup))    backups.Add(Path.GetFileName(r.OddsBackup));
            if (!string.IsNullOrEmpty(r.VisualsBackup)) backups.Add(Path.GetFileName(r.VisualsBackup));
            MessageBox.Show("Shop salvo (3 arquivos).\nBackups em _bkp/:\n  " +
                string.Join("\n  ", backups.ToArray()),
                "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
