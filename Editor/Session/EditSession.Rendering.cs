using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
        // ------------------------------------------------------------------
        // 描画

        /// <summary>
        /// シーンの深度バッファと比較して、面の裏に隠れている頂点ドットを描かないようにする。
        ///
        /// プレビュー対象のメッシュはシーンビューが既に描画済みなので、深度バッファには
        /// 「実際に見えている形状」が入っている。CPU 側で遮蔽判定をやり直す必要はない。
        /// </summary>
        private static void BeginOccludedVertexCulling(out CompareFunction previous)
        {
            previous = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;
        }

        private void DrawGizmos()
        {
            if (Event.current.type != EventType.Repaint) return;

            var camera = Camera.current;
            if (camera == null) return;

            var cameraPosition = camera.transform.position;

            if (!_hasSelection)
            {
                if (_hoverTarget?.WorldVertices == null || _hoverIndex < 0 ||
                    _hoverIndex >= _hoverTarget.WorldVertices.Length) return;

                var hovered = _hoverTarget.WorldVertices[_hoverIndex];

                // ブラシ円は面に隠れると位置を見失うので、深度比較の対象にしない
                DrawBrushCircle(hovered, new Color(1f, 0.8f, 0.2f, 0.6f));

                BeginOccludedVertexCulling(out var previousHoverZTest);
                Handles.color = new Color(1f, 0.8f, 0.2f, 0.9f);
                DrawVertexDot(hovered, cameraPosition, 0.045f, 0.35f);
                Handles.zTest = previousHoverZTest;
                return;
            }

            // ドラッグ中、プレビューのメッシュは既に変形しているが WorldVertices は
            // ドラッグ開始時のままなので、同じ変位を足した位置へ描く。
            // これをしないと、動かした先の面にドットが埋もれて遮蔽判定で消えてしまう
            var displacement = _dragging ? _handlePosition - _centerWorld : Vector3.zero;
            var mirrorDisplacement = _dragging ? MirrorDisplacement(displacement) : Vector3.zero;

            // 円もハンドルと一緒に動かす。中心の頂点は変位をそのまま受け取る（重み 1）ので、
            // 動かした先でも「この円の中が影響範囲」という対応が保たれる
            DrawBrushCircle(_centerWorld + displacement, new Color(0.3f, 0.8f, 1f, 0.8f));
            if (_mirrorActive)
            {
                DrawBrushCircle(_mirrorCenterWorld + mirrorDisplacement, new Color(1f, 0.4f, 0.6f, 0.8f));
            }

            BeginOccludedVertexCulling(out var previousZTest);

            // 影響を受ける頂点を表示する（多すぎる場合は間引く）
            var step = Mathf.Max(1, _influences.Count / MaxDrawnVertices);
            for (var i = 0; i < _influences.Count; i += step)
            {
                var influence = _influences[i];
                var vertices = influence.Target.WorldVertices;
                if (vertices == null || influence.Index >= vertices.Length) continue;

                var strength = Mathf.Clamp01(influence.Weight + influence.MirrorWeight);
                Handles.color = Color.Lerp(new Color(0.2f, 0.4f, 0.8f, 0.25f),
                    new Color(1f, 0.3f, 0.3f, 0.9f), strength);

                var world = vertices[influence.Index]
                            + displacement * influence.Weight
                            + mirrorDisplacement * influence.MirrorWeight;

                DrawVertexDot(world, cameraPosition, 0.025f, 0.18f);
            }

            Handles.zTest = previousZTest;
        }

        /// <summary>
        /// 頂点ドットを 1 つ描く。
        ///
        /// 面のちょうど上に描くと深度比較が拮抗してドットが明滅するため、カメラ側へわずかに
        /// 浮かせる。浮かせる量をドットの見かけの大きさに比例させることで、スクリーン上の
        /// 浮き量は距離やズームによらず一定になり、逆に「メッシュの厚みを越えて裏の頂点まで
        /// 見えてしまう」ことも起きにくい。
        /// </summary>
        private void DrawVertexDot(Vector3 world, Vector3 cameraPosition, float screenScale, float maxRadiusRatio)
        {
            var size = GetVertexDotSize(world, screenScale, maxRadiusRatio);

            var toCamera = cameraPosition - world;
            var distance = toCamera.magnitude;
            if (distance > 1e-6f) world += toCamera * (size * VertexDotDepthBias / distance);

            Handles.DotHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
        }

        /// <summary>
        /// 頂点プレビューのドットサイズを計算する。
        /// カメラが離れたときにスクリーン上のドットが相対的に巨大化してメッシュを覆い隠さないよう、
        /// 距離に応じた減衰およびブラシ半径に対する上限補正をかける。
        /// </summary>
        private float GetVertexDotSize(Vector3 worldPosition, float screenScale, float maxRadiusRatio = 0.25f)
        {
            var handleSize = HandleUtility.GetHandleSize(worldPosition);
            // 近距離・通常編集距離（handleSize ≒ 0.05m 前後）を基準とする
            const float refSize = 0.05f;

            // 基準距離より離れるほど、スクリーン上の見かけサイズを緩やかに減衰させる
            var distanceScale = handleSize <= refSize ? 1f : Mathf.Sqrt(refSize / handleSize);
            var size = handleSize * screenScale * distanceScale;

            // ブラシ円に対してドットが大きくなりすぎないように上限を設ける
            var maxByRadius = BrushRadius * maxRadiusRatio;
            return Mathf.Min(size, maxByRadius);
        }

        private void DrawBrushCircle(Vector3 center, Color color)
        {
            var camera = Camera.current;
            if (camera == null) return;

            Handles.color = color;
            Handles.DrawWireDisc(center, camera.transform.forward, BrushRadius);
        }
    }
}
