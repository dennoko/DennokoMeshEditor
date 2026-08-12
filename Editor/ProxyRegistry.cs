using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// NDMF プレビューのプロキシ Renderer をシーンビュー編集ツールへ受け渡すためのレジストリ。
    ///
    /// シーンビュー側から NDMF に問い合わせる（プル型）のではなく、
    /// <see cref="DenMeshEditorPreviewFilter"/> のノードが <c>IRenderFilterNode.OnFrame</c> で
    /// 受け取ったプロキシをここへ登録する（プッシュ型）。
    ///
    /// OnFrame は NDMF の OnPreCull 経路から毎フレーム呼ばれるため、
    /// 「プロキシは OnPreCull 以降に読む必要がある」というタイミング制約が構造的に満たされる。
    /// internal API へのリフレクションは不要。
    /// </summary>
    internal static class ProxyRegistry
    {
        private static readonly Dictionary<Renderer, Renderer> Map = new Dictionary<Renderer, Renderer>();

        internal static void Report(Renderer original, Renderer proxy)
        {
            if (original == null || proxy == null) return;
            Map[original] = proxy;
        }

        /// <summary>
        /// 登録を解除する。パイプライン再構築では新しいノードが登録した後に
        /// 古いノードの Dispose が走ることがあるため、自分が登録したものだけを消す。
        /// </summary>
        internal static void Remove(Renderer original, Renderer proxy)
        {
            if (original == null) return;
            if (Map.TryGetValue(original, out var current) && current != proxy) return;

            Map.Remove(original);
        }

        /// <summary>
        /// 元 Renderer に対応するプロキシを取得する。
        /// プロキシはパイプライン再構築のたびに破棄されるため、Unity の null 比較で必ず検証する。
        /// </summary>
        internal static bool TryGet(Renderer original, out Renderer proxy)
        {
            proxy = null;
            if (original == null) return false;
            if (!Map.TryGetValue(original, out var found)) return false;
            if (found == null)
            {
                Map.Remove(original);
                return false;
            }

            proxy = found;
            return true;
        }

        /// <summary>
        /// プロキシが取得できればそれを、できなければ元の Renderer を返す。
        /// フォールバック時は他ツールの影響が反映されないため、UI 側で警告を出す。
        /// </summary>
        internal static Renderer ResolveOrOriginal(Renderer original, out bool isProxy)
        {
            isProxy = TryGet(original, out var proxy);
            return isProxy ? proxy : original;
        }

        /// <summary>
        /// 破棄済みの Renderer を参照しているエントリを取り除く。
        /// <see cref="TryGet"/> は値側しか掃除しないため、キー側が破棄された分が溜まっていく。
        /// 編集セッションの終了時など、区切りの良いところで呼ぶ。
        /// </summary>
        internal static void Prune()
        {
            List<Renderer> stale = null;

            foreach (var pair in Map)
            {
                if (pair.Key != null && pair.Value != null) continue;

                stale ??= new List<Renderer>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;

            foreach (var key in stale)
            {
                Map.Remove(key);
            }
        }
    }
}
