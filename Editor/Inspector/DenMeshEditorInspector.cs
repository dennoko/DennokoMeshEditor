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
        private SerializedProperty _blendShapeName;

        // バージョン表記 + 更新チェックの結果。State は保持せず表示のたびに
        // 「現在のローカル版 vs 取得済みの最新版」で再計算した値を受け取る。
        private DennokoVersionChecker.Result _versionResult;
        private static GUIStyle _versionLinkStyle;

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
            _blendShapeName = serializedObject.FindProperty("blendShapeName");

            // 前回の取得結果を反映しつつ、未取得／前回エラーなら取得を開始する
            //（要否の判定は StartCheckBackgroundTask 内で行う）。Inspector を選び直す
            // たびに一時的な取得失敗から自己回復できる。
            ReloadVersionResult();
            DenMeshEditorVersion.StartCheckBackgroundTask();
        }

        /// <summary>取得完了時に <see cref="DenMeshEditorVersion"/> から呼ばれる。</summary>
        internal void ReloadVersionResult()
        {
            _versionResult = DenMeshEditorVersion.LoadResultFromSessionState();
            Repaint();
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

            DrawVersionBar();
            EditorGUILayout.Space();
            DrawTargets();
            EditorGUILayout.Space();
            DrawEditControls(component);
            EditorGUILayout.Space();
            DrawBrushSettings();
            EditorGUILayout.Space();
            DrawBakeSection(component);

            // ミラー軸・半径・減衰は PropertyField 経由で書き換わるため、
            // ここで拾わないと編集セッションが古い設定のまま描画を続ける
            //（対象を選び直すまでミラー中心が更新されない）。
            if (serializedObject.ApplyModifiedProperties() && EditSession.IsActive(component))
            {
                EditSession.NotifySettingsChanged();
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 一番上の 1 行。左に現在のバージョン、その隣に更新状態、右端に再確認ボタン。
        /// 「更新あり」のときだけクリックでダウンロードページを開く。
        /// </summary>
        private void DrawVersionBar()
        {
            if (_versionLinkStyle == null)
            {
                _versionLinkStyle = new GUIStyle(EditorStyles.miniLabel);
            }

            var prevColor = GUI.contentColor;

            EditorGUILayout.BeginHorizontal();

            GUI.contentColor = new Color(0.68f, 0.68f, 0.68f);
            GUILayout.Label($"v{_versionResult.LocalVersion}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            GUI.contentColor = prevColor;

            switch (_versionResult.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                {
                    var tooltip = string.IsNullOrEmpty(_versionResult.Message)
                        ? "クリックでダウンロードページを開きます"
                        : _versionResult.Message;

                    GUI.contentColor = new Color(0.35f, 0.8f, 0.4f);
                    var clicked = GUILayout.Button(
                        new GUIContent($"更新あり {_versionResult.LatestVersion} ↗", tooltip),
                        _versionLinkStyle, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;

                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    if (clicked)
                    {
                        DenMeshEditorVersion.OpenUpdatePage(_versionResult.Url);
                    }
                    break;
                }

                case DennokoVersionChecker.State.Error:
                    GUI.contentColor = new Color(1f, 0.72f, 0.3f);
                    GUILayout.Label(
                        new GUIContent("最新版を取得できません", "↻ ボタンで再確認できます"),
                        EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                case DennokoVersionChecker.State.Checking:
                    GUI.contentColor = new Color(0.55f, 0.55f, 0.55f);
                    GUILayout.Label("確認中...", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                default: // UpToDate — バージョン表記だけ
                    break;
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(new GUIContent("↻", "アップデートを再確認"), EditorStyles.miniButton, GUILayout.Width(22)))
            {
                DenMeshEditorVersion.ForceRecheck();
                ReloadVersionResult(); // 即座に「確認中...」表示へ
            }

            EditorGUILayout.EndHorizontal();

            var separator = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(separator, new Color(0f, 0f, 0f, 0.2f));
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
                var countProp = element.FindPropertyRelative("count");
                var vertexCountProp = element.FindPropertyRelative("vertexCount");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(targetProp, GUIContent.none);

                GUILayout.Label($"{countProp.intValue} 頂点", GUILayout.Width(64));

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
                // arraySize++ は直前の要素を複製する。編集データ（byte[] blob）まで
                // 引き継がれると厄介なので、SerializedProperty で個別に潰すのではなく
                // 素の MeshEdit を直接追加する
                serializedObject.ApplyModifiedProperties();

                var component = (DenMeshEditor)target;
                Undo.RecordObject(component, "Add Den Mesh Editor Target");
                component.edits.Add(new MeshEdit());
                EditorUtility.SetDirty(component);

                if (PrefabUtility.IsPartOfPrefabInstance(component))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }

                serializedObject.Update();
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
                var prevColor = GUI.backgroundColor;
                if (editing)
                {
                    GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                    if (GUILayout.Button("編集終了", GUILayout.Height(28)))
                    {
                        EditSession.End();
                    }
                }
                else
                {
                    GUI.backgroundColor = new Color(0.35f, 0.75f, 1f);
                    if (GUILayout.Button("編集", GUILayout.Height(28)))
                    {
                        serializedObject.ApplyModifiedProperties();
                        EditSession.Begin(component);
                    }
                }
                GUI.backgroundColor = prevColor;
            }

            if (!editing) return;

            EditorGUILayout.HelpBox(
                "シーンビューで頂点をクリックして選択し、移動ハンドルでドラッグします。\n"
                + "Esc で選択解除、もう一度 Esc で編集終了。",
                MessageType.Info);

            var session = EditSession.Active;

            if (session != null && session.AnyVertexCountMismatch)
            {
                EditorGUILayout.HelpBox(
                    "他の NDMF ツールがメッシュの頂点数を変更しているため、"
                    + "そのツールの影響を反映した編集ができません。\n"
                    + "頂点の対応が取れなくなるのを防ぐため、変更前の形状を基準に編集しています。",
                    MessageType.Warning);
            }
            else if (session != null && session.ShowFallbackWarning)
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
            EditorGUILayout.LabelField("ブラシ設定", EditorStyles.boldLabel);
            EditorGUILayout.Slider(_brushRadius, EditSession.MinBrushRadius, EditSession.MaxBrushRadius, new GUIContent("半径", "プロポーショナル編集の影響半径（ワールド単位）"));
            EditorGUILayout.PropertyField(_falloff, new GUIContent("減衰"));

            EditorGUILayout.Space(2);

            var mirrorActive = _mirror.boolValue;
            var prevColor = GUI.backgroundColor;
            if (mirrorActive)
            {
                GUI.backgroundColor = new Color(0.35f, 0.95f, 0.45f);
            }

            var buttonText = mirrorActive ? "ミラー: 有効 (ON)" : "ミラー: 無効 (OFF)";
            if (GUILayout.Button(buttonText, GUILayout.Height(28)))
            {
                _mirror.boolValue = !mirrorActive;
                serializedObject.ApplyModifiedProperties();
                EditSession.NotifySettingsChanged();
            }

            GUI.backgroundColor = prevColor;

            using (new EditorGUI.DisabledScope(!_mirror.boolValue))
            {
                DrawMirrorAxisButtons();
            }

            if (_mirror.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "ミラーは編集操作そのものを反転します（中心座標と変位ベクトルの両方を反射）。"
                    + "左右対称なトポロジは不要です。基準はアバタールートのローカル空間です。",
                    MessageType.None);
            }
        }

        private void DrawMirrorAxisButtons()
        {
            EditorGUILayout.LabelField("ミラー軸");
            EditorGUILayout.BeginHorizontal();

            var currentAxis = (MirrorAxis)_mirrorAxis.enumValueIndex;
            var defaultColor = GUI.backgroundColor;
            var selectedColor = new Color(0.35f, 0.7f, 1f);

            var axes = new[]
            {
                (Axis: MirrorAxis.X, Label: "X 軸", Tooltip: "X 軸（左右対称）"),
                (Axis: MirrorAxis.Y, Label: "Y 軸", Tooltip: "Y 軸（上下対称）"),
                (Axis: MirrorAxis.Z, Label: "Z 軸", Tooltip: "Z 軸（前後対称）"),
            };

            foreach (var item in axes)
            {
                var isSelected = currentAxis == item.Axis;
                GUI.backgroundColor = isSelected ? selectedColor : defaultColor;

                if (GUILayout.Button(new GUIContent(item.Label, item.Tooltip), GUILayout.Height(26)))
                {
                    if (!isSelected)
                    {
                        _mirrorAxis.enumValueIndex = (int)item.Axis;
                        serializedObject.ApplyModifiedProperties();
                        EditSession.NotifySettingsChanged();
                    }
                }
            }

            GUI.backgroundColor = defaultColor;
            EditorGUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------

        private void DrawBakeSection(DenMeshEditor component)
        {
            EditorGUILayout.LabelField("ベイク", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_bakeAsBlendShape,
                new GUIContent("シェイプキーとして追加", "ON にすると元の形状を保ったまま、編集分をシェイプキーとして追加します"));

            if (_bakeAsBlendShape.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(_blendShapeName,
                        new GUIContent("シェイプキー名", "追加するシェイプキーの名前。空の場合は元メッシュ名に _edited を付与した名前になります"));
                }
            }

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
