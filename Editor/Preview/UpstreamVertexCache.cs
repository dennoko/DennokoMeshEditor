using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 同じ上流メッシュを参照する Renderer 同士で <see cref="UpstreamVertices"/> を共有する。
    ///
    /// 同じメッシュアセットを使う Renderer が何個あっても、頂点の保持（頂点数 × 12 バイト × 2）と
    /// 読み直し・比較は 1 回分で済む。プローブは間隔が来るまで何もしないので、
    /// 共有している Renderer が同じフレームに何度呼んでも実際の読み取りは 1 回になる。
    ///
    /// 上流フィルタが Renderer ごとに別の Mesh インスタンスを出力する場合は共有されない（従来と同じ）。
    ///
    /// 寿命は参照カウントで管理する。NDMF の再構築では新ノードの生成後に旧ノードの
    /// <c>Dispose</c> が走ることがあるが、参照カウントなので取り違えない。
    /// 中身はマネージドデータだけなので、ドメインリロードで自然に消える。
    /// </summary>
    internal static class UpstreamVertexCache
    {
        private sealed class Slot
        {
            public UpstreamVertices Value;
            public int RefCount;
        }

        /// <summary>
        /// Mesh の参照そのものをキーにする。UnityEngine.Object の等値比較は破棄済みオブジェクトで
        /// 振る舞いが変わるので使わない（破棄後の Release でも確実に同じ枠を引けるようにする）。
        /// </summary>
        private sealed class ReferenceComparer : IEqualityComparer<Mesh>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(Mesh x, Mesh y) => ReferenceEquals(x, y);

            public int GetHashCode(Mesh obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly Dictionary<Mesh, Slot> Slots = new Dictionary<Mesh, Slot>(ReferenceComparer.Instance);

        /// <summary>
        /// <paramref name="mesh"/> の共有インスタンスを取得する。必ず <see cref="Release"/> と対にする。
        /// </summary>
        internal static UpstreamVertices Acquire(Mesh mesh)
        {
            if (mesh == null) return null;

            if (!Slots.TryGetValue(mesh, out var slot))
            {
                slot = new Slot { Value = new UpstreamVertices(mesh) };
                Slots.Add(mesh, slot);
            }

            slot.RefCount++;
            return slot.Value;
        }

        /// <summary>
        /// <see cref="Acquire"/> で得たインスタンスを返す。参照が無くなったら破棄する。
        /// null を渡してもよい。
        /// </summary>
        internal static void Release(UpstreamVertices upstream)
        {
            if (upstream == null) return;
            // Mesh が Unity 側で破棄済みでも、参照キーを使って枠を解放する。
            if (!Slots.TryGetValue(upstream.Mesh, out var slot)) return;

            // 同じ Mesh で作り直された別インスタンスを誤って減らさない
            if (!ReferenceEquals(slot.Value, upstream)) return;

            if (--slot.RefCount > 0) return;

            Slots.Remove(upstream.Mesh);
        }
    }
}
