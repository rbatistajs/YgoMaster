using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using YgoMaster;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Tabs
{
    // Edita DataLE/CardList.json (dict {cid: rarity 1..4}) — tab
    // primário pra editar o pool de cards instalado. UI nova com:
    //   - DataGridView com Type/Subtype/Attr/ATK/DEF coloridos + Rarity
    //     clicável (menu inline N/R/SR/UR por linha)
    //   - Button group colorido no rodapé pra bulk-edit dos selecionados
    //   - Filtros: regulation (dinâmico — todas em RegulationInfo.json) +
    //     rarity + search por nome/CID
    //
    // Nomes/atributos via CardNameLookup (parse standalone dos .bytes
    // decifrados — sem duel.dll, sem info/). Pools via FormatPools.
    class CardListTab : UserControl
    {
        // ---- Rarity constants ----
        const int RARITY_N  = 1;
        const int RARITY_R  = 2;
        const int RARITY_SR = 3;
        const int RARITY_UR = 4;

        static readonly Dictionary<int, string> RarityLabels = new Dictionary<int, string>
        {
            { RARITY_N,  "N"  }, { RARITY_R,  "R"  },
            { RARITY_SR, "SR" }, { RARITY_UR, "UR" },
        };

        // Cores das raridades — referência visual do client (N=cinza,
        // R=bronze, SR=prata, UR=ouro). Tons saturados pros botões
        // ativos; aplicamos opacity nos inativos via render custom.
        static readonly Dictionary<int, Color> RarityColors = new Dictionary<int, Color>
        {
            { RARITY_N,  Color.FromArgb(0xB0, 0xB0, 0xB0) },   // cinza claro
            { RARITY_R,  Color.FromArgb(0xCD, 0x7F, 0x32) },   // bronze
            { RARITY_SR, Color.FromArgb(0xC0, 0xC0, 0xC8) },   // prata
            { RARITY_UR, Color.FromArgb(0xFF, 0xCC, 0x33) },   // ouro
        };

        // ---- Data ----
        readonly string _dataDir;
        readonly string _cardListPath;
        readonly string _cardListDelPath;   // CardList.del.json — cards desativados
        readonly string _regulationPath;    // Regulation.json — banlist/limits
        Dictionary<int, int> _cardlist    = new Dictionary<int, int>();   // cid → rarity (ativos)
        Dictionary<int, int> _deactivated = new Dictionary<int, int>();   // cid → rarity (desativados, preservados)
        Dictionary<int, CardInfo> _cards = new Dictionary<int, CardInfo>();   // cid → metadados
        // CARD_Same.bytes — wrapper que load/save direto.
        // _sameResolvedCache é dict cid → canon resolved (1 lookup),
        // reconstruído a cada edit pra UI ficar sincronizada.
        CardSameLookup _sameLookup;
        Dictionary<int, int> _sameResolvedCache = new Dictionary<int, int>();
        bool _sameDirty;

        // Legend flag lives in CARD_Prop (PropB bit, via PropWriter). The "1 per
        // Type" rule runs on the client (Goat hook reading DLL_CardIsLegend).
        string _legendFilter = "any";   // any | yes | no
        // Todas as regulations descobertas em Regulation.json (Goat,
        // Rush, Normal, Fusion, Synchro, Xyz, Link, Unlimited, etc.).
        // Ordenadas pra UI estável (Goat/Rush primeiro).
        List<RegulationFormat> _formats = new List<RegulationFormat>();
        // Doc raw do Regulation.json — mutado in-memory quando user edita
        // limit inline. Salvo no Save junto com CardList.
        Dictionary<string, object> _regulationDoc;
        bool _regulationDirty;
        bool _dirty;
        // Toggle: mostrar desativados no grid (mesclados com ativos).
        bool _showDeactivated;

        // Mapeamentos pra display amigável dos enums numéricos do client.
        static readonly Dictionary<int, string> AttrNames = new Dictionary<int, string>
        {
            { 1, "EARTH" }, { 2, "WATER" }, { 3, "FIRE" },
            { 4, "WIND" }, { 5, "LIGHT" }, { 6, "DARK" }, { 7, "DIVINE" },
        };
        // Cores por tipo (fundo da coluna Type). Mirror das cores
        // canônicas do YGO: monstro=amarelo, effect=laranja, ritual=azul,
        // fusion=roxo, syncro=branco, xyz=preto, link=azul-petróleo,
        // pendulum=verde-amarelo, magic=verde, trap=rosa.
        static readonly Dictionary<CardFrame, Color> FrameColors = new Dictionary<CardFrame, Color>
        {
            { CardFrame.Normal,     Color.FromArgb(0xFD, 0xE6, 0x8A) },   // amarelo claro
            { CardFrame.Effect,     Color.FromArgb(0xFF, 0x8B, 0x53) },   // laranja
            { CardFrame.Ritual,     Color.FromArgb(0x9D, 0xB5, 0xCC) },   // azul
            { CardFrame.Fusion,     Color.FromArgb(0xA0, 0x86, 0xB7) },   // roxo
            { CardFrame.Sync,       Color.FromArgb(0xEF, 0xEF, 0xEF) },   // branco
            { CardFrame.Xyz,        Color.FromArgb(0x2E, 0x2E, 0x2E) },   // preto
            { CardFrame.Link,       Color.FromArgb(0x2F, 0x6B, 0x8F) },   // azul-petróleo
            { CardFrame.Pend,       Color.FromArgb(0xB4, 0xCC, 0x86) },   // pend verde
            { CardFrame.PendFx,     Color.FromArgb(0xB4, 0xCC, 0x86) },
            { CardFrame.Magic,      Color.FromArgb(0x29, 0xA0, 0x7B) },   // spell verde
            { CardFrame.Trap,       Color.FromArgb(0xBC, 0x5C, 0x74) },   // trap rosa
            { CardFrame.Token,      Color.FromArgb(0xB5, 0xB5, 0xB5) },
        };
        static readonly Dictionary<CardFrame, string> FrameShortNames = new Dictionary<CardFrame, string>
        {
            { CardFrame.Normal,     "Normal"   }, { CardFrame.Effect,     "Effect"   },
            { CardFrame.Ritual,     "Ritual"   }, { CardFrame.Fusion,     "Fusion"   },
            { CardFrame.Sync,       "Synchro"  }, { CardFrame.SyncPend,   "Sync/Pen" },
            { CardFrame.Xyz,        "Xyz"      }, { CardFrame.XyzPend,    "Xyz/Pen"  },
            { CardFrame.Link,       "Link"     }, { CardFrame.Pend,       "Pendulum" },
            { CardFrame.PendFx,     "Pend/Eff" }, { CardFrame.FusionPend, "Fus/Pen"  },
            { CardFrame.RitualPend, "Rit/Pen"  }, { CardFrame.Magic,      "Spell"    },
            { CardFrame.Trap,       "Trap"     }, { CardFrame.Token,      "Token"    },
            { CardFrame.Dsync,      "Dark Syn" },
        };

        // ---- Filters ----
        // Format filter: "all" = sem filtro; senão = id da regulation
        // (ex: "2005" pra Goat, "100001" pra Rush). Card só passa se
        // estiver no pool dessa regulation.
        string _formatFilter = "all";
        string _rarityFilter = "any";   // any | "1".."4"
        string _typeFilter   = "any";   // any | Monster | Spell | Trap
        string _kindFilter   = "any";   // any | CardKind int
        string _subtypeFilter = "any";  // any | CardFrame int
        string _iconFilter   = "any";   // any | CardIcon int
        string _searchText   = "";

        // ---- UI ----
        DataGridView _grid;
        ComboBox _cmbFormat, _cmbRarity, _cmbType, _cmbKind, _cmbSubtype, _cmbIcon;
        TextBox _txtSearch;
        Label _lblCounts, _lblDirty, _lblSelInfo;
        Button _btnSave;
        readonly Dictionary<int, RarityButton> _bulkButtons = new Dictionary<int, RarityButton>();

        // Details pane à direita — mostra info detalhada da row selecionada
        // (futuro: imagem do card também). Atualiza no SelectionChanged.
        Panel   _detailsPane;
        Label   _detLblMeta, _detLblStats;
        TextBox _detTxtName, _detTxtDesc;
        // Placeholder onde a imagem do card vai entrar no futuro
        // (estrutura preparada, sem fetch logic ainda).
        Panel   _detImagePlaceholder;
        // ProgressBar + label de fase mostrados durante o save async
        // (no lugar do label dirty, no save bar do bottom).
        ProgressBar _saveProgress;
        Label       _saveStatus;

        // Tracking de edits pendentes: cid → (Name, Desc). Só inclui
        // cards que tiveram edit real (compara com original ao salvar
        // e remove no-ops antes do rebuild).
        readonly Dictionary<int, CardTextWriter.Edit> _editedTexts =
            new Dictionary<int, CardTextWriter.Edit>();

        // Pending CARD_Prop edits (Kind/Icon overrides per cid). Applied on
        // Save via PropWriter, then reinstalled to the prop container.
        readonly Dictionary<int, PropWriter.Edit> _propEdits =
            new Dictionary<int, PropWriter.Edit>();
        // Suprime TextChanged handlers durante UpdateDetailsPane (quando
        // a UI tá só refletindo a row selecionada, não é edit do user).
        bool _suppressTextEdits;
        // CID do card atualmente carregado nos TextBoxes do details pane.
        // -1 quando nenhum card está sendo exibido.
        int _currentDetailsCid = -1;

        // Flag global: save async em execução. Bloqueia novos saves
        // simultâneos + indica que controles devem ficar disabled.
        bool _saving;

        public CardListTab()
        {
            _dataDir = Program.DataDir;
            _cardListPath    = Path.Combine(_dataDir, "CardList.json");
            _cardListDelPath = Path.Combine(_dataDir, "CardList.del.json");
            _regulationPath  = Path.Combine(_dataDir, "Regulation.json");
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;

            LoadAllData();
            BuildUi();
            RefreshGrid();
            UpdateBulkButtons();
            UpdateDetailsPane();
        }

        // ---- Data loading ----
        void LoadAllData()
        {
            // CardList.json: { "1234": 4, "5678": 3, ... } — cards ATIVOS
            // CardList.del.json: { "9999": 2, ... } — cards DESATIVADOS
            // (mesmo formato; preservam a rarity original pra reativação fácil).
            _cardlist.Clear();
            _deactivated.Clear();
            LoadCidRarityFile(_cardListPath,    _cardlist);
            LoadCidRarityFile(_cardListDelPath, _deactivated);

            _cards   = CardNameLookup.LoadFull(_dataDir);
            _formats = FormatPools.LoadAll(_dataDir);
            _sameLookup = new CardSameLookup(_dataDir);
            _sameResolvedCache = _sameLookup.ResolveAll();
            // Carrega Regulation.json raw (preserva campos desconhecidos
            // e require/etc no save). Mutated em OnLimitChanged.
            _regulationDoc = new Dictionary<string, object>();
            if (File.Exists(_regulationPath))
            {
                try
                {
                    Dictionary<string, object> parsed = MiniJSON.Json.Deserialize(
                        File.ReadAllText(_regulationPath)) as Dictionary<string, object>;
                    if (parsed != null) _regulationDoc = parsed;
                }
                catch { /* doc vazio = save vai sobrescrever com nada */ }
            }
        }

        // Parser comum pra arquivos { "cid": rarity } — usado pelo
        // CardList.json e CardList.del.json (mesmo schema).
        static void LoadCidRarityFile(string path, Dictionary<int, int> target)
        {
            if (!File.Exists(path)) return;
            try
            {
                Dictionary<string, object> raw = MiniJSON.Json.Deserialize(
                    File.ReadAllText(path)) as Dictionary<string, object>;
                if (raw == null) return;
                foreach (KeyValuePair<string, object> kv in raw)
                {
                    int cid;
                    if (!int.TryParse(kv.Key, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out cid)) continue;
                    try { target[cid] = Convert.ToInt32(kv.Value); }
                    catch { }
                }
            }
            catch { /* file corrupt — segue com dict vazio */ }
        }

        // ---- UI ----
        void BuildUi()
        {
            SuspendLayout();

            // Top: header com info
            Label header = new Label
            {
                Text = "Edita DataLE/CardList.json — pool de cards instalado " +
                       "com suas rarities. Click na coluna Rarity pra mudar 1 card; " +
                       "selecione vários e use os botões coloridos pra bulk. " +
                       "Backup automático em _bkp/ ao salvar.",
                AutoSize = false, Dock = DockStyle.Top, Height = 36,
                ForeColor = Theme.FgMuted, Padding = new Padding(4, 4, 4, 4),
            };

            // Toolbar: format/rarity/search filters. Wraps to next row when
            // window is too narrow to fit all combos in a single line.
            FlowLayoutPanel toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(4, 4, 4, 4),
            };
            // Combo populado dinamicamente de Regulation.json — "All" no
            // topo + uma entry por format encontrada (Goat e Rush primeiro,
            // resto alfabético). Cada entry mostra count de cards no pool.
            _cmbFormat = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbFormat.Items.Add(new FilterItem("All regulations", "all"));
            foreach (RegulationFormat rf in _formats)
            {
                string label = rf.DisplayName + " (" + rf.Cids.Count + ")";
                _cmbFormat.Items.Add(new FilterItem(label, rf.Id));
            }
            _cmbFormat.SelectedIndex = 0;
            _cmbFormat.SelectedIndexChanged += (s, e) => {
                _formatFilter = ((FilterItem)_cmbFormat.SelectedItem).Key;
                if (_grid.Columns.Contains("limit"))
                    _grid.Columns["limit"].Visible = _formatFilter != "all";
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Regulation:", _cmbFormat));

            _cmbRarity = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbRarity.Items.AddRange(new object[] {
                new FilterItem("Any", "any"),
                new FilterItem("N",   "1"),
                new FilterItem("R",   "2"),
                new FilterItem("SR",  "3"),
                new FilterItem("UR",  "4"),
            });
            _cmbRarity.SelectedIndex = 0;
            _cmbRarity.SelectedIndexChanged += (s, e) => {
                _rarityFilter = ((FilterItem)_cmbRarity.SelectedItem).Key;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Rarity:", _cmbRarity));

            _cmbType = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbType.Items.AddRange(new object[] {
                new FilterItem("Any",     "any"),
                new FilterItem("Monster", "Monster"),
                new FilterItem("Spell",   "Spell"),
                new FilterItem("Trap",    "Trap"),
            });
            _cmbType.SelectedIndex = 0;
            _cmbType.SelectedIndexChanged += (s, e) => {
                _typeFilter = ((FilterItem)_cmbType.SelectedItem).Key;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Type:", _cmbType));

            _cmbKind = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbKind.Items.Add(new FilterItem("Any", "any"));
            foreach (CardKind k in Enum.GetValues(typeof(CardKind)))
                _cmbKind.Items.Add(new FilterItem(k.ToString(), ((int)k).ToString()));
            _cmbKind.SelectedIndex = 0;
            _cmbKind.SelectedIndexChanged += (s, e) => {
                _kindFilter = ((FilterItem)_cmbKind.SelectedItem).Key;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Kind:", _cmbKind));

            _cmbSubtype = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbSubtype.Items.Add(new FilterItem("Any", "any"));
            foreach (KeyValuePair<CardFrame, string> kv in FrameShortNames)
                _cmbSubtype.Items.Add(new FilterItem(kv.Value, ((int)kv.Key).ToString()));
            _cmbSubtype.SelectedIndex = 0;
            _cmbSubtype.SelectedIndexChanged += (s, e) => {
                _subtypeFilter = ((FilterItem)_cmbSubtype.SelectedItem).Key;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Frame:", _cmbSubtype));

            _cmbIcon = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbIcon.Items.Add(new FilterItem("Any", "any"));
            foreach (CardIcon ic in Enum.GetValues(typeof(CardIcon)))
                _cmbIcon.Items.Add(new FilterItem(ic.ToString(), ((int)ic).ToString()));
            _cmbIcon.SelectedIndex = 0;
            _cmbIcon.SelectedIndexChanged += (s, e) => {
                _iconFilter = ((FilterItem)_cmbIcon.SelectedItem).Key;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Icon:", _cmbIcon));

            ComboBox cmbLegend = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLegend.Items.AddRange(new object[] {
                new FilterItem("Any", "any"),
                new FilterItem("Yes", "yes"),
                new FilterItem("No",  "no"),
            });
            cmbLegend.SelectedIndex = 0;
            cmbLegend.SelectedIndexChanged += (s, e) => {
                _legendFilter = ((FilterItem)cmbLegend.SelectedItem).Key;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Legend:", cmbLegend));

            _txtSearch = new TextBox { Width = 240 };
            _txtSearch.TextChanged += (s, e) => {
                _searchText = _txtSearch.Text;
                RefreshGrid();
            };
            toolbar.Controls.Add(MakeFilterCol("Search:", _txtSearch));
            // Toggle: mostrar cards desativados (CardList.del.json).
            // Quando off, só mostra ativos. Quando on, mescla ambos
            // (desativados aparecem com fundo cinza opaco).
            CheckBox chkShowDel = new CheckBox
            {
                Text = "Mostrar desativados",
                AutoSize = true,
                Margin = new Padding(16, 8, 0, 0),
                ForeColor = Theme.FgMuted,
            };
            chkShowDel.CheckedChanged += (s, e) =>
            {
                _showDeactivated = chkShowDel.Checked;
                RefreshGrid();
            };
            toolbar.Controls.Add(chkShowDel);

            // Contadores
            _lblCounts = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 20,
                ForeColor = Theme.FgAccent, Padding = new Padding(6, 2, 0, 0) };

            // Grid central
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
                // Grid não é read-only globalmente pra permitir edição
                // inline da coluna Rarity (combo). Outras colunas têm
                // ReadOnly=true via MakeCol.
                ReadOnly = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
            };
            _grid.Columns.Add(MakeCol("cid",    "CID",     70,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("name",   "Name",    300, DataGridViewContentAlignment.MiddleLeft));
            DataGridViewCheckBoxColumn colLegend = new DataGridViewCheckBoxColumn
            {
                Name = "legend", HeaderText = "Legend", Width = 70,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
                ReadOnly = false,
            };
            _grid.Columns.Add(colLegend);
            // SameCID: canon do card via CARD_Same.bytes — EDITÁVEL.
            // Digite um CID pra mudar o canonical (ou "—" pra apontar
            // pra si mesmo). Save grava no CARD_Same.bytes + reinstala
            // container 8e63fc3d no LocalData.
            DataGridViewTextBoxColumn colSame = (DataGridViewTextBoxColumn)
                MakeCol("sameCid","SameCID", 80, DataGridViewContentAlignment.MiddleRight);
            colSame.ReadOnly = false;
            _grid.Columns.Add(colSame);
            _grid.Columns.Add(MakeCol("type",   "Type",    70,  DataGridViewContentAlignment.MiddleCenter));
            DataGridViewComboBoxColumn colKind = new DataGridViewComboBoxColumn
            {
                Name = "kind", HeaderText = "Kind", Width = 130,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
                FlatStyle = FlatStyle.Flat,
                ValueMember = "Value", DisplayMember = "Label",
            };
            List<EnumItem> kindItems = new List<EnumItem>();
            foreach (CardKind k in Enum.GetValues(typeof(CardKind)))
                kindItems.Add(new EnumItem((int)k, k.ToString()));
            colKind.DataSource = kindItems;
            colKind.ReadOnly = false;
            _grid.Columns.Add(colKind);

            _grid.Columns.Add(MakeCol("frame",  "Frame",   90,  DataGridViewContentAlignment.MiddleCenter));

            DataGridViewComboBoxColumn colIcon = new DataGridViewComboBoxColumn
            {
                Name = "icon", HeaderText = "Icon", Width = 90,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
                FlatStyle = FlatStyle.Flat,
                ValueMember = "Value", DisplayMember = "Label",
            };
            List<EnumItem> iconItems = new List<EnumItem>();
            foreach (CardIcon ic in Enum.GetValues(typeof(CardIcon)))
                iconItems.Add(new EnumItem((int)ic, ic.ToString()));
            colIcon.DataSource = iconItems;
            colIcon.ReadOnly = false;
            _grid.Columns.Add(colIcon);
            _grid.Columns.Add(MakeCol("attr",   "Attr",    60,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("lvl",    "Lvl",     40,  DataGridViewContentAlignment.MiddleCenter));
            _grid.Columns.Add(MakeCol("atk",    "ATK",     60,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("def",    "DEF",     60,  DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeCol("fmt",    "Fmt",     50,  DataGridViewContentAlignment.MiddleCenter));
            // Coluna Limit — ComboBox inline editável. Visível só quando
            // _formatFilter != "all". Mudança atualiza o Regulation.json
            // do format atualmente selecionado (move o cid entre buckets
            // a0/a1/a2/a3). Cell value é int (0..3), display formatado
            // em OnCellFormatting.
            DataGridViewComboBoxColumn colLimit = new DataGridViewComboBoxColumn
            {
                Name = "limit", HeaderText = "Limit", Width = 110,
                DefaultCellStyle = {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                },
                FlatStyle = FlatStyle.Flat,
                ValueMember = "Value",
                DisplayMember = "Label",
            };
            colLimit.DataSource = new List<LimitItem>
            {
                new LimitItem(0, "0 (Forbidden)"),
                new LimitItem(1, "1 (Limited)"),
                new LimitItem(2, "2 (Semi)"),
                new LimitItem(3, "3"),
            };
            _grid.Columns.Add(colLimit);
            colLimit.ReadOnly = false;
            colLimit.Visible  = false;
            // Rarity como ComboBox inline (mesmo padrão do ShopPackEditDialog).
            // Cell guarda int (1..4) mas display mostra label "N"/"R"/
            // "SR"/"UR" via Value/DisplayMember. Cores de fundo por
            // valor são aplicadas em OnCellFormatting.
            DataGridViewComboBoxColumn colRarity = new DataGridViewComboBoxColumn
            {
                Name = "rarity", HeaderText = "Rarity", Width = 90,
                DefaultCellStyle = {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                },
                FlatStyle = FlatStyle.Flat,
                ValueMember = "Value",
                DisplayMember = "Label",
            };
            colRarity.DataSource = new List<RarityItem>
            {
                new RarityItem(RARITY_N,  "N"),
                new RarityItem(RARITY_R,  "R"),
                new RarityItem(RARITY_SR, "SR"),
                new RarityItem(RARITY_UR, "UR"),
            };
            _grid.Columns.Add(colRarity);
            // Tem cells comecam ReadOnly (porque o grid.ReadOnly=true);
            // libera só a coluna rarity pra edição inline.
            colRarity.ReadOnly = false;
            // Checkbox "Ativo" — toggle direto. Marcado = card ativo
            // (CardList.json), desmarcado = desativado (CardList.del.json).
            // Mais rápido que o menu antigo (Deactivate/Reactivate via ⋯).
            DataGridViewCheckBoxColumn colActive = new DataGridViewCheckBoxColumn
            {
                Name = "active", HeaderText = "Ativo", Width = 50,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
                ReadOnly = false,
            };
            _grid.Columns.Add(colActive);
            _grid.CellValueChanged += OnActiveCheckChanged;
            // Commit imediato em CheckBox e ComboBox (sem precisar perder foco)
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (!_grid.IsCurrentCellDirty) return;
                if (_grid.CurrentCell is DataGridViewCheckBoxCell
                    || _grid.CurrentCell is DataGridViewComboBoxCell)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            // Cell rendering custom: colorir a célula Rarity por valor
            _grid.CellFormatting += OnCellFormatting;
            // Persiste mudança da combo no _cardlist
            _grid.CellValueChanged += OnGridRarityChanged;
            _grid.DataError += (s, e) => { e.ThrowException = false; };
            _grid.SelectionChanged += (s, e) => {
                UpdateBulkButtons();
                UpdateDetailsPane();
            };
            // Sort numérico nas colunas que guardam ints como string.
            _grid.SortCompare += OnSortCompare;

            // Bottom: bulk edit bar com botões coloridos + save + progress
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 138 };

            // Linha 1: info de seleção (quantas rows + se há rarities mistas)
            _lblSelInfo = new Label
            {
                AutoSize = false, Dock = DockStyle.Top, Height = 22,
                Padding = new Padding(8, 4, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
            };

            // Linha 2: button group de raridade
            FlowLayoutPanel rarityBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8, 4, 8, 4),
            };
            foreach (int r in new[] { RARITY_N, RARITY_R, RARITY_SR, RARITY_UR })
            {
                RarityButton btn = new RarityButton(r, RarityLabels[r], RarityColors[r]);
                int captured = r;
                btn.Click += (s, e) => OnRarityButtonClick(captured);
                _bulkButtons[r] = btn;
                rarityBar.Controls.Add(btn);
            }

            // Linha 3: Save unificado — salva rarities + textos numa
            // chamada só. Async com ProgressBar pra não travar UI.
            Panel saveBar = new Panel
            {
                Dock = DockStyle.Top, Height = 44,
                Padding = new Padding(8, 4, 8, 4),
            };
            // Save button alinhado à direita
            _btnSave = new Button { Text = "Save changes",
                Width = 200, Height = 32,
                Dock = DockStyle.Right,
                BackColor = SystemColors.Highlight, ForeColor = SystemColors.HighlightText,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            _btnSave.Click += OnSaveClick;
            saveBar.Controls.Add(_btnSave);
            // Dirty label OU status do save (mesmo espaço — alterna)
            _lblDirty = new Label { AutoSize = false, Dock = DockStyle.Fill,
                ForeColor = Theme.FgDanger,
                Padding = new Padding(0, 8, 12, 0),
                Font = new Font(Font, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleLeft };
            saveBar.Controls.Add(_lblDirty);

            // Linha 4: progress bar oculta — fica visível apenas
            // durante o save async. Marquee porque o save tem 4 fases
            // sequenciais (ler/build/backup/gravar) e o user só precisa
            // saber que está rodando, não progresso preciso.
            Panel progressBar = new Panel { Dock = DockStyle.Top, Height = 26,
                Padding = new Padding(8, 2, 8, 2), Visible = false };
            _saveProgress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
            };
            _saveStatus = new Label
            {
                Text = "", Dock = DockStyle.Right, Width = 280,
                Padding = new Padding(8, 4, 0, 0),
                ForeColor = Theme.FgAccent,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            progressBar.Controls.Add(_saveProgress);
            progressBar.Controls.Add(_saveStatus);
            // Guardamos a ref via Tag pra mostrar/esconder com 1 toggle
            _saveProgress.Tag = progressBar;

            // Empilha bottom em ordem reversa (Dock=Top Z-stack).
            // ProgressBar fica na base, saveBar acima, etc.
            bottom.Controls.Add(progressBar);
            bottom.Controls.Add(saveBar);
            bottom.Controls.Add(rarityBar);
            bottom.Controls.Add(_lblSelInfo);

            // Middle area = grid (Dock=Fill) + details pane (Dock=Right).
            // Wrapping num Panel pra que details fique colado na borda
            // direita e grid ocupe o resto sem TableLayoutPanel complicar
            // o sizing das colunas autosize do DataGridView.
            Panel middle = new Panel { Dock = DockStyle.Fill };
            _detailsPane = BuildDetailsPane();
            middle.Controls.Add(_grid);
            middle.Controls.Add(_detailsPane);

            Controls.Add(middle);
            Controls.Add(bottom);
            Controls.Add(_lblCounts);
            Controls.Add(toolbar);
            Controls.Add(header);

            ResumeLayout(performLayout: true);
        }

        // Details pane: nome (editável) + meta line + stats + placeholder
        // imagem + descrição (editável, scroll) + save bar embaixo. Tudo
        // num Panel Dock=Right (380px). Edits do user vão pra
        // _editedTexts e ficam pendentes até o botão Save fazer rebuild
        // dos .bytes.
        Panel BuildDetailsPane()
        {
            Panel p = new Panel
            {
                Dock = DockStyle.Right, Width = 380,
                Padding = new Padding(8, 8, 8, 8),
                BackColor = SystemColors.Control,
                BorderStyle = BorderStyle.FixedSingle,
            };

            // ----- Name (editável) -----
            _detTxtName = new TextBox
            {
                Dock = DockStyle.Top, Multiline = false,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11f, FontStyle.Bold),
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _detTxtName.TextChanged += (s, e) => OnDetailTextChanged(isName: true);

            // ----- Meta + stats labels -----
            _detLblMeta = new Label
            {
                Text = "", AutoSize = false, Dock = DockStyle.Top, Height = 22,
                ForeColor = Theme.FgMuted,
                Padding = new Padding(0, 2, 0, 0),
            };
            _detLblStats = new Label
            {
                Text = "", AutoSize = false, Dock = DockStyle.Top, Height = 22,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Padding = new Padding(0, 0, 0, 4),
            };
            // Placeholder pra imagem do card (futuro).
            _detImagePlaceholder = new Panel
            {
                Dock = DockStyle.Top, Height = 180,
                BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A),
                Margin = new Padding(0, 4, 0, 4),
            };
            Label imgHint = new Label
            {
                Text = "(card image — futuro)",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(0x80, 0x80, 0x80),
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Italic),
            };
            _detImagePlaceholder.Controls.Add(imgHint);

            // ----- Descrição (editável, ocupa o resto) -----
            _detTxtDesc = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = true,
                AcceptsReturn = true,
            };
            _detTxtDesc.TextChanged += (s, e) => OnDetailTextChanged(isName: false);

            // Stack: Fill (desc) preenche o resto; Top stack acima dele.
            p.Controls.Add(_detTxtDesc);           // Fill: pega o resto
            p.Controls.Add(_detImagePlaceholder);  // Top
            p.Controls.Add(_detLblStats);          // Top (acima do image)
            p.Controls.Add(_detLblMeta);           // Top (acima do stats)
            p.Controls.Add(_detTxtName);           // Top (acima do meta — fica no topo)
            return p;
        }

        // Normalização de quebras de linha: os arquivos .bytes guardam
        // `\n` (Unix), mas TextBox do WinForms só renderiza `\r\n` como
        // nova linha. Convertemos `\n` → `\r\n` ao carregar pro TextBox
        // e voltamos a `\n` ao ler de volta — preserva o formato
        // original do arquivo sem quebrar a UX de edição.
        static string NormalizeForTextBox(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            // Tira \r soltos primeiro pra evitar duplicar (caso já venha
            // com \r\n por algum motivo)
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }
        static string DenormalizeFromTextBox(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        // Tracking de edits — chamado quando user digita em Name/Desc.
        // Suprime quando UpdateDetailsPane tá só refletindo a row
        // selecionada (não é edit real).
        void OnDetailTextChanged(bool isName)
        {
            if (_suppressTextEdits) return;
            if (_currentDetailsCid <= 0) return;

            CardInfo info;
            _cards.TryGetValue(_currentDetailsCid, out info);
            string origName = info != null ? (info.Name ?? "") : "";
            string origDesc = info != null ? (info.Desc ?? "") : "";
            // Denormalize: TextBox tem \r\n, original tem \n
            string curName = DenormalizeFromTextBox(_detTxtName.Text ?? "");
            string curDesc = DenormalizeFromTextBox(_detTxtDesc.Text ?? "");

            bool changed = curName != origName || curDesc != origDesc;
            if (changed)
            {
                CardTextWriter.Edit ed;
                if (!_editedTexts.TryGetValue(_currentDetailsCid, out ed))
                {
                    ed = new CardTextWriter.Edit();
                    _editedTexts[_currentDetailsCid] = ed;
                }
                ed.Name = curName != origName ? curName : null;
                ed.Desc = curDesc != origDesc ? curDesc : null;
            }
            else
            {
                // Voltou ao original → remove da lista de pendentes
                _editedTexts.Remove(_currentDetailsCid);
            }
            UpdateDirtyLabel();
        }

        // Atualiza o label de pendentes embaixo, refletindo tanto edits
        // de rarity quanto de texto. Botão Save habilitado se houver
        // qualquer dos dois.
        void UpdateDirtyLabel()
        {
            int textCount = _editedTexts.Count;
            int propCount = _propEdits.Count;
            bool hasRarity = _dirty;
            bool any = hasRarity || textCount > 0 || _sameDirty || propCount > 0;
            _btnSave.Enabled = any && !_saving;

            if (!any)
            {
                _lblDirty.Text = "";
                return;
            }
            List<string> parts = new List<string>();
            if (hasRarity) parts.Add("rarities");
            if (textCount > 0) parts.Add(textCount + " texto" + (textCount == 1 ? "" : "s"));
            if (_sameDirty) parts.Add("CARD_Same");
            if (propCount > 0) parts.Add(propCount + " prop" + (propCount == 1 ? "" : "s"));
            _lblDirty.Text = "* pendente: " + string.Join(" + ", parts);
        }

        // Build a label-over-input column for the filter toolbar so the
        // pair stays together when the FlowLayoutPanel wraps on narrow widths.
        FlowLayoutPanel MakeFilterCol(string labelText, Control input)
        {
            FlowLayoutPanel col = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(2, 2, 12, 2),
            };
            col.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(2, 0, 0, 2),
            });
            input.Margin = new Padding(2, 0, 2, 0);
            col.Controls.Add(input);
            return col;
        }

        DataGridViewColumn MakeCol(string name, string header, int width,
                                    DataGridViewContentAlignment align)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = width,
                // Sort habilitado por padrão. Pra colunas numéricas
                // (cid, lvl, atk, def) o SortCompare custom evita o
                // string-sort feio ("1000" antes de "2").
                SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = { Alignment = align },
                ReadOnly = true,
            };
        }

        // Compare numérico pra colunas que guardam ints como string.
        // Pra outras (name, type, attr, fmt, rarity) deixa o default
        // string-sort do DataGridView agir.
        void OnSortCompare(object sender, DataGridViewSortCompareEventArgs e)
        {
            string col = _grid.Columns[e.Column.Index].Name;
            if (col == "cid"   || col == "lvl" || col == "atk"
                || col == "def" || col == "limit" || col == "sameCid")
            {
                int a = ParseIntOrZero(e.CellValue1);
                int b = ParseIntOrZero(e.CellValue2);
                e.SortResult = a.CompareTo(b);
                e.Handled = true;
            }
        }

        static int ParseIntOrZero(object v)
        {
            string s = Convert.ToString(v) ?? "";
            int n;
            return int.TryParse(s, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // Atualiza o painel de detalhes com base na 1ª row selecionada.
        // Se múltiplas rows selecionadas, mostra info da primeira (ainda
        // permite o bulk via button group do bottom). Se nenhuma → reset.
        // _suppressTextEdits = true durante o set dos TextBoxes pra que
        // o handler TextChanged não interprete como edit do user.
        void UpdateDetailsPane()
        {
            if (_detailsPane == null) return;   // pode rodar antes do BuildUi terminar
            DataGridViewRow row = null;
            if (_grid.SelectedRows.Count > 0)
                row = _grid.SelectedRows[0];

            _suppressTextEdits = true;
            try
            {
                if (row == null || !(row.Tag is int cid))
                {
                    _currentDetailsCid = -1;
                    _detTxtName.Text  = "";
                    _detTxtName.Enabled = false;
                    _detTxtDesc.Text  = "";
                    _detTxtDesc.Enabled = false;
                    _detLblMeta.Text  = "(Selecione 1 card)";
                    _detLblStats.Text = "";
                    return;
                }

                _currentDetailsCid = cid;
                _detTxtName.Enabled = true;
                _detTxtDesc.Enabled = true;

                CardInfo info;
                _cards.TryGetValue(cid, out info);
                string origName = info != null ? (info.Name ?? "") : "";
                string origDesc = info != null ? (info.Desc ?? "") : "";

                // Se o card tem edits pendentes, mostra os edits (pra
                // user não perder o que digitou ao trocar de row e voltar).
                // Tudo normalizado pra \r\n pra TextBox renderizar quebras.
                CardTextWriter.Edit ed;
                if (_editedTexts.TryGetValue(cid, out ed))
                {
                    _detTxtName.Text = NormalizeForTextBox(ed.Name ?? origName);
                    _detTxtDesc.Text = NormalizeForTextBox(ed.Desc ?? origDesc);
                }
                else
                {
                    _detTxtName.Text = NormalizeForTextBox(origName);
                    _detTxtDesc.Text = NormalizeForTextBox(origDesc);
                }

                // Meta line: Type — Subtype — Attr  (Fmt)
                string meta = "CID " + cid;
                string stats = "";
                if (info != null)
                {
                    string fr;
                    FrameShortNames.TryGetValue(info.Frame, out fr);
                    if (info.Frame == CardFrame.Magic)
                    {
                        meta = "CID " + cid + " · Spell — " + IconName(info.Icon);
                    }
                    else if (info.Frame == CardFrame.Trap)
                    {
                        meta = "CID " + cid + " · Trap — " + IconName(info.Icon);
                    }
                    else
                    {
                        string at;
                        AttrNames.TryGetValue(info.Attr, out at);
                        meta = "CID " + cid + " · Monster — " + (fr ?? "") +
                               (string.IsNullOrEmpty(at) ? "" : " — " + at);
                        string lvlLabel = info.Frame == CardFrame.Link ? "LINK-" : "Lv ";
                        stats = lvlLabel + info.Level +
                                "   ATK " + info.Atk +
                                (info.Frame == CardFrame.Link ? "" : "   DEF " + info.Def);
                    }
                    string tag = FormatTagOf(cid);
                    if (tag != "—") meta += "   [" + tag + "]";
                    // Quando filtra por regulation: mostra o limit (ex:
                    // "1 cópia" / "Forbidden") junto da meta.
                    if (_formatFilter != "all")
                    {
                        RegulationFormat selFmt = FindFormat(_formatFilter);
                        if (selFmt != null)
                        {
                            int lim = selFmt.LimitOf(cid);
                            string limLabel = lim == 0 ? "Forbidden"
                                            : lim == 1 ? "Limited to 1"
                                            : lim == 2 ? "Semi-limited (2)"
                                                       : "3 cópias";
                            meta += "   · " + limLabel + " in " + selFmt.DisplayName;
                        }
                    }
                }
                _detLblMeta.Text  = meta;
                _detLblStats.Text = stats;
            }
            finally
            {
                _suppressTextEdits = false;
            }
        }

        // ---- Grid render: pinta as colunas Rarity e Type ----
        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string colName = _grid.Columns[e.ColumnIndex].Name;
            if (colName == "rarity")
            {
                // Cell value é int (1..4) via ValueMember. Pegamos da
                // row diretamente — robusto contra display value.
                int r;
                try { r = Convert.ToInt32(_grid.Rows[e.RowIndex].Cells["rarity"].Value); }
                catch { return; }
                Color c;
                if (!RarityColors.TryGetValue(r, out c)) return;
                e.CellStyle.BackColor = c;
                e.CellStyle.ForeColor = (r == RARITY_R) ? Color.White : Color.Black;
                e.CellStyle.Font = new Font(Font, FontStyle.Bold);
            }
            else if (colName == "frame")
            {
                // Pinta o Subtype com a cor canônica do frame (lookup via
                // CardInfo cached). Se for Spell/Trap icon, herda cor do
                // type (verde/rosa).
                int cid = ToInt(_grid.Rows[e.RowIndex].Tag);
                CardInfo info;
                if (_cards.TryGetValue(cid, out info))
                {
                    Color c;
                    if (FrameColors.TryGetValue(info.Frame, out c))
                    {
                        e.CellStyle.BackColor = c;
                        // Texto branco em fundos escuros (Xyz=preto, Link=petróleo)
                        e.CellStyle.ForeColor =
                            (info.Frame == CardFrame.Xyz || info.Frame == CardFrame.Link
                             || info.Frame == CardFrame.Magic || info.Frame == CardFrame.Trap)
                            ? Color.White : Color.Black;
                    }
                }
            }
            else if (colName == "type")
            {
                // Type column ganha cor do frame (Monster/Spell/Trap).
                int cid = ToInt(_grid.Rows[e.RowIndex].Tag);
                CardInfo info;
                if (_cards.TryGetValue(cid, out info))
                {
                    Color c;
                    if (FrameColors.TryGetValue(info.Frame, out c))
                    {
                        e.CellStyle.BackColor = c;
                        e.CellStyle.ForeColor =
                            (info.Frame == CardFrame.Xyz || info.Frame == CardFrame.Link
                             || info.Frame == CardFrame.Magic || info.Frame == CardFrame.Trap)
                            ? Color.White : Color.Black;
                    }
                }
            }
            else if (colName == "limit")
            {
                // Cores por limit count. Combo ValueMember guarda int (0-3);
                // pegamos da cell direto pra evitar parsear o display label.
                object cell = _grid.Rows[e.RowIndex].Cells["limit"].Value;
                if (cell == null || cell == DBNull.Value) return;
                int lim;
                try { lim = Convert.ToInt32(cell); } catch { return; }
                switch (lim)
                {
                    case 0:
                        e.CellStyle.BackColor = Color.FromArgb(0xC0, 0x39, 0x2B);   // vermelho
                        e.CellStyle.ForeColor = Color.White;
                        e.CellStyle.Font = new Font(Font, FontStyle.Bold);
                        break;
                    case 1:
                        e.CellStyle.BackColor = Color.FromArgb(0xE6, 0x7E, 0x22);   // laranja
                        e.CellStyle.ForeColor = Color.White;
                        e.CellStyle.Font = new Font(Font, FontStyle.Bold);
                        break;
                    case 2:
                        e.CellStyle.BackColor = Color.FromArgb(0xF1, 0xC4, 0x0F);   // amarelo
                        e.CellStyle.ForeColor = Color.Black;
                        e.CellStyle.Font = new Font(Font, FontStyle.Bold);
                        break;
                    default:   // 3 = unlimited; sem destaque
                        e.CellStyle.ForeColor = Theme.FgMuted;
                        break;
                }
            }
        }

        static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o); } catch { return 0; }
        }

        // Aplica rarity em 1 card e atualiza a row visualmente. Usado
        // pelo button group de bulk e pela combo inline.
        void ApplyRarityToSingle(int cid, int rarity)
        {
            _cardlist[cid] = rarity;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is int rowCid && rowCid == cid)
                {
                    row.Cells["rarity"].Value = rarity;   // int → combo display
                    break;
                }
            }
            MarkDirty();
            UpdateBulkButtons();
        }

        // ---- Filtros ----
        bool PassesFilter(int cid, int rarity)
        {
            if (_formatFilter != "all")
            {
                RegulationFormat rf = FindFormat(_formatFilter);
                if (rf == null || !rf.Cids.Contains(cid)) return false;
            }
            if (_rarityFilter != "any")
            {
                int rf;
                if (int.TryParse(_rarityFilter, out rf) && rarity != rf) return false;
            }
            if (_legendFilter != "any")
            {
                CardInfo li;
                bool isL = _cards.TryGetValue(cid, out li) && li.IsLegend;
                if (_legendFilter == "yes" && !isL) return false;
                if (_legendFilter == "no"  &&  isL) return false;
            }
            if (_typeFilter != "any" || _kindFilter != "any"
                || _subtypeFilter != "any" || _iconFilter != "any")
            {
                CardInfo info;
                if (!_cards.TryGetValue(cid, out info) || info == null) return false;
                if (_typeFilter != "any")
                {
                    string t = info.Frame == CardFrame.Magic ? "Spell"
                             : info.Frame == CardFrame.Trap  ? "Trap"
                             : "Monster";
                    if (t != _typeFilter) return false;
                }
                if (_kindFilter != "any")
                {
                    int kn;
                    if (int.TryParse(_kindFilter, out kn) && (int)info.Kind != kn) return false;
                }
                if (_subtypeFilter != "any")
                {
                    int frm;
                    if (int.TryParse(_subtypeFilter, out frm) && (int)info.Frame != frm) return false;
                }
                if (_iconFilter != "any")
                {
                    int ic;
                    if (int.TryParse(_iconFilter, out ic) && (int)info.Icon != ic) return false;
                }
            }
            string s = (_searchText ?? "").Trim();
            if (s.Length > 0)
            {
                if (IsAllDigits(s))
                {
                    if (!cid.ToString().StartsWith(s)) return false;
                }
                else
                {
                    CardInfo info;
                    if (!_cards.TryGetValue(cid, out info) || string.IsNullOrEmpty(info.Name))
                        return false;
                    if (info.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
            }
            return true;
        }

        static bool IsAllDigits(string s)
        {
            for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return s.Length > 0;
        }

        // Tag compacto pra coluna Fmt: G (Goat), R (Rush), G/R, ou — se
        // não tá em nenhum dos dois. Atalho útil pq Goat/Rush são os
        // formats mais relevantes nesse fork — outros formats não
        // entram na tag (vão estar implícitos no filtro Regulation se
        // o user quiser ver).
        string FormatTagOf(int cid)
        {
            HashSet<int> goat = FindCids(FormatPools.GoatFormatId);
            HashSet<int> rush = FindCids(FormatPools.RushFormatId);
            bool g = goat != null && goat.Contains(cid);
            bool r = rush != null && rush.Contains(cid);
            if (g && r) return "G/R";
            if (g) return "G";
            if (r) return "R";
            return "—";
        }

        RegulationFormat FindFormat(string id)
        {
            foreach (RegulationFormat rf in _formats)
                if (rf.Id == id) return rf;
            return null;
        }
        HashSet<int> FindCids(string id)
        {
            RegulationFormat rf = FindFormat(id);
            return rf != null ? rf.Cids : null;
        }

        // ---- Refresh grid ----
        void RefreshGrid()
        {
            if (_grid == null) return;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            int shown = 0;
            // Sort por CID ascending. Cards desativados só entram se o
            // toggle estiver ligado — vão pro fim da lista (renderizados
            // depois dos ativos pra ficar visualmente separado).
            List<int> cids = new List<int>(_cardlist.Keys);
            cids.Sort();
            if (_showDeactivated)
            {
                List<int> delCids = new List<int>(_deactivated.Keys);
                delCids.Sort();
                cids.AddRange(delCids);
            }
            foreach (int cid in cids)
            {
                bool isDeact = _deactivated.ContainsKey(cid);
                int rarity = isDeact ? _deactivated[cid] : _cardlist[cid];
                if (!PassesFilter(cid, rarity)) continue;
                CardInfo info;
                _cards.TryGetValue(cid, out info);
                // Coluna rarity é ComboBox com ValueMember=Value (int).
                // Clampa range fora de 1..4 pra Normal (default) — evita
                // DataError se CardList.json tiver valor inesperado.
                int rarityVal = (rarity >= 1 && rarity <= 4) ? rarity : 1;

                // Colunas: cid, name, type (cor), frame/icon, attr, lvl,
                // atk, def, fmt, rarity. Pra spell/trap: lvl/atk/def vazios.
                string name = info != null ? info.Name : "";
                string typeStr, frameStr, attrStr, lvlStr, atkStr, defStr;
                if (info == null)
                {
                    typeStr = frameStr = attrStr = lvlStr = atkStr = defStr = "";
                }
                else if (info.Frame == CardFrame.Magic)
                {
                    typeStr = "Spell"; frameStr = IconName(info.Icon);
                    attrStr = lvlStr = atkStr = defStr = "";
                }
                else if (info.Frame == CardFrame.Trap)
                {
                    typeStr = "Trap"; frameStr = IconName(info.Icon);
                    attrStr = lvlStr = atkStr = defStr = "";
                }
                else
                {
                    typeStr = "Monster";
                    string fr;
                    frameStr = FrameShortNames.TryGetValue(info.Frame, out fr) ? fr : "";
                    string at;
                    attrStr = AttrNames.TryGetValue(info.Attr, out at) ? at : "";
                    // Link monsters não têm level "normal"; o servidor
                    // codifica link rating no mesmo bitfield mas é OK
                    // mostrar o número aqui.
                    lvlStr = info.Level > 0 ? info.Level.ToString() : "";
                    atkStr = info.Atk.ToString();
                    defStr = info.Frame == CardFrame.Link ? "—" : info.Def.ToString();
                }

                // Limit do format selecionado (vazio quando filtro = all)
                // Limit é int (0..3) quando filtra por regulation; null
                // quando "all" (coluna invisível mesmo).
                object limitVal = DBNull.Value;
                if (_formatFilter != "all")
                {
                    RegulationFormat selFmt = FindFormat(_formatFilter);
                    if (selFmt != null) limitVal = selFmt.LimitOf(cid);
                }
                // SameCID: "—" se solo/self-canon, senão mostra o canon
                string sameCidStr = "—";
                if (_sameResolvedCache.TryGetValue(cid, out int canon) && canon != cid)
                    sameCidStr = canon.ToString();
                object kindVal = info != null ? (object)(int)info.Kind : DBNull.Value;
                object iconVal = info != null ? (object)(int)info.Icon : DBNull.Value;
                bool isLegend = info != null && info.IsLegend;
                int idx = _grid.Rows.Add(cid, name, isLegend, sameCidStr, typeStr, kindVal, frameStr, iconVal,
                                          attrStr, lvlStr, atkStr, defStr,
                                          FormatTagOf(cid), limitVal, rarityVal, !isDeact);
                DataGridViewRow row = _grid.Rows[idx];
                row.Tag = cid;
                if (isDeact)
                {
                    // Visual pra desativados: fundo cinza claro + texto
                    // opaco. Sinaliza estado sem esconder dados.
                    row.DefaultCellStyle.BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(0x80, 0x80, 0x80);
                    row.DefaultCellStyle.Font = new Font(Font, FontStyle.Italic);
                }
                shown++;
            }
            _grid.ResumeLayout(performLayout: true);
            _lblCounts.Text = "Mostrando " + shown + " / " + _cardlist.Count + " cards";
            UpdateBulkButtons();
        }

        static string IconName(CardIcon icon)
        {
            switch (icon)
            {
                case CardIcon.Continuous: return "Cont.";
                case CardIcon.Equip:      return "Equip";
                case CardIcon.Field:      return "Field";
                case CardIcon.QuickPlay:  return "Quick";
                case CardIcon.Ritual:     return "Ritual";
                case CardIcon.Ritual_R:   return "Ritual";
                case CardIcon.Counter:    return "Counter";
                default:                  return "Normal";
            }
        }

        // ---- Bulk edit ----
        // Aplica rarity nas rows selecionadas. Pra mudar 1 row só, use o
        // click direto na célula Rarity (menu inline). Bulk "all visible"
        // foi removido por ser destrutivo demais — pra atingir muitas
        // cards de uma vez, usa Ctrl+A após filtrar.
        void OnRarityButtonClick(int rarity)
        {
            List<int> targets = GetActionTargets();
            if (targets.Count == 0)
            {
                MessageBox.Show("Selecione 1 ou mais cards na tabela primeiro.",
                    "Sem alvo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (int cid in targets)
            {
                _cardlist[cid] = rarity;
                // Atualiza row in-place se ainda visível (faster que full refresh)
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Tag is int rowCid && rowCid == cid)
                    {
                        row.Cells["rarity"].Value = rarity;   // int → combo display
                        break;
                    }
                }
            }
            MarkDirty();
            UpdateBulkButtons();
        }

        List<int> GetActionTargets()
        {
            List<int> result = new List<int>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (row.Tag is int cid) result.Add(cid);
            return result;
        }

        // Re-renderiza os botões de raridade com base no contexto:
        //   - 0 cards alvo → tudo opaco/disabled
        //   - 1+ cards alvo com mesma rarity → essa fica saturada, outras opacas
        //   - 1+ cards alvo com rarities mistas → todas semi-opacas (estado "mixed")
        void UpdateBulkButtons()
        {
            List<int> targets = GetActionTargets();
            HashSet<int> rarities = new HashSet<int>();
            foreach (int cid in targets)
            {
                int r;
                if (_cardlist.TryGetValue(cid, out r)) rarities.Add(r);
            }
            bool any = targets.Count > 0;
            int activeR = (rarities.Count == 1) ? GetFirst(rarities) : 0;
            foreach (KeyValuePair<int, RarityButton> kv in _bulkButtons)
            {
                kv.Value.SetState(
                    enabled: any,
                    active:  any && kv.Key == activeR);
            }
            // Label de info de seleção
            int selCount = _grid.SelectedRows.Count;
            _lblSelInfo.Text = "Selecionados: " + selCount +
                (selCount > 0 && rarities.Count > 1 ? "  (rarities mistas)" : "");
        }

        static int GetFirst(HashSet<int> set)
        {
            foreach (int v in set) return v;
            return 0;
        }

        // ---- Save ----
        void MarkDirty()
        {
            _dirty = true;
            UpdateDirtyLabel();
        }

        // Save unificado async: rarities (CardList.json — rápido) +
        // textos (CARD_*.bytes — pode demorar 1-2s). Roda em Task.Run,
        // mostra ProgressBar Marquee durante a execução, atualiza UI no
        // thread principal via Invoke pra evitar cross-thread.
        void OnSaveClick(object sender, EventArgs e)
        {
            if (_saving) return;
            bool hasRarity = _dirty;
            int textCount = _editedTexts.Count;
            int propCount = _propEdits.Count;
            if (!hasRarity && textCount == 0 && !_sameDirty && propCount == 0)
            {
                MessageBox.Show("Nada pra salvar.", "OK", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            // Confirmação extra quando vai mexer em .bytes (operação
            // mais séria que CardList.json)
            if (textCount > 0)
            {
                DialogResult dr = MessageBox.Show(
                    "Vai salvar " + textCount + " texto(s) de card no .bytes" +
                    (hasRarity ? " + rarities no CardList.json." : ".") + "\n\n" +
                    "Backup automático em _bkp/. Continuar?",
                    "Save changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }

            // Snapshot dos dados sob lock (cópia rasa — dict refs são
            // imutáveis enquanto Task roda)
            Dictionary<int, int> rarityClone =
                hasRarity ? new Dictionary<int, int>(_cardlist) : null;
            Dictionary<int, int> deactivatedClone =
                hasRarity ? new Dictionary<int, int>(_deactivated) : null;
            // RegulationDoc: passa por referência (compartilhada com main
            // thread) só se foi editado. JsonFileWriter.SaveAtomic é o
            // único writer; user não pode editar enquanto save tá rodando
            // pois _saving guarda contra reentrance no MarkDirty.
            Dictionary<string, object> regulationDocClone =
                _regulationDirty ? _regulationDoc : null;
            Dictionary<int, CardTextWriter.Edit> textClone =
                textCount > 0 ? new Dictionary<int, CardTextWriter.Edit>(_editedTexts) : null;
            Dictionary<int, PropWriter.Edit> propEditsClone =
                _propEdits.Count > 0 ? new Dictionary<int, PropWriter.Edit>(_propEdits) : null;

            BeginSaveUi();

            bool sameDirtySnapshot = _sameDirty;
            System.Threading.Tasks.Task.Run(() =>
            {
                SaveResult res = new SaveResult();
                try
                {
                    if (rarityClone != null)
                    {
                        ReportProgress(regulationDocClone != null
                            ? "Salvando CardList + .del + Regulation…"
                            : "Salvando CardList.json + .del.json…");
                        res.RarityBkp = SaveCardListSync(
                            rarityClone, deactivatedClone, regulationDocClone);
                        res.RaritySaved = true;
                    }
                    if (textClone != null)
                    {
                        res.SlotsRewritten = CardTextWriter.Save(_dataDir, textClone, ReportProgress);
                    }
                    if (sameDirtySnapshot)
                    {
                        ReportProgress("Salvando CARD_Same.bytes…");
                        _sameLookup.Save();
                        res.SameSaved = true;
                    }
                    if (propEditsClone != null)
                    {
                        res.PropSlots = PropWriter.Save(_dataDir, propEditsClone, ReportProgress);
                    }
                    if (res.SlotsRewritten > 0 || res.SameSaved || res.PropSlots > 0)
                    {
                        ReportProgress("Instalando nos containers do jogo…");
                        res.InstallResult = ContainerInstaller.Install(_dataDir, null);
                    }
                }
                catch (Exception ex) { res.Error = ex; }
                return res;
            }).ContinueWith(OnSaveCompleted,
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        class SaveResult
        {
            public bool RaritySaved;
            public string RarityBkp;
            public int SlotsRewritten;
            public bool SameSaved;
            public int PropSlots;
            public ContainerInstaller.Result InstallResult;
            public Exception Error;
        }

        // Grava CardList.json (ativos) + CardList.del.json (desativados)
        // + Regulation.json (se editado). Cada um com backup + atomic
        // via JsonFileWriter. Retorna path do backup do CardList.json.
        string SaveCardListSync(Dictionary<int, int> rarities,
                                 Dictionary<int, int> deactivated,
                                 Dictionary<string, object> regulationDoc)
        {
            string bkpPath = WriteCidRarityFile(_cardListPath, rarities, "CardList");
            // Sempre grava o .del.json (mesmo se vazio — facilita rollback)
            WriteCidRarityFile(_cardListDelPath, deactivated, "CardList.del");
            // Regulation.json só salva se foi editado (evita backup
            // desnecessário quando user só mudou rarity/active).
            if (regulationDoc != null)
            {
                JsonFileWriter.SaveAtomic(_regulationPath,
                    MiniJSON.Json.Serialize(regulationDoc), "Regulation");
            }
            return bkpPath;
        }

        static string WriteCidRarityFile(string path, Dictionary<int, int> data, string bkpPrefix)
        {
            List<int> sortedCids = new List<int>(data.Keys);
            sortedCids.Sort();
            Dictionary<string, object> sortedOut = new Dictionary<string, object>(sortedCids.Count);
            foreach (int cid in sortedCids)
                sortedOut[cid.ToString(CultureInfo.InvariantCulture)] = data[cid];
            return JsonFileWriter.SaveAtomic(path, MiniJSON.Json.Serialize(sortedOut), bkpPrefix);
        }

        // Callback de progress do CardTextWriter — chamado do worker
        // thread; marshal pra UI thread via Invoke.
        void ReportProgress(string phase)
        {
            if (_saveStatus.InvokeRequired)
            {
                _saveStatus.BeginInvoke(new Action<string>(ReportProgress), phase);
                return;
            }
            _saveStatus.Text = phase;
        }

        // Liga ProgressBar + status label, disable botão Save e edit
        // controls (evita race conditions com novas edits durante save).
        void BeginSaveUi()
        {
            _saving = true;
            _btnSave.Enabled = false;
            _detTxtName.Enabled = false;
            _detTxtDesc.Enabled = false;
            _saveStatus.Text = "Iniciando…";
            Panel pBar = _saveProgress != null ? _saveProgress.Tag as Panel : null;
            if (pBar != null) pBar.Visible = true;
        }

        void EndSaveUi()
        {
            _saving = false;
            _detTxtName.Enabled = _currentDetailsCid > 0;
            _detTxtDesc.Enabled = _currentDetailsCid > 0;
            Panel pBar = _saveProgress != null ? _saveProgress.Tag as Panel : null;
            if (pBar != null) pBar.Visible = false;
            _saveStatus.Text = "";
            UpdateDirtyLabel();   // re-enable Save button se ainda houver edits
        }

        // Roda no UI thread (TaskScheduler.FromCurrentSynchronizationContext).
        // Trata erro, recarrega caches afetados, mostra mensagem final.
        void OnSaveCompleted(System.Threading.Tasks.Task<SaveResult> t)
        {
            EndSaveUi();
            SaveResult res = t.Result;
            if (res.Error != null)
            {
                MessageBox.Show("Falha ao salvar:\n" + res.Error.Message,
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // Cleanup state
            if (res.RaritySaved) { _dirty = false; _regulationDirty = false; }
            if (res.SameSaved)   _sameDirty = false;
            bool needReload = res.SlotsRewritten > 0 || res.PropSlots > 0;
            if (res.PropSlots > 0) _propEdits.Clear();
            if (res.SlotsRewritten > 0) _editedTexts.Clear();
            if (needReload)
            {
                CardNameLookup.Invalidate();
                _cards = CardNameLookup.LoadFull(_dataDir);
                RefreshGrid();
                UpdateDetailsPane();
            }
            UpdateDirtyLabel();

            List<string> parts = new List<string>();
            if (res.RaritySaved) parts.Add("CardList.json (rarities)");
            if (res.SlotsRewritten > 0)
                parts.Add("CARD_*.bytes (" + res.SlotsRewritten + " slots)");
            if (res.SameSaved) parts.Add("CARD_Same.bytes");
            if (res.PropSlots > 0)
                parts.Add("CARD_Prop.bytes (" + res.PropSlots + " edits)");
            if (res.InstallResult != null && res.InstallResult.OkCount > 0)
                parts.Add("Game containers (" + res.InstallResult.OkCount + ")");
            MessageBox.Show("Salvo: " + string.Join(", ", parts) +
                "\nBackups em _bkp/.", "OK",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- Helpers ----
        class FilterItem
        {
            public string Label, Key;
            public FilterItem(string label, string key) { Label = label; Key = key; }
            public override string ToString() { return Label; }
        }

        // Wrapper pra DataGridViewComboBoxColumn (Rarity inline) — cell
        // value é int (Value), display é label (N/R/SR/UR).
        class RarityItem
        {
            public int Value { get; set; }
            public string Label { get; set; }
            public RarityItem(int v, string l) { Value = v; Label = l; }
            public override string ToString() { return Label; }
        }

        // Wrapper pra coluna Limit (0/1/2/3) — display amigável.
        class LimitItem
        {
            public int Value { get; set; }
            public string Label { get; set; }
            public LimitItem(int v, string l) { Value = v; Label = l; }
            public override string ToString() { return Label; }
        }

        class EnumItem
        {
            public int Value { get; set; }
            public string Label { get; set; }
            public EnumItem(int v, string l) { Value = v; Label = l; }
            public override string ToString() { return Label; }
        }

        // Handler do checkbox "Ativo": move o card entre _cardlist e
        // _deactivated baseado no novo estado. Inverso direto do que
        // antes era Deactivate/Reactivate via menu ⋯.
        // Handler do TextBox SameCID inline. Parsea valor (int ou "—"),
        // atualiza CARD_Same.bytes via _sameLookup, marca dirty. Save
        // grava no .bytes + reinstala container 8e63fc3d via
        // ContainerInstaller após o write atomic.
        //
        // Convenção:
        //   "—" ou "" ou valor == cid → self-canon (variant aponta pra si)
        //   <CID> → variant aponta pra esse cid (canonical)
        void OnGridSameCidChanged(int rowIndex)
        {
            DataGridViewRow row = _grid.Rows[rowIndex];
            if (!(row.Tag is int cid)) return;
            string text = Convert.ToString(row.Cells["sameCid"].Value) ?? "";
            text = text.Trim();
            int newCanon;
            if (text == "" || text == "—") newCanon = cid;
            else if (!int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out newCanon))
            {
                MessageBox.Show("SameCID precisa ser número inteiro (ou '—').",
                    "Inválido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // Reverte display pro estado atual
                row.Cells["sameCid"].Value =
                    _sameResolvedCache.TryGetValue(cid, out int c) && c != cid ? c.ToString() : "—";
                return;
            }

            bool changed = _sameLookup.SetCanon(cid, newCanon);
            if (changed)
            {
                _sameResolvedCache = _sameLookup.ResolveAll();
                _sameDirty = true;
                MarkDirty();
                // Re-render só essa cell pra refletir resolve chain
                string display = _sameResolvedCache.TryGetValue(cid, out int c2) && c2 != cid
                    ? c2.ToString() : "—";
                row.Cells["sameCid"].Value = display;
            }
        }

        // Handler do combo Limit inline: atualiza Regulation.json do
        // format atualmente selecionado (move o cid entre buckets
        // a0/a1/a2/a3). Marca _regulationDirty. Persiste no Save junto
        // com CardList. Só roda quando há format específico selecionado
        // (combo Format != "all"); a coluna está hidden em "all" mesmo.
        void OnGridLimitChanged(int rowIndex)
        {
            if (_formatFilter == "all") return;   // safety
            DataGridViewRow row = _grid.Rows[rowIndex];
            if (!(row.Tag is int cid)) return;
            int newLimit;
            try { newLimit = Convert.ToInt32(row.Cells["limit"].Value); }
            catch { return; }
            if (newLimit < 0 || newLimit > 3) return;

            // 1) Update in-memory RegulationFormat (cache do FormatPools)
            RegulationFormat fmt = FindFormat(_formatFilter);
            if (fmt == null) return;
            int oldLimit = fmt.LimitOf(cid);
            if (newLimit == oldLimit) return;
            if (newLimit == 3) fmt.Limits.Remove(cid);   // 3 é implícito unlimited
            else               fmt.Limits[cid] = newLimit;
            // Allowed set: cid sai se limit==0, entra se >0
            if (newLimit == 0) fmt.Cids.Remove(cid);
            else               fmt.Cids.Add(cid);

            // 2) Update _regulationDoc raw (esse é o que vai pro disco)
            UpdateRegulationDocBuckets(_formatFilter, cid, newLimit);

            _regulationDirty = true;
            MarkDirty();
        }

        // Move o cid pros buckets corretos do format dentro de _regulationDoc.
        // Schema: regulationDoc[fmtId].available.{a0,a1,a2,a3}: List<int>
        // - newLimit == 0 → cid em a0, removido de a1/a2/a3
        // - newLimit == 1 → cid em a1, removido de a0/a2/a3
        // - newLimit == 2 → cid em a2, removido de a0/a1/a3
        // - newLimit == 3 → cid removido de TODOS (3 é implícito)
        void UpdateRegulationDocBuckets(string fmtId, int cid, int newLimit)
        {
            object fmtObj;
            if (!_regulationDoc.TryGetValue(fmtId, out fmtObj)) return;
            Dictionary<string, object> fmt = fmtObj as Dictionary<string, object>;
            if (fmt == null) return;
            Dictionary<string, object> avail =
                Utils.GetValue<Dictionary<string, object>>(fmt, "available");
            if (avail == null)
            {
                avail = new Dictionary<string, object>();
                fmt["available"] = avail;
            }
            // Remove de TODOS os buckets primeiro
            for (int n = 0; n <= 3; n++)
            {
                object bObj;
                if (!avail.TryGetValue("a" + n, out bObj)) continue;
                List<object> list = bObj as List<object>;
                if (list == null) continue;
                // Remove TODAS as ocorrências (defensive: pode haver duplicate em json malformado)
                list.RemoveAll(x =>
                {
                    try { return Convert.ToInt32(x) == cid; }
                    catch { return false; }
                });
            }
            // Adiciona no bucket novo (exceto 3 que é implícito)
            if (newLimit >= 0 && newLimit <= 2)
            {
                string key = "a" + newLimit;
                List<object> list = avail.ContainsKey(key) ? avail[key] as List<object> : null;
                if (list == null) { list = new List<object>(); avail[key] = list; }
                list.Add(cid);
            }
        }

        void OnActiveCheckChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "active") return;
            DataGridViewRow row = _grid.Rows[e.RowIndex];
            if (!(row.Tag is int cid)) return;
            bool isChecked = Convert.ToBoolean(row.Cells["active"].Value);
            bool wasActive = _cardlist.ContainsKey(cid);
            if (isChecked == wasActive) return;   // no-op
            if (isChecked) ReactivateCard(cid);
            else           DeactivateCard(cid);
        }

        // Move card de _cardlist (ativos) pra _deactivated (preservando rarity).
        // Card continua visível se "Mostrar desativados" estiver ON; senão somem.
        void DeactivateCard(int cid)
        {
            if (!_cardlist.TryGetValue(cid, out int rarity)) return;
            _cardlist.Remove(cid);
            _deactivated[cid] = rarity;
            MarkDirty();
            RefreshGrid();
        }

        // Move de _deactivated → _cardlist (rarity volta como tava antes).
        void ReactivateCard(int cid)
        {
            if (!_deactivated.TryGetValue(cid, out int rarity)) return;
            _deactivated.Remove(cid);
            _cardlist[cid] = rarity;
            MarkDirty();
            RefreshGrid();
        }

        // Handler do combo Rarity OU Limit inline (mesma event signature).
        // Roteia pro handler específico baseado no nome da coluna.
        void OnGridRarityChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string colName = _grid.Columns[e.ColumnIndex].Name;
            if (colName == "limit")   { OnGridLimitChanged(e.RowIndex); return; }
            if (colName == "sameCid") { OnGridSameCidChanged(e.RowIndex); return; }
            if (colName == "kind" || colName == "icon")
            {
                OnGridPropChanged(e.RowIndex, colName);
                return;
            }
            if (colName == "legend") { OnGridLegendChanged(e.RowIndex); return; }
            if (colName != "rarity") return;
            DataGridViewRow row = _grid.Rows[e.RowIndex];
            if (!(row.Tag is int cid)) return;
            int rarity;
            try { rarity = Convert.ToInt32(row.Cells["rarity"].Value); }
            catch { return; }
            if (rarity < 1 || rarity > 4) return;
            // Aplica no dict certo: ativos OU desativados (preservando estado).
            Dictionary<int, int> target = _deactivated.ContainsKey(cid)
                ? _deactivated : _cardlist;
            if (target.TryGetValue(cid, out int existing) && existing == rarity) return;
            target[cid] = rarity;
            MarkDirty();
            UpdateBulkButtons();
        }

        void OnGridLegendChanged(int rowIndex)
        {
            DataGridViewRow row = _grid.Rows[rowIndex];
            if (!(row.Tag is int cid)) return;
            bool checkedVal;
            try { checkedVal = Convert.ToBoolean(row.Cells["legend"].Value); }
            catch { return; }

            CardInfo info;
            _cards.TryGetValue(cid, out info);
            if (info == null) return;

            // Any card can be Legend except pendulums: their scale (PropB bits
            // 26-29) overlaps the Legend mask, so the bit would change the scale
            // instead of flagging Legend. Revert the checkbox.
            if (checkedVal && !info.IsLegendCapable)
            {
                row.Cells["legend"].Value = false;
                MessageBox.Show("Cards Pêndulo não podem ser Legend (o bit colide com o scale).",
                    "Legend", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PropWriter.Edit ed;
            if (!_propEdits.TryGetValue(cid, out ed))
            {
                ed = new PropWriter.Edit();
                _propEdits[cid] = ed;
            }
            ed.Legend = info.IsLegend == checkedVal ? (bool?)null : checkedVal;
            if (ed.Kind == null && ed.Icon == null && ed.Legend == null) _propEdits.Remove(cid);
            UpdateDirtyLabel();
        }

        void OnGridPropChanged(int rowIndex, string colName)
        {
            DataGridViewRow row = _grid.Rows[rowIndex];
            if (!(row.Tag is int cid)) return;
            object cellVal = row.Cells[colName].Value;
            if (cellVal == null || cellVal == DBNull.Value) return;
            int newVal;
            try { newVal = Convert.ToInt32(cellVal); } catch { return; }

            CardInfo info;
            _cards.TryGetValue(cid, out info);
            if (info == null) return;

            PropWriter.Edit ed;
            if (!_propEdits.TryGetValue(cid, out ed))
            {
                ed = new PropWriter.Edit();
                _propEdits[cid] = ed;
            }
            if (colName == "kind")
            {
                if ((int)info.Kind == newVal) { ed.Kind = null; }
                else ed.Kind = (CardKind)newVal;
            }
            else if (colName == "icon")
            {
                if ((int)info.Icon == newVal) { ed.Icon = null; }
                else ed.Icon = (CardIcon)newVal;
            }
            if (ed.Kind == null && ed.Icon == null && ed.Legend == null) _propEdits.Remove(cid);
            UpdateDirtyLabel();
        }

        // Botão custom com renderização colorida + estado active/inactive.
        // Inactive = semi-opaco (cor misturada com branco) pra dar feel
        // de "não selecionado". Active = saturado + bold + border.
        class RarityButton : Button
        {
            readonly int _rarity;
            readonly Color _baseColor;
            bool _active;
            bool _enabledState;

            public RarityButton(int rarity, string label, Color color)
            {
                _rarity = rarity;
                _baseColor = color;
                Text = label;
                Width = 90; Height = 36;
                FlatStyle = FlatStyle.Flat;
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold);
                Margin = new Padding(4, 2, 4, 2);
                Cursor = Cursors.Hand;
                FlatAppearance.BorderSize = 2;
                ApplyVisual();
            }

            public void SetState(bool enabled, bool active)
            {
                _enabledState = enabled;
                _active = active;
                Enabled = enabled;
                ApplyVisual();
            }

            void ApplyVisual()
            {
                if (!_enabledState)
                {
                    // Disabled: opacity bem baixa, sem border
                    BackColor = Blend(_baseColor, SystemColors.Control, 0.85f);
                    ForeColor = SystemColors.GrayText;
                    FlatAppearance.BorderColor = SystemColors.Control;
                    Font = new Font(Font, FontStyle.Regular);
                    return;
                }
                if (_active)
                {
                    // Active: cor saturada, border preta
                    BackColor = _baseColor;
                    ForeColor = (_rarity == RARITY_R) ? Color.White : Color.Black;
                    FlatAppearance.BorderColor = Color.Black;
                    Font = new Font(Font, FontStyle.Bold);
                }
                else
                {
                    // Inactive (mas enabled): cor opaca pra indicar "clicável"
                    BackColor = Blend(_baseColor, SystemColors.Control, 0.55f);
                    ForeColor = SystemColors.ControlText;
                    FlatAppearance.BorderColor = SystemColors.ControlDark;
                    Font = new Font(Font, FontStyle.Regular);
                }
            }

            // Mistura linear de duas cores. t=0 → c1; t=1 → c2.
            static Color Blend(Color c1, Color c2, float t)
            {
                if (t < 0) t = 0; if (t > 1) t = 1;
                int r = (int)(c1.R * (1 - t) + c2.R * t);
                int g = (int)(c1.G * (1 - t) + c2.G * t);
                int b = (int)(c1.B * (1 - t) + c2.B * t);
                return Color.FromArgb(r, g, b);
            }
        }
    }
}
