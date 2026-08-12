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
    /// </summary>
    internal class DenMeshEditorPreviewFilter : IRenderFilter
    {
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var targets = new List<Renderer>();
            var seen = new HashSet<Renderer>();

            foreach (var component in context.GetComponentsByType<DenMeshEditor>())
            {
                if (component == null) continue;
                if (!context.ActiveInHierarchy(component.gameObject)) continue;

                // 編集値の変更でプレビューが再構築されるように監視する
                context.Observe(component);

                foreach (var edit in component.edits)
                {
                    if (edit?.target == null) continue;

                    // 編集が空でも対象に含める。シーンビュー編集ツールがプロキシを必要とするため。
                    if (seen.Add(edit.target)) targets.Add(edit.target);
                }
            }

            var builder = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var target in targets)
            {
                builder.Add(RenderGroup.For(target));
            }

            return builder.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var components = new List<DenMeshEditor>();
            foreach (var component in context.GetComponentsByType<DenMeshEditor>())
            {
                if (component == null) continue;
                if (!context.ActiveInHierarchy(component.gameObject)) continue;

                context.Observe(component);
                components.Add(component);
            }

            var node = new DenMeshEditorPreviewNode(proxyPairs, components);
            return Task.FromResult<IRenderFilterNode>(node);
        }

        /// <summary>
        /// 対象 Renderer に紐づく編集データを、全コンポーネント分まとめて 1 つに合成する。
        /// 同一 Renderer を複数のコンポーネントが対象にしている場合はデルタを加算する。
        ///
        /// 編集セッション中の未確定データ（<see cref="LiveEdits"/>）があれば、
        /// そのコンポーネントの寄与だけを未確定データで置き換える。
        /// </summary>
        internal static MeshEdit GatherEdits(IEnumerable<DenMeshEditor> components, Renderer target, int vertexCount)
        {
            Dictionary<int, Vector3> merged = null;

            foreach (var component in components)
            {
                if (component == null) continue;

                foreach (var edit in component.edits)
                {
                    if (edit == null || edit.target != target) continue;

                    // 頂点数が編集時と違う場合は適用しない（元メッシュ差し替え・再インポート等）
                    if (edit.vertexCount != 0 && edit.vertexCount != vertexCount) continue;

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

                    if (!edit.HasEdits) continue;

                    var count = Mathf.Min(edit.indices.Count, edit.deltas.Count);
                    merged ??= new Dictionary<int, Vector3>(count);

                    for (var i = 0; i < count; i++)
                    {
                        var index = edit.indices[i];
                        merged.TryGetValue(index, out var accumulated);
                        merged[index] = accumulated + edit.deltas[i];
                    }
                }
            }

            if (merged == null || merged.Count == 0) return null;

            var result = new MeshEdit { target = target };
            result.SetFrom(merged, vertexCount);
            return result.HasEdits ? result : null;
        }
    }

    internal class DenMeshEditorPreviewNode : IRenderFilterNode
    {
        /// <summary>
        /// プロキシ 1 つ分の状態。
        /// </summary>
        private sealed class Entry
        {
            public Renderer Original;
            public Renderer Proxy;

            /// <summary>上流ノードが出力したメッシュ。デルタ加算の基準。</summary>
            public Mesh Source;

            public Vector3[] BaseVertices;

            /// <summary>自分が生成したメッシュ。編集が無ければ null。</summary>
            public Mesh Generated;

            public Vector3[] Scratch;

            public int Version = int.MinValue;
        }

        private readonly Dictionary<Renderer, Entry> _entries = new Dictionary<Renderer, Entry>();
        private readonly List<DenMeshEditor> _components;

        public RenderAspects WhatChanged => RenderAspects.Mesh;

        internal DenMeshEditorPreviewNode(IEnumerable<(Renderer, Renderer)> proxyPairs, List<DenMeshEditor> components)
        {
            _components = components;

            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null) continue;
                _entries[original] = new Entry { Original = original, Proxy = proxy };
            }
        }

        /// <summary>
        /// NDMF の OnPreCull 経路から毎フレーム呼ばれる。
        ///
        /// NDMF は毎フレーム <c>ProxyObjectController.OnPreFrame</c> でプロキシの sharedMesh を
        /// 元 Renderer のものへ戻すため、編集済みメッシュの差し込みは Instantiate 時ではなく
        /// ここで毎フレーム行う必要がある。
        ///
        /// ここでプロキシを登録することで、シーンビュー編集ツールも最新のプロキシを参照できる。
        /// </summary>
        public void OnFrame(Renderer original, Renderer proxy)
        {
            ProxyRegistry.Report(original, proxy);

            if (!_entries.TryGetValue(original, out var entry)) return;
            entry.Proxy = proxy;

            var upstream = MeshDeltaApplier.GetSharedMesh(proxy);
            if (upstream == null) return;

            // 上流ノードの出力が差し替わったら基準を取り直す。
            // 自分が書き込んだメッシュが残っている場合（フレーム処理が途中で打ち切られた等）は据え置く。
            if (upstream != entry.Source && upstream != entry.Generated)
            {
                entry.Source = upstream;
                entry.BaseVertices = upstream.vertices;
                entry.Version = int.MinValue;
                DestroyGenerated(entry);
            }

            if (entry.Version != LiveEdits.Version)
            {
                entry.Version = LiveEdits.Version;
                Rebuild(entry);
            }

            if (entry.Generated != null) MeshDeltaApplier.SetSharedMesh(proxy, entry.Generated);
        }

        private void Rebuild(Entry entry)
        {
            if (entry.Source == null || entry.BaseVertices == null) return;

            var edit = DenMeshEditorPreviewFilter.GatherEdits(_components, entry.Original, entry.BaseVertices.Length);

            if (edit == null || !MeshDeltaApplier.IsCompatible(entry.Source, edit))
            {
                DestroyGenerated(entry);
                return;
            }

            if (entry.Generated == null)
            {
                // IRenderFilter の規約：メッシュは新規インスタンスを作り、Dispose で破棄する
                entry.Generated = Object.Instantiate(entry.Source);
                entry.Generated.name = entry.Source.name + " (Den Mesh Editor)";
                entry.Generated.hideFlags = HideFlags.HideAndDontSave;
            }

            MeshDeltaApplier.UpdateVertices(entry.Generated, entry.BaseVertices, edit, ref entry.Scratch);
        }

        private static void DestroyGenerated(Entry entry)
        {
            if (entry.Generated == null) return;

            Object.DestroyImmediate(entry.Generated);
            entry.Generated = null;
        }

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                ProxyRegistry.Remove(entry.Original, entry.Proxy);
                DestroyGenerated(entry);
            }

            _entries.Clear();
        }
    }
}
