using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Dennokoworks.DenMeshEditor;

[assembly: ExportsPlugin(typeof(Dennokoworks.DenMeshEditor.Editor.DenMeshEditorPlugin))]

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// ビルド時に頂点デルタを適用する非破壊プラグイン。
    ///
    /// Transforming フェーズで処理する。頂点数を変更しうる Avatar Optimizer は Optimizing フェーズで
    /// 動作するため、このフェーズで処理すれば必ず先行でき、頂点インデックスの対応が保たれる。
    /// </summary>
    internal class DenMeshEditorPlugin : Plugin<DenMeshEditorPlugin>
    {
        public override string QualifiedName => "dennokoworks.den-mesh-editor";
        public override string DisplayName => "Den Mesh Editor";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                // Scale Adjuster などボーンを操作するパスの後に動かす
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("Apply Mesh Edits", Execute)
                .PreviewingWith(new DenMeshEditorPreviewFilter());
        }

        private static void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<DenMeshEditor>(true);
            if (components.Length == 0) return;

            var componentList = new List<DenMeshEditor>(components);

            // 対象 Renderer ごとに 1 度だけ処理する（複数コンポーネントが同じ Renderer を指す場合に備える）
            var processed = new HashSet<Renderer>();

            foreach (var component in components)
            {
                if (component == null) continue;

                foreach (var edit in component.edits)
                {
                    if (edit?.target == null || !processed.Add(edit.target)) continue;

                    ApplyToRenderer(edit.target, componentList);
                }
            }

            // VRChat のアップロード制約回避のため、ビルド成果物からコンポーネント自身を削除
            foreach (var component in components)
            {
                if (component != null) Object.DestroyImmediate(component);
            }
        }

        private static void ApplyToRenderer(Renderer target, List<DenMeshEditor> components)
        {
            var sourceMesh = MeshDeltaApplier.GetSharedMesh(target);
            if (sourceMesh == null) return;

            var merged = DenMeshEditorPreviewFilter.GatherEdits(components, target, sourceMesh.vertexCount);
            if (merged == null) return;

            if (!MeshDeltaApplier.IsCompatible(sourceMesh, merged))
            {
                Debug.LogWarning(
                    $"[Den Mesh Editor] {target.name} のメッシュ頂点数が編集時と異なるため"
                    + $"（現在 {sourceMesh.vertexCount} / 編集時 {merged.vertexCount}）、編集を適用できませんでした。"
                    + "元メッシュが差し替わったか、再インポートで頂点順が変化した可能性があります。",
                    target);
                return;
            }

            var edited = MeshDeltaApplier.CreateEdited(sourceMesh, merged);
            if (edited == null) return;

            MeshDeltaApplier.SetSharedMesh(target, edited);
        }
    }
}
