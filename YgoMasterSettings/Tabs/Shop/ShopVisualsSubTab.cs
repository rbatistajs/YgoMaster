using System;
using System.Drawing;
using System.Windows.Forms;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs.Shop
{
    // Sub-tab de ShopPackOddsVisuals.json — apenas 3 booleans globais
    // que afetam apresentação do pack opening no cliente. Form simples
    // com CheckBoxes.
    class ShopVisualsSubTab : UserControl
    {
        readonly ShopTab _parent;
        readonly ShopVisualsData _visuals;
        CheckBox _chkJebait, _chkOnBack, _chkOnPack;
        bool _suppress;

        public ShopVisualsSubTab(ShopTab parent, ShopVisualsData visuals)
        {
            _parent = parent;
            _visuals = visuals;
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;
            AutoScroll = true;
            BuildUi();
            LoadValues();
        }

        void BuildUi()
        {
            SuspendLayout();
            Label header = new Label
            {
                Text = "Flags visuais — controlam como o cliente exibe rarities " +
                       "durante o pack opening. Aplicado globalmente a todos os " +
                       "packs (não há override per-pack).",
                AutoSize = false, Dock = DockStyle.Top, Height = 50,
                ForeColor = Theme.FgMuted, Padding = new Padding(8, 8, 8, 4),
            };

            TableLayoutPanel t = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _chkJebait = AddRow(t, "RarityJebait",
                "Fake-out de rarity durante a animação de opening (suspense)");
            _chkOnBack = AddRow(t, "RarityOnCardBack",
                "Mostra rarity no verso do card antes do flip");
            _chkOnPack = AddRow(t, "RarityOnPack",
                "Mostra rarity no pack art (antes do opening)");

            Controls.Add(t);
            Controls.Add(header);
            ResumeLayout(performLayout: true);
        }

        CheckBox AddRow(TableLayoutPanel t, string key, string hint)
        {
            int row = t.RowCount;
            CheckBox cb = new CheckBox
            {
                Text = key, AutoSize = true,
                Margin = new Padding(0, 8, 12, 4),
                Font = new Font(Font, FontStyle.Bold),
            };
            cb.CheckedChanged += (s, e) =>
            {
                if (_suppress) return;
                _visuals.Root[key] = cb.Checked;
                _parent.MarkDirty();
            };
            t.Controls.Add(cb, 0, row);
            t.Controls.Add(new Label
            {
                Text = hint, AutoSize = true,
                ForeColor = Theme.FgMuted,
                Margin = new Padding(12, 8, 0, 4),
            }, 1, row);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowCount = row + 1;
            return cb;
        }

        void LoadValues()
        {
            _suppress = true;
            try
            {
                _chkJebait.Checked = ShopData.GetBool(_visuals.Root, "RarityJebait");
                _chkOnBack.Checked = ShopData.GetBool(_visuals.Root, "RarityOnCardBack");
                _chkOnPack.Checked = ShopData.GetBool(_visuals.Root, "RarityOnPack");
            }
            finally { _suppress = false; }
        }
    }
}
