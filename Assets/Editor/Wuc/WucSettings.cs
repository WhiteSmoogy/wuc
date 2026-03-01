using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Wuc
{
    [FilePath("ProjectSettings/WucSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class WucSettings : ScriptableSingleton<WucSettings>
    {
        [SerializeField] private int _portRangeStart = 23557;
        [SerializeField] private int _portRangeEnd = 23657;
        [SerializeField] private string _projectIdOverride = string.Empty;

        public int PortRangeStart => _portRangeStart;
        public int PortRangeEnd => _portRangeEnd;
        public string ProjectIdOverride => _projectIdOverride ?? string.Empty;

        public static WucSettings LoadOrCreate()
        {
            var settings = instance;
            settings.EnsureDefaults();
            return settings;
        }

        public string ResolveProjectId(string projectPath)
        {
            var overrideId = ProjectIdOverride.Trim();
            if (!string.IsNullOrEmpty(overrideId))
                return overrideId;

            return BuildProjectIdFromPath(projectPath);
        }

        public void UpdateValues(int portRangeStart, int portRangeEnd, string projectIdOverride)
        {
            var normalizedStart = Mathf.Max(1, portRangeStart);
            var normalizedEnd = Mathf.Max(normalizedStart, portRangeEnd);
            var normalizedOverride = (projectIdOverride ?? string.Empty).Trim();

            var changed = normalizedStart != _portRangeStart
                || normalizedEnd != _portRangeEnd
                || normalizedOverride != (_projectIdOverride ?? string.Empty);

            if (!changed)
                return;

            _portRangeStart = normalizedStart;
            _portRangeEnd = normalizedEnd;
            _projectIdOverride = normalizedOverride;
            Save(true);
        }

        private void EnsureDefaults()
        {
            var changed = false;

            if (_portRangeStart <= 0)
            {
                _portRangeStart = 23557;
                changed = true;
            }

            if (_portRangeEnd < _portRangeStart)
            {
                _portRangeEnd = _portRangeStart + 100;
                changed = true;
            }

            if (changed)
                Save(true);
        }

        internal static string BuildProjectIdFromPath(string projectPath)
        {
            var normalized = NormalizeProjectPath(projectPath);
            var bytes = Encoding.UTF8.GetBytes(normalized);
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2 + 7);
            sb.Append("sha256:");
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        internal static string NormalizeProjectPath(string path)
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (Application.platform == RuntimePlatform.WindowsEditor)
                fullPath = fullPath.ToLowerInvariant();

            return fullPath;
        }
    }
}
