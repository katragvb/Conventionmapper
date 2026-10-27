using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ConventionMapper.Testing
{
    /// <summary>
    /// File snapshots: a missing file is created and the check fails once so it gets reviewed and committed; a different file
    /// fails with a line diff; with CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1 files are rewritten. Line endings and trailing spaces
    /// are ignored. The same rules as the generated DataFlowReport.AssertMatches.
    /// </summary>
    internal static class Snapshots
    {
        public const string UpdateVariable = "CONVENTIONMAPPER_UPDATE_SNAPSHOTS";

        public static bool Updating
        {
            get
            {
                var value = Environment.GetEnvironmentVariable(UpdateVariable);
                return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>A relative path starts at the folder of the calling source file (falls back to the working directory).</summary>
        public static string Resolve(string path, string callerFilePath)
        {
            if (Path.IsPathRooted(path)) return path;
            var folder = string.IsNullOrEmpty(callerFilePath) ? null : Path.GetDirectoryName(callerFilePath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(folder!, path));
        }

        /// <summary>A file name from a key such as "Mappings.ToDto(Order, string?)".</summary>
        public static string FileName(string key, string extension)
        {
            var chars = key.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '.' && chars[i] != '_' && chars[i] != '-' && chars[i] != '(' && chars[i] != ')' &&
                    chars[i] != ',' && chars[i] != ' ')
                    chars[i] = '_';
            return new string(chars).Replace(", ", ",").Replace(' ', '_') + extension;
        }

        /// <summary>Compares <paramref name="file"/> with <paramref name="content"/>; adds a failure description to <paramref name="failures"/>.</summary>
        public static void Check(string file, string content, string what, List<string> failures)
        {
            var expected = Normalize(content);
            var exists = File.Exists(file);
            var actual = exists ? Normalize(File.ReadAllText(file)) : null;
            if (actual == expected) return;
            if (Updating || !exists)
            {
                var folder = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(file, expected);
            }
            if (Updating) return;
            failures.Add(!exists
                ? "Created " + what + " snapshot " + file + ": review it and commit it."
                : what.Substring(0, 1).ToUpperInvariant() + what.Substring(1) + " changed: " + file + "\n" + Diff(actual!, expected) +
                  "\nIf the change is intended, update the file (run the tests with " + UpdateVariable + "=1) and commit it.");
        }

        public static string Normalize(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder(text.Length);
            foreach (var line in lines) sb.Append(line.TrimEnd()).Append('\n');
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        /// <summary>Line diff of the committed file (-) against the build (+), with a line of context around the change.</summary>
        public static string Diff(string oldText, string newText)
        {
            var a = oldText.Split('\n');
            var b = newText.Split('\n');
            int start = 0, endA = a.Length, endB = b.Length;
            while (start < endA && start < endB && a[start] == b[start]) start++;
            while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
            var n = endA - start;
            var m = endB - start;
            var sb = new StringBuilder();
            if (start > 0) sb.Append("  ").Append(a[start - 1]).Append('\n');
            if ((long)n * m > 4000000L)
            {
                for (var i = start; i < endA && i < start + 40; i++) sb.Append("- ").Append(a[i]).Append('\n');
                for (var i = start; i < endB && i < start + 40; i++) sb.Append("+ ").Append(b[i]).Append('\n');
                return sb.ToString();
            }
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0, shown = 0;
            while ((x < n || y < m) && shown < 200)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { sb.Append("  ").Append(a[start + x]).Append('\n'); x++; y++; }
                else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) { sb.Append("+ ").Append(b[start + y]).Append('\n'); y++; }
                else { sb.Append("- ").Append(a[start + x]).Append('\n'); x++; }
                shown++;
            }
            if (endA < a.Length && shown < 200) sb.Append("  ").Append(a[endA]).Append('\n');
            return sb.ToString();
        }
    }
}
