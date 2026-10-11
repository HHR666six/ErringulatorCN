using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ErringulatorCN.Core
{
    /// <summary>置换与随机工具。</summary>
    internal static class Permute
    {
        /// <summary>把任意文本种子稳定地转成 32 位整数（跨运行、跨机器一致）。</summary>
        public static int SeedToInt(string seed)
        {
            if (string.IsNullOrEmpty(seed)) seed = "";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return BitConverter.ToInt32(hash, 0);
        }

        /// <summary>生成一个随机种子文本（小写字母 + 数字）。</summary>
        public static string NewSeed()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            var sb = new StringBuilder(10);
            using var rng = RandomNumberGenerator.Create();
            byte[] buf = new byte[10];
            rng.GetBytes(buf);
            foreach (byte b in buf) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        /// <summary>Fisher-Yates 洗牌。</summary>
        public static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = 0; i < list.Count - 1; i++)
            {
                int r = rng.Next(i, list.Count);
                (list[r], list[i]) = (list[i], list[r]);
            }
        }

        /// <summary>
        /// 生成"来源索引"置换：result[i] 表示第 i 个条目应该取第 result[i] 个条目的效果。
        /// </summary>
        public static int[] BuildPermutation(int count, ErringMode mode, int shiftAmount, Random rng)
        {
            var perm = new int[count];
            for (int i = 0; i < count; i++) perm[i] = i;

            switch (mode)
            {
                case ErringMode.Reverse:
                    Array.Reverse(perm);
                    break;

                case ErringMode.Shift:
                    {
                        int k = ((shiftAmount % count) + count) % count;
                        for (int i = 0; i < count; i++) perm[i] = (i + k) % count;
                        break;
                    }

                default:
                    Shuffle(perm, rng);
                    break;
            }
            return perm;
        }
    }
}
