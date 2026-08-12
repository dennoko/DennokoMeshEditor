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
            // 編集セッション中はデルタが空でもプロキシが必要（シーンビューが形状を読むため）。
            // それ以外は編集を持つ Renderer だけを対象にして、常時プロキシを作らないようにする。
            var editing = context.Observe(EditSession.ActiveComponent, c => c, (a, b) => a == b);

            var targets = new List<Renderer>();
            var seen = new HashSet<Renderer>();

            foreach (var component in context.GetComponentsByType<DenMeshEditor>())
            {
                if (component == null) continue;
                if (!context.ActiveInHierarchy(component.gameObject)) continue;

                ObserveEdits(context, component);

                var isEditing = ReferenceEquals(component, editing);

                foreach (var edit in component.edits)
                {
                    if (edit?.target == null) continue;
                    if (!isEditing && !edit.HasEdits) continue;

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

                ObserveEdits(context, component);
                components.Add(component);
            }

            var node = new DenMeshEditorPreviewNode(proxyPairs, components);
            return Task.FromResult<IRenderFilterNode>(node);
        }

        /// <summary>
        /// 編集データの変更だけを監視する。
        ///
        /// 引数なしの <c>context.Observe(component)</c> は比較関数が常に false
        /// （NDMF: SingleObjectQueries.cs）なので、brushRadius や falloff のような
        /// プレビュー結果に影響しないプロパティを触っただけでもパイプライン全体が
        /// 再構築される。スライダーをドラッグすると毎フレーム メッシュを複製し直すことになるため、
        /// 実際に描画へ効く値だけを抽出して監視する。
        /// </summary>
        private static void ObserveEdits(ComputeContext context, DenMeshEditor component)
        {
            context.Observe(component, EditsFingerprint, (a, b) => a == b);
        }

        private static int EditsFingerprint(DenMeshEditor component)
        {
            if (component == null) return 0;

            unchecked
            {
                var hash = 17;
                foreach (var edit in component.edits)
                {
                    if (edit == null)
                    {
                        hash = hash * 31 + 1;
                        continue;
                    }

                    hash = hash * 31 + (edit.target != null ? edit.target.GetInstanceID() : 0);
                    hash = hash * 31 + edit.vertexCount;
                    hash = hash * 31 + edit.indices.Count;

                    for (var i = 0; i < edit.indices.Count; i++)
                    {
                        hash = hash * 31 + edit.indices[i];
                    }

                    for (var i = 0; i < edit.deltas.Count; i++)
                    {
                        var delta = edit.deltas[i];
                        hash = hash * 31 + delta.x.GetHashCode();
                        hash = hash * 31 + delta.y.GetHashCode();
                        hash = hash * 31 + delta.z.GetHashCode();
                    }
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
                                $"頂点数が編集時と異なるため {edit.indices.Count} 頂点分の編集を適用できませんでした"
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
        /// <summary>上流メッシュの変化検出に使うサンプル点数。</summary>
        private const int FingerprintSamples = 64;

        /// <summary>
        /// プロキシ 1 つ分の状態。
        /// </summary>
        private sealed class Entry
        {
            public Renderer Original;
            public Renderer Proxy;

            /// <summary>上流ノードが出力したメッシュ。デルタ加算の基準。</summary>
            public Mesh Source;

            /// <summary>上流メッシュの頂点。毎フレーム読み直すため List を使い回す。</summary>
            public readonly List<Vector3> UpstreamVertices = new List<Vector3>();

            /// <summary>UpstreamVertices から間引いたサンプル。上流の書き換え検出用。</summary>
            public Vector3[] Fingerprint;

            /// <summary>自分が生成したメッシュ。編集が無ければ null。</summary>
            public Mesh Generated;

            public readonly List<Vector3> Scratch = new List<Vector3>();

            public int Version = int.MinValue;

            /// <summary>読み取り不可メッシュの警告を 1 度だけ出すためのフラグ。</summary>
            public bool WarnedNotReadable;
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

            // 上流ノードの出力インスタンスが差し替わったら作り直す。
            // 自分が書き込んだメッシュが残っている場合（フレーム処理が途中で打ち切られた等）は据え置く。
            if (upstream != entry.Source && upstream != entry.Generated)
            {
                entry.Source = upstream;
                entry.Fingerprint = null;
                entry.Version = int.MinValue;
                DestroyGenerated(entry);
            }

            if (entry.Source == null) return;

            // 上流がメッシュを「その場で」書き換えるケース（本ツールの UpdateVertices と同じ方式）では
            // インスタンスが変わらないため、毎フレーム読み直して変化を検出する。
            // GetVertices は List を使い回すので、容量が足りていれば確保は発生しない。
            entry.Source.GetVertices(entry.UpstreamVertices);

            // 頂点を持つはずなのに読めなかった場合は Read/Write が無効な可能性が高い。
            // 事前に isReadable で弾くとエディタ上で読めているケースまで止めてしまうので、
            // 実際に失敗したときだけ 1 度だけ警告する。
            if (entry.UpstreamVertices.Count == 0 && entry.Source.vertexCount > 0)
            {
                if (!entry.WarnedNotReadable)
                {
                    entry.WarnedNotReadable = true;
                    Debug.LogWarning(
                        $"[Den Mesh Editor] {original.name} のメッシュ「{entry.Source.name}」から頂点を読み取れませんでした。"
                        + "インポート設定の Read/Write Enabled を有効にしてください。",
                        original);
                }

                return;
            }

            var upstreamChanged = UpdateFingerprint(entry);

            if (upstreamChanged || entry.Version != LiveEdits.Version)
            {
                entry.Version = LiveEdits.Version;
                Rebuild(entry);
            }

            if (entry.Generated != null) MeshDeltaApplier.SetSharedMesh(proxy, entry.Generated);
        }

        /// <summary>
        /// 上流頂点から等間隔にサンプルを取り、前回と違っていれば true を返す。
        /// 全頂点比較はコピーと同コストなので、O(<see cref="FingerprintSamples"/>) で近似する。
        /// </summary>
        private static bool UpdateFingerprint(Entry entry)
        {
            var vertices = entry.UpstreamVertices;
            var count = vertices.Count;
            if (count == 0) return false;

            var samples = Mathf.Min(FingerprintSamples, count);

            if (entry.Fingerprint == null || entry.Fingerprint.Length != samples)
            {
                entry.Fingerprint = new Vector3[samples];
                for (var i = 0; i < samples; i++)
                {
                    entry.Fingerprint[i] = vertices[(int)((long)i * count / samples)];
                }

                return true;
            }

            var changed = false;
            for (var i = 0; i < samples; i++)
            {
                var value = vertices[(int)((long)i * count / samples)];
                if (value == entry.Fingerprint[i]) continue;

                entry.Fingerprint[i] = value;
                changed = true;
            }

            return changed;
        }

        private void Rebuild(Entry entry)
        {
            if (entry.Source == null || entry.UpstreamVertices.Count == 0) return;

            var edit = DenMeshEditorPreviewFilter.GatherEdits(
                _components, entry.Original, entry.UpstreamVertices.Count);

            if (edit == null)
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

                // 毎フレーム SetVertices するので動的メッシュとして確保させる
                entry.Generated.MarkDynamic();

                // ドメインリロードで Dispose が走らないケースに備えて追跡する
                GeneratedMeshTracker.Track(entry.Generated);
            }

            MeshDeltaApplier.UpdateVertices(entry.Generated, entry.UpstreamVertices, edit, entry.Scratch);
        }

        private static void DestroyGenerated(Entry entry)
        {
            if (entry.Generated == null) return;

            GeneratedMeshTracker.Forget(entry.Generated);
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
