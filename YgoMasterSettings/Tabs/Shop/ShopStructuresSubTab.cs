using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using YgoMasterSettings.Dialogs;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs.Shop
{
    // Sub-tab de Structure Decks. Lista os entries em StructureShop +
    // permite editar price/release/accessory linkage. Pra editar o
    // CONTEÚDO do deck em si, abrir o JSON em DataLE/StructureDecks/<id>.json
    // manualmente (escopo Fase 1 — editor de deck full vai ficar pra
    // depois).
    class ShopStructuresSubTab : UserControl
    {
        readonly ShopTab _parent;
        readonly ShopData _shop;
        readonly string _dataDir;
        DataGridView _grid;
        Label _lblCount;

        public ShopStructuresSubTab(ShopTab parent, ShopData shop, string dataDir)
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
            // Toolbar: só "+ Add" global. Edit/Delete por-row via "⋯".
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
            };
            toolbar.Controls.Add(NewBtn("+ Add structure", OnAdd));

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
            _grid.Columns.Add(MakeCol("shopId",  "Shop ID",   90,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("target",  "Deck ID",   90,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("deckFile","Deck file", 200, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("price",   "Price",     70,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("limit",   "Limit buy", 70,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("box",     "Box ID",    80,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("sleeve",  "Sleeve ID", 80,  DataGridViewContentAlignment.MiddleRight));
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
            _grid.CellDoubleClick += (s, e) => {
                if (e.RowIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].Name == "actions") return;
                OnEdit();
            };

            Controls.Add(_grid);
            Controls.Add(_lblCount);
            Controls.Add(toolbar);
            ResumeLayout(performLayout: true);
        }

        Button NewBtn(string text, EventHandler onClick, bool danger = false)
        {
            Button b = new Button { Text = text, Width = 130, Height = 28,
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
            List<string> keys = new List<string>(_shop.Structures.Keys);
            keys.Sort();
            foreach (string key in keys)
            {
                Dictionary<string, object> s =
                    _shop.Structures[key] as Dictionary<string, object>;
                if (s == null) continue;
                int targetId = ShopData.GetInt(s, "targetId");
                int price = ShopData.GetInt(s, "price");
                int limit = ShopData.GetInt(s, "limit_buy_count");
                Dictionary<string, object> accessory =
                    s.ContainsKey("accessory") ? s["accessory"] as Dictionary<string, object> : null;
                int boxId = ShopData.GetInt(accessory, "box");
                int sleeveId = ShopData.GetInt(accessory, "sleeve");
                string deckFile = targetId + ".json";
                string deckPath = Path.Combine(_dataDir, "StructureDecks", deckFile);
                if (!File.Exists(deckPath)) deckFile = "(missing) " + deckFile;
                int idx = _grid.Rows.Add(key, targetId, deckFile, price, limit, boxId, sleeveId, "⋯");
                _grid.Rows[idx].Tag = key;
            }
            _grid.ResumeLayout(performLayout: true);
            _lblCount.Text = "Mostrando " + _grid.Rows.Count + " structures";
        }

        Dictionary<string, object> GetSelected()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Tag == null) return null;
            string key = _grid.CurrentRow.Tag as string;
            return key != null && _shop.Structures.ContainsKey(key)
                ? _shop.Structures[key] as Dictionary<string, object>
                : null;
        }

        void ShowActionsMenu(int rowIndex, int colIndex)
        {
            _grid.ClearSelection();
            _grid.Rows[rowIndex].Selected = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Edit", null, (s, _) => OnEdit());
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
            using (ShopStructureEditDialog dlg =
                new ShopStructureEditDialog(_shop, _dataDir, null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                string key = ShopData.GetInt(dlg.Result, "shopId").ToString(CultureInfo.InvariantCulture);
                _shop.Structures[key] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnEdit()
        {
            Dictionary<string, object> s = GetSelected();
            if (s == null) return;
            using (ShopStructureEditDialog dlg =
                new ShopStructureEditDialog(_shop, _dataDir, s))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                string key = ShopData.GetInt(dlg.Result, "shopId").ToString(CultureInfo.InvariantCulture);
                _shop.Structures[key] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnDelete()
        {
            Dictionary<string, object> s = GetSelected();
            if (s == null) return;
            int id = ShopData.GetInt(s, "shopId");
            DialogResult dr = MessageBox.Show(
                "Apagar structure shop entry " + id + "?",
                "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            _shop.Structures.Remove(id.ToString(CultureInfo.InvariantCulture));
            _parent.MarkDirty();
            RefreshGrid();
        }
    }
}
