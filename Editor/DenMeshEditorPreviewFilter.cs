using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
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

            foreach (var component in components)
            {
                if (component == null) continue;
                ObserveEdits(context, component);
            }

            var node = new DenMeshEditorPreviewNode(proxyPairs, components);
            return Task.FromResult<IRenderFilterNode>(node);
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

    internal class DenMeshEditorPreviewNode : IRenderFilterNode
    {
        /// <summary>上流メッシュの変化検出に使うサンプル点数。</summary>
        private const int FingerprintSamples = 64;

        /// <summary>
        /// 上流メッシュを読み直す間隔（秒）。
        ///
        /// 上流が「その場で」メッシュを書き換えるケースを拾うには読み直すしかないが、
        /// <c>Mesh.GetVertices</c> は全頂点のコピーであり、編集済み Renderer の数だけ
        /// 毎フレーム走らせるとシーン全体が重くなる（5 万頂点なら 1 Renderer あたり 600KB/frame）。
        /// 自分の編集内容の変化は <see cref="LiveEdits.Version"/> で即座に拾えるので、
        /// 上流側の検出だけをこの間隔まで落とす。
        /// </summary>
        private const double UpstreamProbeInterval = 0.2;

        /// <summary>
        /// プロキシ 1 つ分の状態。
        /// </summary>
        private sealed class Entry
        {
            public Renderer Original;
            public Renderer Proxy;

            /// <summary>上流ノードが出力したメッシュ。デルタ加算の基準。</summary>
            public Mesh Source;

            /// <summary>上流メッシュの頂点。毎回読み直すため List を使い回す。</summary>
            public readonly List<Vector3> UpstreamVertices = new List<Vector3>();

            /// <summary>UpstreamVertices から間引いたサンプル。上流の書き換え検出用。</summary>
            public Vector3[] Fingerprint;

            /// <summary>自分が生成したメッシュ。編集が無ければ null。</summary>
            public Mesh Generated;

            /// <summary>デルタ適用時に上書きした頂点の退避領域。編集頂点数ぶんしか使わない。</summary>
            public readonly List<Vector3> Restore = new List<Vector3>();

            public int Version = int.MinValue;

            /// <summary>次に上流メッシュを読み直す時刻。</summary>
            public double NextProbe;

            /// <summary>読み直しの位相（0..1）。全 Renderer が同じフレームに集中しないようずらす。</summary>
            public double Phase;

            /// <summary>一度でも上流を読めたか。初回だけは間隔を待たずに読む。</summary>
            public bool Probed;

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

                _entries[original] = new Entry
                {
                    Original = original,
                    Proxy = proxy,

                    // 全 Renderer の読み直しが同じフレームに集中しないよう位相をずらす
                    Phase = (original.GetInstanceID() & 0xFF) / 255.0,
                };
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
                entry.Probed = false;
                entry.WarnedNotReadable = false;
                DestroyGenerated(entry);
            }

            if (entry.Source == null) return;

            var now = EditorApplication.timeSinceStartup;
            var rebuild = entry.Version != LiveEdits.Version;

            // 上流の読み直しは間隔を空けて行う。ここを毎フレームにすると、
            // 編集していない待機中も編集済み Renderer の数だけ全頂点コピーが走り続ける。
            // 自分の編集内容の変化は LiveEdits.Version で即座に拾えるので、
            // 間隔を空けて困るのは「上流がメッシュを in-place で書き換える」ケースだけ。
            if (!entry.Probed || now >= entry.NextProbe)
            {
                entry.NextProbe = now + UpstreamProbeInterval * (0.75 + 0.5 * entry.Phase);

                if (ReadUpstream(entry, original) && UpdateFingerprint(entry)) rebuild = true;
            }

            if (rebuild && entry.UpstreamVertices.Count > 0)
            {
                entry.Version = LiveEdits.Version;
                Rebuild(entry);
            }

            if (entry.Generated != null) MeshDeltaApplier.SetSharedMesh(proxy, entry.Generated);
        }

        /// <summary>
        /// 上流メッシュの頂点を読み直す。読めなければ false。
        /// </summary>
        private static bool ReadUpstream(Entry entry, Renderer original)
        {
            // 上流がメッシュを「その場で」書き換えるケース（本ツールの UpdateVertices と同じ方式）では
            // インスタンスが変わらないため、読み直して変化を検出する。
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

                // 読めないメッシュを毎フレーム叩き続けないよう、探索済みとして扱う
                entry.Probed = true;
                return false;
            }

            entry.Probed = true;
            return true;
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

            MeshDeltaApplier.UpdateVertices(
                entry.Generated, entry.UpstreamVertices, edit, entry.Source.bounds, entry.Restore);
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
