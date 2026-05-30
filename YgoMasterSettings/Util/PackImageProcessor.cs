using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace YgoMasterSettings.Util
{
    // Pipeline de geração de imagens pra pack do shop. Input: 1 imagem
    // arbitrária (qualquer tamanho/proporção, PNG/JPG). Output: 3
    // variantes nas pastas que o cliente lê.
    //
    // Mirror do que fetch_pack_images.py fazia, mas em C# usando
    // System.Drawing (sem deps externas). Resize de qualidade alta
    // via InterpolationMode.HighQualityBicubic (equivalente próximo
    // ao LANCZOS do PIL).
    //
    // Output:
    //  HD          → ClientData/Images/CardPack/highend_hd/tcg/<id>.png
    //  SD          → ClientData/Images/CardPack/SD/tcg/<id>.png       (max 256)
    //  HighlightT. → ClientData/Images/Shop/HighlightThumbs/tcg/<id>.png (1024×578)
    //
    // A "imageId" é o nome SEM extensão (ex: "set11101000") — bate
    // com o que vai no `packImage` field do Shop.json.
    static class PackImageProcessor
    {
        const int SD_MAX_DIM = 256;
        const int HIGHLIGHT_W = 1024;
        const int HIGHLIGHT_H = 578;
        static readonly Color HIGHLIGHT_BG = Color.FromArgb(255, 12, 14, 20);

        public class Result
        {
            public string HdPath;
            public string SdPath;
            public string HighlightPath;
        }

        // Gera as 3 variantes a partir de uma imagem source no disco.
        // dataDir = DataLE root. imageId = nome sem extensão.
        public static Result ProcessFromFile(string dataDir, string sourcePath, string imageId)
        {
            using (Bitmap src = new Bitmap(sourcePath))
                return ProcessFromBitmap(dataDir, src, imageId);
        }

        public static Result ProcessFromBitmap(string dataDir, Bitmap src, string imageId)
        {
            if (string.IsNullOrEmpty(imageId))
                throw new ArgumentException("imageId vazio", "imageId");

            string hdDir   = Path.Combine(dataDir, "ClientData", "Images", "CardPack", "highend_hd", "tcg");
            string sdDir   = Path.Combine(dataDir, "ClientData", "Images", "CardPack", "SD",         "tcg");
            string hlDir   = Path.Combine(dataDir, "ClientData", "Images", "Shop",     "HighlightThumbs", "tcg");
            Directory.CreateDirectory(hdDir);
            Directory.CreateDirectory(sdDir);
            Directory.CreateDirectory(hlDir);

            string hdPath = Path.Combine(hdDir, imageId + ".png");
            string sdPath = Path.Combine(sdDir, imageId + ".png");
            string hlPath = Path.Combine(hlDir, imageId + ".png");

            // HD = copy direto do source como PNG (re-encoda; perda zero
            // do conteúdo, ganha consistência de formato)
            using (Bitmap hd = new Bitmap(src))
                hd.Save(hdPath, ImageFormat.Png);

            // SD = thumbnail máx 256 (preserve aspect)
            using (Bitmap sd = ResizeWithin(src, SD_MAX_DIM, SD_MAX_DIM))
                sd.Save(sdPath, ImageFormat.Png);

            // HighlightThumb = banner 1024x578 com arte centralizada
            // em canvas dark
            using (Bitmap hl = MakeHighlightBanner(src))
                hl.Save(hlPath, ImageFormat.Png);

            return new Result { HdPath = hdPath, SdPath = sdPath, HighlightPath = hlPath };
        }

        // Resize que cabe dentro de (maxW × maxH) preservando aspect.
        // Equivalente ao img.thumbnail() do PIL.
        static Bitmap ResizeWithin(Bitmap src, int maxW, int maxH)
        {
            double rw = (double)maxW / src.Width;
            double rh = (double)maxH / src.Height;
            double r = Math.Min(rw, rh);
            if (r >= 1.0) r = 1.0;   // não upscale
            int newW = Math.Max(1, (int)Math.Round(src.Width * r));
            int newH = Math.Max(1, (int)Math.Round(src.Height * r));
            return HighQualityResize(src, newW, newH);
        }

        // Compõe o banner horizontal: arte do pack escalada por altura
        // (fit pela altura, mantém aspect), centralizada num canvas
        // 1024×578 com BG dark.
        static Bitmap MakeHighlightBanner(Bitmap src)
        {
            double scale = (double)HIGHLIGHT_H / Math.Max(1, src.Height);
            int newW = Math.Max(1, (int)Math.Round(src.Width * scale));
            int newH = HIGHLIGHT_H;
            using (Bitmap scaled = HighQualityResize(src, newW, newH))
            {
                Bitmap canvas = new Bitmap(HIGHLIGHT_W, HIGHLIGHT_H, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(HIGHLIGHT_BG);
                    int x = (HIGHLIGHT_W - newW) / 2;
                    g.DrawImage(scaled, x, 0, newW, newH);
                }
                return canvas;
            }
        }

        // Resize com qualidade alta (próximo do LANCZOS do PIL).
        // HighQualityBicubic é o melhor que GDI+ oferece sem libs extras.
        static Bitmap HighQualityResize(Bitmap src, int w, int h)
        {
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode      = SmoothingMode.HighQuality;
                g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
                using (ImageAttributes attrs = new ImageAttributes())
                {
                    // Wrap mode tile-flip-XY evita halo nos bordas
                    attrs.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                }
            }
            return dst;
        }

        // Status detalhado de cada variante — usado pela UI pra mostrar
        // warning quando incompleto e oferecer "Regenerar".
        public class Status
        {
            public bool HasHd;
            public bool HasSd;
            public bool HasHl;
            public bool IsComplete { get { return HasHd && HasSd && HasHl; } }
            public bool HasAny     { get { return HasHd || HasSd || HasHl; } }
            // Texto compacto pra UI ("HD ✓ · SD ✓ · HL ✗")
            public string Summary()
            {
                return "HD " + (HasHd ? "✓" : "✗") +
                       " · SD " + (HasSd ? "✓" : "✗") +
                       " · HL " + (HasHl ? "✓" : "✗");
            }
        }

        public static Status GetStatus(string dataDir, string imageId)
        {
            Status s = new Status();
            if (string.IsNullOrEmpty(imageId)) return s;
            string baseDir = Path.Combine(dataDir, "ClientData", "Images");
            string hdDir = Path.Combine(baseDir, "CardPack", "highend_hd",      "tcg");
            string sdDir = Path.Combine(baseDir, "CardPack", "SD",              "tcg");
            string hlDir = Path.Combine(baseDir, "Shop",     "HighlightThumbs", "tcg");
            s.HasHd = File.Exists(Path.Combine(hdDir, imageId + ".png"))
                   || File.Exists(Path.Combine(hdDir, imageId + ".jpg"));
            s.HasSd = File.Exists(Path.Combine(sdDir, imageId + ".png"))
                   || File.Exists(Path.Combine(sdDir, imageId + ".jpg"));
            s.HasHl = File.Exists(Path.Combine(hlDir, imageId + ".png"));
            return s;
        }

        // Compat: ainda usado pela grid do ShopPacksSubTab
        public static bool AllVariantsExist(string dataDir, string imageId)
        {
            return GetStatus(dataDir, imageId).IsComplete;
        }

        // Regenera todas as 3 variantes pra um imageId, usando a melhor
        // source disponível (HD > SD > HighlightThumb). Util pra packs
        // antigos do Goat que só têm SD — gera HD (upscale) + HL a
        // partir dela. Qualidade do HD vai depender da source: se SD
        // (256px), HD vai ser 256px também (sem ganho real, mas a
        // estrutura existe pra cliente carregar).
        public static Result RegenerateFromBest(string dataDir, string imageId)
        {
            string srcPath = AnyImagePathOf(dataDir, imageId);
            if (srcPath == null)
                throw new FileNotFoundException(
                    "Nenhuma imagem encontrada pra " + imageId);
            return ProcessFromFile(dataDir, srcPath, imageId);
        }

        public static string HdPathOf(string dataDir, string imageId)
        {
            string png = Path.Combine(dataDir, "ClientData", "Images", "CardPack", "highend_hd", "tcg", imageId + ".png");
            if (File.Exists(png)) return png;
            string jpg = Path.Combine(dataDir, "ClientData", "Images", "CardPack", "highend_hd", "tcg", imageId + ".jpg");
            return File.Exists(jpg) ? jpg : png;   // returns expected path mesmo se missing
        }

        // Busca a melhor imagem disponível pra preview, em ordem de
        // preferência: HD → SD → HighlightThumb. Retorna null se
        // nenhuma das 3 existe. Útil pra packs antigos do Goat que só
        // têm uma das variantes.
        public static string AnyImagePathOf(string dataDir, string imageId)
        {
            if (string.IsNullOrEmpty(imageId)) return null;
            string baseDir = Path.Combine(dataDir, "ClientData", "Images");
            string[] candidates =
            {
                Path.Combine(baseDir, "CardPack", "highend_hd",      "tcg", imageId + ".png"),
                Path.Combine(baseDir, "CardPack", "highend_hd",      "tcg", imageId + ".jpg"),
                Path.Combine(baseDir, "CardPack", "SD",              "tcg", imageId + ".png"),
                Path.Combine(baseDir, "CardPack", "SD",              "tcg", imageId + ".jpg"),
                Path.Combine(baseDir, "Shop",     "HighlightThumbs", "tcg", imageId + ".png"),
            };
            foreach (string p in candidates)
                if (File.Exists(p)) return p;
            return null;
        }
    }
}
