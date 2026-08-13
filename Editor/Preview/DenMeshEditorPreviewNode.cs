using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
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
