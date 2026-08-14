using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 編集結果を NDMF プレビューへ反映するフィルタ。
    ///
    /// 併せて、生成されたプロキシ Renderer を <see cref="ProxyRegistry"/> へ登録し、
    /// シーンビュー編集ツールが「他ツール適用後の形状」を参照できるようにする。
    ///
    /// <b>スケーラビリティ上の要点</b>：
    /// <see cref="Instantiate"/> はシーンを走査しない。対象 Renderer に関係するコンポーネントは
    /// <see cref="GetTargetGroups"/> の時点で確定させ、<c>RenderGroup.WithData</c> で
    /// グループに添付して渡す。これを怠ると、
    ///   - グループ数 × シーン全走査（<c>ComputeContext.GetComponentsByType</c> はキャッシュされない）
    ///   - グループ数 × 全コンポーネントの監視登録
    /// が発生し、シーン上のコンポーネント数 N に対して <c>O(N^2)</c> になる。
    /// しかも NDMF の <c>PropertyMonitor</c> は監視値の抽出関数を毎フレーム再評価するため、
    /// このコストはロード時だけでなく待機中も継続的にかかる。
    /// </summary>
    internal class DenMeshEditorPreviewFilter : IRenderFilter
    {
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            // 編集セッション中はデルタが空でもプロキシが必要（シーンビューが形状を読むため）。
            // それ以外は編集を持つ Renderer だけを対象にして、常時プロキシを作らないようにする。
            var editing = context.Observe(EditSession.ActiveComponent, c => c, (a, b) => a == b);

            // Renderer → その Renderer を対象にしているコンポーネント
            var byRenderer = new Dictionary<Renderer, List<DenMeshEditor>>();
            var order = new List<Renderer>();

            foreach (var component in context.GetComponentsByType<DenMeshEditor>())
            {
                if (component == null) continue;
                if (!context.ActiveInHierarchy(component.gameObject)) continue;

                var isEditing = ReferenceEquals(component, editing);

                // ここで監視するのは「対象 Renderer の集合」と「編集の有無」だけ。
                // デルタの中身はグループ分割に影響しないので、ノード側（Instantiate）で見る
                ObserveShape(context, component, isEditing);

                foreach (var edit in component.edits)
                {
                    if (edit?.target == null) continue;
                    if (!isEditing && !edit.HasEdits) continue;

                    if (!byRenderer.TryGetValue(edit.target, out var owners))
                    {
                        owners = new List<DenMeshEditor>();
                        byRenderer.Add(edit.target, owners);
                        order.Add(edit.target);
                    }

                    if (!owners.Contains(component)) owners.Add(component);
                }
            }

            var builder = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var target in order)
            {
                // WithData で添付したリストはグループの同一性に含まれる
                // （RenderGroup<T> は IEnumerable を SequenceEqual で比較する）。
                // 対象コンポーネントの構成が変われば自動的にノードが作り直される。
                builder.Add(RenderGroup.For(target).WithData(byRenderer[target]));
            }

            return builder.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            // GetTargetGroups が確定させた「この Renderer に関係するコンポーネント」だけを扱う。
            // シーン走査も、無関係なコンポーネントの監視も行わない
            var components = group.GetData<List<DenMeshEditor>>() ?? new List<DenMeshEditor>();

            ObserveNodeInputs(context, components, group.Renderers);

            var node = new DenMeshEditorPreviewNode(proxyPairs, components);
            return Task.FromResult<IRenderFilterNode>(node);
        }

        /// <summary>
        /// ノード 1 つ分の入力を監視する。
        ///
        /// <see cref="DenMeshEditorPreviewNode.Refresh"/> がノードを再利用する場合は
        /// <see cref="Instantiate"/> が呼ばれないため、そちらからも同じ監視を張り直す必要がある。
        /// 張り忘れるとノードが二度と無効化されなくなるので、経路を 1 つにまとめておく。
        /// </summary>
        internal static void ObserveNodeInputs(
            ComputeContext context,
            IEnumerable<DenMeshEditor> components,
            IEnumerable<Renderer> originals)
        {
            foreach (var component in components)
            {
                if (component == null) continue;
                ObserveEdits(context, component);
            }

            ObserveDownstreamSync(context, originals);
        }

        /// <summary>
        /// 下流フィルタに上書きされている対象では、<see cref="LiveEdits.SyncedVersion"/> を監視する。
        ///
        /// この経路に入ると、ドラッグ中の更新のたびにパイプラインが作り直されて重くなる。
        /// そのため、上書きが実際に検出された対象でだけ監視を張る。検出されていない通常時は
        /// 誰も見ていないので、<see cref="LiveEdits.Invalidate"/> はパイプラインに影響しない。
        /// </summary>
        private static void ObserveDownstreamSync(ComputeContext context, IEnumerable<Renderer> originals)
        {
            // ラッチの成立そのものをノードの再構築契機にする。
            // これが無いと、検出した時点では誰も SyncedVersion を見ていないため
            // 「上書きされているのに永久に無効化されない」状態で固まる
            context.Observe(DownstreamGuard.OverrideGeneration, v => v, (a, b) => a == b);

            var overridden = false;
            foreach (var original in originals)
            {
                if (!DownstreamGuard.IsOverridden(original)) continue;

                overridden = true;
                break;
            }

            if (!overridden) return;

            context.Observe(LiveEdits.SyncedVersion, v => v, (a, b) => a == b);
        }

        /// <summary>
        /// グループ分割に影響する部分だけを監視する。対象 Renderer と、編集の有無。
        ///
        /// 編集セッション中のコンポーネントについては編集の有無を見ない。
        /// セッション中はデルタが空の対象もグループへ入れている（<see cref="GetTargetGroups"/>）ため
        /// グループ分割には影響しない一方、これを見てしまうと「最初の 1 頂点を動かした瞬間」や
        /// 「編集が空に戻る Undo」のたびにパイプライン全体が作り直されてしまう。
        /// </summary>
        private static void ObserveShape(ComputeContext context, DenMeshEditor component, bool isEditing)
        {
            if (isEditing)
            {
                context.Observe(component, EditingShapeFingerprint, (a, b) => a == b);
                return;
            }

            context.Observe(component, ShapeFingerprint, (a, b) => a == b);
        }

        /// <summary>
        /// 編集データの変更だけを監視する。
        ///
        /// 引数なしの <c>context.Observe(component)</c> は比較関数が常に false
        /// （NDMF: SingleObjectQueries.cs）なので、brushRadius や falloff のような
        /// プレビュー結果に影響しないプロパティを触っただけでもパイプライン全体が
        /// 再構築される。実際に描画へ効く値だけを抽出して監視する。
        ///
        /// 編集セッション中のコンポーネントはそもそも監視しない。セッション中の変更は
        /// <see cref="LiveEdits.Version"/> 経由で <see cref="DenMeshEditorPreviewNode.OnFrame"/> が
        /// 拾い、生成済みメッシュの頂点だけを書き換える。ここで監視すると、ドラッグの確定や
        /// Undo のたびに NDMF がプレビューパイプライン全体を作り直すことになり
        /// （プロキシの再生成 + 全フィルタの再実行 + メッシュの複製）、高頂点数のアバターでは
        /// Undo 連打がそのままフリーズになる。
        /// セッションの開始・終了は ActiveComponent の変化として拾うので、終了時に通常の監視へ戻る。
        /// </summary>
        private static void ObserveEdits(ComputeContext context, DenMeshEditor component)
        {
            var editing = context.Observe(EditSession.ActiveComponent, c => c, (a, b) => a == b);
            if (ReferenceEquals(component, editing)) return;

            context.Observe(component, EditsFingerprint, (a, b) => a == b);
        }

        private static int ShapeFingerprint(DenMeshEditor component)
        {
            return ComputeShapeFingerprint(component, true);
        }

        private static int EditingShapeFingerprint(DenMeshEditor component)
        {
            return ComputeShapeFingerprint(component, false);
        }

        private static int ComputeShapeFingerprint(DenMeshEditor component, bool includeHasEdits)
        {
            if (component == null) return 0;

            unchecked
            {
                var hash = 17;
                hash = hash * 31 + component.edits.Count;

                foreach (var edit in component.edits)
                {
                    if (edit == null)
                    {
                        hash = hash * 31 + 1;
                        continue;
                    }

                    hash = hash * 31 + (edit.target != null ? edit.target.GetInstanceID() : 0);
                    if (includeHasEdits) hash = hash * 31 + (edit.HasEdits ? 1 : 0);
                }

                return hash;
            }
        }

        /// <summary>
        /// 編集内容のフィンガープリント。
        ///
        /// NDMF はこの関数を毎フレーム呼ぶ（PropertyMonitor.CheckAllObjectsLoop）。
        /// デルタ全体を走査すると編集頂点数に比例したコストが常時かかるため、
        /// <see cref="MeshEdit.Revision"/> を見るだけの O(編集対象数) に抑える。
        /// vertexCount と Count も混ぜているのは、revision を通らない外部書き換えに対する安全網。
        /// </summary>
        private static int EditsFingerprint(DenMeshEditor component)
        {
            if (component == null) return 0;

            unchecked
            {
                var hash = 17;
                hash = hash * 31 + component.edits.Count;

                foreach (var edit in component.edits)
                {
                    if (edit == null)
                    {
                        hash = hash * 31 + 1;
                        continue;
                    }

                    hash = hash * 31 + (edit.target != null ? edit.target.GetInstanceID() : 0);
                    hash = hash * 31 + edit.vertexCount;
                    hash = hash * 31 + edit.Count;
                    hash = hash * 31 + edit.Revision;
                }

                return hash;
            }
        }

        /// <summary>
        /// 対象 Renderer に紐づく編集データを、全コンポーネント分まとめて 1 つに合成する。
        /// 同一 Renderer を複数のコンポーネントが対象にしている場合はデルタを加算する。
        ///
        /// 編集セッション中の未確定データ（<see cref="LiveEdits"/>）があれば、
        /// そのコンポーネントの寄与だけを未確定データで置き換える。
        /// </summary>
        /// <param name="skipped">
        /// 頂点数の不一致などで適用できなかった編集の説明を受け取る。
        /// null を渡すと収集しない（プレビューのように毎フレーム呼ばれる経路用）。
        /// </param>
        internal static MeshEdit GatherEdits(IEnumerable<DenMeshEditor> components, Renderer target, int vertexCount,
            List<string> skipped = null)
        {
            Dictionary<int, Vector3> merged = null;

            foreach (var component in components)
            {
                if (component == null) continue;

                foreach (var edit in component.edits)
                {
                    if (edit == null || edit.target != target) continue;

                    // 頂点数が編集時と違う場合は適用しない（元メッシュ差し替え・再インポート等）。
                    // 黙って捨てるとユーザーが気づけないので、呼び出し側へ理由を返す。
                    if (edit.vertexCount != 0 && edit.vertexCount != vertexCount)
                    {
                        if (edit.HasEdits)
                        {
                            skipped?.Add(
                                $"頂点数が編集時と異なるため {edit.Count} 頂点分の編集を適用できませんでした"
                                + $"（現在 {vertexCount} / 編集時 {edit.vertexCount}）。"
                                + "元メッシュが差し替わったか、再インポートで頂点順が変化した可能性があります。");
                        }

                        continue;
                    }

                    if (LiveEdits.TryGet(edit, out var live))
                    {
                        merged ??= new Dictionary<int, Vector3>(live.Count);
                        foreach (var pair in live)
                        {
                            if (pair.Value.sqrMagnitude <= 0f) continue;
                            merged.TryGetValue(pair.Key, out var accumulated);
                            merged[pair.Key] = accumulated + pair.Value;
                        }

                        continue;
                    }

                    var count = edit.Count;
                    if (count == 0) continue;

                    merged ??= new Dictionary<int, Vector3>(count);

                    for (var i = 0; i < count; i++)
                    {
                        var index = edit.GetIndex(i);
                        merged.TryGetValue(index, out var accumulated);
                        merged[index] = accumulated + edit.GetDelta(i);
                    }
                }
            }

            if (merged == null || merged.Count == 0) return null;

            var result = new MeshEdit { target = target };
            result.SetFrom(merged, vertexCount);
            return result.HasEdits ? result : null;
        }
    }
}
