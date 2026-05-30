using System;
using System.IO;

namespace YgoMasterSettings.Util
{
    // Helper centralizado pra escrever JSON com backup + atomic write.
    // Convenção do projeto:
    //   - Backup em `<dir>/_bkp/<prefix>.YYYYMMDD_HHMMSS.bak.json` antes
    //     de qualquer write (preserva versão anterior)
    //   - Write atomic via `.tmp` + `File.Replace` (readers nunca veem
    //     estado parcial)
    //
    // Uso típico: tabs que mutam JSON files (GridGates, Shop, etc).
    // Retorna path do backup criado (vazio se arquivo destino não existia).
    static class JsonFileWriter
    {
        public static string SaveAtomic(string targetPath, string jsonContent, string backupPrefix)
        {
            string dir = Path.GetDirectoryName(targetPath);
            string bkpDir = Path.Combine(dir, "_bkp");
            Directory.CreateDirectory(bkpDir);
            string bkpPath = "";
            if (File.Exists(targetPath))
            {
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                bkpPath = Path.Combine(bkpDir, backupPrefix + "." + ts + ".bak.json");
                File.Copy(targetPath, bkpPath, overwrite: true);
            }

            // Write atomic via .tmp + Replace: garante que readers nunca
            // vejam estado parcial. Se o Replace falhar (filesystem que
            // não suporta), fallback pra Delete + Move.
            string tmp = targetPath + ".tmp";
            File.WriteAllText(tmp, jsonContent);
            try
            {
                if (File.Exists(targetPath))
                    File.Replace(tmp, targetPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, targetPath);
            }
            catch (PlatformNotSupportedException)
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tmp, targetPath);
            }
            return bkpPath;
        }
    }
}
