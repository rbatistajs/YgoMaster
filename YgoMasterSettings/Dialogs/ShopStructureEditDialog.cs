using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Dialogs
{
    // Editor de 1 entry de StructureShop. Foco no shop-level (price /
    // limit_buy_count / acessórios / linkagem com deck file). Pra
    // editar o CONTEÚDO do deck (m/e/s cards), o user deve abrir o
    // arquivo DataLE/StructureDecks/<targetId>.json manualmente —
    // botão "Abrir deck file" facilita.
    class ShopStructureEditDialog : Form
    {
        public Dictionary<string, object> Result { get; private set; }

        readonly bool _isEdit;
        readonly ShopData _shop;
        readonly string _dataDir;
        readonly Dictionary<string, object> _initial;

        TextBox _txtShopId, _txtTargetId;
        NumericUpDown _numPrice, _numLimitBuy, _numCategory, _numSubCat;
        DateTimePicker _dtRelease;
        // Acessórios (item IDs de cosméticos vinculados)
        NumericUpDown _numBox, _numSleeve, _numField, _numObject, _numAvBase;
        Label _lblDeckInfo;
        Button _btnOpenDeck;

        public ShopStructureEditDialog(ShopData shop, string dataDir,
                                        Dictionary<string, object> initial)
        {
            _shop = shop;
            _dataDir = dataDir;
            _isEdit = initial != null && initial.ContainsKey("shopId");
            _initial = initial ?? new Dictionary<string, object>();

            Text = _isEdit
                ? ("Edit Structure shop " + ShopData.GetInt(_initial, "shopId"))
                : "Add Structure shop";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 580);
            MinimumSize = new Size(540, 480);
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
                _isEdit ? "" : "Inteiro único — convenção: 21XXXXXX");

            _txtTargetId = new TextBox { Width = 120 };
            _txtTargetId.TextChanged += (s, e) => UpdateDeckInfo();
            AddRow(t, "Target deck ID", _txtTargetId,
                "Aponta pra StructureDecks/<id>.json");

            _lblDeckInfo = new Label { AutoSize = false, Width = 420, Height = 36,
                ForeColor = Theme.FgMuted, Margin = new Padding(0, 4, 0, 4) };
            _btnOpenDeck = new Button { Text = "Abrir deck file…", Width = 140, Height = 26,
                Margin = new Padding(0, 4, 8, 4) };
            _btnOpenDeck.Click += (s, e) => OpenDeckFile();
            AddRowControls(t, "", new Control[] { _btnOpenDeck, _lblDeckInfo });

            _numPrice = new NumericUpDown { Width = 120, Minimum = 0, Maximum = 999999 };
            AddRow(t, "Price (gems)", _numPrice);

            _numLimitBuy = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 1 };
            AddRow(t, "Limit buy count", _numLimitBuy, "0 = unlimited");

            _numCategory = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 2 };
            AddRow(t, "Category", _numCategory);

            _numSubCat = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 1 };
            AddRow(t, "Sub category", _numSubCat);

            _dtRelease = new DateTimePicker { Width = 240, Format = DateTimePickerFormat.Long };
            AddRow(t, "Release date", _dtRelease);

            // Acessórios
            Label hdrAcc = new Label
            {
                Text = "Accessory item IDs (cosméticos inclusos no deck):",
                AutoSize = false, Width = 420, Height = 20,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, 16, 0, 4),
            };
            t.Controls.Add(hdrAcc, 0, t.RowCount);
            t.SetColumnSpan(hdrAcc, 3);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowCount++;

            _numBox     = NewItemId(); AddRow(t, "Box",     _numBox);
            _numSleeve  = NewItemId(); AddRow(t, "Sleeve",  _numSleeve);
            _numField   = NewItemId(); AddRow(t, "Field",   _numField);
            _numObject  = NewItemId(); AddRow(t, "Object",  _numObject);
            _numAvBase  = NewItemId(); AddRow(t, "Avatar base", _numAvBase);

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

        NumericUpDown NewItemId() => new NumericUpDown
        {
            Width = 120, Minimum = 0, Maximum = 99999999,
        };

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

        void UpdateDeckInfo()
        {
            int tid;
            if (!int.TryParse(_txtTargetId.Text.Trim(), out tid) || tid <= 0)
            {
                _lblDeckInfo.Text = "(deck ID inválido)";
                return;
            }
            string path = Path.Combine(_dataDir, "StructureDecks", tid + ".json");
            if (!File.Exists(path))
            {
                _lblDeckInfo.Text = "⚠ Deck file não existe: " + tid + ".json";
                _lblDeckInfo.ForeColor = Theme.FgDanger;
                return;
            }
            try
            {
                Dictionary<string, object> deck = MiniJSON.Json.Deserialize(
                    File.ReadAllText(path)) as Dictionary<string, object>;
                Dictionary<string, object> contents =
                    deck != null && deck.ContainsKey("contents")
                    ? deck["contents"] as Dictionary<string, object> : null;
                int m = 0, ex = 0, sp = 0;
                if (contents != null)
                {
                    m  = CountList(contents, "m");
                    ex = CountList(contents, "e");
                    sp = CountList(contents, "s");
                }
                _lblDeckInfo.Text = "✓ Deck OK — Main " + m + " · Extra " + ex + " · S/T " + sp;
                _lblDeckInfo.ForeColor = Color.FromArgb(0x27, 0xAE, 0x60);
            }
            catch (Exception ex)
            {
                _lblDeckInfo.Text = "⚠ Erro lendo deck: " + ex.Message;
                _lblDeckInfo.ForeColor = Theme.FgDanger;
            }
        }
        static int CountList(Dictionary<string, object> contents, string key)
        {
            object v;
            if (!contents.TryGetValue(key, out v)) return 0;
            Dictionary<string, object> d = v as Dictionary<string, object>;
            if (d == null) return 0;
            object ids;
            if (!d.TryGetValue("ids", out ids)) return 0;
            List<object> list = ids as List<object>;
            return list != null ? list.Count : 0;
        }

        void OpenDeckFile()
        {
            int tid;
            if (!int.TryParse(_txtTargetId.Text.Trim(), out tid) || tid <= 0)
            {
                MessageBox.Show("Target deck ID inválido.",
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string path = Path.Combine(_dataDir, "StructureDecks", tid + ".json");
            if (!File.Exists(path))
            {
                MessageBox.Show("Deck file não existe:\n" + path,
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { System.Diagnostics.Process.Start(path); }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao abrir:\n" + ex.Message,
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void LoadInitial()
        {
            _txtShopId.Text = ShopData.GetInt(_initial, "shopId").ToString(CultureInfo.InvariantCulture);
            _txtTargetId.Text = ShopData.GetInt(_initial, "targetId").ToString(CultureInfo.InvariantCulture);
            int v;
            v = ShopData.GetInt(_initial, "price");           if (v >= _numPrice.Minimum    && v <= _numPrice.Maximum)    _numPrice.Value    = v;
            v = ShopData.GetInt(_initial, "limit_buy_count"); if (v >= _numLimitBuy.Minimum && v <= _numLimitBuy.Maximum) _numLimitBuy.Value = v;
            v = ShopData.GetInt(_initial, "category", 2);     if (v >= _numCategory.Minimum && v <= _numCategory.Maximum) _numCategory.Value = v;
            v = ShopData.GetInt(_initial, "subCategory", 1);  if (v >= _numSubCat.Minimum   && v <= _numSubCat.Maximum)   _numSubCat.Value   = v;
            long ts = ShopData.GetInt(_initial, "releaseDate");
            if (ts > 0)
            {
                try { _dtRelease.Value = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime(); }
                catch { }
            }
            Dictionary<string, object> acc = _initial.ContainsKey("accessory")
                ? _initial["accessory"] as Dictionary<string, object> : null;
            if (acc != null)
            {
                _numBox.Value    = ClampToMax(_numBox,    ShopData.GetInt(acc, "box"));
                _numSleeve.Value = ClampToMax(_numSleeve, ShopData.GetInt(acc, "sleeve"));
                _numField.Value  = ClampToMax(_numField,  ShopData.GetInt(acc, "field"));
                _numObject.Value = ClampToMax(_numObject, ShopData.GetInt(acc, "object"));
                _numAvBase.Value = ClampToMax(_numAvBase, ShopData.GetInt(acc, "av_base"));
            }
            UpdateDeckInfo();
        }

        static decimal ClampToMax(NumericUpDown n, int v)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return v;
        }

        void OnSave(object sender, EventArgs e)
        {
            int shopId;
            if (!int.TryParse(_txtShopId.Text.Trim(), out shopId) || shopId <= 0)
            {
                MessageBox.Show("Shop ID inválido.", "Erro",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int targetId;
            if (!int.TryParse(_txtTargetId.Text.Trim(), out targetId) || targetId <= 0)
            {
                MessageBox.Show("Target deck ID inválido.", "Erro",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_isEdit && _shop.Structures.ContainsKey(shopId.ToString(CultureInfo.InvariantCulture)))
            {
                MessageBox.Show("Já existe structure shop com ID " + shopId + ".",
                    "Duplicado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Dictionary<string, object> r = new Dictionary<string, object>(_initial);
            r["shopId"] = shopId;
            r["productType"] = 2;
            r["targetId"] = targetId;
            r["category"] = (int)_numCategory.Value;
            r["subCategory"] = (int)_numSubCat.Value;
            r["price"] = (int)_numPrice.Value;
            r["limit_buy_count"] = (int)_numLimitBuy.Value;
            r["releaseDate"] = (long)(_dtRelease.Value.ToUniversalTime() -
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            r["accessory"] = new Dictionary<string, object>
            {
                { "box",     (int)_numBox.Value },
                { "sleeve",  (int)_numSleeve.Value },
                { "field",   (int)_numField.Value },
                { "object",  (int)_numObject.Value },
                { "av_base", (int)_numAvBase.Value },
            };
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
