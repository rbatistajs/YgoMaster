using System.Drawing;
using System.Windows.Forms;
using YgoMasterSettings.Tabs;

namespace YgoMasterSettings
{
    // Shell — TabControl com 1 tab por feature. Por enquanto só CardList;
    // tabs adicionais (gates, shop) serão portadas depois conforme o
    // roguelike for ganhando ferramentas próprias.
    class MainWindow : Form
    {
        public MainWindow()
        {
            Text = "YgoMaster Settings — Roguelike";
            ClientSize = new Size(1280, 800);
            MinimumSize = new Size(960, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;

            TabControl tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 6),
            };

            tabs.TabPages.Add(MakeTab("Card List", new CardListTab()));
            tabs.TabPages.Add(MakeTab("Shop", new ShopTab()));

            Controls.Add(tabs);
            Theme.Apply(this);
        }

        // Wrap qualquer UserControl numa TabPage.
        static TabPage MakeTab(string title, UserControl body)
        {
            TabPage page = new TabPage(title) { Padding = new Padding(8) };
            body.Dock = DockStyle.Fill;
            page.Controls.Add(body);
            return page;
        }
    }
}
