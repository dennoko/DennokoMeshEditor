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
    /// 通常の終了だけでなく「復元前にエディタが落ちた」場合や「セッション中に上書きインポートされた」
    /// 場合も次の機会に戻す。
    ///
    /// 復元は「書き戻せたことを確認できるまで退避値を捨てない」形になっている。
    /// これが本クラスの自己回復の要で、詳細は <see cref="Restore"/> を参照。
    /// </summary>
    internal static class SelectionOutline
    {
        /// <summary>抑制前の値の退避先。キーが存在する＝こちらが抑制している。</summary>
        private const string BackupKey = "Dennokoworks.DenMeshEditor.SelectionOutlineBackup";

        private static PropertyInfo _property;

        /// <summary>
        /// 復元前にエディタが落ちていた・上書きインポートで復元し損ねていた場合の後始末。
        ///
        /// 編集セッションはドメインリロードを跨いで生き残らない（<c>EditSession.End</c> が
        /// <c>beforeAssemblyReload</c> で必ず走る）ため、起動時に退避値が残っていれば
        /// それは戻しそこねた分だと判断できる。
        ///
        /// このタイミングでは AnnotationUtility へ書き込めないことがあるので、
        /// 一度試したうえで <c>delayCall</c> でもう一度試す。失敗しても
        /// <see cref="Restore"/> が退避値を残すため、後から実行される方が拾う。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RestoreLeftover()
        {
            Restore();
            EditorApplication.delayCall += Restore;
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

        /// <summary>
        /// 退避値を書き戻す。退避値が無ければ（＝こちらが抑制していなければ）何もしない。
        ///
        /// キーを消すのは書き戻しを確認できたときだけにしてある。先に消してしまうと、
        /// リフレクションの解決に失敗した回・書き込みが効かなかった回に退避値が失われ、
        /// 「アウトラインが消えたまま二度と戻らない」状態になる。
        /// 残っている限りは起動時・<c>delayCall</c>・Inspector 表示時のいずれかが再試行する。
        /// </summary>
        internal static void Restore()
        {
            if (!EditorPrefs.HasKey(BackupKey)) return;

            var previous = EditorPrefs.GetBool(BackupKey);

            var property = ResolveProperty();
            if (property == null) return;

            try
            {
                property.SetValue(null, previous);

                // 書き込みが効いたかを読み返して確認する
                if ((bool)property.GetValue(null) != previous) return;
            }
            catch
            {
                // internal API のアクセス失敗時は退避値を残して次の機会に回す
                return;
            }

            EditorPrefs.DeleteKey(BackupKey);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// internal API なので、見つからなければ黙って諦める（アウトラインが出るだけで編集はできる）。
        /// 将来の Unity で移動・改名されてもツールが壊れないようにする。
        ///
        /// 失敗はキャッシュしない。ドメインリロード直後など、まだ解決できない時点で
        /// 「見つからない」を覚え込むと、そのドメインの間ずっと抑制も復元もできなくなる。
        /// </summary>
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
