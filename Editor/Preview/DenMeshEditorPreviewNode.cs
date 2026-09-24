using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenMeshEditor.Editor
{
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

            /// <summary>
            /// <see cref="Source"/> の頂点と、その変化の検出。同じ上流メッシュを参照する Renderer 同士で
            /// 共有される（<see cref="UpstreamVertexCache"/>）ので、読み取り専用として扱う。
            /// </summary>
            public UpstreamVertices Upstream;

            /// <summary>自分が生成したメッシュ。編集が無ければ null。</summary>
            public Mesh Generated;

            /// <summary>デルタ適用時に上書きした頂点の退避領域。編集頂点数ぶんしか使わない。</summary>
            public readonly VertexRestoreBuffer Restore = new VertexRestoreBuffer();

            /// <summary>複数の編集や未確定データを合成するときの作業領域。使い回して確保を避ける。</summary>
            public readonly Dictionary<int, Vector3> Merged = new Dictionary<int, Vector3>();

            /// <summary>現在の編集状態。毎フレーム集め直す（List は使い回す）。</summary>
            public List<EditState> Current = new List<EditState>();

            /// <summary>最後にメッシュへ反映できたときの編集状態。</summary>
            public List<EditState> Applied = new List<EditState>();

            /// <summary><see cref="Applied"/> が有効か。上流が差し替わったら無効に戻す。</summary>
            public bool HasApplied;

            /// <summary>最後にメッシュへ反映できたときの <see cref="UpstreamVertices.Generation"/>。</summary>
            public int AppliedUpstreamGeneration;

            /// <summary>作り直しの失敗を 1 度だけ報告するためのフラグ。成功すると戻す。</summary>
            public bool LoggedFailure;
        }

        private readonly Dictionary<Renderer, Entry> _entries = new Dictionary<Renderer, Entry>();
        private readonly List<DenMeshEditor> _components;

        public RenderAspects WhatChanged => RenderAspects.Mesh;

        internal DenMeshEditorPreviewNode(IEnumerable<(Renderer, Renderer)> proxyPairs, List<DenMeshEditor> components)
        {
            _components = components;
            PreviewStats.CountNodeCreated();
#if DEN_MESH_EDITOR_DEBUG
            AliveNodes.Add(this);
#endif

            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null) continue;

                _entries[original] = new Entry
                {
                    Original = original,
                    Proxy = proxy,
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
            using (PreviewMarkers.OnFrame.Auto())
            {
                OnFrameCore(original, proxy);
            }
        }

        private void OnFrameCore(Renderer original, Renderer proxy)
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
                UpstreamVertexCache.Release(entry.Upstream);
                entry.Upstream = UpstreamVertexCache.Acquire(upstream);
                entry.HasApplied = false;
                DestroyGenerated(entry);
            }

            if (entry.Source == null) return;

            // 上流の読み直しは間隔を空けて、なるべく安く行う（詳細は UpstreamVertices）。
            // 間隔を空けて困るのは「上流がメッシュを in-place で書き換える」ケースだけ
            entry.Upstream.Probe(EditorApplication.timeSinceStartup, original);

            // 通知ではなく状態の比較で作り直しを決める。自分に関係する編集の比較値と
            // 上流の世代が、最後に反映できたときと 1 つでも違えば作り直す。
            // 複数カメラで同じフレームに何度呼ばれても、2 回目以降は一致するので作り直さない
            EditState.Collect(_components, original, entry.Current, true);

            var dirty = !entry.HasApplied
                        || entry.AppliedUpstreamGeneration != entry.Upstream.Generation
                        || !EditState.SequenceEqual(entry.Applied, entry.Current);

            if (dirty) TryRebuild(entry, original);

            if (entry.Generated != null) MeshDeltaApplier.SetSharedMesh(proxy, entry.Generated);

            // 描画後に読み戻して、下流フィルタの上書きを検出させる。
            // 編集が無くて何も代入していない場合は上流メッシュを申告する。こうしておくと
            // 「編集を 1 つも持たない状態」でも併用構成を検出できる
            DownstreamGuard.Expect(this, original, proxy, entry.Generated != null ? entry.Generated : entry.Source);
        }


        /// <summary>
        /// 作り直しを試み、成功したときだけ「反映済み」の状態を進める。
        ///
        /// 失敗・未実行のときは反映済みの状態を据え置くので、次のフレームで必ず再試行される。
        /// 例外はここで止める。NDMF の <c>NodeController.OnFrame</c> は例外を捕まえないため、
        /// 素通しすると同じフレームの他ノードの処理（<c>ProxyPipeline.OnFrame</c> のループ）まで止まる。
        /// </summary>
        private void TryRebuild(Entry entry, Renderer original)
        {
            // 反映に使う上流の世代は、作り直しの前に控える
            var upstreamGeneration = entry.Upstream.Generation;

            bool applied;
            try
            {
                applied = Rebuild(entry);
            }
            catch (Exception e)
            {
                // 失敗は毎フレーム再試行されるので、報告は成功するまでの間 1 度だけにする
                if (!entry.LoggedFailure)
                {
                    entry.LoggedFailure = true;
                    Debug.LogError(
                        $"[Dennoko Mesh Editor] {original.name} のプレビューを更新できませんでした。\n{e}",
                        original);
                }

                return;
            }

            if (!applied) return;

            entry.LoggedFailure = false;
            entry.HasApplied = true;
            entry.AppliedUpstreamGeneration = upstreamGeneration;

            // 反映した状態を控える。確保しないよう 2 本のリストを入れ替えて使い回す
            (entry.Applied, entry.Current) = (entry.Current, entry.Applied);
        }

        /// <summary>
        /// 現在の上流頂点と編集内容から生成メッシュを作り直す。
        /// 反映を完了できた（編集が空で生成メッシュを破棄した場合を含む）ときだけ true。
        /// 上流をまだ読めていない場合は何もせず false を返す。
        /// </summary>
        private bool Rebuild(Entry entry)
        {
            if (entry.Source == null || entry.Upstream == null || !entry.Upstream.HasRead) return false;

            var vertices = entry.Upstream.Vertices;
            if (vertices.Count == 0) return false;

            using var marker = PreviewMarkers.Rebuild.Auto();
            PreviewStats.CountRebuild();

            DenMeshEditorPreviewFilter.GatherResult kind;
            MeshEdit single;
            using (PreviewMarkers.GatherEdits.Auto())
            {
                kind = DenMeshEditorPreviewFilter.GatherEditsInto(
                    _components, entry.Original, vertices.Count, entry.Merged, true, out single);
            }

            if (kind == DenMeshEditorPreviewFilter.GatherResult.Empty)
            {
                DestroyGenerated(entry);
                return true;
            }

            var created = false;
            if (entry.Generated == null)
            {
                // IRenderFilter の規約：メッシュは新規インスタンスを作り、Dispose で破棄する
                using (PreviewMarkers.Instantiate.Auto())
                {
                    entry.Generated = Object.Instantiate(entry.Source);
                }

                PreviewStats.CountGeneratedCreated();
                entry.Generated.name = entry.Source.name + " (Dennoko Mesh Editor)";
                entry.Generated.hideFlags = HideFlags.HideAndDontSave;

                // 毎フレーム SetVertices するので動的メッシュとして確保させる
                entry.Generated.MarkDynamic();

                // ドメインリロードで Dispose が走らないケースに備えて追跡する
                GeneratedMeshTracker.Track(entry.Generated);
                created = true;
            }

            try
            {
                if (kind == DenMeshEditorPreviewFilter.GatherResult.Single)
                {
                    MeshDeltaApplier.UpdateVertices(
                        entry.Generated, vertices, single, entry.Source.bounds, entry.Restore);
                }
                else
                {
                    MeshDeltaApplier.UpdateVertices(
                        entry.Generated, vertices, entry.Merged, entry.Source.bounds, entry.Restore);
                }
            }
            catch
            {
                // 頂点を書き込めなかった複製は上流の単なるコピーなので、残さず捨てる。
                // 既存メッシュの更新に失敗した場合は、前回の内容のまま次フレームで再試行する
                if (created) DestroyGenerated(entry);
                throw;
            }

            return true;
        }

        private static void DestroyGenerated(Entry entry)
        {
            if (entry.Generated == null) return;

            GeneratedMeshTracker.Forget(entry.Generated);
            Object.DestroyImmediate(entry.Generated);
            PreviewStats.CountGeneratedDestroyed();
            entry.Generated = null;
        }

        /// <summary>
        /// パイプライン再構築時に、生成済みメッシュを持ったまま自分自身を再利用する。
        ///
        /// 下流フィルタに上書きされている構成では、ドラッグ中の更新を
        /// <see cref="LiveEdits.SyncedVersion"/> 経由のパイプライン再構築として流す。
        /// ここで <c>null</c> を返すと、そのたびにノードごと作り直されて
        /// 全頂点のメッシュ複製（<c>Object.Instantiate</c>）が走ってしまう。
        /// 自分自身を返せば <c>NodeController</c> が参照カウントで寿命を管理してくれるので、
        /// 生成済みメッシュを使い回したまま下流だけを作り直させられる
        /// （<see cref="WhatChanged"/> が <c>Mesh</c> を返すので下流は必ず更新される）。
        ///
        /// プロキシの対応が変わっている場合だけは作り直す。共有状態を書き換えずに済ませる。
        ///
        /// <b>編集内容の変化はここでは見ない。</b> NDMF はノードを再利用する場合も新しい
        /// <c>NodeController</c> を作り、そのコンストラクタ内で <see cref="OnFrame"/> を呼ぶ。
        /// つまり下流ノードの生成より前に <see cref="EditState"/> の比較が必ず走り、
        /// 内容が変わっていればそこで作り直される（セッション外の Undo や Prefab の Revert でも
        /// 更新が漏れないのはこの性質による）。
        /// </summary>
        public Task<IRenderFilterNode> Refresh(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            var reused = TryReuse(proxyPairs, context, updatedAspects);
            PreviewStats.CountNodeRefresh(reused);

            return Task.FromResult<IRenderFilterNode>(reused ? this : null);
        }

        private bool TryReuse(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            // 上流のメッシュが差し替わった場合は、基準頂点から取り直した方が確実
            if ((updatedAspects & RenderAspects.Mesh) != 0) return false;

            var matched = 0;
            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null) continue;
                if (!_entries.TryGetValue(original, out var entry)) return false;
                if (entry.Proxy != proxy) return false;

                matched++;
            }

            if (matched != _entries.Count) return false;

            // Instantiate を通らないので、監視は新しい ComputeContext へ張り直す
            DenMeshEditorPreviewFilter.ObserveNodeInputs(context, _components, _entries.Keys);

            return true;
        }

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                ProxyRegistry.Remove(entry.Original, entry.Proxy);
                DownstreamGuard.Forget(this, entry.Original);
                DestroyGenerated(entry);

                UpstreamVertexCache.Release(entry.Upstream);
                entry.Upstream = null;
            }

            _entries.Clear();
