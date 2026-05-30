using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Dialogs
{
    // Editor de 1 entry de AccessoryShop. Accessories são cosméticos
    // (sleeves, protectors, boxes, icons, fields, etc) — sem cardList.
    // Form simples com os fields conhecidos do JSON.
    class ShopAccessoryEditDialog : Form
    {
        public Dictionary<string, object> Result { get; private set; }

        readonly bool _isEdit;
        readonly ShopData _shop;
        readonly string _dataDir;
        readonly Dictionary<string, object> _initial;

        TextBox _txtShopId, _txtItemId, _txtIconData;
        NumericUpDown _numCategory, _numSubCat, _numMax, _numNum,
                      _numLimitBuy, _numIconType, _numPrice;
        DateTimePicker _dtLimitDate;
        CheckBox _chkUseLimitDate;

        public ShopAccessoryEditDialog(ShopData shop, string dataDir,
                                        Dictionary<string, object> initial)
        {
            _shop = shop;
            _dataDir = dataDir;
            _isEdit = initial != null && initial.ContainsKey("shopId");
            _initial = initial ?? new Dictionary<string, object>();

            Text = _isEdit
                ? ("Edit Accessory " + ShopData.GetInt(_initial, "shopId"))
                : "Add Accessory";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(480, 420);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildUi();
            LoadInitial();
        }

        void BuildUi()
        {
            TableLayoutPanel t = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 3,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
                ColumnStyles = {
                    new ColumnStyle(SizeType.AutoSize),
                    new ColumnStyle(SizeType.AutoSize),
                    new ColumnStyle(SizeType.AutoSize),
                },
            };

            _txtShopId = new TextBox { Width = 120 };
            if (_isEdit) _txtShopId.ReadOnly = true;
            AddRow(t, "Shop ID", _txtShopId,
                _isEdit ? "" : "Convenção: 3XXXXXXX");

            _txtItemId = new TextBox { Width = 120 };
            AddRow(t, "Item ID", _txtItemId, "Item cosmético referenciado");

            _numCategory = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99 };
            AddRow(t, "Category", _numCategory,
                "3=protector, 4=deck case, 5=field, 6=field obj, etc");

            _numSubCat = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 1 };
            AddRow(t, "Sub category", _numSubCat);

            _txtIconData = new TextBox { Width = 280 };
            AddRow(t, "Icon data", _txtIconData, "Nome do asset (ex: thumb1000003_01)");

            _numIconType = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 2 };
            AddRow(t, "Icon type", _numIconType);

            _numMax = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 999, Value = 1 };
            AddRow(t, "Max", _numMax, "Máximo de cópias do item");

            _numNum = new NumericUpDown { Width = 80, Minimum = 1, Maximum = 999, Value = 1 };
            AddRow(t, "Num", _numNum, "Quantidade dada por compra");

            _numLimitBuy = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 1 };
            AddRow(t, "Limit buy", _numLimitBuy, "0 = unlimited");

            _numPrice = new NumericUpDown { Width = 120, Minimum = 0, Maximum = 999999 };
            AddRow(t, "Price", _numPrice);

            // Limit date opcional
            _chkUseLimitDate = new CheckBox { Text = "Tem data limite",
                AutoSize = true, Margin = new Padding(0, 6, 8, 4) };
            _dtLimitDate = new DateTimePicker { Width = 200, Enabled = false,
                Format = DateTimePickerFormat.Long };
            _chkUseLimitDate.CheckedChanged += (s, e) => _dtLimitDate.Enabled = _chkUseLimitDate.Checked;
            AddRowControls(t, "Limit date", new Control[] { _chkUseLimitDate, _dtLimitDate });

            // Bottom buttons
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
            };
            Button btnSave = new Button { Text = _isEdit ? "Save" : "Add",
                Width = 100, Height = 30,
                BackColor = SystemColors.Highlight, ForeColor = SystemColors.HighlightText,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold) };
            btnSave.Click += OnSave;
            Button btnCancel = new Button { Text = "Cancel",
                Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnCancel);
            AcceptButton = btnSave; CancelButton = btnCancel;

            Panel wrap = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            wrap.Controls.Add(t);
            Controls.Add(wrap);
            Controls.Add(buttons);
        }

        void AddRow(TableLayoutPanel t, string label, Control input, string hint = null)
        {
            int row = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true,
                Margin = new Padding(0, 8, 12, 4),
                Font = new Font(Font, FontStyle.Bold) }, 0, row);
            input.Margin = new Padding(0, 4, 8, 4);
            t.Controls.Add(input, 1, row);
            if (hint != null)
            {
                Label h = new Label { Text = hint, AutoSize = true,
                    ForeColor = Theme.FgMuted, Margin = new Padding(8, 8, 0, 4) };
                t.Controls.Add(h, 2, row);
            }
            t.RowCount = row + 1;
        }
        void AddRowControls(TableLayoutPanel t, string label, Control[] controls)
        {
            int row = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true,
                Margin = new Padding(0, 8, 12, 4),
                Font = new Font(Font, FontStyle.Bold) }, 0, row);
            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true, WrapContents = false,
                Margin = new Padding(0, 4, 0, 4),
            };
            foreach (Control c in controls) flow.Controls.Add(c);
            t.Controls.Add(flow, 1, row);
            t.SetColumnSpan(flow, 2);
            t.RowCount = row + 1;
        }

        void LoadInitial()
        {
            _txtShopId.Text   = ShopData.GetInt(_initial, "shopId").ToString(CultureInfo.InvariantCulture);
            _txtItemId.Text   = ShopData.GetInt(_initial, "itemId").ToString(CultureInfo.InvariantCulture);
            _txtIconData.Text = ShopData.GetStr(_initial, "iconData");

            int v;
            v = ShopData.GetInt(_initial, "category");        ClampSet(_numCategory, v);
            v = ShopData.GetInt(_initial, "subCategory", 1);  ClampSet(_numSubCat,   v);
            v = ShopData.GetInt(_initial, "max", 1);          ClampSet(_numMax,      v);
            v = ShopData.GetInt(_initial, "num", 1);          ClampSet(_numNum,      v);
            v = ShopData.GetInt(_initial, "limit_buy_count", 1); ClampSet(_numLimitBuy, v);
            v = ShopData.GetInt(_initial, "iconType", 2);     ClampSet(_numIconType, v);
            v = ShopData.GetInt(_initial, "price");           ClampSet(_numPrice,    v);

            long limitTs = ShopData.GetInt(_initial, "limitdate_ts");
            if (limitTs > 0)
            {
                _chkUseLimitDate.Checked = true;
                try { _dtLimitDate.Value = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(limitTs).ToLocalTime(); }
                catch { }
            }
        }

        static void ClampSet(NumericUpDown n, int v)
        {
            if (v < n.Minimum) v = (int)n.Minimum;
            if (v > n.Maximum) v = (int)n.Maximum;
            n.Value = v;
        }

        void OnSave(object sender, EventArgs e)
        {
            int shopId, itemId;
            if (!int.TryParse(_txtShopId.Text.Trim(), out shopId) || shopId <= 0)
            {
                MessageBox.Show("Shop ID inválido.", "Erro",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!int.TryParse(_txtItemId.Text.Trim(), out itemId) || itemId <= 0)
            {
                MessageBox.Show("Item ID inválido.", "Erro",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_isEdit && _shop.Accessories.ContainsKey(shopId.ToString(CultureInfo.InvariantCulture)))
            {
                MessageBox.Show("Já existe accessory com ID " + shopId + ".",
                    "Duplicado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Dictionary<string, object> r = new Dictionary<string, object>(_initial);
            r["shopId"]          = shopId;
            r["itemId"]          = itemId;
            r["productType"]     = 3;
            r["category"]        = (int)_numCategory.Value;
            r["subCategory"]     = (int)_numSubCat.Value;
            r["iconData"]        = _txtIconData.Text;
            r["iconType"]        = (int)_numIconType.Value;
            r["max"]             = (int)_numMax.Value;
            r["num"]             = (int)_numNum.Value;
            r["limit_buy_count"] = (int)_numLimitBuy.Value;
            r["price"]           = (int)_numPrice.Value;
            r["targetCategory"]  = (int)_numCategory.Value;
            r["targetId"]        = itemId;
            if (_chkUseLimitDate.Checked)
            {
                r["limitdate_ts"] = (long)(_dtLimitDate.Value.ToUniversalTime() -
                    new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            }
            else
            {
                r["limitdate_ts"] = 0;
            }

            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
