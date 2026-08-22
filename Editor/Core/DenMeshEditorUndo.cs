using UnityEditor;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// コンポーネントの書き換えを Undo へ積むための共通処理。
    ///
    /// 本ツールでは <see cref="Undo.RecordObject"/> を使ってはいけない。
    ///
    /// RecordObject は「変更前の状態を控えておき、フラッシュ時に変更後と比較して
    /// PropertyModification（プロパティ差分）を作る」実装で、コストがオブジェクトの
    /// シリアライズ内容の量に比例する。Ctrl+Z ではその列を逆向きに適用し直すため、
    /// 往路と復路の両方で同じコストがかかる。
    ///
    /// 変形データは byte[] blob で保持されており、頂点数が多いメッシュでは
    /// Prefab インスタンス上で Undo 時に Prefab Reconciliation（全階層照合）が走り、
    /// プログレスバー（Hold on）で数十秒フリーズする。
    ///
    /// <see cref="Undo.RegisterCompleteObjectUndo"/> はオブジェクトのシリアライズ状態を
    /// バイナリのスナップショットとして積むため、Prefab 照合を回避して高速に動作する。
    /// </summary>
    internal static class DenMeshEditorUndo
    {
        /// <summary>
        /// Undo グループを切って、変更前の状態を積む。書き換えの前に呼ぶこと。
        ///
        /// グループを切らないと、Unity は同じグループ内の記録を 1 段にまとめてしまい、
        /// 無関係な操作どうしが Ctrl+Z 一回でまとめて巻き戻る。
        /// </summary>
        internal static void BeginGroup(Object target, string name)
        {
            if (target == null) return;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            Undo.RegisterCompleteObjectUndo(target, name);
        }

        /// <summary>
        /// グループを切らずに、変更前の状態だけを積む。
        /// 連続した設定変更を 1 段へまとめたい場合（オーバーレイ）に使う。
        /// </summary>
        internal static void Record(Object target, string name)
        {
            if (target == null) return;

            Undo.RegisterCompleteObjectUndo(target, name);
        }

        /// <summary>
        /// 書き換えた後に呼ぶ。
        ///
        /// RegisterCompleteObjectUndo は呼んだ時点でスナップショットを積むため、
        /// RecordObject のようなフラッシュ（Undo.FlushUndoRecordObjects）は要らない。
        /// 一方で、Prefab インスタンス上のオーバーライドは差分検出に乗らなくなるので、
        /// ここで明示的に記録する。
        /// </summary>
        internal static void Apply(Object target)
        {
            if (target == null) return;

            EditorUtility.SetDirty(target);

            if (PrefabUtility.IsPartOfPrefabInstance(target))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
        }

        /// <summary>次の操作が同じ段へ入らないようにグループを閉じる。</summary>
        internal static void EndGroup()
        {
            Undo.IncrementCurrentGroup();
        }
    }
}