#if DEN_MESH_EDITOR_DEBUG
            AliveNodes.Remove(this);
#endif
        }

#if DEN_MESH_EDITOR_DEBUG
        private static readonly HashSet<DenMeshEditorPreviewNode> AliveNodes = new HashSet<DenMeshEditorPreviewNode>();

        /// <summary>
        /// プレビューの生成メッシュと、ビルドと同じ経路（<see cref="DenMeshEditorPreviewFilter.GatherEdits"/> +
        /// <see cref="MeshDeltaApplier.CreateEdited"/>）で作ったメッシュの頂点が一致するかを調べる。
        ///
        /// 基準が違って比較できないもの（上流フィルタがメッシュを差し替えている、未確定データがある、
        /// まだ反映が終わっていない）は数えるだけで比較しない。
        /// </summary>
        [MenuItem("Tools/dennokoworks/Dennoko Mesh Editor/Debug/Compare Preview With Build Result")]
        private static void CompareWithBuildResult()
        {
            var compared = 0;
            var mismatched = 0;
            var skipped = 0;
            var states = new List<EditState>();

            foreach (var node in AliveNodes)
            {
                foreach (var entry in node._entries.Values)
                {
                    if (entry.Original == null || entry.Source == null
                        || entry.Source != MeshDeltaApplier.GetSharedMesh(entry.Original))
                    {
                        skipped++;
                        continue;
                    }

                    EditState.Collect(node._components, entry.Original, states, true);

                    var pending = !entry.HasApplied || !EditState.SequenceEqual(entry.Applied, states);
                    var hasLive = false;
                    foreach (var state in states)
                    {
                        if (state.LiveStamp != 0) hasLive = true;
                    }

                    if (pending || hasLive)
                    {
                        skipped++;
                        continue;
                    }

                    var edit = DenMeshEditorPreviewFilter.GatherEdits(
                        node._components, entry.Original, entry.Source.vertexCount);
                    var expected = edit != null ? MeshDeltaApplier.CreateEdited(entry.Source, edit) : null;

                    try
                    {
                        compared++;
                        if (SameVertices(expected, entry.Generated)) continue;

                        mismatched++;
                        Debug.LogWarning(
                            $"[Dennoko Mesh Editor] {entry.Original.name}: プレビューとビルド結果の頂点が一致しません。",
                            entry.Original);
                    }
                    finally
                    {
                        if (expected != null) Object.DestroyImmediate(expected);
                    }
                }
            }

            Debug.Log(
                $"[Dennoko Mesh Editor] プレビューとビルド結果の比較 — 比較 {compared} / 不一致 {mismatched} / 対象外 {skipped}");
        }

        private static bool SameVertices(Mesh expected, Mesh actual)
        {
            if (expected == null || actual == null) return expected == actual;

            var a = expected.vertices;
            var b = actual.vertices;
            if (a.Length != b.Length) return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (!a[i].x.Equals(b[i].x) || !a[i].y.Equals(b[i].y) || !a[i].z.Equals(b[i].z)) return false;
            }

            return true;
        }
#endif
    }
}
