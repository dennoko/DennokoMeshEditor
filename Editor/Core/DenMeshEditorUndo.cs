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
    ///
    /// ただし差分ではなく丸ごとのコピーなので、<b>呼ぶたびに blob 全体が Undo スタックへ積まれる</b>。
    /// スライダーのドラッグのように毎フレーム変更が出る経路では、記録は操作の開始時 1 回だけにし、
    /// <see cref="BeginGroup"/> が返したグループ番号を <see cref="Collapse"/> へ渡して 1 段にまとめること。
    /// </summary>
    internal static class DenMeshEditorUndo
    {
        /// <summary>
        /// Undo グループを切って、変更前の状態を積む。書き換えの前に呼ぶこと。
        /// 戻り値は切ったグループの番号（<see cref="Collapse"/> 用。不要なら捨ててよい）。
        ///
        /// グループを切らないと、Unity は同じグループ内の記録を 1 段にまとめてしまい、
        /// 無関係な操作どうしが Ctrl+Z 一回でまとめて巻き戻る。
        /// </summary>
        internal static int BeginGroup(Object target, string name)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();

            Record(target, name);

            // グループ名は記録の後に付ける。RegisterCompleteObjectUndo は
            // 自分に渡された名前をグループ名として書き込むことがあるため
            Undo.SetCurrentGroupName(name);

            return group;
        }

        /// <summary>
        /// グループを切らずに、変更前の状態だけを積む。
        /// 直前に積まれた別の記録（オブジェクト生成・コンポーネント追加など）と
        /// 同じ段へ入れたい場合に使う。
        /// </summary>
        internal static void Record(Object target, string name)
        {
            if (target == null) return;

            Undo.RegisterCompleteObjectUndo(target, name);
        }

        /// <summary>
        /// <see cref="BeginGroup"/> 以降に積まれた記録を 1 段へまとめる。
        /// ドラッグのように変更イベントが連続する操作の終わり（MouseUp）で呼ぶ。
        /// </summary>
        internal static void Collapse(int group)
        {
            if (group < 0) return;

            Undo.CollapseUndoOperations(group);
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
