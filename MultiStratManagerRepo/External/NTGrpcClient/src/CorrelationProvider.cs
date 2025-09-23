using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NTGrpcClient
{
    public static class CorrelationProvider
    {
        private static readonly ConcurrentDictionary<string, string> Map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Get(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId)) return string.Empty;
            return Map.GetOrAdd(baseId, ComputeDeterministic);
        }

        public static void Release(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId)) return;
            string _;
            Map.TryRemove(baseId, out _);
        }

        private static string ComputeDeterministic(string baseId)
        {
            try
            {
                using (var md5 = MD5.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes(baseId);
                    var hash = md5.ComputeHash(bytes);
                    var sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString(); // 32-char lowercase hex
                }
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
