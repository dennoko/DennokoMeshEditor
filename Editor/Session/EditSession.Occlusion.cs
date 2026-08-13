using System;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
        /// <summary>遮蔽判定用の三角形ハッシュグリッドのセルサイズ（GUI ポイント）。</summary>
        private const float TriangleCellSize = 48f;

        /// <summary>ハッシュグリッドのバケット数。2 のべき乗であること。</summary>
        private const int TriangleBuckets = 8192;

        /// <summary>1 つの三角形を登録するセル数の上限。超えたものはオーバーフロー側へ回す。</summary>
        private const int MaxCellsPerTriangle = 12;

        // ------------------------------------------------------------------
        // 遮蔽三角形のハッシュグリッド

        /// <summary>
        /// セル座標からバケット番号を求める。画面外へ大きく外れた座標も扱えるよう、
        /// 密な配列ではなくハッシュにする（衝突しても外接矩形判定で弾かれるだけで正しさは保たれる）。
        /// </summary>
        private static int CellHash(int cellX, int cellY)
        {
            unchecked
            {
                return ((cellX * 73856093) ^ (cellY * 19349663)) & (TriangleBuckets - 1);
            }
        }

        /// <summary>
        /// 三角形をグリッドへ入れられるか判定し、入れられる場合はセル範囲を返す。
        ///
        /// 外接矩形はあらかじめ <see cref="PickThresholdPixels"/> だけ広げておく。
        /// こうしておけば検索側はクリック位置が属するセル 1 つを引くだけで、
        /// 「クリック位置から 24px 以内に外接矩形がある三角形」を漏れなく拾える。
        /// </summary>
        /// <returns>0 = 無視してよい / 1 = セルへ格納 / 2 = オーバーフロー（毎回評価する）</returns>
        private static int ClassifyTriangle(TargetState target, int start,
            out int cellX0, out int cellY0, out int cellX1, out int cellY1)
        {
            cellX0 = cellY0 = cellX1 = cellY1 = 0;

            var triangles = target.Triangles;
            var positions = target.ScreenPositions;
            var count = positions.Length;

            int a = triangles[start], b = triangles[start + 1], c = triangles[start + 2];
            if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count) return 0;

            var inFront = target.InFront;
            var frontCount = (inFront[a] ? 1 : 0) + (inFront[b] ? 1 : 0) + (inFront[c] ? 1 : 0);

            // 完全にカメラ後方。遮蔽しえない
            if (frontCount == 0) return 0;

            // カメラ面をまたぐ三角形はスクリーン座標が信用できないので絞り込まない
            if (frontCount < 3) return 2;

            Vector2 pa = positions[a], pb = positions[b], pc = positions[c];

            var minX = Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x)) - PickThresholdPixels;
            var maxX = Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x)) + PickThresholdPixels;
            var minY = Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y)) - PickThresholdPixels;
            var maxY = Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y)) + PickThresholdPixels;

            // 覆うセル数が過大（＝画面上で極端に大きい）、あるいは NaN を含む場合はオーバーフロー扱い。
            // 先に float で判定することで、int へ変換したときのオーバーフローも避けられる
            var spanX = (maxX - minX) / TriangleCellSize + 1f;
            var spanY = (maxY - minY) / TriangleCellSize + 1f;
            if (!(spanX * spanY <= MaxCellsPerTriangle)) return 2;

            cellX0 = Mathf.FloorToInt(minX / TriangleCellSize);
            cellX1 = Mathf.FloorToInt(maxX / TriangleCellSize);
            cellY0 = Mathf.FloorToInt(minY / TriangleCellSize);
            cellY1 = Mathf.FloorToInt(maxY / TriangleCellSize);

            return 1;
        }

        /// <summary>
        /// 遮蔽判定用の三角形をスクリーン空間のハッシュグリッドへ登録する。
        ///
        /// これが無いと、ピック 1 回（＝マウス移動 1 回）あたり全三角形を走査することになる。
        /// 構築は視点か形状が変わったときだけなので、ホバー中は 1 度も走らない。
        /// </summary>
        private static void BuildTriangleGrid(TargetState target)
        {
            target.GridValid = false;
            target.Overflow.Clear();

            if (!target.HasTriangles || target.ScreenPositions == null) return;

            var triangles = target.Triangles;
            var triangleCount = triangles.Count / 3;

            if (target.Stamp == null || target.Stamp.Length != triangleCount)
            {
                target.Stamp = new int[triangleCount];
                target.StampGeneration = 0;
            }

            if (target.BucketStart == null) target.BucketStart = new int[TriangleBuckets + 1];
            else Array.Clear(target.BucketStart, 0, target.BucketStart.Length);

            // パス 1：バケットごとの件数を数える
            var total = 0;
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var kind = ClassifyTriangle(target, t, out var x0, out var y0, out var x1, out var y1);
                if (kind == 0) continue;

                if (kind == 2)
                {
                    target.Overflow.Add(t);
                    continue;
                }

                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        target.BucketStart[CellHash(x, y) + 1]++;
                        total++;
                    }
                }
            }

            for (var i = 0; i < TriangleBuckets; i++)
            {
                target.BucketStart[i + 1] += target.BucketStart[i];
            }

            if (target.BucketItems == null || target.BucketItems.Length < total)
            {
                target.BucketItems = new int[Mathf.NextPowerOfTwo(Mathf.Max(total, 64))];
            }

            if (target.BucketCursor == null) target.BucketCursor = new int[TriangleBuckets];
            Array.Copy(target.BucketStart, target.BucketCursor, TriangleBuckets);

            // パス 2：実際に詰める
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                if (ClassifyTriangle(target, t, out var x0, out var y0, out var x1, out var y1) != 1) continue;

                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var bucket = CellHash(x, y);
                        target.BucketItems[target.BucketCursor[bucket]++] = t;
                    }
                }
            }

            target.GridValid = true;
        }

        /// <summary>同じ三角形を二重に評価しないためのマーク。初出なら true。</summary>
        private static bool MarkTriangle(TargetState target, int start)
        {
            var id = start / 3;
            if (target.Stamp[id] == target.StampGeneration) return false;

            target.Stamp[id] = target.StampGeneration;
            return true;
        }

        /// <summary>
        /// 遮蔽しうる三角形をあらかじめ絞り込む。候補はすべてクリック位置から
        /// <see cref="PickThresholdPixels"/> 以内にあり、グリッドへはその分だけ広げた
        /// 外接矩形で登録してあるので、クリック位置のセルを 1 つ引けば取りこぼしがない。
        ///
        /// 候補ごとに全三角形を走査すると候補数に比例して重くなるため、ここで 1 回だけ行う。
        /// </summary>
        private void CollectNearbyTriangles(Vector2 mousePosition)
        {
            _nearbyTriangles.Clear();

            foreach (var target in _targets)
            {
                if (!target.HasTriangles || target.ScreenPositions == null) continue;

                if (!target.GridValid)
                {
                    // グリッドを作れていない場合は安全側に倒して全走査する
                    var triangles = target.Triangles;
                    for (var t = 0; t + 2 < triangles.Count; t += 3) TryAddTriangle(target, t, mousePosition);
                    continue;
                }

                unchecked
                {
                    target.StampGeneration++;
                }

                if (target.StampGeneration == 0)
                {
                    Array.Clear(target.Stamp, 0, target.Stamp.Length);
                    target.StampGeneration = 1;
                }

                var bucket = CellHash(
                    Mathf.FloorToInt(mousePosition.x / TriangleCellSize),
                    Mathf.FloorToInt(mousePosition.y / TriangleCellSize));

                for (var k = target.BucketStart[bucket]; k < target.BucketStart[bucket + 1]; k++)
                {
                    var start = target.BucketItems[k];
                    if (MarkTriangle(target, start)) TryAddTriangle(target, start, mousePosition);
                }

                foreach (var start in target.Overflow)
                {
                    if (MarkTriangle(target, start)) TryAddTriangle(target, start, mousePosition);
                }
            }
        }

        private void TryAddTriangle(TargetState target, int start, Vector2 mousePosition)
        {
            var triangles = target.Triangles;
            var positions = target.ScreenPositions;
            var count = positions.Length;

            int a = triangles[start], b = triangles[start + 1], c = triangles[start + 2];
            if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count) return;

            var inFront = target.InFront;

            if (inFront[a] && inFront[b] && inFront[c])
            {
                if (!ScreenBoundsContain(positions[a], positions[b], positions[c],
                        mousePosition, PickThresholdPixels))
                {
                    return;
                }
            }
            else if (!inFront[a] && !inFront[b] && !inFront[c])
            {
                // 完全にカメラ後方。遮蔽しえない
                return;
            }

            // カメラ面をまたぐ三角形はスクリーン座標が信用できないので絞り込まず残す

            _nearbyTriangles.Add(new TriangleRef { Target = target, Start = start });
        }

        private static bool ScreenBoundsContain(Vector2 a, Vector2 b, Vector2 c, Vector2 point, float margin)
        {
            if (point.x < Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - margin) return false;
            if (point.x > Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + margin) return false;
            if (point.y < Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - margin) return false;
            if (point.y > Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + margin) return false;
            return true;
        }

        /// <summary>
        /// カメラから頂点へのレイが、途中で他の面に遮られているかを判定する。
        /// </summary>
        private bool IsOccluded(TargetState owner, int index, Vector3 cameraPosition)
        {
            var vertex = owner.WorldVertices[index];
            var toVertex = vertex - cameraPosition;
            var distance = toVertex.magnitude;
            if (distance <= 1e-6f) return false;

            var direction = toVertex / distance;

            // 頂点自身が乗っている面や、ごく手前の面を遮蔽と誤判定しないための余裕。
            // これが無いと、隣接三角形との交差で常に「遮蔽されている」と判定されてしまう
            var slack = Mathf.Max(1e-4f, distance * 0.003f);
            var limit = distance - slack;
            if (limit <= 0f) return false;

            var depthLimit = owner.ViewDepths[index] - slack;

            foreach (var reference in _nearbyTriangles)
            {
                var target = reference.Target;
                var vertices = target.WorldVertices;
                var depths = target.ViewDepths;
                var triangles = target.Triangles;

                int a = triangles[reference.Start], b = triangles[reference.Start + 1],
                    c = triangles[reference.Start + 2];

                // 3 頂点とも対象頂点より奥にある三角形は、深度が線形なので面全体も奥にある
                if (depths[a] >= depthLimit && depths[b] >= depthLimit && depths[c] >= depthLimit) continue;

                if (RayTriangle(cameraPosition, direction, vertices[a], vertices[b], vertices[c], out var hit) &&
                    hit < limit)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Möller–Trumbore によるレイと三角形の交差判定。
        /// 裏面も交差として扱う（両面表示のマテリアルでも遮蔽として正しく働かせるため）。
        /// </summary>
        private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
            out float distance)
        {
            distance = 0f;

            var edge1 = b - a;
            var edge2 = c - a;

            var p = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, p);

            // 視線と平行、あるいは面積ゼロの三角形
            if (Mathf.Abs(determinant) < 1e-12f) return false;

            var inverse = 1f / determinant;
            var s = origin - a;

            var u = Vector3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f) return false;

            var q = Vector3.Cross(s, edge1);
            var v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;

            distance = Vector3.Dot(edge2, q) * inverse;
            return distance > 0f;
        }
    }
}
