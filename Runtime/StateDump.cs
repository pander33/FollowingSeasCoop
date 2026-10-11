using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// Everything this copy holds of the shared world, one line a thing, written so that the files
    /// of two copies can be compared line by line (<c>Tools/Compare-Copies.ps1</c>). A line is
    /// <c>section, key, tolerance, value</c> separated by tabs; a tolerance of <c>-</c> marks a line
    /// that is only there to be read (it differs between copies by its nature).
    /// </summary>
    internal sealed class StateDump
    {
        private readonly List<string> _lines = new List<string>();

        public int Count => _lines.Count;

        /// <summary>A value that must be the same on every copy; numbers in it may differ by the tolerance.</summary>
        public void Line(string section, string key, string value, float tolerance = 0.02f)
        {
            _lines.Add(section + "\t" + Clean(key) + "\t" + tolerance.ToString("0.###", CultureInfo.InvariantCulture) + "\t" + Clean(value));
        }

        /// <summary>A value that is this copy's own.</summary>
        public void Info(string section, string key, string value)
        {
            _lines.Add(section + "\t" + Clean(key) + "\t-\t" + Clean(value));
        }

        public static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        public void Write(string path, string header)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(header).Append("\r\n");
            foreach (string line in _lines) sb.Append(line).Append("\r\n");
            // Written whole and then put in place: the comparing script never reads half a file.
            string temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
        }
    }
}
