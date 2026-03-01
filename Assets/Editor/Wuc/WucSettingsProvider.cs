using System.IO;
using UnityEditor;
using UnityEngine;

namespace Wuc
{
    internal static class WucSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            var provider = new SettingsProvider("Project/Wuc", SettingsScope.Project)
            {
                label = "Wuc",
                guiHandler = _ => DrawSettingsGui(),
                keywords = new[] { "wuc", "port", "projectId", "unity", "editor" },
            };
            return provider;
        }

        private static void DrawSettingsGui()
        {
            var settings = WucSettings.LoadOrCreate();

            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Wuc binds the first free port inside the configured range. " +
                "Changes take effect after Unity reload/restart.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            var start = EditorGUILayout.IntField("Port Range Start", settings.PortRangeStart);
            var end = EditorGUILayout.IntField("Port Range End", settings.PortRangeEnd);
            var overrideId = EditorGUILayout.TextField("Project ID Override", settings.ProjectIdOverride);
            if (EditorGUI.EndChangeCheck())
                settings.UpdateValues(start, end, overrideId);

            var projectPath = WucSettings.NormalizeProjectPath(Path.Combine(Application.dataPath, ".."));
            var effectiveProjectId = settings.ResolveProjectId(projectPath);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Resolved Project ID", effectiveProjectId);
            EditorGUILayout.LabelField("Registry Directory", Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".wuc",
                "instances"));
        }
    }
}
