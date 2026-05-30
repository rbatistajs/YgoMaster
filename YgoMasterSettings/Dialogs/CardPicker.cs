using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using YgoMaster;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Dialogs
{
    // UserControl reutilizável pra escolher cards de uma pool (CardList).
    // Tem filtros (regulation + search por nome/CID) + grid com checkbox
    // implícita (multi-select native do DataGridView) + eventos pra que
    // o caller saiba o que o user adicionou.
    //
    // Uso típico: split-panel left side num editor de pack — usuário
    // filtra, seleciona N cards, click "Add selected" no caller, e o
    // CardPicker dispara `CardsAdded(IEnumerable<int>)`.
    class CardPicker : UserControl
    {
        // ---- Eventos públicos ----
        // Disparado quando user clica no botão "Add selected" (ou
        // double-click numa row). Caller decide o que fazer com os cids.
        public event Action<IEnumerable<int>> CardsAdded;

        // Provider de cids marcados (ex: já presentes no pack que tá
        // sendo editado). Quando setado, a coluna ✓ aparece e rows
        // com cid no set ganham cor de fundo destacada. Caller chama
        // RefreshMarkers() quando o set mudar.
        public Func<HashSet<int>> MarkedCidsProvider { get; set; }

        // ---- Dados ----
        readonly string _dataDir;
        Dictionary<int, CardInfo> _cards;
        List<RegulationFormat> _formats;

        // ---- UI ----
        ComboBox _cmbFormat;
        TextBox  _txtSearch;
        DataGridView _grid;
        Label _lblCount;

        // Filtros
        string _formatFilter = "all";
        string _searchText = "";

        public CardPicker(string dataDir)
        {
            _dataDir = dataDir;
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;
            LoadData();
            BuildUi();
            RefreshGrid();
        }

        void LoadData()
        {
            _cards   = CardNameLookup.LoadFull(_dataDir);
            _formats = FormatPools.LoadAll(_dataDir);
        }

        void BuildUi()
        {
            SuspendLayout();

            // Top: filtros (regulation + search) em TableLayoutPanel pra
            // não quebrar linha quando o picker fica estreito (FlowLayout
            // default tem WrapContents=true e some o que não cabe).
            // 4 colunas: Reg label | Reg combo (fill) | Search label | Search textbox (fill)
            TableLayoutPanel top = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 32,
                ColumnCount = 4, RowCount = 1,
                Padding = new Padding(2),
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  50f));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  50f));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Label lblReg = new Label { Text = "Reg:", AutoSize = true,
                Margin = new Padding(2, 8, 4, 0),
                Font = new Font(Font, FontStyle.Bold) };
            _cmbFormat = new ComboBox {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 4, 8, 0),
            };
            _cmbFormat.Items.Add(new ComboItem("All", "all"));
            foreach (RegulationFormat rf in _formats)
                _cmbFormat.Items.Add(new ComboItem(rf.DisplayName + " (" + rf.Cids.Count + ")", rf.Id));
            _cmbFormat.SelectedIndex = 0;
            _cmbFormat.SelectedIndexChanged += (s, e) => {
                _formatFilter = ((ComboItem)_cmbFormat.SelectedItem).Key;
                RefreshGrid();
            };

            Label lblSearch = new Label { Text = "Search:", AutoSize = true,
                Margin = new Padding(2, 8, 4, 0),
                Font = new Font(Font, FontStyle.Bold) };
            _txtSearch = new TextBox {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 2, 0),
            };
            _txtSearch.TextChanged += (s, e) => {
                _searchText = _txtSearch.Text;
                RefreshGrid();
            };

            top.Controls.Add(lblReg,      0, 0);
            top.Controls.Add(_cmbFormat,  1, 0);
            top.Controls.Add(lblSearch,   2, 0);
            top.Controls.Add(_txtSearch,  3, 0);

            // Counter
            _lblCount = new Label
            {
                AutoSize = false, Dock = DockStyle.Top, Height = 18,
                Padding = new Padding(4, 2, 0, 0),
                ForeColor = Theme.FgMuted,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Italic),
            };

            // Grid
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoGenerateColumns = false,
                ReadOnly = true,
                BackgroundColor = SystemColors.Window,
            };
            // 3 colunas: cid, name (Fill), type. Cards já presentes no
            // pack ganham fundo verde claro via OnCellFormatting — sem
            // coluna ✓ dedicada (era visualmente ruim com a faixa lateral).
            DataGridViewColumn cCid  = MakeCol("cid",  "CID",  60, DataGridViewContentAlignment.MiddleRight);
            DataGridViewColumn cName = MakeCol("name", "Name", 200, DataGridViewContentAlignment.MiddleLeft);
            DataGridViewColumn cType = MakeCol("type", "Type", 70, DataGridViewContentAlignment.MiddleCenter);
            cCid.AutoSizeMode  = DataGridViewAutoSizeColumnMode.None;
            cName.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            cType.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            cCid.MinimumWidth  = 60;
            cType.MinimumWidth = 70;
            _grid.Columns.Add(cCid);
            _grid.Columns.Add(cName);
            _grid.Columns.Add(cType);
            // Pinta rows com cid já no set marcado (fundo verde claro)
            _grid.CellFormatting += OnCellFormatting;
            // Double-click adiciona direto (atalho)
            _grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                AddSelected();
            };

            Controls.Add(_grid);
            Controls.Add(_lblCount);
            Controls.Add(top);
            ResumeLayout(performLayout: true);
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

        // Dispara o evento CardsAdded com os cids selecionados.
        public void AddSelected()
        {
            List<int> cids = GetSelectedCids();
            if (cids.Count > 0 && CardsAdded != null) CardsAdded(cids);
        }

        // Lista de cids atualmente selecionados — útil pra callers que
        // querem fazer ação custom (ex: set qty=3, decrement).
        public List<int> GetSelectedCids()
        {
            List<int> cids = new List<int>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (row.Tag is int cid) cids.Add(cid);
            return cids;
        }

        // Re-aplica coloração quando o set de cids marcados mudar
        // (caller chama após editar o pack contents).
        public void RefreshMarkers()
        {
            if (_grid == null) return;
            _grid.Invalidate();   // re-pinta rows pra refletir mudança
        }

        // Pinta fundo verde claro nas rows cujo cid está no
        // MarkedCidsProvider — indica visualmente quais já estão no pack.
        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (MarkedCidsProvider == null) return;
            if (e.RowIndex < 0) return;
            if (!(_grid.Rows[e.RowIndex].Tag is int cid)) return;
            HashSet<int> marked = MarkedCidsProvider();
            if (marked == null || !marked.Contains(cid)) return;
            e.CellStyle.BackColor = Color.FromArgb(0xE8, 0xF5, 0xE9);
        }

        // Re-renderiza grid aplicando filtros (regulation + search).
        // Limita a 500 rows pra performance — search/filter pra reduzir
        // antes (4700+ cards na pool full).
        void RefreshGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            HashSet<int> regPool = null;
            if (_formatFilter != "all")
            {
                foreach (RegulationFormat rf in _formats)
                    if (rf.Id == _formatFilter) { regPool = rf.Cids; break; }
            }
            string s = (_searchText ?? "").Trim();
            bool isNumSearch = s.Length > 0 && IsAllDigits(s);

            int shown = 0;
            const int MAX = 500;
            List<int> cids = new List<int>(_cards.Keys);
            cids.Sort();
            foreach (int cid in cids)
            {
                if (regPool != null && !regPool.Contains(cid)) continue;
                CardInfo info = _cards[cid];
                if (s.Length > 0)
                {
                    if (isNumSearch)
                    {
                        if (!cid.ToString().StartsWith(s)) continue;
                    }
                    else
                    {
                        if (info.Name == null ||
                            info.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                }
                string typeStr = FrameToShort(info.Frame);
                int idx = _grid.Rows.Add(cid, info.Name ?? "", typeStr);
                _grid.Rows[idx].Tag = cid;
                shown++;
                if (shown >= MAX) break;
            }
            _grid.ResumeLayout(performLayout: true);
            _lblCount.Text = shown >= MAX
                ? "Mostrando " + MAX + "+ — refine a busca"
                : ("Mostrando " + shown);
        }

        static string FrameToShort(CardFrame f)
        {
            if (f == CardFrame.Magic) return "Spell";
            if (f == CardFrame.Trap)  return "Trap";
            return "Monster";
        }
        static bool IsAllDigits(string s)
        {
            for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return s.Length > 0;
        }

        class ComboItem
        {
            public string Label, Key;
            public ComboItem(string label, string key) { Label = label; Key = key; }
            public override string ToString() { return Label; }
        }
    }
}
