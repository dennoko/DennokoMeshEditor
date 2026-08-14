using System;
using System.Reflection;
using UnityEditor;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 編集セッションの間だけ、シーンビューの選択アウトライン（オレンジの輪郭）を止める。
    ///
    /// 編集中に見えている形状は NDMF のプロキシで、元 Renderer は <c>forceRenderingOff</c> で
    /// 描画されていない。それでも選択アウトラインは元 Renderer の形状（＝編集前の形状）で
    /// 描かれるため、編集結果の上にずれた輪郭が重なって見づらい。
    ///
    /// アウトラインは Renderer 単位では切れない。<c>EditorUtility.SetSelectedRenderState</c> は
    /// 選択ワイヤーフレーム側にしか効かず、アウトラインを制御しているのは Gizmos メニューの
    /// "Selection Outline" だけで、その実体は internal な
    /// <c>UnityEditor.AnnotationUtility.showSelectionOutline</c> しかない。そのためリフレクションで触る。
    ///
    /// この設定はエディタ全体（全シーンビュー）に効き、EditorPrefs に保存される。
    /// 勝手に切り替えたまま残さないよう、退避値を EditorPrefs にも書いておき、
    /// 通常の終了だけでなく「復元前にエディタが落ちた」場合も次回起動時に戻す。
    /// </summary>
    internal static class SelectionOutline
    {
        /// <summary>抑制前の値の退避先。キーが存在する＝こちらが抑制している。</summary>
        private const string BackupKey = "Dennokoworks.DenMeshEditor.SelectionOutlineBackup";

        private static PropertyInfo _property;
        private static bool _resolved;

        /// <summary>
        /// 復元前にエディタが落ちていた場合の後始末。
        ///
        /// 編集セッションはドメインリロードを跨いで生き残らない（<c>EditSession.End</c> が
        /// <c>beforeAssemblyReload</c> で必ず走る）ため、起動時に退避値が残っていれば
        /// それは戻しそこねた分だと判断できる。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RestoreLeftover()
        {
            Restore();
        }

        internal static void Suppress()
        {
            // 二重に抑制すると「抑制中の値」を退避してしまう
            if (EditorPrefs.HasKey(BackupKey)) return;

            var property = ResolveProperty();
            if (property == null) return;

            try
            {
                // ユーザーが自分で切っている場合は何もしない（戻すときに勝手に点けないため）
                if (!(bool)property.GetValue(null)) return;

                EditorPrefs.SetBool(BackupKey, true);
                property.SetValue(null, false);
            }
            catch
            {
                // internal API のアクセス失敗時は何もしない
            }
        }

        internal static void Restore()
        {
            if (!EditorPrefs.HasKey(BackupKey)) return;

            var previous = EditorPrefs.GetBool(BackupKey);

            // 復元の可否にかかわらずキーは消す。残すと次回起動時に無限に再試行することになる
            EditorPrefs.DeleteKey(BackupKey);

            var property = ResolveProperty();
            try
            {
                property?.SetValue(null, previous);
            }
            catch
            {
                // internal API のアクセス失敗時は何もしない
            }
        }

        /// <summary>
        /// internal API なので、見つからなければ黙って諦める（アウトラインが出るだけで編集はできる）。
        /// 将来の Unity で移動・改名されてもツールが壊れないようにする。
        /// </summary>
        private static PropertyInfo ResolveProperty()
        {
            if (_resolved) return _property;
            _resolved = true;

            var type = FindAnnotationUtility();
            if (type == null) return null;

            _property = type.GetProperty(
                "showSelectionOutline",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (_property == null || _property.PropertyType != typeof(bool)
                || !_property.CanRead || !_property.CanWrite)
            {
                _property = null;
            }

            return _property;
        }

        private static Type FindAnnotationUtility()
        {
            // AnnotationUtility は UnityEditor.CoreModule にある。EditorUtility も同じアセンブリ
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
