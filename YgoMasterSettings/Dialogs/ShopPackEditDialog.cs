using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using YgoMaster;
using YgoMasterSettings.Util;

namespace YgoMasterSettings.Dialogs
{
    // Modal pra add/edit de 1 pack (booster). 5 tabs internas:
    //   Basic    — ID, name, type, release date, price
    //   Cards    — cardList editor (picker + lista do pack)
    //   Visuals  — packImage (combo) + upload de imagem nova que gera
    //              auto HD/SD/HighlightThumb via PackImageProcessor
    //   Odds     — oddsName combo (lê de ShopPackOdds.json)
    //   Chain    — unlockSecrets (multi-select de outros packs)
    //
    // Result fica em Result quando user clica Save (null se Cancel).
    // O caller (ShopPacksSubTab) integra ao ShopData e marca dirty.
    class ShopPackEditDialog : Form
    {
        public Dictionary<string, object> Result { get; private set; }

        readonly bool _isEdit;
        readonly ShopData _shop;
        readonly string _dataDir;
        readonly Dictionary<string, object> _initial;
        // Working copy: mutado conforme user edita; copiado pro Result no Save.
        readonly Dictionary<string, object> _working;

        // Controls tab Basic
        TextBox _txtId, _txtName;
        ComboBox _cmbPackType, _cmbSecretType;
        DateTimePicker _dtRelease;
        NumericUpDown _numCardNum, _numSubCat, _numPrice;

        // Controls tab Cards
        CardPicker _cardPicker;
        DataGridView _gridPackCards;
        Label _lblTotalCards;
        // Working dict: cid (string) → quantidade (int)
        readonly Dictionary<string, object> _cardList;

        // Controls tab Visuals
        ComboBox _cmbPackImage;
        PictureBox _imgPreview;
        Button _btnImportImage, _btnRegenVariants;
        Label _lblVariantStatus;
        TextBox _txtIconData;
        NumericUpDown _numIconMrk, _numIconType;

        // Controls tab Odds
        ComboBox _cmbOddsName;
        TextBox _txtOddsPreview;

        // Controls tab Chain
        CheckedListBox _lstChain;

        public ShopPackEditDialog(ShopData shop, string dataDir, Dictionary<string, object> initial)
        {
            _shop = shop;
            _dataDir = dataDir;
            _isEdit = initial != null && initial.ContainsKey("packId");
            _initial = initial ?? new Dictionary<string, object>();
            _working = new Dictionary<string, object>(_initial);

            // cardList copy mutável (compartilhado entre tabs)
            object cl;
            _cardList = _initial.TryGetValue("cardList", out cl) && cl is Dictionary<string, object>
                ? new Dictionary<string, object>((Dictionary<string, object>)cl)
                : new Dictionary<string, object>();

            Text = _isEdit
                ? ("Edit Pack " + ShopData.GetInt(_initial, "packId"))
                : "Add Pack";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 640);
            MinimumSize = new Size(800, 560);
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
            tabs.TabPages.Add(BuildBasicTab());
            tabs.TabPages.Add(BuildCardsTab());
            tabs.TabPages.Add(BuildVisualsTab());
            tabs.TabPages.Add(BuildOddsTab());
            tabs.TabPages.Add(BuildChainTab());

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

        // ===== Tab Basic =====
        TabPage BuildBasicTab()
        {
            TabPage p = new TabPage("Basic") { Padding = new Padding(12) };
            TableLayoutPanel t = NewForm();

            _txtId = new TextBox { Width = 120 };
            if (_isEdit) _txtId.ReadOnly = true;
            AddRow(t, "Pack ID", _txtId, _isEdit ? "" : "Inteiro único — não pode colidir com outro pack");

            _txtName = new TextBox { Width = 480 };
            AddRow(t, "Name", _txtName);

            _cmbPackType = NewCombo(new[] { "1 = Booster", "2 = Special", "3 = Bonus", "4 = Selection" });
            AddRow(t, "Pack type", _cmbPackType);

            _cmbSecretType = NewCombo(new[] { "0 = Visible", "4 = Locked (unlocked via prev pack)" });
            AddRow(t, "Secret type", _cmbSecretType);

            _dtRelease = new DateTimePicker { Width = 240, Format = DateTimePickerFormat.Long };
            AddRow(t, "Release date", _dtRelease);

            _numCardNum = new NumericUpDown { Width = 80, Minimum = 1, Maximum = 24, Value = 8 };
            AddRow(t, "Cards per pack", _numCardNum, "Default 8");

            _numSubCat = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 1 };
            AddRow(t, "Sub category", _numSubCat);

            _numPrice = new NumericUpDown { Width = 100, Minimum = 0, Maximum = 999999, Value = 0 };
            AddRow(t, "Price (gems)", _numPrice, "0 = usa default global");

            p.Controls.Add(t);
            return p;
        }

