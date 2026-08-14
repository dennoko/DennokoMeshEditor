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
        public override string DisplayName => "Dennoko Mesh Editor";

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
                    if (edit?.target == null) continue;

                    // アバター配下でない Renderer はビルド用クローンに含まれず、参照が
                    // シーン上の実オブジェクトを指したままになる。そこへ書き込むと
                    // 「元アセットを書き換えない」という非破壊の前提が壊れるので必ず弾く。
                    if (!edit.target.transform.IsChildOf(context.AvatarRootTransform))
                    {
                        Debug.LogWarning(
                            $"[Dennoko Mesh Editor] {edit.target.name} はアバター配下にないため適用をスキップしました。"
                            + "編集対象はアバタールートの子孫にある Renderer を指定してください。",
                            component);
                        continue;
                    }

                    if (!processed.Add(edit.target)) continue;

                    ApplyToRenderer(context, edit.target, componentList);
                }
            }

            // VRChat のアップロード制約回避のため、ビルド成果物からコンポーネント自身を削除
            foreach (var component in components)
            {
                if (component != null) Object.DestroyImmediate(component);
            }
        }

        private static void ApplyToRenderer(BuildContext context, Renderer target, List<DenMeshEditor> components)
        {
            var sourceMesh = MeshDeltaApplier.GetSharedMesh(target);
            if (sourceMesh == null) return;

            // 頂点数の不一致などで捨てられた編集は、ここで必ずユーザーへ伝える。
            // 黙ってスキップするとアップロード後まで気づけない。
            var skipped = new List<string>();
            var merged = DenMeshEditorPreviewFilter.GatherEdits(
                components, target, sourceMesh.vertexCount, skipped);

            foreach (var message in skipped)
            {
                Debug.LogWarning($"[Dennoko Mesh Editor] {target.name}: {message}", target);
            }

            if (merged == null) return;

            var edited = MeshDeltaApplier.CreateEdited(sourceMesh, merged);
            if (edited == null) return;

            // 下流ツールのエラー報告が元メッシュに紐づくようにする
            ObjectRegistry.RegisterReplacedObject(sourceMesh, edited);

            // BuildContext.Serialize() でも回収されるが、明示的に保存しておく方が確実
            context.AssetSaver.SaveAsset(edited);

            MeshDeltaApplier.SetSharedMesh(target, edited);
        }
    }
}
