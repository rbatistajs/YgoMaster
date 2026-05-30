using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using YgoMasterSettings.Dialogs;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs.Shop
{
    // Sub-tab de Accessories (sleeves, protectors, boxes, icons, etc).
    // Lista grande (~275 entries) com filtro por category. Edit abre
    // dialog mais simples — accessories são cosméticos sem cardList.
    class ShopAccessoriesSubTab : UserControl
    {
        readonly ShopTab _parent;
        readonly ShopData _shop;
        readonly string _dataDir;
        DataGridView _grid;
        Label _lblCount;
        ComboBox _cmbCategoryFilter;
        TextBox  _txtSearch;

        int    _categoryFilter = -1;   // -1 = all
        string _searchText = "";

        // Category → display name. Valores conhecidos do client (3=protector,
        // 4=deck case, etc — adicionar conforme aparecer).
        static readonly Dictionary<int, string> CategoryLabels = new Dictionary<int, string>
        {
            { 1, "Avatar"       }, { 2, "Icon Frame"  }, { 3, "Protector"  },
            { 4, "Deck Case"    }, { 5, "Field"       }, { 6, "Field Obj"  },
            { 7, "Avatar Home"  }, { 8, "Wallpaper"   }, { 9, "Profile Tag"},
            { 10, "Pack Ticket" }, { 11, "Consume"    },
        };

        public ShopAccessoriesSubTab(ShopTab parent, ShopData shop, string dataDir)
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

            // Toolbar: só "+ Add" global + filtros. Edit/Delete por-row via "⋯".
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
            };
            toolbar.Controls.Add(NewBtn("+ Add accessory", OnAdd));

            // Filtros
            toolbar.Controls.Add(new Label { Text = "  Category:", AutoSize = true,
                Margin = new Padding(8, 8, 4, 0),
                Font = new Font(Font, FontStyle.Bold) });
            _cmbCategoryFilter = new ComboBox { Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbCategoryFilter.Items.Add(new CategoryItem(-1, "All"));
            foreach (KeyValuePair<int, string> kv in CategoryLabels)
                _cmbCategoryFilter.Items.Add(new CategoryItem(kv.Key, kv.Value));
            _cmbCategoryFilter.SelectedIndex = 0;
            _cmbCategoryFilter.SelectedIndexChanged += (s, e) => {
                _categoryFilter = ((CategoryItem)_cmbCategoryFilter.SelectedItem).Id;
                RefreshGrid();
            };
            toolbar.Controls.Add(_cmbCategoryFilter);

            toolbar.Controls.Add(new Label { Text = "  Search:", AutoSize = true,
                Margin = new Padding(8, 8, 4, 0),
                Font = new Font(Font, FontStyle.Bold) });
            _txtSearch = new TextBox { Width = 180 };
            _txtSearch.TextChanged += (s, e) => { _searchText = _txtSearch.Text; RefreshGrid(); };
            toolbar.Controls.Add(_txtSearch);

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
            _grid.Columns.Add(MakeCol("shopId",   "Shop ID",   90,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("itemId",   "Item ID",   90,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("category", "Category",  120, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("subCat",   "SubCat",    60,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("iconData", "Icon",      200, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("max",      "Max",       50,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("limit",    "LimitBuy",  70,  DataGridViewContentAlignment.MiddleCenter));
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
            string s = (_searchText ?? "").Trim();
            int total = 0, shown = 0;
            List<string> keys = new List<string>(_shop.Accessories.Keys);
            keys.Sort();
            foreach (string key in keys)
            {
                total++;
                Dictionary<string, object> a =
                    _shop.Accessories[key] as Dictionary<string, object>;
                if (a == null) continue;
                int cat = ShopData.GetInt(a, "category");
                if (_categoryFilter != -1 && cat != _categoryFilter) continue;
                int shopId = ShopData.GetInt(a, "shopId");
                int itemId = ShopData.GetInt(a, "itemId");
                int sub = ShopData.GetInt(a, "subCategory");
                string icon = ShopData.GetStr(a, "iconData");
                int max = ShopData.GetInt(a, "max");
                int limit = ShopData.GetInt(a, "limit_buy_count");
                if (s.Length > 0)
                {
                    string hay = (shopId + " " + itemId + " " + icon).ToLowerInvariant();
                    if (!hay.Contains(s.ToLowerInvariant())) continue;
                }
                string catLabel;
                CategoryLabels.TryGetValue(cat, out catLabel);
                int idx = _grid.Rows.Add(shopId, itemId,
                    (catLabel ?? cat.ToString()) + " (" + cat + ")",
                    sub, icon, max, limit, "⋯");
                _grid.Rows[idx].Tag = key;
                shown++;
            }
            _grid.ResumeLayout(performLayout: true);
            _lblCount.Text = "Mostrando " + shown + " / " + total + " accessories";
        }

        Dictionary<string, object> GetSelected()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Tag == null) return null;
            string key = _grid.CurrentRow.Tag as string;
            return key != null && _shop.Accessories.ContainsKey(key)
                ? _shop.Accessories[key] as Dictionary<string, object>
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
            using (ShopAccessoryEditDialog dlg =
                new ShopAccessoryEditDialog(_shop, _dataDir, null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                string key = ShopData.GetInt(dlg.Result, "shopId").ToString(CultureInfo.InvariantCulture);
                _shop.Accessories[key] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnEdit()
        {
            Dictionary<string, object> a = GetSelected();
            if (a == null) return;
            using (ShopAccessoryEditDialog dlg =
                new ShopAccessoryEditDialog(_shop, _dataDir, a))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                string key = ShopData.GetInt(dlg.Result, "shopId").ToString(CultureInfo.InvariantCulture);
                _shop.Accessories[key] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnDelete()
        {
            Dictionary<string, object> a = GetSelected();
            if (a == null) return;
            int id = ShopData.GetInt(a, "shopId");
            DialogResult dr = MessageBox.Show(
                "Apagar accessory " + id + "?",
                "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            _shop.Accessories.Remove(id.ToString(CultureInfo.InvariantCulture));
            _parent.MarkDirty();
            RefreshGrid();
        }

        class CategoryItem
        {
            public int Id; public string Label;
            public CategoryItem(int id, string label) { Id = id; Label = label; }
            public override string ToString() { return Label; }
        }
    }
}
