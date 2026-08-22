using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Editor
{
    // Builds one or more addressable mod groups for the active build target, then
    // exports the platform catalog files + the group's robot DLLs into Mods/<GroupName>/.
    //
    // For all three platforms in one go (each in a fresh Unity process, since switching
    // build target mid-session doesn't reliably re-import), use:
    //   Tools/build-mods-all-platforms.ps1 -Groups "NY Modpack","China Modpack"
    public class AddressablesModExporter : EditorWindow
    {
        const string RobotsRoot = "Assets/Prefabs/Reefscape/Robots/Mods";
        const string ModsOutputRoot = "Mods";

        public class ModBuildSpec
        {
            public string GroupName;
            public string Version;
            public string ZipName;
        }

        Vector2 _scroll;
        readonly HashSet<string> _selected = new HashSet<string>();
        readonly Dictionary<string, string> _versionByGroup = new Dictionary<string, string>();
        readonly Dictionary<string, string> _zipNameByGroup = new Dictionary<string, string>();

        [MenuItem("Tools/Addressables/Build And Export Mods")]
        static void Open() => GetWindow<AddressablesModExporter>("Build & Export Mods");

        void OnGUI()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                EditorGUILayout.HelpBox("No Addressables settings found.", MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Select mod groups to build:", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var group in settings.groups)
            {
                if (group == null || group.ReadOnly) continue;
                if (group.GetSchema<BundledAssetGroupSchema>() == null) continue;

                var name = group.Name;
                bool was = _selected.Contains(name);
                bool now = EditorGUILayout.ToggleLeft(name, was);
                if (now && !was) _selected.Add(name);
                if (!now && was) _selected.Remove(name);

                if (now)
                {
                    EditorGUI.indentLevel++;
                    _versionByGroup.TryGetValue(name, out var version);
                    _versionByGroup[name] = EditorGUILayout.TextField("Version (optional)", version ?? "");

                    _zipNameByGroup.TryGetValue(name, out var zipName);
                    _zipNameByGroup[name] = EditorGUILayout.TextField("Zip name override (optional)", zipName ?? "");
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Active build target: {EditorUserBuildSettings.activeBuildTarget}");

            using (new EditorGUI.DisabledScope(_selected.Count == 0))
            {
                if (GUILayout.Button("Build Selected"))
                {
                    var specs = _selected.Select(name => new ModBuildSpec
                    {
                        GroupName = name,
                        Version = _versionByGroup.TryGetValue(name, out var v) ? v : null,
                        ZipName = _zipNameByGroup.TryGetValue(name, out var z) && !string.IsNullOrWhiteSpace(z) ? z : null
                    }).ToList();
                    BuildAndExport(specs);
                }
            }
        }

        // Built-in-shaders/MonoScript bundle naming and the "shared bundle settings" group are
        // PROJECT-WIDE settings, not per-group — so each selected group gets its own, separate
        // BuildPlayerContent() call (only that group's IncludeInBuild is true) with its own naming
        // prefix. Building several groups in a single Addressables build would make them collide.
        public static bool BuildAndExport(List<ModBuildSpec> specs)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) { Debug.LogError("No Addressables settings found."); return false; }
            if (specs == null || specs.Count == 0) { Debug.LogError("No groups selected."); return false; }

            var originalIncludeInBuild = new Dictionary<AddressableAssetGroup, bool>();
            foreach (var group in settings.groups)
            {
                var schema = group?.GetSchema<BundledAssetGroupSchema>();
                if (schema != null) originalIncludeInBuild[group] = schema.IncludeInBuild;
            }

            var originalBuiltInBundleNaming = settings.BuiltInBundleNaming;
            var originalBuiltInBundleCustomNaming = settings.BuiltInBundleCustomNaming;
            var originalMonoScriptBundleNaming = settings.MonoScriptBundleNaming;
            var originalMonoScriptBundleCustomNaming = settings.MonoScriptBundleCustomNaming;
            var originalSharedBundleSettings = settings.SharedBundleSettings;
            var originalSharedBundleSettingsCustomGroupIndex = settings.SharedBundleSettingsCustomGroupIndex;

            try
            {
                bool allOk = true;
                foreach (var spec in specs)
                {
                    if (!BuildOneGroup(settings, spec))
                    {
                        allOk = false;
                        continue;
                    }
                    ExportGroup(spec.GroupName, spec.ZipName ?? spec.GroupName, spec.Version);
                }

                if (allOk) Debug.Log("Build and export complete.");
                return allOk;
            }
            finally
            {
                foreach (var kv in originalIncludeInBuild)
                {
                    var schema = kv.Key.GetSchema<BundledAssetGroupSchema>();
                    if (schema != null) schema.IncludeInBuild = kv.Value;
                }

                settings.BuiltInBundleNaming = originalBuiltInBundleNaming;
                settings.BuiltInBundleCustomNaming = originalBuiltInBundleCustomNaming;
                settings.MonoScriptBundleNaming = originalMonoScriptBundleNaming;
                settings.MonoScriptBundleCustomNaming = originalMonoScriptBundleCustomNaming;
                settings.SharedBundleSettings = originalSharedBundleSettings;
                settings.SharedBundleSettingsCustomGroupIndex = originalSharedBundleSettingsCustomGroupIndex;
            }
        }

        static bool BuildOneGroup(AddressableAssetSettings settings, ModBuildSpec spec)
        {
            int groupIndex = -1;
            for (int i = 0; i < settings.groups.Count; i++)
            {
                var group = settings.groups[i];
                if (group == null) continue;
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null) continue;

                bool selected = group.Name == spec.GroupName;
                schema.IncludeInBuild = selected;
                if (selected) groupIndex = i;
            }

            if (groupIndex < 0)
            {
                Debug.LogError($"No addressable group named '{spec.GroupName}' with a BundledAssetGroupSchema.");
                return false;
            }

            var group2 = settings.groups[groupIndex];
            var buildPath = Path.Combine(Application.dataPath, "..", ModsOutputRoot, group2.Name);
            Directory.CreateDirectory(buildPath);

            // SetVariableByName needs the NAME of a profile variable, not a raw path —
            // create it once (same convention as AddressableCustomPath.cs) then point at it.
            var variableName = $"{group2.Name}_BuildPath";
            if (!settings.profileSettings.GetVariableNames().Contains(variableName))
            {
                settings.profileSettings.CreateValue(variableName,
                    "{UnityEngine.Application.dataPath}/../" + ModsOutputRoot + "/" + group2.Name);
            }
            group2.GetSchema<BundledAssetGroupSchema>().BuildPath.SetVariableByName(settings, variableName);

            // Prefix the shared built-in-shaders/MonoScript bundles with this group's name so
            // different mods' bundles don't collide (matches the "chinamodpack" style prefix
            // already configured manually in AddressableAssetSettings.asset).
            var prefix = Regex.Replace(group2.Name, "[^a-zA-Z0-9]", "").ToLowerInvariant();
            settings.BuiltInBundleNaming = BuiltInBundleNaming.Custom;
            settings.BuiltInBundleCustomNaming = prefix;
            settings.MonoScriptBundleNaming = MonoScriptBundleNaming.Custom;
            settings.MonoScriptBundleCustomNaming = prefix;
            settings.SharedBundleSettings = SharedBundleSettings.CustomGroup;
            settings.SharedBundleSettingsCustomGroupIndex = groupIndex;

            Debug.Log($"Building addressables for: {spec.GroupName} (target: {EditorUserBuildSettings.activeBuildTarget}, prefix: {prefix})");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (!string.IsNullOrEmpty(result.Error))
            {
                Debug.LogError($"Addressables build failed for '{spec.GroupName}': {result.Error}");
                return false;
            }
            return true;
        }

        static void ExportGroup(string groupName, string zipName, string version)
        {
            var modsRoot = Path.Combine(Application.dataPath, "..", ModsOutputRoot);
            var modFolder = Path.Combine(modsRoot, groupName);
            Directory.CreateDirectory(modFolder);

            var target = EditorUserBuildSettings.activeBuildTarget;
            var osFolder = OsFolderForTarget(target);
            var aaFolder = Path.Combine(Application.dataPath, "..", "Library", "com.unity.addressables", "aa", osFolder);
            foreach (var fileName in new[] { "catalog.json", "catalog.hash", "settings.json" })
            {
                var src = Path.Combine(aaFolder, fileName);
                if (!File.Exists(src)) { Debug.LogWarning($"[{groupName}] missing {src}"); continue; }
                File.Copy(src, Path.Combine(modFolder, fileName), overwrite: true);
            }

            // Every robot in this group has its own asmdef under RobotsRoot/<GroupName>/<Team>/;
            // the asmdef's "name" field is the DLL Unity emits into Library/ScriptAssemblies/.
            var robotFolder = Path.Combine(Application.dataPath, "..", RobotsRoot, groupName);
            if (Directory.Exists(robotFolder))
            {
                var scriptAssemblies = Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies");
                foreach (var asmdefPath in Directory.GetFiles(robotFolder, "*.asmdef", SearchOption.AllDirectories))
                {
                    var asmName = ExtractAsmdefName(asmdefPath);
                    if (string.IsNullOrEmpty(asmName)) continue;

                    var dllSrc = Path.Combine(scriptAssemblies, asmName + ".dll");
                    if (!File.Exists(dllSrc)) { Debug.LogWarning($"[{groupName}] missing dll {dllSrc}"); continue; }
                    File.Copy(dllSrc, Path.Combine(modFolder, asmName + ".dll"), overwrite: true);
                }
            }
            else
            {
                Debug.LogWarning($"[{groupName}] no robot folder at {robotFolder}, skipping DLL export");
            }

            ZipAndCleanup(modFolder, modsRoot, zipName, ZipPlatformLabel(target), version);
        }

        static void ZipAndCleanup(string modFolder, string modsRoot, string zipName, string platformLabel, string version)
        {
            var archiveName = string.IsNullOrWhiteSpace(version)
                ? $"{zipName} {platformLabel}.zip"
                : $"{zipName} {version} {platformLabel}.zip";
            var zipPath = Path.Combine(modsRoot, archiveName);

            // zipName only brands the ARCHIVE FILE's name. The folder *inside* the zip must
            // stay modFolder's real name (the addressable group name) unchanged: each group's
            // LoadPath profile variable is baked into the catalog as ".../Mods/<groupName>/...",
            // so if the internal folder doesn't match the group name after extraction, the game
            // 404s trying to load the bundle from the (correct) group-name path that no longer
            // exists on disk. Renaming it to a branded zipName here broke exactly that.
            var sourceFolder = Path.GetFullPath(modFolder);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(sourceFolder, zipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
            Directory.Delete(sourceFolder, recursive: true);

            Debug.Log($"Zipped mod folder to {zipPath}");
        }

        static string ExtractAsmdefName(string path)
        {
            var json = File.ReadAllText(path);
            var match = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        static string OsFolderForTarget(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "Windows";
                case BuildTarget.StandaloneOSX:
                    return "OSX";
                case BuildTarget.StandaloneLinux64:
                    return "Linux";
                default:
                    throw new NotSupportedException($"Unsupported build target: {target}");
            }
        }

        static string ZipPlatformLabel(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "Windows";
                case BuildTarget.StandaloneOSX:
                    return "MacOS";
                case BuildTarget.StandaloneLinux64:
                    return "Linux";
                default:
                    throw new NotSupportedException($"Unsupported build target: {target}");
            }
        }

        // Command-line entry point:
        //   -executeMethod Editor.AddressablesModExporter.BuildFromCommandLine
        //   -groups "Name1|Name2"  [-versions "v2.1.0|v1.0.0"]  [-zipNames "Zip One|Zip Two"]
        // -versions and -zipNames, if given, must have the same number of |-separated
        // entries as -groups, matched by position. Use an empty entry ("Name1||Name3")
        // to skip a value for one group.
        public static void BuildFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            string GetArg(string name)
            {
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == name) return args[i + 1];
                return null;
            }

            var groupsArg = GetArg("-groups");
            if (string.IsNullOrEmpty(groupsArg))
            {
                Debug.LogError("BuildFromCommandLine: missing -groups \"Name1|Name2\" argument.");
                EditorApplication.Exit(1);
                return;
            }

            var groupNames = groupsArg.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var versions = GetArg("-versions")?.Split('|');
            var zipNames = GetArg("-zipNames")?.Split('|');

            var specs = groupNames.Select((name, i) => new ModBuildSpec
            {
                GroupName = name,
                Version = versions != null && i < versions.Length && versions[i].Length > 0 ? versions[i] : null,
                ZipName = zipNames != null && i < zipNames.Length && zipNames[i].Length > 0 ? zipNames[i] : null
            }).ToList();

            bool ok = BuildAndExport(specs);
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
