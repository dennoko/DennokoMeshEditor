using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
        private Rect _overlayRect;
        private static bool _hasCustomOverlayPosition;
        private static Vector2 _overlayPosition;
        private static bool _overlayDragging;
        private static Vector2 _overlayDragOffset;

        private void DrawOverlay(SceneView sceneView)
        {
            // Layout と Repaint で GUILayout の構成が変わると
            // 「Getting control N's position in a group with only M controls」で例外になる。
            // Refresh は毎イベント走って警告状態を書き換えるため、レイアウトに影響する状態は
            // Layout イベント時に固定してから両イベントで使い回す。
            if (Event.current.type == EventType.Layout)
            {
                _overlayShowsWarning = _showFallbackWarning;
            }

            var current = Event.current;
            var overlayHeight = _overlayShowsWarning ? 186f : 148f;
            var overlayWidth = 320f;
            const float margin = 10f;

            // シーンビューの実際の描画領域サイズ（GUI 座標系）を取得
            var canvasWidth = GetCanvasWidth(sceneView);
            var canvasHeight = GetCanvasHeight(sceneView);

            var maxX = Mathf.Max(margin, canvasWidth - overlayWidth - margin);
            var maxY = Mathf.Max(margin, canvasHeight - overlayHeight - margin);

            if (!_hasCustomOverlayPosition)
            {
                // デフォルトは右下追従（ウィンドウのリサイズや比率変更にも追従）
                _overlayPosition.x = maxX;
                _overlayPosition.y = maxY;
            }
            else
            {
                // ドラッグ移動後の位置も、画面外に飛び出さないようクランプ
                _overlayPosition.x = Mathf.Clamp(_overlayPosition.x, margin, maxX);
                _overlayPosition.y = Mathf.Clamp(_overlayPosition.y, margin, maxY);
            }

            _overlayRect = new Rect(_overlayPosition.x, _overlayPosition.y, overlayWidth, overlayHeight);
            var headerRect = new Rect(_overlayRect.x, _overlayRect.y, _overlayRect.width, 24f);

            // ヘッダーのドラッグ移動
            if (current.type == EventType.MouseDown && current.button == 0 && headerRect.Contains(current.mousePosition))
            {
                _overlayDragging = true;
                _overlayDragOffset = current.mousePosition - _overlayPosition;
                _hasCustomOverlayPosition = true;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _overlayDragging)
            {
                _overlayPosition = current.mousePosition - _overlayDragOffset;
                _overlayPosition.x = Mathf.Clamp(_overlayPosition.x, margin, maxX);
                _overlayPosition.y = Mathf.Clamp(_overlayPosition.y, margin, maxY);
                current.Use();
                GUI.changed = true;
            }
            else if ((current.type == EventType.MouseUp || current.rawType == EventType.MouseUp) && _overlayDragging)
            {
                _overlayDragging = false;
                current.Use();
            }

            Handles.BeginGUI();

            // 背景（半透明ダーク）と緑の強調枠線（2px）
            EditorGUI.DrawRect(_overlayRect, new Color(0.16f, 0.16f, 0.16f, 0.94f));
            DrawOutlineRect(_overlayRect, new Color(0.25f, 0.88f, 0.45f, 0.95f), 2f);

            // ヘッダーの移動カーソル
            EditorGUIUtility.AddCursorRect(headerRect, MouseCursor.MoveArrow);

            GUILayout.BeginArea(_overlayRect, GUIStyle.none);

            GUILayout.Space(6);

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70f;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            GUILayout.Label("Den Mesh Editor — 編集中", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("⠿", EditorStyles.miniLabel);
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();

            EditorGUI.BeginChangeCheck();
            // ドラッグ中にホイールで変えた未確定の半径もそのまま表示する
            var radius = EditorGUILayout.Slider("半径", BrushRadius, MinBrushRadius, MaxBrushRadius);
            var falloff = (FalloffType)EditorGUILayout.EnumPopup("減衰", _component.falloff);
            var mirror = EditorGUILayout.Toggle("ミラー", _component.mirror);
            if (EditorGUI.EndChangeCheck())
            {
                // スライダーのドラッグは毎フレーム変更を出すので、
                // 最初の変更時のグループ番号を覚えておき、離したときに 1 段へまとめる。
                // ここでグループを切らないと、連続した設定変更どうしが 1 段に潰れる
                if (_settingsUndoGroup < 0)
                {
                    Undo.IncrementCurrentGroup();
                    _settingsUndoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Den Mesh Editor Settings");
                }

                Undo.RecordObject(_component, "Den Mesh Editor Settings");
                _hasPendingRadius = false;
                _component.brushRadius = radius;
                _component.falloff = falloff;
                _component.mirror = mirror;
                EditorUtility.SetDirty(_component);

                // 半径・減衰・ミラーが変わったら影響範囲を計算し直す
                if (_hasSelection)
                {
                    RecomputeMirrorCenter();
                    BuildInfluences();
                }
            }

            if (_settingsUndoGroup >= 0 && Event.current.rawType == EventType.MouseUp)
            {
                Undo.CollapseUndoOperations(_settingsUndoGroup);
                _settingsUndoGroup = -1;
            }

            GUILayout.Space(2);

            var hintStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                normal = { textColor = new Color(0.3f, 0.95f, 0.45f) }
            };
            GUILayout.Label("※ ハンドル操作中にマウスホイールで半径変更", hintStyle);

            if (_overlayShowsWarning)
            {
                EditorGUILayout.HelpBox("NDMF プレビュー未取得。他ツールの影響は反映されていません。",
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            EditorGUIUtility.labelWidth = previousLabelWidth;

            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private static float GetCanvasWidth(SceneView sceneView)
        {
            if (sceneView != null && sceneView.camera != null)
            {
                var ppp = EditorGUIUtility.pixelsPerPoint;
                if (ppp > 0f && sceneView.camera.pixelWidth > 0)
                {
                    return sceneView.camera.pixelWidth / ppp;
                }
            }
            return sceneView != null ? sceneView.position.width : 800f;
        }

        private static float GetCanvasHeight(SceneView sceneView)
        {
            if (sceneView != null && sceneView.camera != null)
            {
                var ppp = EditorGUIUtility.pixelsPerPoint;
                if (ppp > 0f && sceneView.camera.pixelHeight > 0)
                {
                    return sceneView.camera.pixelHeight / ppp;
                }
            }
            return sceneView != null ? sceneView.position.height : 600f;
        }

        private static void DrawOutlineRect(Rect rect, Color color, float width = 2f)
        {
            // Top
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            // Bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            // Left
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + width, width, rect.height - width * 2f), color);
            // Right
            EditorGUI.DrawRect(new Rect(rect.xMax - width, rect.y + width, width, rect.height - width * 2f), color);
        }
    }
}
