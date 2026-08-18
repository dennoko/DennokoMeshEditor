using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
        // ------------------------------------------------------------------
        // 頂点プレビュー
        //
        // 編集対象の頂点位置を、面に隠れていないものだけ小さな二重の円で示す。
        // 「どこを掴めるか」を選択前に見せるためのもの。
        //
        // 描くのはカーソル周辺だけに限る。1 頂点あたり 32 個の Vector3（8 分割の円 × 内外）を
        // 使うため全頂点を描くと数十万頂点ぶんの転送が毎リペイント発生するが、周辺だけなら
        // 数百点で収まる。絞り込みに使うスクリーン座標はピック処理が既に持っている
        // （TargetState.ScreenPositions）ので、追加の計算はキャッシュ済み座標との距離比較だけ。
        //
        // 影響範囲のドット（重みに応じた青〜赤）と区別できるよう、色はモノクロだけを使う。
        // 下地と同化させないために、暗い円（外側）の上に明るい円（内側）を重ねる二重描きにしている。
        // こうすると下地の明るさを調べる必要が無く、明るい面では外側の暗い円が、
        // 暗い面では内側の明るい円が必ず残る。
        //
        // シーンビューの描画結果（RenderTexture.active）を読み戻して下地の色を測る方法も
        // あるが、採らない。頂点ごとに判定するには画面全体の GPU リードバックが要り、
        // 視点を動かしている間ずっと同期待ちが乗る。加えて MSAA・HDR・カラースペース・
        // 描画パイプラインで中身が変わるうえ、その時点のバッファにはグリッドや他ツールの
        // ギズモも描かれているため、下地の色を読んでいるとは限らない。

        /// <summary>マーカーを描くカーソルからの距離（ピクセル）。</summary>
        private const float PreviewRadiusPixels = 140f;

        /// <summary>オーバーレイに重なるマーカーを描かないための余白（ピクセル）。</summary>
        private const float PreviewOverlayMargin = 4f;

        /// <summary>カーソルがこれだけ動いたら組み直す（ピクセル）。</summary>
        private const float PreviewMouseStepPixels = 2f;

        /// <summary>
        /// プレビューで一度に描く点の上限。超える場合は等間隔に間引く。
        /// 引いて全体を写したときなど、カーソル周辺に絞ってもなお多すぎる場合の保険。
        /// </summary>
        private const int MaxPreviewVertices = 3000;

        /// <summary>外側（暗い方）の円の半径。ハンドルサイズに対する倍率。</summary>
        private const float PreviewHaloScale = 0.028f;

        /// <summary>内側（明るい方）の円の半径。ハンドルサイズに対する倍率。</summary>
        private const float PreviewCoreScale = 0.016f;

        /// <summary>ハンドルサイズの係数を測る代表点の、カメラからの距離。</summary>
        private const float HandleSizeProbeDepth = 10f;

        private static readonly Color PreviewHaloColor = new Color(0.02f, 0.02f, 0.02f, 0.85f);
        private static readonly Color PreviewCoreColor = new Color(1f, 1f, 1f, 0.95f);

        private const int CircleSegments = 8;
        private static readonly Vector2[] UnitCircle = PrecomputeUnitCircle(CircleSegments);
        private static readonly Vector3[] PointScratch = new Vector3[CircleSegments];

        private static Vector2[] PrecomputeUnitCircle(int segments)
        {
            var points = new Vector2[segments];
            var step = Mathf.PI * 2f / segments;
            for (var i = 0; i < segments; i++)
            {
                var angle = i * step;
                points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            return points;
        }

        // 線分の組み立て結果。視点・形状・影響範囲が変わらない限り作り直さない
        private readonly List<Vector3> _previewHaloBuffer = new List<Vector3>();
        private readonly List<Vector3> _previewCoreBuffer = new List<Vector3>();
        private Vector3[] _previewHaloSegments;
        private Vector3[] _previewCoreSegments;

        private ViewKey _previewKey;
        private bool _previewValid;
        private int _previewInfluenceGeneration = -1;

        /// <summary>組み立てに使ったカーソル位置。ここから動いたら組み直す。</summary>
        private Vector2 _previewMouse;

        /// <summary>
        /// 影響範囲の世代番号。<see cref="BuildInfluences"/> と <see cref="ClearSelection"/> で進む。
        /// プレビューは影響範囲の頂点を除いて組むので、これが変わったら組み直す必要がある。
        /// </summary>
        private int _influenceGeneration;

        /// <summary>影響範囲が変わったことを頂点プレビューへ伝える。</summary>
        private void NotifyInfluencesChanged()
        {
            unchecked
            {
                _influenceGeneration++;
            }
        }

        private void ClearVertexPreview()
        {
            _previewValid = false;
            _previewHaloBuffer.Clear();
            _previewCoreBuffer.Clear();
            _previewHaloSegments = null;
            _previewCoreSegments = null;
        }

        /// <summary>
        /// 頂点プレビューを描く。
        ///
        /// 点の数は数万になりうるので <c>DotHandleCap</c> は使えない（1 頂点につき 1 描画になる）。
        /// <c>Handles.DrawLines</c> は配列をまとめて描くため、色ごとに 1 回で済む。
        /// </summary>
        private void DrawVertexPreview(Camera camera)
        {
            // Repaint イベントからしか呼ばれないが、mousePosition は最後の位置を保持している
            UpdatePreviewSegments(camera, Event.current.mousePosition);

            if (_previewHaloSegments == null || _previewHaloSegments.Length == 0) return;

            BeginOccludedVertexCulling(out var previousZTest);

            Handles.color = PreviewHaloColor;
            Handles.DrawLines(_previewHaloSegments);

            Handles.color = PreviewCoreColor;
            Handles.DrawLines(_previewCoreSegments);

            Handles.zTest = previousZTest;
        }

        /// <summary>
        /// 描画用の線分配列を組み直す。
        ///
        /// 円の大きさが視点に、描く範囲がカーソル位置に依存するため、どちらかが動けば
        /// 作り直しになる。逆に視点・形状・影響範囲・カーソルのどれも変わっていなければ
        /// 何もしない（定期リペイントやオーバーレイ操作では毎回キャッシュに当たる）。
        /// </summary>
        private void UpdatePreviewSegments(Camera camera, Vector2 mousePosition)
        {
            // 絞り込みにはスクリーン座標が要る。ホバー中はピック処理が既に計算済みで、
            // 視点が変わっていなければここは何もしない
            UpdateScreenPositions(camera);

            var key = ViewKey.From(camera, _geometryGeneration);
            if (_previewValid && _previewInfluenceGeneration == _influenceGeneration
                              && _previewKey.Matches(key)
                              && (mousePosition - _previewMouse).sqrMagnitude
                              < PreviewMouseStepPixels * PreviewMouseStepPixels)
            {
                return;
            }

            _previewKey = key;
            _previewInfluenceGeneration = _influenceGeneration;
            _previewMouse = mousePosition;
            _previewValid = true;

            // オーバーレイに重なるマーカーは見づらいだけなので描かない。
            // 円の半径ぶん外側まで避ける
            var overlay = new Rect(
                _overlayRect.x - PreviewOverlayMargin,
                _overlayRect.y - PreviewOverlayMargin,
                _overlayRect.width + PreviewOverlayMargin * 2f,
                _overlayRect.height + PreviewOverlayMargin * 2f);

            var radiusSq = PreviewRadiusPixels * PreviewRadiusPixels;

            var halo = _previewHaloBuffer;
            var core = _previewCoreBuffer;
            halo.Clear();
            core.Clear();

            var cameraTransform = camera.transform;
            var cameraPosition = cameraTransform.position;
            var forward = cameraTransform.forward;
            var right = cameraTransform.right;
            var up = cameraTransform.up;

            // HandleUtility.GetHandleSize は内部で射影を 2 回行うため、頂点ごとに呼ぶと重い。
            // 透視投影では視線方向の深度にちょうど比例し、正射影では一定なので、
            // 代表点 1 回で係数を求めて使い回す
            var probe = HandleUtility.GetHandleSize(cameraPosition + forward * HandleSizeProbeDepth);
            var handleSizeBase = camera.orthographic ? probe : 0f;
            var handleSizePerDepth = camera.orthographic ? 0f : probe / HandleSizeProbeDepth;

            var step = ComputePreviewStep(mousePosition, radiusSq, overlay);
            var influenceCursor = 0;

            foreach (var target in _targets)
            {
                // この対象の影響頂点は _influences の [from, to)。
                // _influences は _targets の順に、頂点番号の昇順で積まれている
                var from = influenceCursor;
                while (influenceCursor < _influences.Count && _influences[influenceCursor].Target == target)
                {
                    influenceCursor++;
                }

                var to = influenceCursor;
                var scan = from;

                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                var positions = target.ScreenPositions;
                if (positions == null || positions.Length != vertices.Length) continue;

                var inFront = target.InFront;

                for (var i = 0; i < vertices.Length; i += step)
                {
                    while (scan < to && _influences[scan].Index < i) scan++;

                    // 影響範囲の頂点は重み色のドットで別に描かれる。ドラッグ中に位置を追うのは
                    // そちらだけなので、ここでも描くと動かす前の位置にマーカーが取り残される
                    if (scan < to && _influences[scan].Index == i) continue;

                    if (!IsPreviewVisible(positions[i], inFront[i], mousePosition, radiusSq, overlay)) continue;

                    var world = vertices[i];

                    var toCamera = cameraPosition - world;
                    var depth = -Vector3.Dot(toCamera, forward);
                    var handleSize = handleSizeBase + handleSizePerDepth * Mathf.Max(depth, 0f);

                    // 引いて全体を見ているときほど頂点が密集するので、頂点ドットと同じ規則で
                    // 見かけサイズを減衰させる。素のハンドルサイズのままだと画面が点で埋まる
                    var scaled = handleSize * VertexDotDistanceScale(handleSize);

                    var haloSize = scaled * PreviewHaloScale;

                    // 面のちょうど上に描くと深度比較が拮抗して明滅する（DrawVertexDot と同じ理由）。
                    // 浮かせる量は内外で揃える。ずらすと重なり方が深度に左右される
                    var distance = toCamera.magnitude;
                    if (distance > 1e-6f) world += toCamera * (haloSize * VertexDotDepthBias / distance);

                    AddCircle(halo, world, right, up, haloSize);

                    var coreSize = scaled * PreviewCoreScale;
                    AddCircle(core, world, right, up, coreSize);
                }
            }

            _previewHaloSegments = CopySegments(halo, _previewHaloSegments);
            _previewCoreSegments = CopySegments(core, _previewCoreSegments);
        }

        private static void AddCircle(List<Vector3> buffer, Vector3 center, Vector3 right, Vector3 up, float radius)
        {
            var rRight = right * radius;
            var rUp = up * radius;
            var count = UnitCircle.Length;

            for (var i = 0; i < count; i++)
            {
                PointScratch[i] = center + rRight * UnitCircle[i].x + rUp * UnitCircle[i].y;
            }

            for (var i = 0; i < count; i++)
            {
                buffer.Add(PointScratch[i]);
                buffer.Add(PointScratch[(i + 1) % count]);
            }
        }

        /// <summary>
        /// カーソル周辺にあり、かつオーバーレイに隠れていないか。
        /// </summary>
        private static bool IsPreviewVisible(
            Vector2 screen, bool inFront, Vector2 mousePosition, float radiusSq, Rect overlay)
        {
            // カメラの後ろの頂点はスクリーン座標が (0, 0) に潰れている。
            // 弾いておかないと、カーソルが左上にあるときに一斉に現れる
            if (!inFront) return false;

            if ((screen - mousePosition).sqrMagnitude > radiusSq) return false;

            return !overlay.Contains(screen);
        }

        /// <summary>
        /// 間引き幅を決める。描く候補を数えるだけなので、影響範囲の除外は考慮しない
        /// （多めに数えて間引きがわずかに粗くなるだけ）。
        /// </summary>
        private int ComputePreviewStep(Vector2 mousePosition, float radiusSq, Rect overlay)
        {
            var total = 0;

            foreach (var target in _targets)
            {
                var positions = target.ScreenPositions;
                if (positions == null) continue;

                var inFront = target.InFront;
                for (var i = 0; i < positions.Length; i++)
                {
                    if (IsPreviewVisible(positions[i], inFront[i], mousePosition, radiusSq, overlay)) total++;
                }
            }

            return Mathf.Max(1, Mathf.CeilToInt(total / (float)MaxPreviewVertices));
        }

        /// <summary>
        /// <c>Handles.DrawLines</c> は渡した配列を丸ごと描くので、長さを厳密に合わせる。
        /// カーソル周辺だけを描くようになって点の数は組み直しのたびに変わりうるが、
        /// 数百点ぶんなので確保し直しても大きな負担にはならない。
        /// </summary>
        private static Vector3[] CopySegments(List<Vector3> source, Vector3[] destination)
        {
            if (source.Count == 0) return null;

            if (destination == null || destination.Length != source.Count)
            {
                destination = new Vector3[source.Count];
            }

            source.CopyTo(destination);
            return destination;
        }
    }
}
