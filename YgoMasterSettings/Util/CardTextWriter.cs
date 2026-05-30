using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YgoMasterSettings.Util
{
    // Rebuild dos 3 arquivos paralelos de localização do CardData
    // (CARD_Name.bytes, CARD_Desc.bytes, CARD_Indx.bytes) aplicando
    // edits por cardId. CARD_Prop.bytes NÃO é modificado — usado só
    // pra mapear slot → cardId.
    //
    // Estratégia: full rebuild (não in-place). Itera os slots na ordem
    // original do Indx, pra cada slot resolve a string final
    // (editada por cardId via dict de edits, ou original via offset),
    // concatena nos buffers novos anotando offsets, escreve atomicamente
    // (.tmp + File.Replace) após backup.
    //
    // Edge cases tratados:
    //  - Slot com cardId=0 (placeholder) → mantém string original
    //  - Vários slots com mesmo cardId (alt-arts) → todos recebem o
    //    texto novo, sem deduplicação (cada slot ganha sua própria cópia
    //    no Name/Desc buffer — simples e safe)
    //  - Sentinel do Indx (último par) → preservado com offset = tamanho
    //    final dos buffers Name/Desc (mesmo padrão que parser detecta
    //    como EOF: position >= length)
    static class CardTextWriter
    {
        public class Edit
        {
            public string Name;   // null = não editar
            public string Desc;   // null = não editar
        }

        // Retorna o número de slots gravados em caso de sucesso.
        // Throw em IO error pra UI tratar.
        //
        // `progress` opcional recebe descrição de fase atual ("Lendo
        // arquivos…", "Reconstruindo buffers…", "Backup…", "Gravando…").
        // Usado pra atualizar ProgressBar/label da UI sem travar a tela.
        public static int Save(string dataDir, Dictionary<int, Edit> edits,
                                Action<string> progress = null)
        {
            if (progress != null) progress("Lendo CardData…");
            string indxPath = Path.Combine(dataDir, "CardData", "en-US", "CARD_Indx.bytes");
            string namePath = Path.Combine(dataDir, "CardData", "en-US", "CARD_Name.bytes");
            string descPath = Path.Combine(dataDir, "CardData", "en-US", "CARD_Desc.bytes");
            string propPath = Path.Combine(dataDir, "CardData", "#",     "CARD_Prop.bytes");
            if (!File.Exists(indxPath) || !File.Exists(namePath) || !File.Exists(descPath)
                || !File.Exists(propPath))
                throw new FileNotFoundException("CardData files missing in " + dataDir);

            // ----- ler tudo do disco -----
            List<KeyValuePair<uint, uint>> slotIndx;
            Dictionary<uint, string> namesByOff, descsByOff;
            List<int> slotCardIds;
            using (BinaryReader rd = new BinaryReader(File.OpenRead(indxPath)))
                slotIndx = ReadIndxSlots(rd);
            using (BinaryReader rd = new BinaryReader(File.OpenRead(namePath)))
                namesByOff = ReadStringsByOffset(rd);
            using (BinaryReader rd = new BinaryReader(File.OpenRead(descPath)))
                descsByOff = ReadStringsByOffset(rd);
            using (BinaryReader rd = new BinaryReader(File.OpenRead(propPath)))
                slotCardIds = ReadPropCardIds(rd, slotIndx.Count);

            // ----- rebuild buffers paralelos -----
            if (progress != null) progress("Reconstruindo buffers (" + slotIndx.Count + " slots)…");
            MemoryStream nameOut = new MemoryStream(namesByOff.Count * 32);
            MemoryStream descOut = new MemoryStream(descsByOff.Count * 128);
            MemoryStream indxOut = new MemoryStream((slotIndx.Count + 1) * 8);
            BinaryWriter indxWr = new BinaryWriter(indxOut);

            for (int slot = 0; slot < slotIndx.Count; slot++)
            {
                KeyValuePair<uint, uint> origOffs = slotIndx[slot];
                string origName, origDesc;
                namesByOff.TryGetValue(origOffs.Key,   out origName);
                descsByOff.TryGetValue(origOffs.Value, out origDesc);
                if (origName == null) origName = "";
                if (origDesc == null) origDesc = "";

                int cardId = slot < slotCardIds.Count ? slotCardIds[slot] : 0;
                Edit ed = null;
                if (cardId > 0 && edits != null) edits.TryGetValue(cardId, out ed);

                string finalName = ed != null && ed.Name != null ? ed.Name : origName;
                string finalDesc = ed != null && ed.Desc != null ? ed.Desc : origDesc;

                uint nameOffNew = (uint)nameOut.Length;
                uint descOffNew = (uint)descOut.Length;
                indxWr.Write(nameOffNew);
                indxWr.Write(descOffNew);
                WriteNullTerminatedUtf8(nameOut, finalName);
                WriteNullTerminatedUtf8(descOut, finalDesc);
            }
            // Sentinel par final — parser do YdkHelper/nosso usa
            // `position >= length` pra detectar fim, então o critério
            // é só "tem mais 8 bytes". Escrevemos um par dummy apontando
            // pro fim dos buffers (offset inválido, nunca dereferenciado).
            indxWr.Write((uint)nameOut.Length);
            indxWr.Write((uint)descOut.Length);

            // ----- backup + write atomic -----
            if (progress != null) progress("Backup em _bkp/…");
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkpDir = Path.Combine(Path.GetDirectoryName(indxPath), "_bkp");
            Directory.CreateDirectory(bkpDir);
            BackupFile(indxPath, Path.Combine(bkpDir, "CARD_Indx." + ts + ".bak.bytes"));
            BackupFile(namePath, Path.Combine(bkpDir, "CARD_Name." + ts + ".bak.bytes"));
            BackupFile(descPath, Path.Combine(bkpDir, "CARD_Desc." + ts + ".bak.bytes"));

            // Write atomic: escreve .tmp e usa File.Replace pra trocar.
            // Garante que readers nunca vejam arquivo parcialmente escrito.
            if (progress != null) progress("Gravando arquivos…");
            WriteAtomic(indxPath, indxOut.ToArray());
            WriteAtomic(namePath, nameOut.ToArray());
            WriteAtomic(descPath, descOut.ToArray());

            return slotIndx.Count;
        }

        // ----- helpers de leitura -----
        static List<KeyValuePair<uint, uint>> ReadIndxSlots(BinaryReader rd)
        {
            List<KeyValuePair<uint, uint>> slots = new List<KeyValuePair<uint, uint>>();
            while (true)
            {
                uint nameOff = rd.ReadUInt32();
                uint descOff = rd.ReadUInt32();
                if (rd.BaseStream.Position >= rd.BaseStream.Length) break;   // sentinel
                slots.Add(new KeyValuePair<uint, uint>(nameOff, descOff));
            }
            return slots;
        }

        static List<int> ReadPropCardIds(BinaryReader rd, int expectedSlots)
        {
            // Prop tem 8 bytes por slot (PropA + PropB int32). Iteramos
            // até `expectedSlots` (ou EOF, o que vier antes) — algumas
            // installs podem ter Prop maior que Indx (cards "fantasmas"
            // sem entry de texto), nesse caso só usamos os primeiros N.
            List<int> result = new List<int>(expectedSlots);
            long avail = rd.BaseStream.Length / 8;
            int n = (int)Math.Min(avail, expectedSlots);
            for (int i = 0; i < n; i++)
            {
                int propA = rd.ReadInt32();
                rd.ReadInt32();   // PropB skip
                result.Add(propA & 0xFFFF);
            }
            return result;
        }

        static Dictionary<uint, string> ReadStringsByOffset(BinaryReader rd)
        {
            Dictionary<uint, string> result = new Dictionary<uint, string>();
            long len = rd.BaseStream.Length;
            while (rd.BaseStream.Position < len)
            {
                uint offset = (uint)rd.BaseStream.Position;
                string s = ReadNullTerminatedUtf8(rd);
                result[offset] = s;
            }
            return result;
        }

        // ----- string IO -----
        // StreamReader bagunça position (lê demais pro buffer interno).
        // Lemos byte a byte aqui — mais lento mas determinístico.
        static string ReadNullTerminatedUtf8(BinaryReader rd)
        {
            List<byte> bytes = new List<byte>(32);
            while (rd.BaseStream.Position < rd.BaseStream.Length)
            {
                byte b = rd.ReadByte();
                if (b == 0) break;
                bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        static void WriteNullTerminatedUtf8(MemoryStream ms, string s)
        {
            byte[] enc = Encoding.UTF8.GetBytes(s ?? "");
            ms.Write(enc, 0, enc.Length);
            ms.WriteByte(0);
        }

        // ----- file ops -----
        static void BackupFile(string srcPath, string bkpPath)
        {
            try { File.Copy(srcPath, bkpPath, overwrite: true); }
            catch (Exception ex)
            {
                throw new IOException("Backup failed for " + srcPath + ": " + ex.Message, ex);
            }
        }

        // Write atomic via .tmp + File.Replace: garante que nenhum
        // reader vê estado parcial. Se algo der errado a meio, o
        // arquivo original permanece intacto.
        static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            try
            {
                // File.Replace requer o destino existir (que existe — é
                // o arquivo original que estamos sobrescrevendo).
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                // Fallback pra sistemas que não suportam Replace
                File.Delete(path);
                File.Move(tmp, path);
            }
        }
    }
}
