using System.Collections.Generic;
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

            foreach (var index in edit.indices)
            {
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
            var count = Mathf.Min(edit.indices.Count, edit.deltas.Count);
            for (var i = 0; i < count; i++)
            {
                var index = edit.indices[i];
                if (index < 0 || index >= vertices.Length) continue;
                vertices[index] += edit.deltas[i];
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
        /// 生成済みメッシュの頂点を「基準頂点 + デルタ」で書き換える。
        /// ドラッグ中に毎フレーム呼ばれるため、作業配列は呼び出し側で使い回す。
        /// </summary>
        internal static void UpdateVertices(Mesh mesh, Vector3[] baseVertices, MeshEdit edit, ref Vector3[] scratch)
        {
            if (mesh == null || baseVertices == null) return;

            if (scratch == null || scratch.Length != baseVertices.Length)
            {
                scratch = new Vector3[baseVertices.Length];
            }

            System.Array.Copy(baseVertices, scratch, baseVertices.Length);
            if (edit != null) ApplyInPlace(scratch, edit);

            mesh.SetVertices(scratch);

            // 法線・接線は再計算しない
            mesh.RecalculateBounds();
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
            var count = Mathf.Min(edit.indices.Count, edit.deltas.Count);
            for (var i = 0; i < count; i++)
            {
                var index = edit.indices[i];
                if (index < 0 || index >= deltaVertices.Length) continue;
                deltaVertices[index] = edit.deltas[i];
            }

            // 法線・接線のデルタは付けない（再計算しない方針と同じ理由）
            mesh.AddBlendShapeFrame(shapeName, 100f, deltaVertices, null, null);

            return mesh;
        }

        /// <summary>
        /// 編集データを持つ Renderer と、その編集データの組を列挙する。
        /// </summary>
        internal static IEnumerable<MeshEdit> EnumerateActiveEdits(DenMeshEditor component)
        {
            if (component == null) yield break;

            foreach (var edit in component.edits)
            {
                if (edit == null || edit.target == null || !edit.HasEdits) continue;
                yield return edit;
            }
        }
    }
}
