using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs.Shop
{
    // Sub-tab dos flags globais no topo do Shop.json (DefaultPackPrice,
    // NoDuplicatesPerPack, UnlockAllSecrets, etc). Form simples com
    // CheckBoxes pra booleans + NumericUpDowns pros ints.
    //
    // Mudança em qualquer field marca ShopTab dirty.
    class ShopGlobalsSubTab : UserControl
    {
        readonly ShopTab _parent;
        readonly ShopData _shop;

        // Descritor compacto dos flags conhecidos no Shop.json
        class Flag
        {
            public string Key;
            public string Label;
            public string Hint;
            public bool IsBool;
            public int Min, Max;
        }

        static readonly List<Flag> KnownFlags = new List<Flag>
        {
            // ----- Numerics -----
            new Flag { Key = "DefaultSecretDuration",       Label = "Default secret duration", IsBool = false, Min = 0, Max = 9999, Hint = "Dias até unlock auto" },
            new Flag { Key = "DefaultPackPrice",            Label = "Default pack price (x1)", IsBool = false, Min = 0, Max = 99999, Hint = "Gems por 1 pack" },
            new Flag { Key = "DefaultPackPriceX10",         Label = "Default pack price (x10)", IsBool = false, Min = 0, Max = 999999, Hint = "Gems por 10 packs" },
            new Flag { Key = "DefaultStructureDeckPrice",   Label = "Default structure price", IsBool = false, Min = 0, Max = 99999, Hint = "Gems por structure deck" },
            new Flag { Key = "DefaultUnlockSecretsAtPercent", Label = "Unlock at % opened", IsBool = false, Min = 0, Max = 100, Hint = "Quando o próximo pack na chain destrava" },
            new Flag { Key = "DefaultPackCardNum",          Label = "Default cards per pack", IsBool = false, Min = 1, Max = 24, Hint = "Geralmente 8" },
            // ----- Booleans -----
            new Flag { Key = "PutAllCardsInStandardPack",   Label = "Put all cards in standard pack", IsBool = true, Hint = "Forced single pack mode" },
            new Flag { Key = "NoDuplicatesPerPack",         Label = "No duplicates per pack", IsBool = true, Hint = "Mesmo cid não sai 2x no mesmo pack" },
            new Flag { Key = "PerPackRarities",             Label = "Per-pack rarities", IsBool = true, Hint = "Usa rarity definida no pack vs no CardList" },
            new Flag { Key = "DisableCardStyleRarity",      Label = "Disable card style rarity", IsBool = true, Hint = "Sem premium (shine/royal)" },
            new Flag { Key = "DisableUltraRareGuarantee",   Label = "Disable UR guarantee", IsBool = true, Hint = "Remove slot 8 garantia" },
            new Flag { Key = "UpgradeRarityWhenNotFound",   Label = "Upgrade rarity when not found", IsBool = true, Hint = "Sobe rarity se pool vazio" },
            new Flag { Key = "UnlockAllSecrets",            Label = "Unlock all secrets", IsBool = true, Hint = "Todos os packs disponíveis sem chain" },
        };

        readonly Dictionary<string, Control> _inputs = new Dictionary<string, Control>();
        bool _suppress;

        public ShopGlobalsSubTab(ShopTab parent, ShopData shop)
        {
            _parent = parent;
            _shop = shop;
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
                Text = "Flags globais — afetam comportamento de todo o shop. " +
                       "Valores aqui são os defaults usados quando packs não " +
                       "especificam override.",
                AutoSize = false, Dock = DockStyle.Top, Height = 36,
                ForeColor = Theme.FgMuted, Padding = new Padding(4),
            };

            TableLayoutPanel t = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 3,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8),
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            int row = 0;
            foreach (Flag f in KnownFlags)
            {
                Label lbl = new Label { Text = f.Label + ":", AutoSize = true,
                    Margin = new Padding(0, 8, 12, 4),
                    Font = new Font(Font, FontStyle.Bold) };
                t.Controls.Add(lbl, 0, row);
                Control input;
                if (f.IsBool)
                {
                    CheckBox cb = new CheckBox { AutoSize = true,
                        Margin = new Padding(0, 6, 8, 4) };
                    cb.CheckedChanged += (s, e) => OnEdit(f.Key, cb.Checked);
                    input = cb;
                }
                else
                {
                    NumericUpDown n = new NumericUpDown
                    {
                        Width = 120, Minimum = f.Min, Maximum = f.Max,
                        Margin = new Padding(0, 4, 8, 4),
                    };
                    n.ValueChanged += (s, e) => OnEdit(f.Key, (int)n.Value);
                    input = n;
                }
                t.Controls.Add(input, 1, row);
                _inputs[f.Key] = input;
                if (!string.IsNullOrEmpty(f.Hint))
                {
                    Label hint = new Label { Text = f.Hint, AutoSize = true,
                        ForeColor = Theme.FgMuted,
                        Margin = new Padding(8, 8, 0, 4) };
                    t.Controls.Add(hint, 2, row);
                }
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                row++;
            }
            t.RowCount = row;
            Controls.Add(t);
            Controls.Add(header);
            ResumeLayout(performLayout: true);
        }

        void LoadValues()
        {
            _suppress = true;
            try
            {
                foreach (Flag f in KnownFlags)
                {
                    Control c;
                    if (!_inputs.TryGetValue(f.Key, out c)) continue;
                    if (f.IsBool)
                    {
                        ((CheckBox)c).Checked = ShopData.GetBool(_shop.Root, f.Key);
                    }
                    else
                    {
                        int v = ShopData.GetInt(_shop.Root, f.Key);
                        NumericUpDown n = (NumericUpDown)c;
                        if (v < n.Minimum) v = (int)n.Minimum;
                        if (v > n.Maximum) v = (int)n.Maximum;
                        n.Value = v;
                    }
                }
            }
            finally { _suppress = false; }
        }

        void OnEdit(string key, object value)
        {
            if (_suppress) return;
            _shop.Root[key] = value;
            _parent.MarkDirty();
        }
    }
}
