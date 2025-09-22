using System;

namespace NTGrpcClient
{
    internal static class ComponentResolver
    {
        public static string Derive(string source, string component)
        {
            if (!string.IsNullOrWhiteSpace(component))
            {
                return component;
            }

            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "nt" : source.Trim();

            if (normalizedSource.Equals("nt", StringComparison.OrdinalIgnoreCase))
            {
                return "nt_addon";
            }

            if (normalizedSource.Equals("qt", StringComparison.OrdinalIgnoreCase))
            {
                return "qt_addon";
            }

            return "addon";
        }
    }
}
