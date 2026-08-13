using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// ドラッグ中の未確定な編集内容を、シーンビュー編集セッションから
    /// NDMF プレビューノードへ受け渡すための一時領域。
    ///
    /// ドラッグ中にコンポーネントを書き換えると、変更のたびに NDMF が
    /// プレビューパイプラインを再構築する（＝毎フレーム メッシュを複製する）。
    /// そのためコンポーネントへの確定はマウスを離したときの 1 回だけにし、
    /// ドラッグ中はここを経由して反映する。Undo もドラッグ 1 回につき 1 エントリになる。
    ///
    /// さらに、編集セッション中はコンポーネント自体が NDMF の監視対象から外れる
    /// （<c>DenMeshEditorPreviewFilter.ObserveEdits</c>）。そのため
    /// <see cref="Version"/> はドラッグ中だけでなく、確定・Undo / Redo・編集クリアを含む
    /// 「セッション中のあらゆる変更」をプレビューへ伝える唯一の合図になっている。
    /// この経路ではパイプラインは作り直されず、生成済みメッシュの頂点だけが書き換わる。
    /// </summary>
    internal static class LiveEdits
    {
        private static readonly Dictionary<MeshEdit, Dictionary<int, Vector3>> Map =
            new Dictionary<MeshEdit, Dictionary<int, Vector3>>();

        private static int _version;

        /// <summary>
        /// 編集内容の世代番号。プレビューノードはこの値が変わったときだけメッシュを更新する。
        /// </summary>
        internal static int Version => _version;

        /// <summary>
        /// プレビューを作り直す必要があることを通知する。
        /// 確定・クリア・編集終了など、編集内容が変わるすべての経路から呼ぶ。
        /// </summary>
        internal static void Invalidate()
        {
            unchecked
            {
                _version++;
            }
        }

        /// <summary>
        /// 未確定のデルタを公開する。<paramref name="deltas"/> は呼び出し側が保持している
        /// 辞書をそのまま渡してよい（プレビュー側は読み取りしか行わない）。
        /// </summary>
        internal static void Publish(MeshEdit edit, Dictionary<int, Vector3> deltas)
        {
            if (edit == null || deltas == null) return;

            Map[edit] = deltas;
            Invalidate();
        }

        internal static bool TryGet(MeshEdit edit, out Dictionary<int, Vector3> deltas)
        {
            deltas = null;
            return edit != null && Map.TryGetValue(edit, out deltas);
        }

        internal static void Clear()
        {
            if (Map.Count == 0) return;

            Map.Clear();
            Invalidate();
        }
    }
}
