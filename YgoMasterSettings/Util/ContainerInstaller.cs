using System;
using System.IO;
using System.IO.Compression;

namespace YgoMasterSettings.Util
{
    // Replica em C# o que `scripts/install_carddata_to_game.py` faz:
    // pega .bytes decifrados do DataLE/CardData, encripta in-memory (zlib
    // compress + XOR com key 0x3D — mesmo do Encrypter batch da pasta
    // Translating/), e copia pros containers Unity em
    // <game install>/LocalData/eb10c28d/0000/<bucket>/<hash>.
    //
    // Game install é resolvido como o pai do install do Goat (que é o
    // pai do Settings.exe → <game>\YgoMasterLE - Goat\YgoMasterSettings.exe).
    //
    // Containers conhecidos (CONTAINER_MAP.md):
    //   CARD_Same.bytes (MD/)     → 8e/8e63fc3d  (EN; same não muda por idioma)
    //   CARD_Desc.bytes (en-US/)  → 4a/4af912e3  (PT-BR)
    //   CARD_Name.bytes (en-US/)  → 1f/1f6fc0b1  (PT-BR)
    static class ContainerInstaller
    {
        const int CRYPTO_KEY = 0x3D;
        const string CONTAINER_ROOT_REL = @"LocalData\eb10c28d\0000";

        // (relPath dentro do CardData, bucket, hash, label)
        // Indx é PAR de Name/Desc — guarda offsets de cada cid pra dentro
        // dos buffers Name/Desc. Se Name/Desc são reescritos mas Indx
        // fica do install vanilla, os offsets ficam dessincronizados e
        // o cliente renderiza texto cortado (lê do meio do buffer novo).
        static readonly (string Rel, string Bucket, string Hash, string Label)[] Targets =
        {
            (@"MD\CARD_Same.bytes",    "8e", "8e63fc3d", "same"),
            (@"en-US\CARD_Desc.bytes", "4a", "4af912e3", "desc"),
            (@"en-US\CARD_Name.bytes", "1f", "1f6fc0b1", "name"),
            (@"en-US\CARD_Indx.bytes", "3b", "3b2068a5", "indx"),
            (@"#\CARD_Prop.bytes",     "ee", "ee227b5d", "prop"),
        };

        public class Result
        {
            public int OkCount;
            public int FailCount;
            public string BackupDir;
        }

        // Resolve game install: <Program.DataDir>\..\.. → "Yu-Gi-Oh! Master Duel"
        static string GetGameInstallDir(string dataDir)
        {
            string goatInstall = Path.GetDirectoryName(dataDir);       // YgoMasterLE - Goat
            return Path.GetDirectoryName(goatInstall);                 // Yu-Gi-Oh!  Master Duel
        }

        public static Result Install(string dataDir, Action<string> log = null)
        {
            Result r = new Result();
            string gameInstall = GetGameInstallDir(dataDir);
            string containerRoot = Path.Combine(gameInstall, CONTAINER_ROOT_REL);
            string cardDataDir   = Path.Combine(dataDir, "CardData");

            if (!Directory.Exists(containerRoot))
            {
                if (log != null) log("Container root não existe: " + containerRoot);
                return r;
            }

            // Backup nesta run (em DataLE/_bkp/containers_<ts>)
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkpDir = Path.Combine(dataDir, "_bkp", "containers_" + ts);
            Directory.CreateDirectory(bkpDir);
            r.BackupDir = bkpDir;

            foreach (var t in Targets)
            {
                string src = Path.Combine(cardDataDir, t.Rel);
                string dst = Path.Combine(containerRoot, t.Bucket, t.Hash);
                try
                {
                    if (!File.Exists(src))
                    {
                        if (log != null) log("SKIP " + t.Label + ": source missing");
                        r.FailCount++;
                        continue;
                    }
                    byte[] raw = File.ReadAllBytes(src);
                    byte[] enc = Encrypt(raw);

                    if (File.Exists(dst))
                        File.Copy(dst, Path.Combine(bkpDir, t.Hash), overwrite: true);

                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    string tmp = dst + ".tmp";
                    File.WriteAllBytes(tmp, enc);
                    if (File.Exists(dst)) File.Replace(tmp, dst, null, ignoreMetadataErrors: true);
                    else                  File.Move(tmp, dst);

                    if (log != null) log(t.Label + " ok (" + (raw.Length/1024) + "KB → " + (enc.Length/1024) + "KB)");
                    r.OkCount++;
                }
                catch (Exception ex)
                {
                    if (log != null) log("FAIL " + t.Label + ": " + ex.Message);
                    r.FailCount++;
                }
            }
            return r;
        }

        // zlib compress + XOR — mesmo algoritmo do Encrypter batch.py
        static byte[] Encrypt(byte[] raw)
        {
            // 1) Compress (zlib stream com header)
            byte[] compressed;
            using (MemoryStream ms = new MemoryStream())
            {
                // Header zlib: 0x78 0x9C (deflate default level)
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    ds.Write(raw, 0, raw.Length);
                // 2) Adler32 checksum no fim (big-endian)
                uint a = Adler32(raw);
                ms.WriteByte((byte)(a >> 24));
                ms.WriteByte((byte)(a >> 16));
                ms.WriteByte((byte)(a >> 8));
                ms.WriteByte((byte)a);
                compressed = ms.ToArray();
            }
            // 3) XOR scramble
            for (int i = 0; i < compressed.Length; i++)
            {
                long v = i + CRYPTO_KEY + 0x23D;
                v *= CRYPTO_KEY;
                v ^= i % 7;
                compressed[i] ^= (byte)(v & 0xFF);
            }
            return compressed;
        }

        static uint Adler32(byte[] data)
        {
            const uint MOD = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % MOD;
                b = (b + a) % MOD;
            }
            return (b << 16) | a;
        }
    }
}
