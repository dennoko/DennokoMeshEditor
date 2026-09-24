using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 上流メッシュ（上流ノードの出力、または元のメッシュアセット）の頂点と、その変化の検出。
    ///
    /// <b>なぜ読み直し続けるのか</b>：
    /// 上流がメッシュを「その場で」書き換える場合（本ツールの UpdateVertices と同じ方式や、
    /// 他のエディタ拡張がアセットメッシュを直接書き換える場合）はインスタンスが変わらない。
    /// NDMF の引数なし <c>Observe(mesh)</c> は <c>ObjectChangeEvents</c> 経由の通知に頼るので、
    /// スクリプトによる直接の書き換えは拾えない。読み直す以外に検出する方法が無い。
    ///
    /// <b>どう安くしているか</b>：
    ///   - 通常のプローブ（<see cref="ProbeInterval"/> ごと）は、頂点バッファを直接参照して
    ///     64 点だけを読み、前回の値と比べる。全頂点のコピーは発生しない
    ///   - サンプルが変わったとき、または <see cref="FullReadInterval"/> ごとに全頂点を別バッファへ読み、
    ///     保持している頂点と全件比較する。1 頂点でも違えば入れ替えて <see cref="Generation"/> を進める
    /// サンプルは 64 点しか見ないので、サンプル外だけが変わる書き換えは全件比較の周期で拾う。
    /// 64 点の一致だけで「変わっていない」とは確定しない。
    ///
    /// 直接参照できない形式（Float32 以外の位置属性、読み取り不可のメッシュ）や、
    /// 参照中に例外が出た場合は、毎回の全頂点読み直しに戻す（安全側）。
    /// </summary>
    internal sealed class UpstreamVertices
    {
        /// <summary>変化検出に使うサンプル点数。</summary>
        private const int SampleCount = 64;

        /// <summary>
        /// サンプルによる軽量プローブの間隔（秒）。
        /// 自分の編集内容の変化は <see cref="EditState"/> の比較で即座に拾えるので、
        /// 上流側の検出だけをこの間隔まで落とす。
        /// </summary>
        private const double ProbeInterval = 0.2;

        /// <summary>
        /// 全頂点を読み直して全件比較する間隔（秒）。サンプル外だけが変わる書き換えを拾う安全網。
        /// </summary>
        private const double FullReadInterval = 2.0;

        private enum SampleResult
        {
            Unchanged,
            Changed,

            /// <summary>直接参照できない。全頂点の読み直しで判定する。</summary>
            Unsupported,
        }

        internal readonly Mesh Mesh;

        /// <summary>最後に確定した頂点。</summary>
        private List<Vector3> _vertices = new List<Vector3>();

        /// <summary>全件比較用の読み取り先。確定した内容を上書きしないよう別に持つ。</summary>
        private List<Vector3> _scratch = new List<Vector3>();

        /// <summary><see cref="_vertices"/> から等間隔に取ったサンプル（x, y, z の順）。</summary>
        private readonly float[] _samples = new float[SampleCount * 3];

        /// <summary>サンプルを取ったときの頂点数。</summary>
        private int _sampledVertexCount = -1;

        /// <summary>読み直しの位相（0..1）。全メッシュの読み直しが同じフレームに集中しないようずらす。</summary>
        private readonly double _phase;

        private bool _attempted;
        private bool _hasRead;
        private double _nextProbe;
        private double _nextFullRead;
        private bool _warnedNotReadable;

        internal UpstreamVertices(Mesh mesh)
        {
            Mesh = mesh;
            _phase = mesh != null ? (mesh.GetInstanceID() & 0xFF) / 255.0 : 0.0;
        }

        /// <summary>
        /// 頂点の内容の世代。読み直した内容が前回と 1 頂点でも違うたびに進む。
        /// </summary>
        internal int Generation { get; private set; }

        /// <summary>
        /// 最後に確定した頂点。呼び出し側は、同期的に元へ戻す一時書き込み
        /// （<see cref="VertexRestoreBuffer"/>）以外で書き換えてはならない。複数の Renderer で共有されうる。
        /// </summary>
        internal List<Vector3> Vertices => _vertices;

        /// <summary>一度でも読めたか。読めていない間は生成メッシュを作れない。</summary>
        internal bool HasRead => _hasRead;

        /// <summary>
        /// 必要なら上流を調べ、内容が変わっていれば <see cref="Generation"/> を進める。
        /// 同じフレームに何度呼ばれても、間隔が来るまでは何もしない。
        /// </summary>
        /// <param name="context">警告の送り先。</param>
        internal void Probe(double now, Object context)
        {
            if (Mesh == null) return;
            if (_attempted && now < _nextProbe) return;

            using var marker = PreviewMarkers.ProbeUpstream.Auto();

            _attempted = true;
            _nextProbe = now + ProbeInterval * (0.75 + 0.5 * _phase);

            var readAll = !_hasRead || now >= _nextFullRead;
            if (!readAll && ProbeSamples() != SampleResult.Unchanged) readAll = true;

            if (!readAll) return;

            try
            {
                ReadAll(now, context);
            }
            catch (Exception e)
            {
                // GetVertices が例外を投げても NDMF の後続ノードを止めない。
                // 前回読めた頂点と世代は維持し、次のプローブで全件読み直しを再試行する。
                if (_warnedNotReadable) return;

                _warnedNotReadable = true;
                Debug.LogWarning(
                    $"[Dennoko Mesh Editor] 上流メッシュの頂点を読み取れませんでした。"
                    + $"次のプローブで再試行します。 ({e.GetType().Name}: {e.Message})",
                    context);
            }
        }

        /// <summary>
        /// 全頂点を読み、保持している頂点と全件比較する。違っていれば入れ替えて世代を進める。
        /// </summary>
        private void ReadAll(double now, Object context)
        {
            PreviewStats.CountFullRead();
            Mesh.GetVertices(_scratch);

            // 頂点を持つはずなのに読めなかった場合は Read/Write が無効な可能性が高い。
            // 事前に isReadable で弾くとエディタ上で読めているケースまで止めてしまうので、
            // 実際に失敗したときだけ 1 度だけ警告する。読めない間は世代を進めない
            if (_scratch.Count == 0 && Mesh.vertexCount > 0)
            {
                if (!_warnedNotReadable)
                {
                    _warnedNotReadable = true;
                    Debug.LogWarning(
                        $"[Dennoko Mesh Editor] {(context != null ? context.name : "?")} のメッシュ「{Mesh.name}」から"
                        + "頂点を読み取れませんでした。インポート設定の Read/Write Enabled を有効にしてください。",
                        context);
                }

                return;
            }

            if (!_hasRead || !SameVertices(_vertices, _scratch))
            {
                (_vertices, _scratch) = (_scratch, _vertices);

                unchecked
                {
                    Generation++;
                }
            }

            _hasRead = true;
            TakeSamples();
            _nextFullRead = now + FullReadInterval * (0.75 + 0.5 * _phase);
            _warnedNotReadable = false;
        }

        /// <summary>
        /// 頂点数と全頂点の x / y / z を厳密に比較する。
        /// Vector3 の <c>==</c> は近似比較なので使わない。float.Equals は NaN 同士も一致とみなすので、
        /// NaN を含むメッシュで毎回「変わった」と判定し続けることもない。
        /// </summary>
        private static bool SameVertices(List<Vector3> a, List<Vector3> b)
        {
            if (a.Count != b.Count) return false;

            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (!x.x.Equals(y.x) || !x.y.Equals(y.y) || !x.z.Equals(y.z)) return false;
            }

            return true;
        }

        private void TakeSamples()
        {
            var count = _vertices.Count;
            _sampledVertexCount = count;

            var samples = Math.Min(SampleCount, count);
            for (var i = 0; i < samples; i++)
            {
                var v = _vertices[SampleIndex(i, samples, count)];
                _samples[i * 3] = v.x;
                _samples[i * 3 + 1] = v.y;
                _samples[i * 3 + 2] = v.z;
            }
        }

        private static int SampleIndex(int i, int samples, int count)
        {
            return (int)((long)i * count / samples);
        }

        /// <summary>
        /// 頂点バッファを直接参照して、サンプル点だけを前回の値と比べる。全頂点のコピーは発生しない。
        /// </summary>
        private SampleResult ProbeSamples()
        {
            // 読み取り不可のメッシュを直接参照すると失敗しうるので、従来どおり全頂点の読み直しに任せる
            if (!Mesh.isReadable) return SampleResult.Unsupported;

            PreviewStats.CountSampleProbe();

            Mesh.MeshDataArray array = default;
            var acquired = false;

            try
            {
                array = Mesh.AcquireReadOnlyMeshData(Mesh);
                acquired = true;

                var data = array[0];
                var count = data.vertexCount;
                if (count != _sampledVertexCount) return SampleResult.Changed;
                if (count == 0) return SampleResult.Unchanged;

                if (!data.HasVertexAttribute(VertexAttribute.Position)) return SampleResult.Unsupported;
                if (data.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
                    return SampleResult.Unsupported;
                if (data.GetVertexAttributeDimension(VertexAttribute.Position) != 3) return SampleResult.Unsupported;

                var stream = data.GetVertexAttributeStream(VertexAttribute.Position);
                var offset = data.GetVertexAttributeOffset(VertexAttribute.Position);
                var stride = data.GetVertexBufferStride(stream);

                // float 単位で読むので、4 バイト境界に揃っていない配置は扱わない
                if (offset % 4 != 0 || stride % 4 != 0) return SampleResult.Unsupported;

                var floats = data.GetVertexData<float>(stream);
                var strideFloats = stride / 4;
                var offsetFloats = offset / 4;

                var samples = Math.Min(SampleCount, count);
                for (var i = 0; i < samples; i++)
                {
                    var at = (long)SampleIndex(i, samples, count) * strideFloats + offsetFloats;
                    if (at + 2 >= floats.Length) return SampleResult.Unsupported;

                    var b = (int)at;
                    if (!floats[b].Equals(_samples[i * 3])
                        || !floats[b + 1].Equals(_samples[i * 3 + 1])
                        || !floats[b + 2].Equals(_samples[i * 3 + 2]))
                    {
                        return SampleResult.Changed;
                    }
                }

                return SampleResult.Unchanged;
            }
            catch (Exception)
            {
                // 直接参照に失敗しても、全頂点の読み直しで判定できるので握りつぶしてよい
                return SampleResult.Unsupported;
            }
            finally
            {
                if (acquired) array.Dispose();
            }
        }
    }
}
