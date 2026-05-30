using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YgoMasterSettings
{
    // YgoMasterSettings.exe — UI nativa WinForms pra editar DataLE.
    // O exe é dropado no install dir pelo AfterTarget do csproj.
    static class Program
    {
        // Path do install (= pasta onde o exe está). Usado pra resolver
        // DataLE/*, ClientData/*, etc.
        public static string InstallRoot { get; private set; }
        public static string DataDir     { get; private set; }

        [STAThread]
        static void Main()
        {
            InstallRoot = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            DataDir = Path.Combine(InstallRoot, "DataLE");

            // DPI awareness — complementa o app.manifest. Tem que ser a
            // PRIMEIRA chamada do app (antes de EnableVisualStyles e de
            // qualquer criação de Form/Control).
            try
            {
                System.Runtime.InteropServices.Marshal.PrelinkAll(typeof(NativeDpi));
                NativeDpi.SetProcessDpiAwarenessContext(NativeDpi.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch { /* SO antigo — manifest cobre */ }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }

        // Win10+ API pra setar DPI awareness em runtime (manifest faz o
        // mesmo mas redundância garante).
        static class NativeDpi
        {
            public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        }
    }
}
