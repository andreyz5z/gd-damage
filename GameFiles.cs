// Readers for the two game data formats needed to turn item names into record
// paths: .arz (the record database) and .arc (the localized text archives).
// Both store their payload as raw LZ4 blocks.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc
{
    // LZ4 block format — no frame header, sizes come from the containing table.
    public static class Lz4
    {
        public static byte[] Decode(byte[] src, int start, int compressedLength, int decompressedLength)
        {
            byte[] dst = new byte[decompressedLength];
            int ip = start, inEnd = start + compressedLength, op = 0;
            while (ip < inEnd)
            {
                int token = src[ip++];
                int literals = token >> 4;
                if (literals == 15) { int b; do { b = src[ip++]; literals += b; } while (b == 255); }
                Buffer.BlockCopy(src, ip, dst, op, literals);
                ip += literals; op += literals;
                if (ip >= inEnd) break;
                int offset = src[ip] | (src[ip + 1] << 8); ip += 2;
                int match = token & 0x0F;
                if (match == 15) { int b; do { b = src[ip++]; match += b; } while (b == 255); }
                match += 4;
                int from = op - offset;
                for (int i = 0; i < match; i++) dst[op++] = dst[from++];
            }
            return dst;
        }
    }

    // One record of the game database, reduced to the fields an editor needs.
    public class DbRecord
    {
        public string Path = "";
        public string Class = "";
        public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string name)
        {
            string v;
            return Fields.TryGetValue(name, out v) ? v : null;
        }

        public int GetInt(string name)
        {
            int v;
            return int.TryParse(Get(name), out v) ? v : 0;
        }
    }

    public class Arz
    {
        // Walks every record whose path passes the filter and hands it to the sink.
        // Records outside the filter are never decompressed, which keeps a full
        // scan of the 55 MB base database down to a couple of seconds.
        public static void ForEachRecord(string path, Predicate<string> pathFilter, Action<DbRecord> sink)
        {
            byte[] d = File.ReadAllBytes(path);
            int recordStart = (int)BitConverter.ToUInt32(d, 4);
            int recordCount = (int)BitConverter.ToUInt32(d, 12);
            int stringStart = (int)BitConverter.ToUInt32(d, 16);

            int p = stringStart;
            int stringCount = (int)BitConverter.ToUInt32(d, p); p += 4;
            string[] strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
            {
                int len = BitConverter.ToInt32(d, p); p += 4;
                strings[i] = Encoding.ASCII.GetString(d, p, len); p += len;
            }

            p = recordStart;
            for (int i = 0; i < recordCount; i++)
            {
                int nameIndex = (int)BitConverter.ToUInt32(d, p); p += 4;
                int classLength = BitConverter.ToInt32(d, p); p += 4;
                string cls = Encoding.ASCII.GetString(d, p, classLength); p += classLength;
                int offset = (int)BitConverter.ToUInt32(d, p); p += 4;
                int compressed = (int)BitConverter.ToUInt32(d, p); p += 4;
                int decompressed = (int)BitConverter.ToUInt32(d, p); p += 4;
                p += 8; // file time

                string recordPath = strings[nameIndex];
                if (pathFilter != null && !pathFilter(recordPath)) continue;

                DbRecord rec = new DbRecord();
                rec.Path = recordPath;
                rec.Class = cls;
                byte[] raw = Lz4.Decode(d, 24 + offset, compressed, decompressed);
                int q = 0;
                while (q + 8 <= raw.Length)
                {
                    int type = BitConverter.ToUInt16(raw, q);
                    int count = BitConverter.ToUInt16(raw, q + 2);
                    int field = (int)BitConverter.ToUInt32(raw, q + 4);
                    q += 8;
                    StringBuilder sb = new StringBuilder();
                    for (int k = 0; k < count; k++)
                    {
                        if (k > 0) sb.Append(';');
                        if (type == 1) sb.Append(BitConverter.ToSingle(raw, q).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        else if (type == 2) sb.Append(strings[BitConverter.ToUInt32(raw, q)]);
                        else sb.Append(BitConverter.ToInt32(raw, q));
                        q += 4;
                    }
                    rec.Fields[strings[field]] = sb.ToString();
                }
                sink(rec);
            }
        }
    }

    public class Arc
    {
        // Returns the contents of every file in the archive, keyed by name.
        public static Dictionary<string, string> ReadTextFiles(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 28 || d[0] != 'A' || d[1] != 'R' || d[2] != 'C') return result;

            int entryCount = (int)BitConverter.ToUInt32(d, 8);
            int partCount = (int)BitConverter.ToUInt32(d, 12);
            int stringTableSize = (int)BitConverter.ToUInt32(d, 20);
            int partTable = (int)BitConverter.ToUInt32(d, 24);
            int stringTable = partTable + partCount * 12;
            int entryTable = stringTable + stringTableSize;

            for (int i = 0; i < entryCount; i++)
            {
                int e = entryTable + i * 44;
                int decompressedSize = (int)BitConverter.ToUInt32(d, e + 12);
                int parts = (int)BitConverter.ToUInt32(d, e + 28);
                int firstPart = (int)BitConverter.ToUInt32(d, e + 32);
                int nameLength = (int)BitConverter.ToUInt32(d, e + 36);
                int nameOffset = (int)BitConverter.ToUInt32(d, e + 40);
                string name = Encoding.ASCII.GetString(d, stringTable + nameOffset, nameLength);

                byte[] content = new byte[decompressedSize];
                int op = 0;
                for (int k = 0; k < parts; k++)
                {
                    int pt = partTable + (firstPart + k) * 12;
                    int offset = (int)BitConverter.ToUInt32(d, pt);
                    int compressed = (int)BitConverter.ToUInt32(d, pt + 4);
                    int decompressed = (int)BitConverter.ToUInt32(d, pt + 8);
                    if (compressed == decompressed) Buffer.BlockCopy(d, offset, content, op, decompressed);
                    else Buffer.BlockCopy(Lz4.Decode(d, offset, compressed, decompressed), 0, content, op, decompressed);
                    op += decompressed;
                }
                result[name] = Encoding.UTF8.GetString(content);
            }
            return result;
        }
    }
}
