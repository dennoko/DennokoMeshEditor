using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor
{
    /// <summary>
    /// VRChat アバター改変向けの非破壊メッシュ編集コンポーネント。
    ///
    /// 編集結果は頂点ごとのデルタとして本コンポーネントに保持され、
    /// NDMF プレビューへの反映とビルド時の適用が行われる。元メッシュアセットは書き換えない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("dennokoworks/Dennoko Mesh Editor")]
    public class DenMeshEditor : MonoBehaviour
#if DEN_MESH_EDITOR_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Tooltip("編集対象の Renderer。複数指定できます。")]
        public List<MeshEdit> edits = new List<MeshEdit>();

        [Header("ブラシ設定")]
        [Tooltip("プロポーショナル編集の影響半径（ワールド単位）。")]
        [Range(0.001f, 0.5f)]
        public float brushRadius = 0.03f;

        public FalloffType falloff = FalloffType.Smooth;

        [Header("ミラー")]
        [Tooltip("有効な間に行った操作のみがミラーされます。")]
        public bool mirror;

        public MirrorAxis mirrorAxis = MirrorAxis.X;

        [Tooltip("ON にすると、元の形状を保ったまま編集分をシェイプキーとして追加します。")]
        public bool bakeAsBlendShape;

        [Tooltip("追加するシェイプキーの名前。空の場合は元メッシュ名 + _edited になります。")]
        public string blendShapeName = string.Empty;

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
