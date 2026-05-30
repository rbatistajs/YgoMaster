using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using YgoMaster;
using YgoMasterSettings.Dialogs;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs.Shop
{
    // Sub-tab de Packs (boosters). DataGridView com lista + botões
    // Add / Edit / Delete / Duplicate. Edit/Add abrem ShopPackEditDialog.
    class ShopPacksSubTab : UserControl
    {
        readonly ShopTab _parent;
        readonly ShopData _shop;
        readonly string _dataDir;
        DataGridView _grid;
        Label _lblCount;

        public ShopPacksSubTab(ShopTab parent, ShopData shop, string dataDir)
        {
            _parent = parent;
            _shop = shop;
            _dataDir = dataDir;
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;
            BuildUi();
            RefreshGrid();
        }

        void BuildUi()
        {
            SuspendLayout();

            // Top: só "+ Add" global. Edit/Duplicate/Delete são por-row
            // via coluna Actions "⋯" (padrão do GateRegistryTab).
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
            };
            toolbar.Controls.Add(NewBtn("+ Add pack", OnAdd));

            _lblCount = new Label
            {
                AutoSize = false, Dock = DockStyle.Top, Height = 20,
                ForeColor = Theme.FgAccent, Padding = new Padding(6, 2, 0, 0),
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                ReadOnly = true,
                BackgroundColor = SystemColors.Window,
            };
            _grid.Columns.Add(MakeCol("id",        "ID",       70,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("name",      "Name",     320, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("type",      "Type",     70,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("cards",     "Cards",    60,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("odds",      "Odds",     150, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("price",     "Price",    70,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("image",     "Image",    120, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("imgStatus", "ImgOK",    60,  DataGridViewContentAlignment.MiddleCenter));
            // Coluna Actions "⋯" — click abre context menu com
            // Edit / Duplicate / Delete (padrão do GateRegistryTab).
            _grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "actions", HeaderText = "Actions",
                Text = "⋯", UseColumnTextForButtonValue = true, Width = 60,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
            });
            _grid.CellClick += (s, e) => {
                if (e.RowIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].Name == "actions")
                    ShowActionsMenu(e.RowIndex, e.ColumnIndex);
            };
            // Double-click na row = atalho pra Edit
            _grid.CellDoubleClick += (s, e) => {
                if (e.RowIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].Name == "actions") return;
                OnEdit();
            };
            // Colorir ImgOK
            _grid.CellFormatting += OnCellFormatting;

            Controls.Add(_grid);
            Controls.Add(_lblCount);
            Controls.Add(toolbar);
            ResumeLayout(performLayout: true);
        }

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "imgStatus") return;
            string v = Convert.ToString(e.Value) ?? "";
            if (v == "✓")
            {
                e.CellStyle.ForeColor = Color.FromArgb(0x27, 0xAE, 0x60);
                e.CellStyle.Font = new Font(Font, FontStyle.Bold);
            }
            else if (v.Length > 0)
            {
                e.CellStyle.ForeColor = Color.FromArgb(0xC0, 0x39, 0x2B);
                e.CellStyle.Font = new Font(Font, FontStyle.Bold);
            }
        }

        Button NewBtn(string text, EventHandler onClick, bool danger = false)
        {
            Button b = new Button { Text = text, Width = 100, Height = 28,
                Margin = new Padding(2) };
            if (danger)
            {
                b.BackColor = Color.FromArgb(0xC0, 0x39, 0x2B);
                b.ForeColor = Color.White;
                b.FlatStyle = FlatStyle.Flat;
            }
            b.Click += onClick;
            return b;
        }

        DataGridViewColumn MakeCol(string name, string header, int width,
                                    DataGridViewContentAlignment align)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = width,
                SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = { Alignment = align },
                ReadOnly = true,
            };
        }

        void RefreshGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            // Sort packs por ID ascendente
            List<string> keys = new List<string>(_shop.Packs.Keys);
            keys.Sort((a, b) =>
            {
                int ai, bi;
                int.TryParse(a, out ai); int.TryParse(b, out bi);
                return ai - bi;
            });
            foreach (string key in keys)
            {
                Dictionary<string, object> pack = _shop.Packs[key] as Dictionary<string, object>;
                if (pack == null) continue;
                int packId = ShopData.GetInt(pack, "packId");
                string name = ShopData.GetStr(pack, "nameTextId");
                int packType = ShopData.GetInt(pack, "packType");
                int cardCount = 0;
                Dictionary<string, object> cardList =
                    pack.ContainsKey("cardList") ? pack["cardList"] as Dictionary<string, object> : null;
                if (cardList != null)
                {
                    foreach (KeyValuePair<string, object> kv in cardList)
                    {
                        try { cardCount += Convert.ToInt32(kv.Value); } catch { }
                    }
                }
                string odds = ShopData.GetStr(pack, "oddsName");
                if (string.IsNullOrEmpty(odds)) odds = "(default)";
                int price = ShopData.GetInt(pack, "price");
                string image = ShopData.GetStr(pack, "packImage");
                string imgOk = !string.IsNullOrEmpty(image) &&
                               PackImageProcessor.AllVariantsExist(_dataDir, image)
                                ? "✓" : "✗";

                int idx = _grid.Rows.Add(packId, name, PackTypeLabel(packType),
                    cardCount, odds, price, image, imgOk, "⋯");
                _grid.Rows[idx].Tag = key;
            }
            _grid.ResumeLayout(performLayout: true);
            _lblCount.Text = "Mostrando " + _grid.Rows.Count + " packs";
        }

        static string PackTypeLabel(int t)
        {
            switch (t)
            {
                case 1: return "Booster";
                case 2: return "Special";
                case 3: return "Bonus";
                case 4: return "Selection";
                default: return t.ToString();
            }
        }

        // Posiciona ContextMenuStrip logo abaixo da cell "⋯" da row N.
        // Mesma estrutura do GateRegistryTab.
        void ShowActionsMenu(int rowIndex, int colIndex)
        {
            // Garante que a row clicada vire a CurrentRow pra que as
            // handlers Edit/Duplicate/Delete peguem o pack certo
            _grid.ClearSelection();
            _grid.Rows[rowIndex].Selected = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Edit",      null, (s, _) => OnEdit());
            menu.Items.Add("Duplicate", null, (s, _) => OnDuplicate());
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem del = new ToolStripMenuItem("Delete",
                null, (s, _) => OnDelete());
            del.ForeColor = Theme.FgDanger;
            menu.Items.Add(del);

            Rectangle r = _grid.GetCellDisplayRectangle(colIndex, rowIndex, false);
            menu.Show(_grid, new Point(r.Left, r.Bottom));
        }

        void OnAdd(object sender, EventArgs e)
        {
            using (ShopPackEditDialog dlg = new ShopPackEditDialog(_shop, _dataDir, null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                int newId = ShopData.GetInt(dlg.Result, "packId");
                _shop.Packs[newId.ToString(CultureInfo.InvariantCulture)] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        Dictionary<string, object> GetSelectedPack()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Tag == null) return null;
            string key = _grid.CurrentRow.Tag as string;
            if (key == null) return null;
            return _shop.Packs.ContainsKey(key) ? _shop.Packs[key] as Dictionary<string, object> : null;
        }

        void OnEdit()
        {
            Dictionary<string, object> pack = GetSelectedPack();
            if (pack == null) return;
            using (ShopPackEditDialog dlg = new ShopPackEditDialog(_shop, _dataDir, pack))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                int newId = ShopData.GetInt(dlg.Result, "packId");
                // ID não muda em edit (read-only no dialog) — sobrescreve
                _shop.Packs[newId.ToString(CultureInfo.InvariantCulture)] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnDuplicate()
        {
            Dictionary<string, object> pack = GetSelectedPack();
            if (pack == null) return;
            // Copy raso → user edita o novo no dialog (ID novo será solicitado)
            Dictionary<string, object> copy = new Dictionary<string, object>(pack);
            // Limpa ID — dialog vai pedir novo
            copy.Remove("packId");
            string oldName = ShopData.GetStr(copy, "nameTextId");
            if (!string.IsNullOrEmpty(oldName)) copy["nameTextId"] = oldName + " (copy)";
            using (ShopPackEditDialog dlg = new ShopPackEditDialog(_shop, _dataDir, copy))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                int newId = ShopData.GetInt(dlg.Result, "packId");
                _shop.Packs[newId.ToString(CultureInfo.InvariantCulture)] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnDelete()
        {
            Dictionary<string, object> pack = GetSelectedPack();
            if (pack == null) return;
            int id = ShopData.GetInt(pack, "packId");
            string name = ShopData.GetStr(pack, "nameTextId");
            DialogResult dr = MessageBox.Show(
                "Apagar pack " + id + " (" + name + ")?",
                "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            _shop.Packs.Remove(id.ToString(CultureInfo.InvariantCulture));
            _parent.MarkDirty();
            RefreshGrid();
        }
    }
}
