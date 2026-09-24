using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// デルタ適用の共通ロジック。プレビュー・ビルド・ベイクのすべてがこれを共有することで、
    /// 三者の結果が食い違わないことをコードの共有によって保証する。
    ///
    /// 法線・接線は再計算しない（設計方針）。シェーディングの変化を避けるため。
    /// </summary>
    internal static class MeshDeltaApplier
    {
        /// <summary>
        /// Renderer が参照しているメッシュを取得する。SkinnedMeshRenderer / MeshRenderer の両対応。
        /// </summary>
        internal static Mesh GetSharedMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case null:
                    return null;
                case SkinnedMeshRenderer smr:
                    return smr.sharedMesh;
                default:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
            }
        }

        internal static void SetSharedMesh(Renderer renderer, Mesh mesh)
        {
            switch (renderer)
            {
                case null:
                    return;
                case SkinnedMeshRenderer smr:
                    smr.sharedMesh = mesh;
                    return;
                default:
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = mesh;
                    return;
            }
        }

        /// <summary>
        /// 編集データがこのメッシュに適用可能か。頂点数の一致と、インデックスの範囲を確認する。
        /// </summary>
        internal static bool IsCompatible(Mesh mesh, MeshEdit edit)
        {
            if (mesh == null || edit == null || !edit.HasEdits) return false;

            // vertexCount が 0 のデータは旧形式または未記録。頂点数チェックは省略する。
            if (edit.vertexCount != 0 && edit.vertexCount != mesh.vertexCount) return false;

            var count = edit.Count;
            for (var i = 0; i < count; i++)
            {
                var index = edit.GetIndex(i);
                if (index < 0 || index >= mesh.vertexCount) return false;
            }

            return true;
        }

        /// <summary>
        /// 頂点配列に対してデルタを直接加算する。適用処理の実体はこれだけであり、
        /// vertices が何であるか（上流ツールが何をしたか）を一切問わない。
        /// </summary>
        internal static void ApplyInPlace(Vector3[] vertices, MeshEdit edit)
        {
            if (vertices == null || edit == null) return;

            var count = edit.Count;
            for (var i = 0; i < count; i++)
            {
                var index = edit.GetIndex(i);
                if (index < 0 || index >= vertices.Length) continue;
                vertices[index] += edit.GetDelta(i);
            }
        }

        /// <summary>
        /// デルタを適用した新しいメッシュを生成する。元メッシュは変更しない。
        /// ボーンウェイト・既存シェイプキー・UV・バインドポーズは Instantiate によって引き継がれる。
        /// </summary>
        internal static Mesh CreateEdited(Mesh source, MeshEdit edit)
        {
            if (!IsCompatible(source, edit)) return null;

            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_edited";

            var vertices = mesh.vertices;
            ApplyInPlace(vertices, edit);
            mesh.vertices = vertices;

            // 法線・接線は再計算しない
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>
        /// 生成済みメッシュの頂点を「基準頂点 + デルタ」で書き換える。ドラッグ中に毎フレーム呼ばれる。
        ///
        /// 基準頂点のコピーは取らない。<paramref name="baseVertices"/> に直接デルタを書き込み、
        /// アップロード後に書き込んだ頂点だけを元へ戻す。デルタは疎なので復元は O(編集頂点数) で済み、
        /// 全頂点分の作業バッファ（Renderer あたり 頂点数 × 12 バイト）も不要になる。
        /// 戻し方の詳細は <see cref="VertexRestoreBuffer"/>。
        /// </summary>
        /// <param name="sourceBounds">上流メッシュのバウンズ。これを膨らませて使う。</param>
        /// <param name="restore">復元用の退避領域。呼び出し側で使い回す。</param>
        internal static void UpdateVertices(Mesh mesh, List<Vector3> baseVertices, MeshEdit edit,
            Bounds sourceBounds, VertexRestoreBuffer restore)
        {
            if (mesh == null || baseVertices == null || edit == null || restore == null) return;

            restore.Begin();

            var count = edit.Count;
            var vertexCount = baseVertices.Count;
            var maxDeltaSq = 0f;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var index = edit.GetIndex(i);
                    if (index < 0 || index >= vertexCount) continue;

                    var delta = edit.GetDelta(i);
                    var sq = delta.sqrMagnitude;
                    if (sq > maxDeltaSq) maxDeltaSq = sq;

                    restore.Add(baseVertices, index, delta);
                }

                mesh.SetVertices(baseVertices);
            }
            finally
            {
                // 基準頂点は上流の読み取り結果そのもの（共有されうる）なので、
                // 途中で例外が出ても書き込んだ分を必ず戻す
                restore.RestoreTo(baseVertices);
            }

            ApplyBounds(mesh, sourceBounds, maxDeltaSq);
        }

        /// <summary>
        /// <see cref="UpdateVertices(Mesh, List{Vector3}, MeshEdit, Bounds, VertexRestoreBuffer)"/> の
        /// 辞書版。複数の編集を合成した結果や、編集セッション中の未確定データを適用するときに使う。
        /// 辞書は列挙するだけで、確保は発生しない。
        /// </summary>
        internal static void UpdateVertices(Mesh mesh, List<Vector3> baseVertices, Dictionary<int, Vector3> deltas,
            Bounds sourceBounds, VertexRestoreBuffer restore)
        {
            if (mesh == null || baseVertices == null || deltas == null || restore == null) return;

            restore.Begin();

            var vertexCount = baseVertices.Count;
            var maxDeltaSq = 0f;

            try
            {
                foreach (var pair in deltas)
                {
                    var index = pair.Key;
                    if (index < 0 || index >= vertexCount) continue;

                    var delta = pair.Value;
                    var sq = delta.sqrMagnitude;
                    if (sq > maxDeltaSq) maxDeltaSq = sq;

                    restore.Add(baseVertices, index, delta);
                }

                mesh.SetVertices(baseVertices);
            }
            finally
            {
                restore.RestoreTo(baseVertices);
            }

            ApplyBounds(mesh, sourceBounds, maxDeltaSq);
        }

        /// <summary>
        /// 法線・接線は再計算しない。
        /// バウンズも全頂点走査（RecalculateBounds）はせず、上流のバウンズを
        /// 最大デルタ長ぶん膨らませる。保守的に大きくなるだけなのでカリング上は安全。
        /// </summary>
        private static void ApplyBounds(Mesh mesh, Bounds sourceBounds, float maxDeltaSq)
        {
            var margin = Mathf.Sqrt(maxDeltaSq);
            sourceBounds.Expand(margin * 2f);
            mesh.bounds = sourceBounds;
        }

        /// <summary>
        /// 元の形状を保ったまま、編集分を新規シェイプキーとして追加したメッシュを生成する。
        /// </summary>
        internal static Mesh CreateWithBlendShape(Mesh source, MeshEdit edit, string shapeName)
        {
            if (!IsCompatible(source, edit)) return null;

            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_edited";

            var deltaVertices = new Vector3[mesh.vertexCount];
            var count = edit.Count;
            for (var i = 0; i < count; i++)
            {
                var index = edit.GetIndex(i);
                if (index < 0 || index >= deltaVertices.Length) continue;
                deltaVertices[index] = edit.GetDelta(i);
            }

            // 法線・接線のデルタは付けない（再計算しない方針と同じ理由）
            mesh.AddBlendShapeFrame(shapeName, 100f, deltaVertices, null, null);

            return mesh;
        }
    }

    /// <summary>
    /// <see cref="MeshDeltaApplier.UpdateVertices(Mesh, List{Vector3}, MeshEdit, Bounds, VertexRestoreBuffer)"/> が
    /// 基準頂点へ一時的に書き込んだ値を元へ戻すための退避領域。
    ///
    /// 「デルタを引き算して戻す」ではなく「元の値を退避して書き戻す」形にしているのは、
    /// float の加減算が可逆でないため。誤差が毎フレーム蓄積するのを避ける。
    /// 書き込む前にインデックスと元の値を記録するので、途中で例外が出ても記録した分だけを
    /// 正しく戻せる。逆順に戻すので、インデックスが重複していても元の値に戻る。
    /// </summary>
    internal sealed class VertexRestoreBuffer
    {
        private readonly List<int> _indices = new List<int>();
        private readonly List<Vector3> _values = new List<Vector3>();

        internal void Begin()
        {
            _indices.Clear();
            _values.Clear();
        }

        /// <summary>元の値を退避してから、デルタを加算する。</summary>
        internal void Add(List<Vector3> vertices, int index, Vector3 delta)
        {
            var original = vertices[index];
            _indices.Add(index);
            _values.Add(original);
            vertices[index] = original + delta;
        }

        internal void RestoreTo(List<Vector3> vertices)
        {
            for (var i = _indices.Count - 1; i >= 0; i--)
            {
                vertices[_indices[i]] = _values[i];
            }

            _indices.Clear();
            _values.Clear();
        }
    }

    /// <summary>
    /// プレビュー用に生成した一時メッシュ（<see cref="HideFlags.HideAndDontSave"/>）の追跡。
    ///
    /// ドメインリロードでは <c>IRenderFilterNode.Dispose</c> が呼ばれず、
    /// DontSave なオブジェクトはリロードを生き延びるため、参照を失ったメッシュが
    /// エディタ再起動まで回収されない。リロード直前に明示的に破棄する。
    /// </summary>
    [InitializeOnLoad]
    internal static class GeneratedMeshTracker
    {
        // 大量の Renderer を扱うので、登録解除が O(n) にならないよう HashSet にする
        private static readonly HashSet<Mesh> Tracked = new HashSet<Mesh>();

        static GeneratedMeshTracker()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
        }

        internal static void Track(Mesh mesh)
        {
            if (mesh != null) Tracked.Add(mesh);
        }

        internal static void Forget(Mesh mesh)
        {
            if (mesh != null) Tracked.Remove(mesh);
        }

        private static void DestroyAll()
        {
            foreach (var mesh in Tracked)
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }

            Tracked.Clear();
        }
    }
}
