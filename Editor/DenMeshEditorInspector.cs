using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    [CustomEditor(typeof(DenMeshEditor))]
    internal class DenMeshEditorInspector : UnityEditor.Editor
    {
        private SerializedProperty _edits;
        private SerializedProperty _brushRadius;
        private SerializedProperty _falloff;
        private SerializedProperty _mirror;
        private SerializedProperty _mirrorAxis;
        private SerializedProperty _bakeAsBlendShape;

        [MenuItem("GameObject/dennokoworks/Den Mesh Editor", false, 20)]
        private static void AddDenMeshEditorMenuItem(MenuCommand menuCommand)
        {
            var target = menuCommand.context as GameObject;
            if (target == null)
            {
                target = Selection.activeGameObject;
            }

            if (target == null)
            {
                target = new GameObject("DenMeshEditor");
                Undo.RegisterCreatedObjectUndo(target, "Create DenMeshEditor");
                GameObjectUtility.SetParentAndAlign(target, menuCommand.context as GameObject);
            }

            var component = target.GetComponent<DenMeshEditor>();
            if (component == null)
            {
                component = Undo.AddComponent<DenMeshEditor>(target);
            }

            Selection.activeGameObject = target;

            var renderer = target.GetComponent<Renderer>();
            if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
            {
                if (component.edits.Count == 0)
                {
                    Undo.RecordObject(component, "Add Target to DenMeshEditor");
                    component.edits.Add(new MeshEdit { target = renderer });
                    EditorUtility.SetDirty(component);
                }

                if (MeshDeltaApplier.GetSharedMesh(renderer) != null)
                {
                    EditSession.Begin(component);
                }
            }
        }

        private void OnEnable()
        {
            _edits = serializedObject.FindProperty("edits");
            _brushRadius = serializedObject.FindProperty("brushRadius");
            _falloff = serializedObject.FindProperty("falloff");
            _mirror = serializedObject.FindProperty("mirror");
            _mirrorAxis = serializedObject.FindProperty("mirrorAxis");
            _bakeAsBlendShape = serializedObject.FindProperty("bakeAsBlendShape");
        }

        /// <summary>
        /// 編集セッション中は、シーンビュー側の操作（半径のホイール変更など）が
        /// コンポーネントへ反映されるので Inspector も追従させる。
        /// </summary>
        public override bool RequiresConstantRepaint()
        {
            return target is DenMeshEditor component && EditSession.IsActive(component);
        }

        public override void OnInspectorGUI()
        {
            var component = (DenMeshEditor)target;
            serializedObject.Update();

            DrawTargets();
            EditorGUILayout.Space();
            DrawEditControls(component);
            EditorGUILayout.Space();
            DrawBrushSettings();
            EditorGUILayout.Space();
            DrawBakeSection(component);

            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------

        private void DrawTargets()
        {
            EditorGUILayout.LabelField("編集対象", EditorStyles.boldLabel);

            var removeAt = -1;

            for (var i = 0; i < _edits.arraySize; i++)
            {
                var element = _edits.GetArrayElementAtIndex(i);
                var targetProp = element.FindPropertyRelative("target");
                var indicesProp = element.FindPropertyRelative("indices");
                var vertexCountProp = element.FindPropertyRelative("vertexCount");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(targetProp, GUIContent.none);

                GUILayout.Label($"{indicesProp.arraySize} 頂点", GUILayout.Width(64));

                if (GUILayout.Button("×", GUILayout.Width(22)))
                {
                    removeAt = i;
                }

                EditorGUILayout.EndHorizontal();

                DrawTargetWarning(targetProp, vertexCountProp);
            }

            if (removeAt >= 0)
            {
                _edits.DeleteArrayElementAtIndex(removeAt);
            }

            if (GUILayout.Button("対象を追加"))
            {
                _edits.arraySize++;
                var added = _edits.GetArrayElementAtIndex(_edits.arraySize - 1);
                added.FindPropertyRelative("target").objectReferenceValue = null;
                added.FindPropertyRelative("vertexCount").intValue = 0;
                added.FindPropertyRelative("indices").ClearArray();
                added.FindPropertyRelative("deltas").ClearArray();
            }

            if (_edits.arraySize == 0)
            {
                EditorGUILayout.HelpBox("編集対象の Renderer を追加してください。", MessageType.Info);
            }
        }

        private static void DrawTargetWarning(SerializedProperty targetProp, SerializedProperty vertexCountProp)
        {
            var renderer = targetProp.objectReferenceValue as Renderer;
            if (renderer == null) return;

            var mesh = MeshDeltaApplier.GetSharedMesh(renderer);
            if (mesh == null)
            {
                EditorGUILayout.HelpBox("この Renderer にメッシュが設定されていません。", MessageType.Warning);
                return;
            }

            var recorded = vertexCountProp.intValue;
            if (recorded != 0 && recorded != mesh.vertexCount)
            {
                EditorGUILayout.HelpBox(
                    $"頂点数が編集時と異なります（現在 {mesh.vertexCount} / 編集時 {recorded}）。"
                    + "編集は適用されません。元メッシュが差し替わったか、再インポートで頂点順が変化した可能性があります。",
                    MessageType.Error);
            }
        }

        // ------------------------------------------------------------------

        private void DrawEditControls(DenMeshEditor component)
        {
            var editing = EditSession.IsActive(component);

            using (new EditorGUI.DisabledScope(!editing && _edits.arraySize == 0))
            {
                if (editing)
                {
                    GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                    if (GUILayout.Button("編集終了", GUILayout.Height(28)))
                    {
                        EditSession.End();
                    }

                    GUI.backgroundColor = Color.white;
                }
                else
                {
                    if (GUILayout.Button("編集", GUILayout.Height(28)))
                    {
                        serializedObject.ApplyModifiedProperties();
                        EditSession.Begin(component);
                    }
                }
            }

            if (!editing) return;

            EditorGUILayout.HelpBox(
                "シーンビューで頂点をクリックして選択し、移動ハンドルでドラッグします。\n"
                + "Esc で選択解除、もう一度 Esc で編集終了。",
                MessageType.Info);

            var session = EditSession.Active;
            if (session != null && session.ShowFallbackWarning)
            {
                EditorGUILayout.HelpBox(
                    "NDMF プレビューのプロキシを取得できていません。"
                    + "Scale Adjuster など他ツールの影響が反映されていない状態で編集しています。\n"
                    + "NDMF のプレビューが有効になっているか確認してください。",
                    MessageType.Warning);
            }
        }

        private void DrawBrushSettings()
        {
            EditorGUILayout.LabelField("ブラシ", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_brushRadius, new GUIContent("半径", "プロポーショナル編集の影響半径（ワールド単位）"));
            EditorGUILayout.PropertyField(_falloff, new GUIContent("減衰"));

            EditorGUILayout.PropertyField(_mirror, new GUIContent("ミラー", "有効な間に行った操作のみがミラーされます"));
            using (new EditorGUI.DisabledScope(!_mirror.boolValue))
            {
                EditorGUILayout.PropertyField(_mirrorAxis, new GUIContent("ミラー軸"));
            }

            if (_mirror.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "ミラーは編集操作そのものを反転します（中心座標と変位ベクトルの両方を反射）。"
                    + "左右対称なトポロジは不要です。基準はアバタールートのローカル空間です。",
                    MessageType.None);
            }
        }

        // ------------------------------------------------------------------

        private void DrawBakeSection(DenMeshEditor component)
        {
            EditorGUILayout.LabelField("ベイク", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_bakeAsBlendShape,
                new GUIContent("シェイプキーとして追加", "ON にすると元の形状を保ったまま、編集分をシェイプキーとして追加します"));

            EditorGUILayout.HelpBox(
                "元メッシュと同じフォルダに _edited を付けた名前で書き出します。"
                + "シーン内の Renderer は差し替えません。",
                MessageType.None);

            var hasEdits = false;
            foreach (var edit in component.edits)
            {
                if (edit == null || edit.target == null || !edit.HasEdits) continue;
                hasEdits = true;
                break;
            }

            using (new EditorGUI.DisabledScope(!hasEdits))
            {
                if (GUILayout.Button("ベイク", GUILayout.Height(24)))
                {
                    serializedObject.ApplyModifiedProperties();
                    DenMeshEditorBaker.Bake(component);
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!hasEdits))
            {
                if (GUILayout.Button("すべての編集をクリア"))
                {
                    if (EditorUtility.DisplayDialog("Den Mesh Editor",
                            "すべての編集内容を破棄します。よろしいですか？", "クリア", "キャンセル"))
                    {
                        EditSession.End();

                        Undo.RecordObject(component, "Clear Den Mesh Editor Edits");
                        foreach (var edit in component.edits)
                        {
                            edit?.Clear();
                        }

                        EditorUtility.SetDirty(component);
                        if (PrefabUtility.IsPartOfPrefabInstance(component))
                        {
                            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                        }

                        LiveEdits.Invalidate();

                        // SerializedObject を経由せずに書き換えたので、
                        // 末尾の ApplyModifiedProperties が古い値を書き戻さないよう読み直す
                        serializedObject.Update();
                    }
                }
            }
        }
    }
}
