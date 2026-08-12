using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor
{
    /// <summary>
    /// プロポーショナル編集の減衰カーブ。
    /// </summary>
    public enum FalloffType
    {
        Smooth,
        Linear,
        Sharp,
        Constant,
    }

    /// <summary>
    /// ミラー編集の対称軸。アバタールートのローカル空間で解釈する。
    /// </summary>
    public enum MirrorAxis
    {
        X,
        Y,
        Z,
    }

    /// <summary>
    /// 1 つの Renderer に対する編集データ。
    ///
    /// 動かした頂点だけを疎に保持する。適用は元の頂点座標への単純な加算であり、
    /// 上流の NDMF ツールがどのようなメッシュ変形を行っていても、
    /// 頂点数と頂点順序さえ保たれていれば正しく動作する（シェイプキーと同じ意味論）。
    /// デルタはメッシュローカル（バインドポーズ）空間で保持する。
    /// </summary>
    [Serializable]
    public class MeshEdit
    {
        public Renderer target;

        [Tooltip("編集時点の頂点数。適用前の整合性チェックに使用します。")]
        public int vertexCount;

        public List<int> indices = new List<int>();
        public List<Vector3> deltas = new List<Vector3>();

        public bool HasEdits => indices.Count > 0 && indices.Count == deltas.Count;

        public Dictionary<int, Vector3> ToDictionary()
        {
            var dict = new Dictionary<int, Vector3>(indices.Count);
            var count = Mathf.Min(indices.Count, deltas.Count);
            for (var i = 0; i < count; i++)
            {
                dict[indices[i]] = deltas[i];
            }

            return dict;
        }

        /// <summary>
        /// 辞書の内容で上書きする。ゼロに戻った頂点は保存しない（データを膨らませないため）。
        /// </summary>
        public void SetFrom(Dictionary<int, Vector3> dict, int newVertexCount)
        {
            indices.Clear();
            deltas.Clear();
            foreach (var kv in dict)
            {
                if (kv.Value.sqrMagnitude <= 0f) continue;
                indices.Add(kv.Key);
                deltas.Add(kv.Value);
            }

            vertexCount = newVertexCount;
        }

        public void Clear()
        {
            indices.Clear();
            deltas.Clear();
        }
    }

    /// <summary>
    /// VRChat アバター改変向けの非破壊メッシュ編集コンポーネント。
    ///
    /// 編集結果は頂点ごとのデルタとして本コンポーネントに保持され、
    /// NDMF プレビューへの反映とビルド時の適用が行われる。元メッシュアセットは書き換えない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("dennokoworks/Den Mesh Editor")]
    public class DenMeshEditor : MonoBehaviour
#if DEN_MESH_EDITOR_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Tooltip("編集対象の Renderer。複数指定できます。")]
        public List<MeshEdit> edits = new List<MeshEdit>();

        [Header("ブラシ設定")]
        [Tooltip("プロポーショナル編集の影響半径（ワールド単位）。")]
        public float brushRadius = 0.03f;

        public FalloffType falloff = FalloffType.Smooth;

        [Header("ミラー")]
        [Tooltip("有効な間に行った操作のみがミラーされます。")]
        public bool mirror;

        public MirrorAxis mirrorAxis = MirrorAxis.X;

        [Header("ベイク")]
        [Tooltip("ON にすると、元の形状を保ったまま編集分をシェイプキーとして追加します。")]
        public bool bakeAsBlendShape;

        /// <summary>
        /// コンポーネント追加時に呼ばれる。Renderer を持つオブジェクトに付与された場合、
        /// その Renderer を最初の編集対象として自動登録する。
        /// </summary>
        private void Reset()
        {
            if (edits.Count > 0) return;

            var renderer = GetComponent<Renderer>();
            if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
            {
                edits.Add(new MeshEdit { target = renderer });
            }
        }

        public MeshEdit FindEdit(Renderer target)
        {
            foreach (var edit in edits)
            {
                if (edit != null && edit.target == target) return edit;
            }

            return null;
        }
    }
}
