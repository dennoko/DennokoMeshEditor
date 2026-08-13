using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
        // スクリーン座標キャッシュ。視点と形状が変わらない限り再計算しない
        private ViewKey _screenCacheKey;
        private bool _screenCacheValid;
        private int _geometryGeneration;

        // 自前射影のパラメータ。HandleUtility.WorldToGUIPoint と一致することを
        // 校正で確認できたときだけ使う（→ TryCalibrateFastProjection）
        private bool _fastProjection;
        private Matrix4x4 _viewProjection;
        private float _fastPixelWidth;
        private float _fastPixelHeight;
        private float _fastRectX;
        private float _fastRectY;
        private float _fastPixelsPerPoint;

        /// <summary>
        /// クリック位置に最も近い「見えている」頂点を選ぶ。
        ///
        /// 手順は 3 段階。
        ///   1. 全頂点のスクリーン座標・視線方向の深度を計算する
        ///   2. クリック位置から一定距離内の頂点を、近い順に候補として集める
        ///   3. 近い順に遮蔽判定を行い、最初に「遮蔽されていない」ものを返す
        ///
        /// 遮蔽判定は編集対象メッシュの三角形に対するレイ交差で行う。手前の候補が
        /// 見えていれば 1 回で確定するため、通常のコストは 1 回分しかかからない。
        /// </summary>
        private bool TryPick(Vector2 mousePosition, out TargetState picked, out int pickedIndex)
        {
            picked = null;
            pickedIndex = -1;

            var camera = Camera.current;
            if (camera == null) return false;

            var cameraPosition = camera.transform.position;
            var cameraForward = camera.transform.forward;

            UpdateScreenCache(camera, cameraPosition, cameraForward);
            CollectCandidates(mousePosition);
            if (_candidates.Count == 0) return false;

            CollectNearbyTriangles(mousePosition);

            foreach (var candidate in _candidates)
            {
                if (IsOccluded(candidate.Target, candidate.Index, cameraPosition)) continue;

                picked = candidate.Target;
                pickedIndex = candidate.Index;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 全頂点のスクリーン座標と視線方向の深度を求め、遮蔽三角形のグリッドを作る。
        ///
        /// 数万頂点に対する射影と三角形の登録はマウス移動のたびに回すには重いので、
        /// 視点と形状が変わっていなければ前回の結果をそのまま使う。ホバー中はカメラも形状も
        /// 止まっているため、ほとんどのマウス移動でキャッシュヒットする。
        /// </summary>
        private void UpdateScreenCache(Camera camera, Vector3 cameraPosition, Vector3 cameraForward)
        {
            var key = ViewKey.From(camera, _geometryGeneration);
            if (_screenCacheValid && _screenCacheKey.Matches(key)) return;

            _screenCacheKey = key;
            _screenCacheValid = true;

            _fastProjection = TryCalibrateFastProjection(camera);

            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                if (vertices == null)
                {
                    target.ScreenPositions = null;
                    target.GridValid = false;
                    continue;
                }

                if (target.ScreenPositions == null || target.ScreenPositions.Length != vertices.Length)
                {
                    target.ScreenPositions = new Vector2[vertices.Length];
                    target.ViewDepths = new float[vertices.Length];
                    target.InFront = new bool[vertices.Length];
                }

                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = vertices[i];

                    // 視線方向の深度。カメラからの直線距離ではなく射影を使う。
                    // 射影は線形なので、三角形の 3 頂点の値から面全体を保守的に判定できる
                    var depth = Vector3.Dot(world - cameraPosition, cameraForward);
                    target.ViewDepths[i] = depth;

                    var inFront = depth > 0f;
                    target.InFront[i] = inFront;

                    if (!inFront)
                    {
                        target.ScreenPositions[i] = Vector2.zero;
                        continue;
                    }

                    target.ScreenPositions[i] = _fastProjection && TryProjectFast(world, out var fast)
                        ? fast
                        : HandleUtility.WorldToGUIPoint(world);
                }

                BuildTriangleGrid(target);
            }
        }

        /// <summary>
        /// 自前のスクリーン射影が <c>HandleUtility.WorldToGUIPoint</c> と一致するかを確かめる。
        ///
        /// WorldToGUIPoint は Handles.matrix や GUI クリップを経由するため単発でも重く、
        /// 数万頂点ぶん回すと効いてくる。ただしそれらが単位でない場合は結果がずれるので、
        /// 代表点で照合してから使う。一致しなければ従来経路のままにする（安全側）。
        /// </summary>
        private bool TryCalibrateFastProjection(Camera camera)
        {
            _fastPixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            if (_fastPixelsPerPoint <= 0f) return false;

            _viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;

            var rect = camera.pixelRect;
            _fastPixelWidth = camera.pixelWidth;
            _fastPixelHeight = camera.pixelHeight;
            _fastRectX = rect.x;
            _fastRectY = rect.y;

            if (_fastPixelWidth <= 0f || _fastPixelHeight <= 0f) return false;

            var t = camera.transform;
            for (var i = 0; i < 2; i++)
            {
                var probe = t.position
                            + t.forward * (i == 0 ? 1.5f : 4.0f)
                            + t.right * (i == 0 ? 0.31f : -0.77f)
                            + t.up * (i == 0 ? -0.19f : 0.53f);

                if (!TryProjectFast(probe, out var fast)) return false;

                var reference = HandleUtility.WorldToGUIPoint(probe);

                // 許容 0.25 ピクセル。ピック判定のしきい値は 24 ピクセルなので十分に細かい
                if ((fast - reference).sqrMagnitude > 0.0625f) return false;
            }

            return true;
        }

        private bool TryProjectFast(Vector3 world, out Vector2 gui)
        {
            var clip = _viewProjection * new Vector4(world.x, world.y, world.z, 1f);
            if (clip.w <= 1e-6f)
            {
                gui = Vector2.zero;
                return false;
            }

            var inverse = 1f / clip.w;
            var px = (clip.x * inverse * 0.5f + 0.5f) * _fastPixelWidth + _fastRectX;
            var py = (clip.y * inverse * 0.5f + 0.5f) * _fastPixelHeight + _fastRectY;

            gui = new Vector2(px / _fastPixelsPerPoint, (_fastPixelHeight - py) / _fastPixelsPerPoint);
            return true;
        }

        private void CollectCandidates(Vector2 mousePosition)
        {
            _candidates.Clear();

            var threshold = PickThresholdPixels * PickThresholdPixels;

            foreach (var target in _targets)
            {
                var positions = target.ScreenPositions;
                if (positions == null) continue;

                for (var i = 0; i < positions.Length; i++)
                {
                    if (!target.InFront[i]) continue;

                    var distance = (positions[i] - mousePosition).sqrMagnitude;
                    if (distance >= threshold) continue;

                    InsertCandidate(target, i, distance);
                }
            }
        }

        /// <summary>近い順を保ったまま、上位 <see cref="MaxPickCandidates"/> 件だけ保持する。</summary>
        private void InsertCandidate(TargetState target, int index, float distance)
        {
            var insertAt = _candidates.Count;
            for (var i = 0; i < _candidates.Count; i++)
            {
                if (distance >= _candidates[i].ScreenDistanceSq) continue;
                insertAt = i;
                break;
            }

            if (insertAt >= MaxPickCandidates) return;

            _candidates.Insert(insertAt, new Candidate
            {
                Target = target,
                Index = index,
                ScreenDistanceSq = distance,
            });

            if (_candidates.Count > MaxPickCandidates) _candidates.RemoveAt(_candidates.Count - 1);
        }
    }
}
