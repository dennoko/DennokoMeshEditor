using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
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
        /// 下流フィルタに上書きされている対象がある場合に、NDMF へ変更を伝えるための値。
        ///
        /// 上流ノードが <c>OnFrame</c> でメッシュをその場で書き換えても、下流ノードは
        /// 自分が <c>Instantiate</c> 時に作ったメッシュを毎フレーム代入し直すだけなので、
        /// 書き換えは下流に届かずに捨てられる。下流まで反映させる方法は
        /// 「ComputeContext を無効化してノードを作り直させる」しかない。
        ///
        /// <see cref="DenMeshEditorPreviewFilter"/> は、下流上書きが検出された対象のときだけ
        /// この値を <c>Observe</c> する。検出されていない通常時は誰も見ていないので、
        /// 値が変わってもパイプラインは作り直されない（従来どおりの高速パス）。
        /// </summary>
        internal static readonly PublishedValue<int> SyncedVersion =
            new PublishedValue<int>(0, "DenMeshEditor.LiveEditsSyncedVersion");

        /// <summary>
        /// <see cref="SyncedVersion"/> を進める最短間隔（秒）。
        ///
        /// この値を進めるとプレビューパイプラインが作り直される。下流ノードは
        /// <c>IRenderFilterNode.Refresh</c> を実装していないことが多く（Avatar Optimizer も未実装）、
        /// その場合はノードごと作り直しになって <c>BakeMesh</c> とジョブが再実行される。
        /// ドラッグのフレームレートそのままで叩くと重すぎるので間引く。
        /// </summary>
        private const double SyncIntervalSeconds = 0.05;

        private static double _nextSync;
        private static bool _syncPending;

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

            RequestSync();
        }

        /// <summary>
        /// 下流上書きが検出されているときだけ、NDMF への通知を間引きながら流す。
        /// </summary>
        private static void RequestSync()
        {
            if (!DownstreamGuard.AnyOverridden) return;

            var now = EditorApplication.timeSinceStartup;
            if (now < _nextSync)
            {
                // 間引きで落とした最後の 1 回（＝ドラッグの最終位置）を取りこぼさないよう、
                // 間隔が明けたところで必ず一度流す
                if (_syncPending) return;

                _syncPending = true;
                EditorApplication.update += FlushPending;
                return;
            }

            Sync(now);
        }

        private static void FlushPending()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < _nextSync) return;

            Sync(now);
        }

        private static void Sync(double now)
        {
            if (_syncPending)
            {
                EditorApplication.update -= FlushPending;
                _syncPending = false;
            }

            _nextSync = now + SyncIntervalSeconds;
            SyncedVersion.Value = _version;
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

        /// <summary>
        /// 未確定データを捨てる。実際に捨てて <see cref="Invalidate"/> まで行った場合は true。
        ///
        /// 戻り値があるのは、呼び出し側が「捨てるものが無くても通知だけはしたい」場合に
        /// <see cref="Invalidate"/> を二重に呼ばずに済ませるため。二重に呼ぶと
        /// <see cref="RequestSync"/> の間引き待ちが余分に 1 回積まれ、下流上書き構成では
        /// 何も変わっていないのに 50ms 後にもう一度パイプラインが作り直される。
        /// </summary>
        internal static bool Clear()
        {
            if (Map.Count == 0) return false;

            Map.Clear();
            Invalidate();
            return true;
        }
    }
}
