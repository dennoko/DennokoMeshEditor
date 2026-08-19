using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal static class DenMeshEditorLocalization
    {
        private const string PrefsKey = "Dennokoworks.DenMeshEditor.Language";
        private const string ResourceFileName = "denmesh_localization";

        public enum LanguageCode
        {
            JA,
            EN
        }

        [Serializable]
        private class LocalizationEntry
        {
            public string key;
            public string ja;
            public string en;
        }

        [Serializable]
        private class LocalizationData
        {
            public LocalizationEntry[] entries;
        }

        private static LanguageCode? _currentLanguage;
        private static Dictionary<string, LocalizationEntry> _dictionary;

        public static event Action OnLanguageChanged;

        public static LanguageCode CurrentLanguage
        {
            get
            {
                if (!_currentLanguage.HasValue)
                {
                    var saved = EditorPrefs.GetString(PrefsKey, "ja");
                    _currentLanguage = string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase)
                        ? LanguageCode.EN
                        : LanguageCode.JA;
                }
                return _currentLanguage.Value;
            }
            set
            {
                if (_currentLanguage == value) return;
                _currentLanguage = value;
                EditorPrefs.SetString(PrefsKey, value == LanguageCode.EN ? "en" : "ja");
                OnLanguageChanged?.Invoke();
                SceneView.RepaintAll();
            }
        }

        public static bool IsJapanese => CurrentLanguage == LanguageCode.JA;

        public static string ButtonText => IsJapanese ? "EN" : "JA";

        public static string ButtonTooltip => IsJapanese ? "Switch language to English" : "言語を日本語に切り替え";

        public static void ToggleLanguage()
        {
            CurrentLanguage = IsJapanese ? LanguageCode.EN : LanguageCode.JA;
        }

        public static string Tr(string key)
        {
            EnsureLoaded();
            if (_dictionary != null && _dictionary.TryGetValue(key, out var entry))
            {
                var text = CurrentLanguage == LanguageCode.EN ? entry.en : entry.ja;
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            var format = Tr(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        private static void EnsureLoaded()
        {
            if (_dictionary != null && _dictionary.Count > 0) return;

            var jsonText = LoadJsonText();
            if (string.IsNullOrEmpty(jsonText))
            {
                return;
            }

            var dict = new Dictionary<string, LocalizationEntry>();
            try
            {
                var data = JsonUtility.FromJson<LocalizationData>(jsonText);
                if (data?.entries != null)
                {
                    foreach (var entry in data.entries)
                    {
                        if (!string.IsNullOrEmpty(entry.key))
                        {
                            dict[entry.key] = entry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DennokoMeshEditor] Failed to parse localization resource: {ex.Message}");
            }

            if (dict.Count > 0)
            {
                _dictionary = dict;
            }
        }

        private static string LoadJsonText()
        {
            // 1. AssetDatabase FindAssets (名称のみ)
            var guids = AssetDatabase.FindAssets(ResourceFileName);
            if (guids != null && guids.Length > 0)
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                        if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
                        {
                            return textAsset.text;
                        }
                    }
                }
            }

            // 2. Direct AssetDatabase path
            var knownAssetPath = "Assets/dennokoworks/DennokoMeshEditor/Editor/Localization/denmesh_localization.json";
            var directAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(knownAssetPath);
            if (directAsset != null && !string.IsNullOrEmpty(directAsset.text))
            {
                return directAsset.text;
            }

            // 3. File system search in Assets/
            try
            {
                var files = System.IO.Directory.GetFiles(Application.dataPath, "denmesh_localization.json", System.IO.SearchOption.AllDirectories);
                if (files != null && files.Length > 0)
                {
                    return System.IO.File.ReadAllText(files[0]);
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        internal static void ReloadResource()
        {
            _dictionary = null;
            EnsureLoaded();
        }
    }
}
