using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Dialogs
{
    // Editor de 1 entry de ShopPackOdds.json. Entries são de 2 tipos:
    //   - Named: tem "name" → referenciada por packs via oddsName
    //   - Default: tem "packTypes" + "gachaType" → aplicada
    //     automaticamente por type quando pack não tem oddsName
    //
    // UI: tabs internas
    //   Header   — radio "Named" vs "Default", + campos correspondentes
    //   Rates    — cardRateList grid editável (slot ranges + rates)
    //   Premiere — premiereRateList grid (opcional, geralmente vazio)
    class ShopPackOddsEditDialog : Form
    {
        public Dictionary<string, object> Result { get; private set; }

        readonly bool _isEdit;
        readonly Dictionary<string, object> _initial;

        // Header tab
        RadioButton _rbNamed, _rbDefault;
        TextBox _txtName;
        CheckBox _chkBooster, _chkSpecial, _chkBonus, _chkSelection;
        NumericUpDown _numGachaType;

        // Rates tab
        DataGridView _gridRates;
        // Premiere tab
        DataGridView _gridPremiere;

        public ShopPackOddsEditDialog(Dictionary<string, object> initial)
        {
            _isEdit = initial != null;
            _initial = initial ?? new Dictionary<string, object>();

            Text = _isEdit ? "Edit Odds" : "Add Odds";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 580);
            MinimumSize = new Size(700, 480);
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
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildHeaderTab());
            tabs.TabPages.Add(BuildRatesTab());
            tabs.TabPages.Add(BuildPremiereTab());

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
            AcceptButton = btnSave;
            CancelButton = btnCancel;

            Controls.Add(tabs);
            Controls.Add(buttons);
        }

        // ===== Header tab =====
        TabPage BuildHeaderTab()
        {
            TabPage p = new TabPage("Header") { Padding = new Padding(12) };
            TableLayoutPanel t = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
                Padding = new Padding(0),
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // Radio Named vs Default
            _rbNamed = new RadioButton { Text = "Named — referenciada por packs via oddsName",
                AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            _rbDefault = new RadioButton { Text = "Default por packType — aplicada automaticamente",
                AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            _rbNamed.CheckedChanged += (s, e) => UpdateHeaderUiState();
            _rbDefault.CheckedChanged += (s, e) => UpdateHeaderUiState();

            // Named fields
            _txtName = new TextBox { Width = 280 };
            // Default fields
            _chkBooster   = new CheckBox { Text = "Booster (1)",   AutoSize = true };
            _chkSpecial   = new CheckBox { Text = "Special (2)",   AutoSize = true };
            _chkBonus     = new CheckBox { Text = "Bonus (3)",     AutoSize = true };
            _chkSelection = new CheckBox { Text = "Selection (4)", AutoSize = true };
            _numGachaType = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 1 };

            // Composição manual sem TableLayout (mais legível pra controls heterogêneos)
            int row = 0;
            t.Controls.Add(_rbNamed, 0, row); t.SetColumnSpan(_rbNamed, 2); row++;
            t.Controls.Add(new Label { Text = "Name:", AutoSize = true,
                Margin = new Padding(20, 8, 8, 4), Font = new Font(Font, FontStyle.Bold) }, 0, row);
            t.Controls.Add(_txtName, 1, row); row++;

            t.Controls.Add(_rbDefault, 0, row); t.SetColumnSpan(_rbDefault, 2); row++;
            t.Controls.Add(new Label { Text = "PackTypes:", AutoSize = true,
                Margin = new Padding(20, 8, 8, 4), Font = new Font(Font, FontStyle.Bold) }, 0, row);
            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
            };
            flow.Controls.Add(_chkBooster);
            flow.Controls.Add(_chkSpecial);
            flow.Controls.Add(_chkBonus);
            flow.Controls.Add(_chkSelection);
            t.Controls.Add(flow, 1, row); row++;

            t.Controls.Add(new Label { Text = "GachaType:", AutoSize = true,
                Margin = new Padding(20, 8, 8, 4), Font = new Font(Font, FontStyle.Bold) }, 0, row);
            t.Controls.Add(_numGachaType, 1, row); row++;
            t.RowCount = row;

            p.Controls.Add(t);
            return p;
        }

        void UpdateHeaderUiState()
        {
            bool named = _rbNamed.Checked;
            _txtName.Enabled      = named;
            _chkBooster.Enabled   = !named;
            _chkSpecial.Enabled   = !named;
            _chkBonus.Enabled     = !named;
            _chkSelection.Enabled = !named;
            _numGachaType.Enabled = !named;
        }

        // ===== Rates tab =====
        TabPage BuildRatesTab()
        {
            TabPage p = new TabPage("Card Rates") { Padding = new Padding(8) };
            Label hint = new Label
            {
                Text = "Slots do pack (1=primeiro, 8=último). Cada slot rule define o range " +
                       "(start..end) e a % por rarity (R1=N, R2=R, R3=SR, R4=UR). " +
                       "Settle min/max só pro slot final (force-upgrade quando " +
                       "não há rarity mínima ainda).",
                AutoSize = false, Dock = DockStyle.Top, Height = 50,
                ForeColor = Theme.FgMuted,
            };
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2),
            };
            Button btnAdd = NewBtn("+ Add row",    (s, e) => AddRow(_gridRates));
            Button btnDel = NewBtn("Remove row",   (s, e) => RemoveSelectedRow(_gridRates));
            Button btnVal = NewBtn("Validar somas", (s, e) => ValidateRates(_gridRates));
            toolbar.Controls.Add(btnAdd);
            toolbar.Controls.Add(btnDel);
            toolbar.Controls.Add(btnVal);

            _gridRates = NewRatesGrid();
            p.Controls.Add(_gridRates);
            p.Controls.Add(toolbar);
            p.Controls.Add(hint);
            return p;
        }

        // ===== Premiere tab =====
        TabPage BuildPremiereTab()
        {
            TabPage p = new TabPage("Premiere") { Padding = new Padding(8) };
            Label hint = new Label
            {
                Text = "Premiere rates (opcional) — fallback usado quando o slot " +
                       "settle não encontra card. Geralmente vazio.",
                AutoSize = false, Dock = DockStyle.Top, Height = 38,
                ForeColor = Theme.FgMuted,
            };
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2),
            };
            toolbar.Controls.Add(NewBtn("+ Add row",  (s, e) => AddPremiereRow()));
            toolbar.Controls.Add(NewBtn("Remove row", (s, e) => RemoveSelectedRow(_gridPremiere)));

            // Premiere = { "rare": [4,3], "rate": { "3": "1.00", "2": "9.00", "1": "90.00" } }
            // Grid: rare_min | rare_max | R1% | R2% | R3% | R4%
            _gridPremiere = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            };
            _gridPremiere.Columns.Add(NewIntCol("rareMin", "Rare min", 60));
            _gridPremiere.Columns.Add(NewIntCol("rareMax", "Rare max", 60));
            _gridPremiere.Columns.Add(NewFloatCol("r1", "R1 %", 60));
            _gridPremiere.Columns.Add(NewFloatCol("r2", "R2 %", 60));
            _gridPremiere.Columns.Add(NewFloatCol("r3", "R3 %", 60));
            _gridPremiere.Columns.Add(NewFloatCol("r4", "R4 %", 60));
            p.Controls.Add(_gridPremiere);
            p.Controls.Add(toolbar);
            p.Controls.Add(hint);
            return p;
        }

        Button NewBtn(string text, EventHandler onClick)
        {
            Button b = new Button { Text = text, Width = 110, Height = 26,
                Margin = new Padding(2) };
            b.Click += onClick;
            return b;
        }

        DataGridView NewRatesGrid()
        {
            DataGridView g = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            };
            g.Columns.Add(NewIntCol("start",     "Start",    50));
            g.Columns.Add(NewIntCol("end",       "End",      50));
            g.Columns.Add(NewBoolCol("standard", "Std"));
            g.Columns.Add(NewIntCol("settleMin", "SettleMin", 70));
            g.Columns.Add(NewIntCol("settleMax", "SettleMax", 70));
            g.Columns.Add(NewFloatCol("r1", "R1 % (N)",  70));
            g.Columns.Add(NewFloatCol("r2", "R2 % (R)",  70));
            g.Columns.Add(NewFloatCol("r3", "R3 % (SR)", 70));
            g.Columns.Add(NewFloatCol("r4", "R4 % (UR)", 70));
            // Pinta a coluna que somar diferente de 100
            g.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (!IsRateCol(g.Columns[e.ColumnIndex].Name)) return;
                double sum = SumRow(g.Rows[e.RowIndex]);
                if (Math.Abs(sum - 100.0) > 0.5)
                    e.CellStyle.BackColor = Color.FromArgb(0xFC, 0xE4, 0xE4);
            };
            return g;
        }

        static bool IsRateCol(string n) => n == "r1" || n == "r2" || n == "r3" || n == "r4";
        static double SumRow(DataGridViewRow row)
        {
            double s = 0;
            foreach (string c in new[] { "r1", "r2", "r3", "r4" })
            {
                object v = row.Cells[c].Value;
                if (v == null) continue;
                double d;
                if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out d)) s += d;
            }
            return s;
        }

        DataGridViewColumn NewIntCol(string name, string header, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = width,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
            };
        }
        DataGridViewColumn NewFloatCol(string name, string header, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = width,
                DefaultCellStyle = {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "0.00",
                },
            };
        }
        DataGridViewColumn NewBoolCol(string name, string header)
        {
            return new DataGridViewCheckBoxColumn { Name = name, HeaderText = header, Width = 45 };
        }

        void AddRow(DataGridView g)
        {
            g.Rows.Add(1, 8, false, "", "", 0.0, 0.0, 0.0, 0.0);
        }
        void AddPremiereRow()
        {
            _gridPremiere.Rows.Add(1, 4, 0.0, 0.0, 0.0, 0.0);
        }
        void RemoveSelectedRow(DataGridView g)
        {
            if (g.CurrentRow != null && !g.CurrentRow.IsNewRow)
                g.Rows.Remove(g.CurrentRow);
        }
        void ValidateRates(DataGridView g)
        {
            List<string> issues = new List<string>();
            for (int i = 0; i < g.Rows.Count; i++)
            {
                double s = SumRow(g.Rows[i]);
                if (Math.Abs(s - 100.0) > 0.5)
                    issues.Add("Row " + (i + 1) + ": soma = " + s.ToString("0.00", CultureInfo.InvariantCulture));
            }
            if (issues.Count == 0)
                MessageBox.Show("✓ Todas as rows somam ~100%",
                    "Validação OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show("Issues:\n" + string.Join("\n", issues.ToArray()),
                    "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ===== Load =====
        void LoadInitial()
        {
            string name = ShopData.GetStr(_initial, "name");
            bool isNamed = !string.IsNullOrEmpty(name);
            _rbNamed.Checked = isNamed;
            _rbDefault.Checked = !isNamed;
            _txtName.Text = name;

            object pt;
            if (_initial.TryGetValue("packTypes", out pt) && pt is List<object>)
            {
                foreach (object o in (List<object>)pt)
                {
                    int t;
                    if (!int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out t)) continue;
                    if (t == 1) _chkBooster.Checked   = true;
                    if (t == 2) _chkSpecial.Checked   = true;
                    if (t == 3) _chkBonus.Checked     = true;
                    if (t == 4) _chkSelection.Checked = true;
                }
            }
            int gt = ShopData.GetInt(_initial, "gachaType", 1);
            if (gt >= _numGachaType.Minimum && gt <= _numGachaType.Maximum)
                _numGachaType.Value = gt;
            UpdateHeaderUiState();

            // cardRateList
            object crlObj;
            if (_initial.TryGetValue("cardRateList", out crlObj) && crlObj is List<object>)
            {
                foreach (object o in (List<object>)crlObj)
                {
                    Dictionary<string, object> row = o as Dictionary<string, object>;
                    if (row == null) continue;
                    int start = ShopData.GetInt(row, "start_num", 1);
                    int end   = ShopData.GetInt(row, "end_num",   1);
                    bool standard = ShopData.GetBool(row, "standard");
                    object sMin, sMax;
                    string sMinStr = "", sMaxStr = "";
                    if (row.TryGetValue("settle_rare_min", out sMin) && sMin != null)
                        sMinStr = Convert.ToString(sMin, CultureInfo.InvariantCulture);
                    if (row.TryGetValue("settle_rare_max", out sMax) && sMax != null)
                        sMaxStr = Convert.ToString(sMax, CultureInfo.InvariantCulture);
                    double r1 = GetRate(row, "1"), r2 = GetRate(row, "2"),
                           r3 = GetRate(row, "3"), r4 = GetRate(row, "4");
                    _gridRates.Rows.Add(start, end, standard, sMinStr, sMaxStr,
                        r1, r2, r3, r4);
                }
            }

            // premiereRateList
            object prlObj;
            if (_initial.TryGetValue("premiereRateList", out prlObj) && prlObj is List<object>)
            {
                foreach (object o in (List<object>)prlObj)
                {
                    Dictionary<string, object> row = o as Dictionary<string, object>;
                    if (row == null) continue;
                    int rMin = 1, rMax = 4;
                    object r;
                    if (row.TryGetValue("rare", out r) && r is List<object>)
                    {
                        List<object> rl = (List<object>)r;
                        if (rl.Count >= 1) int.TryParse(Convert.ToString(rl[0]), out rMin);
                        if (rl.Count >= 2) int.TryParse(Convert.ToString(rl[rl.Count - 1]), out rMax);
                        // Swap se ordem é descendente (formato comum [4,3])
                        if (rMin > rMax) { int tmp = rMin; rMin = rMax; rMax = tmp; }
                    }
                    Dictionary<string, object> rate = row.ContainsKey("rate")
                        ? row["rate"] as Dictionary<string, object> : null;
                    double rr1 = GetRateFlat(rate, "1"), rr2 = GetRateFlat(rate, "2"),
                           rr3 = GetRateFlat(rate, "3"), rr4 = GetRateFlat(rate, "4");
                    _gridPremiere.Rows.Add(rMin, rMax, rr1, rr2, rr3, rr4);
                }
            }
        }

        static double GetRate(Dictionary<string, object> row, string rarity)
        {
            object rateObj;
            if (!row.TryGetValue("rate", out rateObj)) return 0;
            Dictionary<string, object> rate = rateObj as Dictionary<string, object>;
            if (rate == null) return 0;
            return GetRateFlat(rate, rarity);
        }
        static double GetRateFlat(Dictionary<string, object> rate, string rarity)
        {
            if (rate == null) return 0;
            object v;
            if (!rate.TryGetValue(rarity, out v) || v == null) return 0;
            // Estrutura comum: { "rate": "55.00" } ou string direto
            Dictionary<string, object> dv = v as Dictionary<string, object>;
            if (dv != null)
            {
                object r;
                if (!dv.TryGetValue("rate", out r) || r == null) return 0;
                v = r;
            }
            double d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // ===== Save =====
        void OnSave(object sender, EventArgs e)
        {
            Dictionary<string, object> r = new Dictionary<string, object>(_initial);

            if (_rbNamed.Checked)
            {
                string name = _txtName.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show("Name obrigatório pra named entry.",
                        "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                r["name"] = name;
                r.Remove("packTypes");
                r.Remove("gachaType");
            }
            else
            {
                List<object> types = new List<object>();
                if (_chkBooster.Checked)   types.Add(1);
                if (_chkSpecial.Checked)   types.Add(2);
                if (_chkBonus.Checked)     types.Add(3);
                if (_chkSelection.Checked) types.Add(4);
                if (types.Count == 0)
                {
                    MessageBox.Show("Selecione ao menos 1 packType.",
                        "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                r["packTypes"] = types;
                r["gachaType"] = (int)_numGachaType.Value;
                r.Remove("name");
            }

            // cardRateList
            List<object> rates = new List<object>();
            foreach (DataGridViewRow row in _gridRates.Rows)
            {
                if (row.IsNewRow) continue;
                Dictionary<string, object> rule = new Dictionary<string, object>();
                rule["start_num"] = ToInt(row.Cells["start"].Value, 1);
                rule["end_num"]   = ToInt(row.Cells["end"].Value, 1);
                object stdV = row.Cells["standard"].Value;
                bool isStandard = stdV is bool && (bool)stdV;
                rule["standard"] = isStandard;
                string sMin = Convert.ToString(row.Cells["settleMin"].Value) ?? "";
                string sMax = Convert.ToString(row.Cells["settleMax"].Value) ?? "";
                int sMinI;
                if (int.TryParse(sMin.Trim(), out sMinI)) rule["settle_rare_min"] = sMinI;
                int sMaxI;
                if (int.TryParse(sMax.Trim(), out sMaxI)) rule["settle_rare_max"] = sMaxI;
                Dictionary<string, object> rateDict = new Dictionary<string, object>();
                foreach (string col in new[] { "r1", "r2", "r3", "r4" })
                {
                    double pct = ToFloat(row.Cells[col].Value, 0);
                    if (pct <= 0) continue;
                    string key = col.Substring(1);   // r1 → "1"
                    rateDict[key] = new Dictionary<string, object>
                    {
                        { "rate", pct.ToString("0.00", CultureInfo.InvariantCulture) },
                    };
                }
                rule["rate"] = rateDict;
                rates.Add(rule);
            }
            r["cardRateList"] = rates;

            // premiereRateList (omite se vazio)
            List<object> premiere = new List<object>();
            foreach (DataGridViewRow row in _gridPremiere.Rows)
            {
                if (row.IsNewRow) continue;
                int rMin = ToInt(row.Cells["rareMin"].Value, 1);
                int rMax = ToInt(row.Cells["rareMax"].Value, 4);
                // Format comum: [rMax, rMin] (descending). Preservamos
                // como originalmente: se min<max, list ascending; else descending
                List<object> rareList = new List<object>();
                if (rMin <= rMax) { rareList.Add(rMax); rareList.Add(rMin); }
                else              { rareList.Add(rMin); rareList.Add(rMax); }
                Dictionary<string, object> rateDict = new Dictionary<string, object>();
                foreach (string col in new[] { "r1", "r2", "r3", "r4" })
                {
                    double pct = ToFloat(row.Cells[col].Value, 0);
                    if (pct <= 0) continue;
                    rateDict[col.Substring(1)] = pct.ToString("0.00", CultureInfo.InvariantCulture);
                }
                Dictionary<string, object> entry = new Dictionary<string, object>
                {
                    { "rare", rareList },
                    { "rate", rateDict },
                };
                premiere.Add(entry);
            }
            if (premiere.Count > 0) r["premiereRateList"] = premiere;
            else r.Remove("premiereRateList");

            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }

        static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            int i;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : fallback;
        }
        static double ToFloat(object v, double fallback)
        {
            if (v == null) return fallback;
            double d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }
    }
}
