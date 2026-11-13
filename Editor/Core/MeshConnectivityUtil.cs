using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// メッシュの三角形トポロジとバインドポーズ頂点座標から、
    /// 頂点ごとの連結成分 ID（アイランド ID）を算出するヘルパー。
    ///
    /// ハードエッジや UV シームで分割された頂点は同一のローカル座標を持つため、
    /// 座標ベースのウェルディングを行ってから Union-Find で連結成分をまとめる。
    /// </summary>
    internal static class MeshConnectivityUtil
    {
        internal static int[] BuildComponentIds(Mesh mesh)
        {
            if (mesh == null) return null;

            var vertexCount = mesh.vertexCount;
            if (vertexCount == 0) return null;

            var vertices = new List<Vector3>(vertexCount);
            mesh.GetVertices(vertices);
            if (vertices.Count != vertexCount) return null;

            // 1. 同一座標の頂点を 1 つの weldId へまとめる（ハードエッジ / UV シームをまたぐため）
            var vertexToWeld = new int[vertexCount];
            var posToWeld = new Dictionary<Vector3, int>(vertexCount);
            var weldCount = 0;

            for (var i = 0; i < vertexCount; i++)
            {
                var pos = NormalizeZero(vertices[i]);
                if (!posToWeld.TryGetValue(pos, out var weldId))
                {
                    weldId = weldCount++;
                    posToWeld.Add(pos, weldId);
                }

                vertexToWeld[i] = weldId;
            }

            // 2. Union-Find で三角形によって結ばれた weldId 同士を統合する
            var parent = new int[weldCount];
            var rank = new byte[weldCount];
            for (var i = 0; i < weldCount; i++) parent[i] = i;

            var triangles = new List<int>();
            var subMeshCount = mesh.subMeshCount;
            for (var s = 0; s < subMeshCount; s++)
            {
                triangles.Clear();
                mesh.GetTriangles(triangles, s);

                var triCount = triangles.Count;
                for (var t = 0; t + 2 < triCount; t += 3)
                {
                    var w0 = vertexToWeld[triangles[t]];
                    var w1 = vertexToWeld[triangles[t + 1]];
                    var w2 = vertexToWeld[triangles[t + 2]];

                    Union(parent, rank, w0, w1);
                    Union(parent, rank, w1, w2);
                }
            }

            // 3. 各頂点の代表根（連結成分 ID）を配列へ展開する
            var componentIds = new int[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                componentIds[i] = Find(parent, vertexToWeld[i]);
            }

            return componentIds;
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        private static void Union(int[] parent, byte[] rank, int a, int b)
        {
            var ra = Find(parent, a);
            var rb = Find(parent, b);
            if (ra == rb) return;

            if (rank[ra] < rank[rb])
            {
                parent[ra] = rb;
            }
            else if (rank[ra] > rank[rb])
            {
                parent[rb] = ra;
            }
            else
            {
                parent[rb] = ra;
                rank[ra]++;
            }
        }

        private static Vector3 NormalizeZero(Vector3 v)
        {
            return new Vector3(
                v.x == 0f ? 0f : v.x,
                v.y == 0f ? 0f : v.y,
                v.z == 0f ? 0f : v.z);
        }
    }
}
