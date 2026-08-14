using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 編集結果を適用したメッシュをアセットとして書き出す。
    ///
    /// 出力のみを行い、シーン内 Renderer の差し替えはしない（設計方針）。
    /// 元メッシュアセットには一切書き込まない。
    /// </summary>
    internal static class DenMeshEditorBaker
    {
        private const string Suffix = "_edited";

        internal static void Bake(DenMeshEditor component)
        {
            if (component == null) return;

            var created = new List<Object>();
            var skipped = new List<string>();

            foreach (var edit in component.edits)
            {
                if (edit?.target == null || !edit.HasEdits) continue;

                var source = MeshDeltaApplier.GetSharedMesh(edit.target);
                if (source == null)
                {
                    skipped.Add($"{edit.target.name}: メッシュが見つかりません");
                    continue;
                }

                if (!MeshDeltaApplier.IsCompatible(source, edit))
                {
                    skipped.Add(
                        $"{edit.target.name}: 頂点数が編集時と異なります（現在 {source.vertexCount} / 編集時 {edit.vertexCount}）");
                    continue;
                }

                var baseName = MakeBaseName(source.name);

                var shapeName = string.IsNullOrWhiteSpace(component.blendShapeName)
                    ? MakeShapeName(source, baseName)
                    : MakeShapeName(source, component.blendShapeName.Trim());

                var mesh = component.bakeAsBlendShape
                    ? MeshDeltaApplier.CreateWithBlendShape(source, edit, shapeName)
                    : MeshDeltaApplier.CreateEdited(source, edit);

                if (mesh == null)
                {
                    skipped.Add($"{edit.target.name}: メッシュの生成に失敗しました");
                    continue;
                }

                var path = MakeUniquePath(source, baseName);
                mesh.name = Path.GetFileNameWithoutExtension(path);

                AssetDatabase.CreateAsset(mesh, path);
                created.Add(mesh);
            }

            if (created.Count > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                foreach (var asset in created)
                {
                    Debug.Log($"[Dennoko Mesh Editor] ベイクしました: {AssetDatabase.GetAssetPath(asset)}", asset);
                }

                // Bake は OnInspectorGUI の実行中に呼ばれる。その場で Selection を変えると
                // Inspector の serializedObject が破棄されて GUI 例外になるうえ、
                // 選択変更を検知している編集セッションが黙って終了してしまう。
                // 次のエディタ更新まで遅らせ、編集中は選択自体を変えない。
                var assets = created.ToArray();
                EditorApplication.delayCall += () =>
                {
                    if (assets.Length == 0 || assets[0] == null) return;

                    EditorGUIUtility.PingObject(assets[0]);
                    if (EditSession.Active == null) Selection.objects = assets;
                };
            }

            foreach (var message in skipped)
            {
                Debug.LogWarning($"[Dennoko Mesh Editor] ベイクをスキップしました — {message}", component);
            }

            if (created.Count == 0 && skipped.Count == 0)
            {
                Debug.LogWarning("[Dennoko Mesh Editor] ベイク対象がありません（編集データが空です）。", component);
            }
        }

        /// <summary>
        /// 元メッシュ名に _edited を付与する。既に付いている場合は追加しない。
        /// </summary>
        private static string MakeBaseName(string sourceName)
        {
            return sourceName.EndsWith(Suffix) ? sourceName : sourceName + Suffix;
        }

        /// <summary>
        /// 保存先は元メッシュと同一フォルダ。名前が衝突する場合は末尾に「スペース + 連番」を付ける。
        /// </summary>
        private static string MakeUniquePath(Mesh source, string baseName)
        {
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var folder = string.IsNullOrEmpty(sourcePath) ? "Assets" : Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(folder)) folder = "Assets";
            folder = folder.Replace('\\', '/');

            var candidate = $"{folder}/{baseName}.asset";
            if (!Exists(candidate)) return candidate;

            for (var n = 1; ; n++)
            {
                candidate = $"{folder}/{baseName} {n}.asset";
                if (!Exists(candidate)) return candidate;
            }
        }

        private static bool Exists(string path)
        {
            return AssetDatabase.LoadAssetAtPath<Object>(path) != null || File.Exists(path);
        }

        /// <summary>
        /// シェイプキー名も _edited の規則に揃える。同名が既にある場合は連番を付ける。
        /// </summary>
        private static string MakeShapeName(Mesh source, string baseName)
        {
            var existing = new HashSet<string>();
            for (var i = 0; i < source.blendShapeCount; i++)
            {
                existing.Add(source.GetBlendShapeName(i));
            }

            if (!existing.Contains(baseName)) return baseName;

            for (var n = 1; ; n++)
            {
                var candidate = $"{baseName} {n}";
                if (!existing.Contains(candidate)) return candidate;
            }
        }
    }
}
