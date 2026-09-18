using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 【移行期間限定】旧方式（全体設定 showSelectionOutline を書き換えていた時代）の退避値の復元処理。
    ///
    /// 新設計ではグローバル設定の変更を行わないため、新規の抑制処理（Suppress）は廃止された。
    /// 旧バージョンでエディタが異常終了・強制終了した環境では EditorPrefs にキーが残り、
    /// Selection Outline が OFF のままになっている可能性があるため、その復元専用として残している。
    ///
    /// TODO: v1.4.0（v1.2.4 以前からの移行回収期間終了後）に本ファイルごと削除予定。
    /// </summary>
    internal static class SelectionOutline
    {
        /// <summary>旧方式における退避キー。キーが存在する＝旧版で戻し損ねた可能性がある。</summary>
        private const string LegacyBackupKey = "Dennokoworks.DenMeshEditor.SelectionOutlineBackup";
        private const string RestoreMenu = "Tools/Dennoko Mesh Editor/Restore Legacy Selection Outline";

        private static PropertyInfo _property;

        /// <summary>
        /// 共有キーの所有者は分からない。別 Unity の復元情報を消さず、復旧方法を案内する。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RestoreLeftover()
        {
            EditorApplication.delayCall += () =>
            {
                if (!EditorPrefs.HasKey(LegacyBackupKey)) return;
                Debug.LogWarning("[DennokoMeshEditor] 旧版のアウトライン退避情報が残っています。"
                    + "旧版で編集中の他の Unity を終了後、" + RestoreMenu + " から復元できます。");
            };
        }

        [MenuItem(RestoreMenu)]
        private static void RestoreFromMenu()
        {
            if (EditorUtility.DisplayDialog("選択アウトラインの復元",
                "旧版で編集中の他の Unity は終了していますか？\n"
                + "退避情報は複数の Unity で共有されています。復元すると共有の退避情報を削除します。",
                "復元", "キャンセル")) RestoreLegacyBackup();
        }

        [MenuItem(RestoreMenu, true)]
        private static bool CanRestoreFromMenu() => EditorPrefs.HasKey(LegacyBackupKey);

        /// <summary>
        /// 旧退避キーが存在する場合にのみ、AnnotationUtility.showSelectionOutline を書き戻す。
        /// 書き戻しが確認できた場合のみキーを削除する。
        /// </summary>
        internal static void RestoreLegacyBackup()
        {
            if (!EditorPrefs.HasKey(LegacyBackupKey)) return;

            var previous = EditorPrefs.GetBool(LegacyBackupKey);

            var property = ResolveProperty();
            if (property == null)
            {
                Debug.LogWarning("[DennokoMeshEditor] Selection Outline API が見つからないため復元できません。退避情報は保持します。");
                return;
            }

            try
            {
                property.SetValue(null, previous);

                // 書き込みが効いたかを読み返して確認する
                if ((bool)property.GetValue(null) != previous)
                    throw new InvalidOperationException("Selection Outline の復元を確認できませんでした。");
            }
            catch (Exception ex)
            {
                // internal API アクセス失敗時はキーを残して次回に回す
                Debug.LogWarning("[DennokoMeshEditor] アウトラインの復元に失敗しました。退避情報は保持します: " + ex.Message);
                return;
            }

            EditorPrefs.DeleteKey(LegacyBackupKey);
            SceneView.RepaintAll();
            Debug.Log("[DennokoMeshEditor] Restored legacy selection outline setting.");
        }

        private static PropertyInfo ResolveProperty()
        {
            if (_property != null) return _property;

            var type = FindAnnotationUtility();
            if (type == null) return null;

            var property = type.GetProperty(
                "showSelectionOutline",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (property != null && property.PropertyType == typeof(bool)
                && property.CanRead && property.CanWrite)
            {
                _property = property;
            }

            return _property;
        }

        private static Type FindAnnotationUtility()
        {
            var type = typeof(EditorUtility).Assembly.GetType("UnityEditor.AnnotationUtility");
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("UnityEditor.AnnotationUtility");
                if (type != null) return type;
            }

            return null;
        }
    }
}