        // ===== Tab Cards =====
        // Layout:
        //  [Picker | + / − / 1-3 | Pack contents]
        //  Picker (esq): CardPicker com coluna ✓ marcando cards já no
        //   pack. Double-click ou "+" adiciona.
        //  Middle: stack vertical de 3 botões grandes (+1 / −1 / Set 3)
        //   pra ações batch entre selected (picker) ↔ pack.
        //  Pack contents (dir): grid CID|Name|Qty com coluna Qty
        //   editável inline (1-3) + botão Remove no rodapé. Total
        //   embaixo, bem visível.
        TabPage BuildCardsTab()
        {
            TabPage p = new TabPage("Cards") { Padding = new Padding(6) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3, RowCount = 1,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  55f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  45f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // ----- Esquerda: picker -----
            _cardPicker = new CardPicker(_dataDir) { Dock = DockStyle.Fill };
            _cardPicker.CardsAdded += OnCardsAdded;
            _cardPicker.MarkedCidsProvider = GetCardsInPack;

            // ----- Centro: botões verticais de ação -----
            // O cardList do pack é { cid → rarity 1..4 } (NÃO quantidade!).
            // Quando PerPackRarities = true (default), essa rarity
            // sobrescreve a do CardList.json no contexto do pack.
            // Botões: adicionar (com rarity N default) + bulk set rarity
            // dos cards já no pack + remover.
            Panel middle = new Panel { Dock = DockStyle.Fill,
                Padding = new Padding(4, 30, 4, 4) };
            Button btnAdd = new Button
            {
                Text = "Add →", Dock = DockStyle.Top, Height = 32,
                BackColor = Color.FromArgb(0x27, 0xAE, 0x60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
            };
            btnAdd.Click += (s, e) => _cardPicker.AddSelected();
            Button btnRemove = new Button
            {
                Text = "← Remove", Dock = DockStyle.Top, Height = 32,
                Margin = new Padding(0, 4, 0, 10),
            };
            btnRemove.Click += (s, e) => RemoveSelectedFromPack();
            // Separator + bulk rarity buttons (afetam selected no PACK
            // contents — direita). Cores espelham CardList tab.
            Label sepLabel = new Label
            {
                Text = "Set rarity:", Dock = DockStyle.Top, Height = 18,
                Padding = new Padding(0, 2, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Theme.FgMuted,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            Button btnN  = MakeRarityBtn("N",  1, Color.FromArgb(0xB0, 0xB0, 0xB0));
            Button btnR  = MakeRarityBtn("R",  2, Color.FromArgb(0xCD, 0x7F, 0x32));
            Button btnSR = MakeRarityBtn("SR", 3, Color.FromArgb(0xC0, 0xC0, 0xC8));
            Button btnUR = MakeRarityBtn("UR", 4, Color.FromArgb(0xFF, 0xCC, 0x33));
            // Adicionar em ordem reversa (Dock=Top → último em cima)
            middle.Controls.Add(btnUR);
            middle.Controls.Add(btnSR);
            middle.Controls.Add(btnR);
            middle.Controls.Add(btnN);
            middle.Controls.Add(sepLabel);
            middle.Controls.Add(btnRemove);
            middle.Controls.Add(btnAdd);

            // ----- Direita: pack contents -----
            Panel right = new Panel { Dock = DockStyle.Fill };
            _gridPackCards = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
            };
            DataGridViewTextBoxColumn colCid = new DataGridViewTextBoxColumn
            {
                Name = "cid", HeaderText = "CID", Width = 60, ReadOnly = true,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
            };
            DataGridViewTextBoxColumn colName = new DataGridViewTextBoxColumn
            {
                Name = "name", HeaderText = "Name", ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            };
            // Rarity como combo (1=N, 2=R, 3=SR, 4=UR) — sobrescreve a
            // rarity global do CardList.json no contexto do pack quando
            // PerPackRarities=true (default no fork). Cell guarda int
            // (1..4) mas display mostra label via Value/DisplayMember.
            DataGridViewComboBoxColumn colQty = new DataGridViewComboBoxColumn
            {
                Name = "qty", HeaderText = "Rarity", Width = 70,
                DefaultCellStyle = {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                },
                FlatStyle = FlatStyle.Flat,
                ValueMember = "Value",
                DisplayMember = "Label",
            };
            colQty.DataSource = new List<RarityItem>
            {
                new RarityItem(1, "N"),
                new RarityItem(2, "R"),
                new RarityItem(3, "SR"),
                new RarityItem(4, "UR"),
            };
            // Coluna actions: botão remove inline
            DataGridViewButtonColumn colAct = new DataGridViewButtonColumn
            {
                Name = "rm", HeaderText = "", Width = 30,
                Text = "✕", UseColumnTextForButtonValue = true,
                DefaultCellStyle = {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    ForeColor = Theme.FgDanger,
                    Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                },
                FlatStyle = FlatStyle.Flat,
            };
            _gridPackCards.Columns.AddRange(new DataGridViewColumn[] { colCid, colName, colQty, colAct });
            _gridPackCards.CellValueChanged += OnQtyChanged;
            // Pinta a coluna Rarity com a cor da rarity (mesma palette do
            // CardList tab: N=cinza, R=bronze, SR=prata, UR=ouro). Com
            // ValueMember/DisplayMember setados, a CELL VALUE é int
            // (1..4) e o display é a label — usamos cell value (int) na
            // formatação porque é mais robusto que parsear label.
            _gridPackCards.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (_gridPackCards.Columns[e.ColumnIndex].Name != "qty") return;
                int r;
                try { r = Convert.ToInt32(_gridPackCards.Rows[e.RowIndex].Cells["qty"].Value); }
                catch { return; }
                switch (r)
                {
                    case 1: e.CellStyle.BackColor = Color.FromArgb(0xB0, 0xB0, 0xB0);
                            e.CellStyle.ForeColor = Color.Black; break;
                    case 2: e.CellStyle.BackColor = Color.FromArgb(0xCD, 0x7F, 0x32);
                            e.CellStyle.ForeColor = Color.White; break;
                    case 3: e.CellStyle.BackColor = Color.FromArgb(0xC0, 0xC0, 0xC8);
                            e.CellStyle.ForeColor = Color.Black; break;
                    case 4: e.CellStyle.BackColor = Color.FromArgb(0xFF, 0xCC, 0x33);
                            e.CellStyle.ForeColor = Color.Black; break;
                }
            };
            // Suprime DataError se rarity vier fora de 1-4 (defensivo —
            // RebuildPackCardsGrid já clampa, mas pack antigo pode ter
            // valor anormal salvo no JSON).
            _gridPackCards.DataError += (s, e) => { e.ThrowException = false; };
            // Click no ✕ remove a row imediatamente
            _gridPackCards.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (_gridPackCards.Columns[e.ColumnIndex].Name != "rm") return;
                string key = _gridPackCards.Rows[e.RowIndex].Tag as string;
                if (key != null) _cardList.Remove(key);
                RebuildPackCardsGrid();
            };
            // Delete pra remover selected (atalho do teclado)
            _gridPackCards.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) RemoveSelectedFromPack();
            };

