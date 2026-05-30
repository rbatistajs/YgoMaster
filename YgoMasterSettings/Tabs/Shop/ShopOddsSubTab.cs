using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using YgoMasterSettings.Dialogs;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs.Shop
{
    // Sub-tab de ShopPackOdds.json. Lista todas as entries do array
    // (named ou default) com botões Add / Edit / Duplicate / Delete.
    // Edit/Add abrem ShopPackOddsEditDialog.
    class ShopOddsSubTab : UserControl
    {
        readonly ShopTab _parent;
        readonly ShopOddsData _odds;
        DataGridView _grid;
        Label _lblCount;

        public ShopOddsSubTab(ShopTab parent, ShopOddsData odds)
        {
            _parent = parent;
            _odds = odds;
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;
            BuildUi();
            RefreshGrid();
        }

        void BuildUi()
        {
            SuspendLayout();
            // Toolbar: só "+ Add" global. Edit/Duplicate/Delete por-row via "⋯".
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
            };
            toolbar.Controls.Add(NewBtn("+ Add odds", OnAdd));

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
            _grid.Columns.Add(MakeCol("idx",      "#",         40,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("type",     "Type",      80,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("desc",     "Name/Desc", 320, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeCol("slots",    "Slots",     50,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("premiere", "Premiere",  60,  DataGridViewContentAlignment.MiddleCenter));
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
            Button b = new Button { Text = text, Width = 110, Height = 28,
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
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = align },
                ReadOnly = true,
            };
        }

        void RefreshGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            for (int i = 0; i < _odds.Entries.Count; i++)
            {
                Dictionary<string, object> entry = _odds.Entries[i] as Dictionary<string, object>;
                if (entry == null) continue;
                string desc = ShopOddsData.DescribeEntry(entry);
                bool named = !string.IsNullOrEmpty(ShopData.GetStr(entry, "name"));
                int slots = 0, premiere = 0;
                object crl;
                if (entry.TryGetValue("cardRateList", out crl) && crl is List<object>)
                    slots = ((List<object>)crl).Count;
                object prl;
                if (entry.TryGetValue("premiereRateList", out prl) && prl is List<object>)
                    premiere = ((List<object>)prl).Count;
                int idx = _grid.Rows.Add(i, named ? "Named" : "Default", desc, slots, premiere, "⋯");
                _grid.Rows[idx].Tag = i;
            }
            _grid.ResumeLayout(performLayout: true);
            _lblCount.Text = "Mostrando " + _grid.Rows.Count + " odds entries";
        }

        int? GetSelectedIndex()
        {
            if (_grid.CurrentRow == null || !(_grid.CurrentRow.Tag is int)) return null;
            return (int)_grid.CurrentRow.Tag;
        }

        void ShowActionsMenu(int rowIndex, int colIndex)
        {
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
            using (ShopPackOddsEditDialog dlg = new ShopPackOddsEditDialog(null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                _odds.Entries.Add(dlg.Result);
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnEdit()
        {
            int? idx = GetSelectedIndex();
            if (idx == null) return;
            Dictionary<string, object> entry = _odds.Entries[idx.Value] as Dictionary<string, object>;
            if (entry == null) return;
            using (ShopPackOddsEditDialog dlg = new ShopPackOddsEditDialog(entry))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                _odds.Entries[idx.Value] = dlg.Result;
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnDuplicate()
        {
            int? idx = GetSelectedIndex();
            if (idx == null) return;
            Dictionary<string, object> entry = _odds.Entries[idx.Value] as Dictionary<string, object>;
            if (entry == null) return;
            // Copy raso — passa pro dialog pra user editar (name nem deve duplicar)
            Dictionary<string, object> copy = new Dictionary<string, object>(entry);
            string oldName = ShopData.GetStr(copy, "name");
            if (!string.IsNullOrEmpty(oldName)) copy["name"] = oldName + "_copy";
            using (ShopPackOddsEditDialog dlg = new ShopPackOddsEditDialog(copy))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                _odds.Entries.Add(dlg.Result);
                _parent.MarkDirty();
                RefreshGrid();
            }
        }

        void OnDelete()
        {
            int? idx = GetSelectedIndex();
            if (idx == null) return;
            Dictionary<string, object> entry = _odds.Entries[idx.Value] as Dictionary<string, object>;
            string desc = entry != null ? ShopOddsData.DescribeEntry(entry) : "(invalid)";
            DialogResult dr = MessageBox.Show(
                "Apagar odds entry:\n" + desc + "?",
                "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            _odds.Entries.RemoveAt(idx.Value);
            _parent.MarkDirty();
            RefreshGrid();
        }
    }
}
