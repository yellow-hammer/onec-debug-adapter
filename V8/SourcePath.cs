using System.Text.Json;

namespace Onec.DebugAdapter.V8
{
    /// <summary>
    /// VS Code для вкладки Git (индекс/HEAD) передаёт в DAP URI схемы git, а не путь на диске.
    /// Путь модуля берётся из query.path.
    /// </summary>
    internal static class SourcePath
    {
        /// <summary>
        /// Канонический вид пути: Git-URI приводится к пути на диске, путь к полному виду ОС.
        /// Одним ключом адресуются файл из редактора, из вкладки Git и из кэша модулей.
        /// </summary>
        public static string Canonical(string path) => Normalize(Resolve(path));

        /// <summary>Полный путь с разделителями ОС; непригодный путь остаётся как есть.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            }
        }

        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (TryRestAfterScheme(path, "git", out var rest))
                return ResolveGit(rest);

            if (TryRestAfterScheme(path, "file", out _))
                return ResolveFile(path);

            return path;
        }

        private static bool TryRestAfterScheme(string path, string scheme, out string rest)
        {
            rest = "";
            if (path.Length <= scheme.Length || path[scheme.Length] != ':')
                return false;
            if (!path.AsSpan(0, scheme.Length).Equals(scheme, StringComparison.OrdinalIgnoreCase))
                return false;

            rest = path[(scheme.Length + 1)..];
            return true;
        }

        private static string ResolveGit(string rest)
        {
            var queryIndex = rest.IndexOf('?');
            if (queryIndex >= 0)
            {
                var query = Uri.UnescapeDataString(rest[(queryIndex + 1)..]);
                if (TryPathFromGitQuery(query, out var filePath))
                    return filePath;

                rest = rest[..queryIndex];
            }

            return PathFromGitUriPath(rest);
        }

        private static bool TryPathFromGitQuery(string query, out string filePath)
        {
            filePath = "";
            try
            {
                using var document = JsonDocument.Parse(query);
                if (!document.RootElement.TryGetProperty("path", out var property))
                    return false;

                var value = property.GetString();
                if (string.IsNullOrEmpty(value))
                    return false;

                filePath = value;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string PathFromGitUriPath(string uriPath)
        {
            var decoded = Uri.UnescapeDataString(uriPath);
            if (decoded.Length >= 3 && decoded[0] == '/' && char.IsLetter(decoded[1]) && decoded[2] == ':')
                decoded = decoded[1..];

            return decoded.Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ResolveFile(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;

            return path;
        }
    }
}