            // Header com altura 32px pra alinhar com a toolbar do
            // CardPicker (mesma altura → tops dos grids ficam alinhados).
            Label hdr = new Label
            {
                Text = "Pack contents — Rarity per-pack (1=N, 2=R, 3=SR, 4=UR)",
                Dock = DockStyle.Top, Height = 32,
                ForeColor = Theme.FgMuted,
                Padding = new Padding(4, 8, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            // Total no topo (não bottom) — mesma altura 18px do label
            // count do CardPicker pros 2 grids alinharem perfeitamente.
            _lblTotalCards = new Label
            {
                Text = "Total: 0",
                Dock = DockStyle.Top, Height = 18,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                ForeColor = Theme.FgAccent,
                Padding = new Padding(4, 2, 0, 0),
            };
            // Stack order: grid (fill) → total (top) → hdr (top above total)
            right.Controls.Add(_gridPackCards);
            right.Controls.Add(_lblTotalCards);
            right.Controls.Add(hdr);

            layout.Controls.Add(_cardPicker, 0, 0);
            layout.Controls.Add(middle,      1, 0);
            layout.Controls.Add(right,       2, 0);
            p.Controls.Add(layout);
            return p;
        }

        // Factory pros 4 botões de rarity coloridos (N/R/SR/UR).
        // Click aplica a rarity nos cards selecionados no PACK contents
        // (não no picker — operação é "definir rarity das cards já
        // adicionadas ao pack").
        Button MakeRarityBtn(string label, int rarity, Color color)
        {
            Button b = new Button
            {
                Text = label, Dock = DockStyle.Top, Height = 30,
                Margin = new Padding(0, 2, 0, 0),
                BackColor = color,
                ForeColor = (rarity == 2) ? Color.White : Color.Black,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
            };
            b.Click += (s, e) => SetRarityOnPackSelected(rarity);
            return b;
        }

        // Aplica rarity nos cards selecionados do grid de Pack contents
        // (direita). Se nada selecionado, mostra aviso.
        void SetRarityOnPackSelected(int rarity)
        {
            List<string> targets = new List<string>();
            foreach (DataGridViewRow row in _gridPackCards.SelectedRows)
                if (row.Tag is string k) targets.Add(k);
            if (targets.Count == 0)
            {
                MessageBox.Show("Selecione 1 ou mais cards no Pack contents (direita) primeiro.",
                    "Sem alvo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (string k in targets) _cardList[k] = rarity;
            RebuildPackCardsGrid();
        }

        // Set de cids atualmente no pack — usado pelo CardPicker pra
        // marcar visualmente quais cards já estão dentro.
        HashSet<int> GetCardsInPack()
        {
            HashSet<int> set = new HashSet<int>();
            foreach (string key in _cardList.Keys)
            {
                int cid;
                if (int.TryParse(key, out cid)) set.Add(cid);
            }
            return set;
        }

        void OnCardsAdded(IEnumerable<int> cids)
        {
            // Adiciona com rarity Normal (1) por default. Se card já
            // existe no pack, NÃO sobrescreve a rarity existente — só
            // ignora (use os botões Set N/R/SR/UR pra mudar).
            foreach (int cid in cids)
            {
                string key = cid.ToString(CultureInfo.InvariantCulture);
                if (_cardList.ContainsKey(key)) continue;
                _cardList[key] = 1;
            }
            RebuildPackCardsGrid();
        }

        // Handler da coluna Rarity inline (combo 1..4).
        void OnQtyChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || _gridPackCards.Columns[e.ColumnIndex].Name != "qty") return;
            DataGridViewRow row = _gridPackCards.Rows[e.RowIndex];
            string cidKey = row.Tag as string;
            if (cidKey == null) return;
            int rarity;
            string v = Convert.ToString(row.Cells["qty"].Value);
            if (!int.TryParse(v, out rarity) || rarity < 1) rarity = 1;
            if (rarity > 4) rarity = 4;
            _cardList[cidKey] = rarity;
            row.Cells["qty"].Value = rarity;
            UpdateTotalCards();
        }

        void RemoveSelectedFromPack()
        {
            List<string> toRemove = new List<string>();
            foreach (DataGridViewRow row in _gridPackCards.SelectedRows)
                if (row.Tag is string key) toRemove.Add(key);
            foreach (string k in toRemove) _cardList.Remove(k);
            RebuildPackCardsGrid();
        }

        void RebuildPackCardsGrid()
        {
            _gridPackCards.SuspendLayout();
            _gridPackCards.Rows.Clear();
            // Carregar nomes (cache global)
            Dictionary<int, CardInfo> names = CardNameLookup.LoadFull(_dataDir);
            List<int> keys = new List<int>();
            foreach (string k in _cardList.Keys)
            {
                int cid;
                if (int.TryParse(k, out cid)) keys.Add(cid);
            }
            keys.Sort();
            foreach (int cid in keys)
            {
                string key = cid.ToString(CultureInfo.InvariantCulture);
                int rarity;
                try { rarity = Convert.ToInt32(_cardList[key]); } catch { rarity = 1; }
                if (rarity < 1) rarity = 1; if (rarity > 4) rarity = 4;
                CardInfo info;
                names.TryGetValue(cid, out info);
                int idx = _gridPackCards.Rows.Add(cid,
                    info != null ? info.Name : "(unknown)", rarity, "✕");
                _gridPackCards.Rows[idx].Tag = key;
            }
            _gridPackCards.ResumeLayout(performLayout: true);
            UpdateTotalCards();
            // Picker pode estar mostrando marcadores "in pack" — refresh
            if (_cardPicker != null) _cardPicker.RefreshMarkers();
        }

        void UpdateTotalCards()
        {
            // Conta cards por rarity (1=N, 2=R, 3=SR, 4=UR)
            int n = 0, r = 0, sr = 0, ur = 0;
            foreach (KeyValuePair<string, object> kv in _cardList)
            {
                int rarity;
                try { rarity = Convert.ToInt32(kv.Value); } catch { continue; }
                switch (rarity)
                {
                    case 1: n++; break;
                    case 2: r++; break;
                    case 3: sr++; break;
                    case 4: ur++; break;
                }
            }
            _lblTotalCards.Text = _cardList.Count + " cards · " +
                "N " + n + " · R " + r + " · SR " + sr + " · UR " + ur;
        }

        // ===== Tab Visuals =====
        TabPage BuildVisualsTab()
        {
            TabPage p = new TabPage("Visuals") { Padding = new Padding(12) };

            // Layout: form à esquerda + preview à direita
            Panel left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
            TableLayoutPanel t = NewForm();
            t.Dock = DockStyle.Top;

            _cmbPackImage = new ComboBox { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            PopulatePackImagesCombo();
            _cmbPackImage.SelectedIndexChanged += (s, e) => UpdateImagePreview();
            AddRow(t, "Pack image", _cmbPackImage);

            _btnImportImage = new Button
            {
                Text = "Importar imagem nova…", Width = 200, Height = 28,
                BackColor = Color.FromArgb(0x27, 0xAE, 0x60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _btnImportImage.Click += OnImportImage;
            AddRow(t, "", _btnImportImage,
                "Seleciona 1 imagem → gera HD/SD/HighlightThumb automaticamente");

            // Status das 3 variantes — atualizado em UpdateImagePreview
            _lblVariantStatus = new Label
            {
                AutoSize = false, Width = 280, Height = 22,
                Padding = new Padding(0, 4, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
            };
            AddRow(t, "Variantes", _lblVariantStatus,
                "✓ = arquivo existe no disco");

            // Botão regenerar — visível só quando faltam variantes mas
            // já existe pelo menos uma source pra usar como base.
            _btnRegenVariants = new Button
            {
                Text = "Regenerar variantes faltando", Width = 220, Height = 28,
                BackColor = Color.FromArgb(0xE6, 0x7E, 0x22),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Visible = false,
            };
            _btnRegenVariants.Click += OnRegenVariantsClick;
            AddRow(t, "", _btnRegenVariants,
                "Usa a melhor imagem existente como source (upscale se for SD)");

            _numIconMrk = new NumericUpDown { Width = 100, Minimum = 0, Maximum = 999999 };
            AddRow(t, "Icon mrk (cardId)", _numIconMrk, "CardId destacado no pack");

            _numIconType = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 99, Value = 2 };
            AddRow(t, "Icon type", _numIconType);

            _txtIconData = new TextBox { Width = 240 };
            AddRow(t, "Icon data", _txtIconData,
                "Geralmente igual ao packImage (auto-set)");

            left.Controls.Add(t);

            // Direita: preview
            Panel right = new Panel { Dock = DockStyle.Right, Width = 280,
                Padding = new Padding(8) };
            Label hdr = new Label
            {
                Text = "Preview HD",
                Dock = DockStyle.Top, Height = 20,
                Font = new Font(Font, FontStyle.Bold),
            };
            _imgPreview = new PictureBox
            {
                Dock = DockStyle.Fill, BackColor = SystemColors.ControlDark,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
            };
            right.Controls.Add(_imgPreview);
            right.Controls.Add(hdr);

            p.Controls.Add(left);
            p.Controls.Add(right);
            return p;
        }

        void PopulatePackImagesCombo()
        {
            _cmbPackImage.Items.Clear();
            _cmbPackImage.Items.Add("(none)");
            // Union de IDs encontrados em QUALQUER uma das 3 pastas
            // (HD/SD/HighlightThumb). Packs antigos do Goat podem ter
            // só SD ou só HighlightThumb — não filtrar pela HD.
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string baseDir = Path.Combine(_dataDir, "ClientData", "Images");
            string[] folders =
            {
                Path.Combine(baseDir, "CardPack", "highend_hd",      "tcg"),
                Path.Combine(baseDir, "CardPack", "SD",              "tcg"),
                Path.Combine(baseDir, "Shop",     "HighlightThumbs", "tcg"),
            };
            foreach (string dir in folders)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, "set*.*"))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg") continue;
                    ids.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
            List<string> sorted = new List<string>(ids);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string s in sorted) _cmbPackImage.Items.Add(s);
        }

        void UpdateImagePreview()
        {
            string id = _cmbPackImage.SelectedItem as string;
            if (string.IsNullOrEmpty(id) || id == "(none)")
            {
                if (_imgPreview.Image != null) { _imgPreview.Image.Dispose(); _imgPreview.Image = null; }
                _lblVariantStatus.Text = "—";
                _lblVariantStatus.ForeColor = Theme.FgMuted;
                _btnRegenVariants.Visible = false;
                return;
            }
            // Fallback: prefere HD, mas se não existir tenta SD ou
            // HighlightThumb (packs antigos do Goat podem ter só uma
            // das variantes — ainda dá pra ver preview).
            string path = PackImageProcessor.AnyImagePathOf(_dataDir, id);
            if (_imgPreview.Image != null) { _imgPreview.Image.Dispose(); _imgPreview.Image = null; }
            if (path != null)
            {
                try
                {
                    using (FileStream fs = File.OpenRead(path))
                        _imgPreview.Image = Image.FromStream(fs);
                }
                catch { _imgPreview.Image = null; }
            }

            // Status das variantes + warning quando incompleto
            PackImageProcessor.Status st = PackImageProcessor.GetStatus(_dataDir, id);
            _lblVariantStatus.Text = st.Summary();
            if (st.IsComplete)
            {
                _lblVariantStatus.ForeColor = Color.FromArgb(0x27, 0xAE, 0x60);
                _btnRegenVariants.Visible = false;
                _btnRegenVariants.Text = "Regenerar todas as variantes";
            }
            else if (st.HasAny)
            {
                _lblVariantStatus.ForeColor = Color.FromArgb(0xE6, 0x7E, 0x22);   // laranja warn
                _btnRegenVariants.Visible = true;
                // Texto do botão muda conforme a source disponível
                if (!st.HasHd && st.HasSd)
                    _btnRegenVariants.Text = "⚠ Sem HD — Gerar a partir da SD";
                else if (!st.HasHd && !st.HasSd)
                    _btnRegenVariants.Text = "⚠ Só HighlightThumb — Gerar HD/SD";
                else
                    _btnRegenVariants.Text = "Regenerar variantes faltando";
            }
            else
            {
                _lblVariantStatus.ForeColor = Theme.FgDanger;
                _btnRegenVariants.Visible = false;   // nada pra usar como source
            }
        }

        void OnRegenVariantsClick(object sender, EventArgs e)
        {
            string id = _cmbPackImage.SelectedItem as string;
            if (string.IsNullOrEmpty(id) || id == "(none)") return;
            PackImageProcessor.Status st = PackImageProcessor.GetStatus(_dataDir, id);
            if (!st.HasAny)
            {
                MessageBox.Show("Nenhuma imagem disponível como source.",
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // Aviso de qualidade quando source é só SD (vai upscalear)
            string warning = "";
            if (!st.HasHd && st.HasSd)
                warning = "\n\nAVISO: source é a SD (256px max), então o HD " +
                          "gerado vai ter a mesma resolução. Pra qualidade real, " +
                          "use 'Importar imagem nova…' com uma imagem maior.";
            else if (!st.HasHd && !st.HasSd)
                warning = "\n\nAVISO: source é só o HighlightThumb (1024x578 " +
                          "horizontal). HD/SD gerados vão ficar com aspect " +
                          "horizontal — pode não ser o esperado.";
            DialogResult dr = MessageBox.Show(
                "Vai regerar HD/SD/HighlightThumb pra '" + id + "' usando " +
                "a melhor source disponível." + warning + "\n\nContinuar?",
                "Regenerar variantes",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;

            try
            {
                PackImageProcessor.Result res =
                    PackImageProcessor.RegenerateFromBest(_dataDir, id);
                // Atualiza UI
                UpdateImagePreview();
                MessageBox.Show(
                    "Variantes regeradas:\n" +
                    "HD: " + res.HdPath + "\n" +
                    "SD: " + res.SdPath + "\n" +
                    "Highlight: " + res.HighlightPath,
                    "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao regerar:\n" + ex.Message,
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnImportImage(object sender, EventArgs e)
        {
            // 1) Pede ID/nome da imagem (sugere "set" + packId)
            int packId;
            int.TryParse(_txtId.Text.Trim(), out packId);
            string suggested = packId > 0 ? "set" + packId : "set_new";
            string imageId = PromptInput("Nome do asset (sem extensão)", suggested);
            if (string.IsNullOrEmpty(imageId)) return;

            // 2) Pede arquivo source
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
                Title = "Selecione a imagem do pack",
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    PackImageProcessor.Result res =
                        PackImageProcessor.ProcessFromFile(_dataDir, ofd.FileName, imageId);
                    // 3) Atualiza combo + seleciona + atualiza icon data
                    PopulatePackImagesCombo();
                    SelectCombo(_cmbPackImage, imageId);
                    _txtIconData.Text = imageId;
                    UpdateImagePreview();
                    MessageBox.Show(
                        "Imagem importada:\n" +
                        "HD: " + res.HdPath + "\n" +
                        "SD: " + res.SdPath + "\n" +
                        "Highlight: " + res.HighlightPath,
                        "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Falha ao processar imagem:\n" + ex.Message,
                        "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ===== Tab Odds =====
        TabPage BuildOddsTab()
        {
            TabPage p = new TabPage("Odds") { Padding = new Padding(12) };
            TableLayoutPanel t = NewForm();
            t.Dock = DockStyle.Top;

            _cmbOddsName = new ComboBox { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            PopulateOddsCombo();
            _cmbOddsName.SelectedIndexChanged += (s, e) => UpdateOddsPreview();
            AddRow(t, "Odds name", _cmbOddsName,
                "(default) = usa odds genérico do packType");

            p.Controls.Add(t);

            _txtOddsPreview = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Both, WordWrap = false,
                BackColor = SystemColors.Window,
                Font = new Font("Consolas", 9f),
            };
            p.Controls.Add(_txtOddsPreview);
            return p;
        }

        void PopulateOddsCombo()
        {
            _cmbOddsName.Items.Clear();
            _cmbOddsName.Items.Add("(default)");
            string oddsPath = Path.Combine(_dataDir, "ShopPackOdds.json");
            if (!File.Exists(oddsPath)) return;
            try
            {
                // ShopPackOdds.json é array; alguns entries têm "name", outros não
                List<object> odds = MiniJSON.Json.Deserialize(
                    File.ReadAllText(oddsPath)) as List<object>;
                if (odds == null) return;
                foreach (object o in odds)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = ShopData.GetStr(d, "name");
                    if (!string.IsNullOrEmpty(name)) _cmbOddsName.Items.Add(name);
                }
            }
            catch { }
        }

        void UpdateOddsPreview()
        {
            string name = _cmbOddsName.SelectedItem as string;
            if (string.IsNullOrEmpty(name) || name == "(default)")
            {
                _txtOddsPreview.Text = "(usa odds default do packType — ver ShopPackOdds.json entries sem name)";
                return;
            }
            string oddsPath = Path.Combine(_dataDir, "ShopPackOdds.json");
            try
            {
                List<object> odds = MiniJSON.Json.Deserialize(
                    File.ReadAllText(oddsPath)) as List<object>;
                if (odds == null) return;
                foreach (object o in odds)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    if (ShopData.GetStr(d, "name") != name) continue;
                    _txtOddsPreview.Text = MiniJSON.Json.Serialize(d);
                    return;
                }
            }
            catch { }
            _txtOddsPreview.Text = "(odds '" + name + "' não encontrado em ShopPackOdds.json)";
        }

        // ===== Tab Chain =====
        TabPage BuildChainTab()
        {
            TabPage p = new TabPage("Chain") { Padding = new Padding(12) };
            Label hdr = new Label
            {
                Text = "Unlock secrets — packs que serão destravados quando este aqui " +
                       "atingir DefaultUnlockSecretsAtPercent (Globals). Marca os IDs " +
                       "que devem ser destravados.",
                Dock = DockStyle.Top, Height = 50, ForeColor = Theme.FgMuted,
            };
            _lstChain = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BackColor = SystemColors.Window,
            };
            // Popula com TODOS os packs existentes (exceto este se for edit)
            int selfId = _isEdit ? ShopData.GetInt(_initial, "packId") : 0;
            List<KeyValuePair<int, string>> packs = new List<KeyValuePair<int, string>>();
            foreach (KeyValuePair<string, object> kv in _shop.Packs)
            {
                Dictionary<string, object> pp = kv.Value as Dictionary<string, object>;
                if (pp == null) continue;
                int id = ShopData.GetInt(pp, "packId");
                if (id == selfId) continue;
                string name = ShopData.GetStr(pp, "nameTextId");
                packs.Add(new KeyValuePair<int, string>(id, name));
            }
            packs.Sort((a, b) => a.Key - b.Key);
            foreach (KeyValuePair<int, string> kv in packs)
                _lstChain.Items.Add(new ChainItem(kv.Key, kv.Value), false);

            p.Controls.Add(_lstChain);
            p.Controls.Add(hdr);
            return p;
        }

        class ChainItem
        {
            public int Id; public string Name;
            public ChainItem(int id, string name) { Id = id; Name = name; }
            public override string ToString() { return Id + " — " + Name; }
        }

        // ===== Load / Save =====
        void LoadInitial()
        {
            // Basic
            _txtId.Text = ShopData.GetInt(_initial, "packId").ToString(CultureInfo.InvariantCulture);
            _txtName.Text = ShopData.GetStr(_initial, "nameTextId");
            SelectComboByPrefix(_cmbPackType, ShopData.GetInt(_initial, "packType", 1).ToString());
            SelectComboByPrefix(_cmbSecretType, ShopData.GetInt(_initial, "secretType", 0).ToString());
            long ts = ShopData.GetInt(_initial, "releaseDate");
            if (ts > 0)
            {
                try { _dtRelease.Value = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime(); }
                catch { _dtRelease.Value = DateTime.Now; }
            }
            int n;
            n = ShopData.GetInt(_initial, "pack_card_num", 8); if (n >= _numCardNum.Minimum && n <= _numCardNum.Maximum) _numCardNum.Value = n;
            n = ShopData.GetInt(_initial, "subCategory", 1);   if (n >= _numSubCat.Minimum  && n <= _numSubCat.Maximum)  _numSubCat.Value  = n;
            n = ShopData.GetInt(_initial, "price");            if (n >= _numPrice.Minimum   && n <= _numPrice.Maximum)   _numPrice.Value   = n;

            // Cards
            RebuildPackCardsGrid();

            // Visuals
            SelectCombo(_cmbPackImage, ShopData.GetStr(_initial, "packImage"));
            UpdateImagePreview();
            n = ShopData.GetInt(_initial, "iconMrk");
            if (n >= _numIconMrk.Minimum && n <= _numIconMrk.Maximum) _numIconMrk.Value = n;
            n = ShopData.GetInt(_initial, "iconType", 2);
            if (n >= _numIconType.Minimum && n <= _numIconType.Maximum) _numIconType.Value = n;
            _txtIconData.Text = ShopData.GetStr(_initial, "iconData");

            // Odds
            string odds = ShopData.GetStr(_initial, "oddsName");
            SelectCombo(_cmbOddsName, string.IsNullOrEmpty(odds) ? "(default)" : odds);
            UpdateOddsPreview();

            // Chain
            object us;
            if (_initial.TryGetValue("unlockSecrets", out us) && us is List<object>)
            {
                HashSet<int> selected = new HashSet<int>();
                foreach (object o in (List<object>)us)
                {
                    int v;
                    if (int.TryParse(Convert.ToString(o), out v)) selected.Add(v);
                }
                for (int i = 0; i < _lstChain.Items.Count; i++)
                {
                    ChainItem ci = _lstChain.Items[i] as ChainItem;
                    if (ci != null && selected.Contains(ci.Id)) _lstChain.SetItemChecked(i, true);
                }
            }
        }

        void OnSave(object sender, EventArgs e)
        {
            // Validar Pack ID
            int packId;
            if (!int.TryParse(_txtId.Text.Trim(), out packId) || packId <= 0)
            {
                MessageBox.Show("Pack ID precisa ser inteiro > 0.",
                    "Inválido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_isEdit && _shop.Packs.ContainsKey(packId.ToString(CultureInfo.InvariantCulture)))
            {
                MessageBox.Show("Já existe um pack com ID " + packId + ".",
                    "Duplicado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Dictionary<string, object> r = new Dictionary<string, object>(_working);
            r["packId"] = packId;
            r["productType"] = 1;   // Pack
            r["packType"] = ParseLeadingInt(_cmbPackType.SelectedItem as string, 1);
            r["secretType"] = ParseLeadingInt(_cmbSecretType.SelectedItem as string, 0);
            r["nameTextId"] = _txtName.Text;
            r["releaseDate"] = (long)(_dtRelease.Value.ToUniversalTime() -
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            r["pack_card_num"] = (int)_numCardNum.Value;
            r["subCategory"] = (int)_numSubCat.Value;
            if (_numPrice.Value > 0) r["price"] = (int)_numPrice.Value;
            else r.Remove("price");

            r["cardList"] = new Dictionary<string, object>(_cardList);

            string img = _cmbPackImage.SelectedItem as string;
            if (!string.IsNullOrEmpty(img) && img != "(none)")
            {
                r["packImage"] = img;
                if (!string.IsNullOrEmpty(_txtIconData.Text))
                    r["iconData"] = _txtIconData.Text;
                else
                    r["iconData"] = img;
                // preview JSON auto-build: [{"type":3,"path":"<img>"}]
                Dictionary<string, object> previewEntry = new Dictionary<string, object>
                {
                    { "type", 3 }, { "path", img },
                };
                r["preview"] = MiniJSON.Json.Serialize(new List<object> { previewEntry });
            }
            if (_numIconMrk.Value > 0) r["iconMrk"] = (int)_numIconMrk.Value;
            r["iconType"] = (int)_numIconType.Value;

            string oddsName = _cmbOddsName.SelectedItem as string;
            if (!string.IsNullOrEmpty(oddsName) && oddsName != "(default)")
                r["oddsName"] = oddsName;
            else
                r.Remove("oddsName");

            // Chain
            List<object> chain = new List<object>();
            for (int i = 0; i < _lstChain.Items.Count; i++)
            {
                if (!_lstChain.GetItemChecked(i)) continue;
                ChainItem ci = _lstChain.Items[i] as ChainItem;
                if (ci != null) chain.Add(ci.Id);
            }
            if (chain.Count > 0) r["unlockSecrets"] = chain;
            else r.Remove("unlockSecrets");

            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ===== helpers =====
        TableLayoutPanel NewForm()
        {
            return new TableLayoutPanel
            {
                ColumnCount = 3, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnStyles = {
                    new ColumnStyle(SizeType.AutoSize),
                    new ColumnStyle(SizeType.AutoSize),
                    new ColumnStyle(SizeType.AutoSize),
                },
            };
        }
        void AddRow(TableLayoutPanel t, string label, Control input, string hint = null)
        {
            int row = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label
            {
                Text = label, AutoSize = true,
                Margin = new Padding(0, 8, 12, 4),
                Font = new Font(Font, FontStyle.Bold),
            }, 0, row);
            input.Margin = new Padding(0, 4, 8, 4);
            t.Controls.Add(input, 1, row);
            if (hint != null)
            {
                Label h = new Label { Text = hint, AutoSize = true,
                    ForeColor = Theme.FgMuted,
                    Margin = new Padding(8, 8, 0, 4) };
                t.Controls.Add(h, 2, row);
            }
            t.RowCount = row + 1;
        }
        static ComboBox NewCombo(string[] items)
        {
            ComboBox c = new ComboBox { Width = 220,
                DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (string s in items) c.Items.Add(s);
            if (items.Length > 0) c.SelectedIndex = 0;
            return c;
        }
        static void SelectCombo(ComboBox c, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 0; i < c.Items.Count; i++)
            {
                if (string.Equals(c.Items[i].ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    c.SelectedIndex = i;
                    return;
                }
            }
        }
        static void SelectComboByPrefix(ComboBox c, string prefix)
        {
            for (int i = 0; i < c.Items.Count; i++)
            {
                string s = c.Items[i].ToString();
                if (s.StartsWith(prefix + " ")) { c.SelectedIndex = i; return; }
            }
        }
        static int ParseLeadingInt(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            int sp = s.IndexOf(' ');
            string num = sp >= 0 ? s.Substring(0, sp) : s;
            int v;
            return int.TryParse(num, out v) ? v : fallback;
        }
        // Mini-prompt inline (sem Forms.PromptDialog nativo no .NET 4.8)
        static string PromptInput(string title, string defaultValue)
        {
            using (Form f = new Form
            {
                Text = title,
                ClientSize = new Size(360, 100),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
            })
            {
                TextBox tb = new TextBox { Text = defaultValue ?? "",
                    Left = 12, Top = 12, Width = 336 };
                Button ok = new Button { Text = "OK", Left = 184, Top = 50,
                    Width = 80, DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = "Cancel", Left = 268, Top = 50,
                    Width = 80, DialogResult = DialogResult.Cancel };
                f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        // Wrapper pra DataGridViewComboBoxColumn mostrar label ("N"/"R"/
        // "SR"/"UR") enquanto a cell guarda int (1..4). Cell value usa
        // ValueMember="Value"; display usa DisplayMember="Label".
        class RarityItem
        {
            public int Value { get; set; }
            public string Label { get; set; }
            public RarityItem(int v, string l) { Value = v; Label = l; }
            public override string ToString() { return Label; }
        }
    }
}
