using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// UV マスク画像から頂点ごとの移動量倍率（0〜1）を算出するヘルパー。
    /// </summary>
    internal static class UvMaskUtil
    {
        internal const int MaxUvChannel = 7;

        /// <summary>
        /// 指定メッシュの UV チャンネルでマスク画像をサンプリングし、頂点ごとの倍率配列を返す。
        /// UV 境界等で同一座標に複数頂点が存在する場合は、最も暗い色（最小値）を採用する。
        /// 対象 UV が存在しない場合や画像が未指定の場合は null を返す。
        /// </summary>
        internal static float[] BuildMaskWeights(Mesh mesh, int uvChannel, Texture2D maskTexture)
        {
            if (mesh == null || maskTexture == null) return null;
            if (uvChannel < 0 || uvChannel > MaxUvChannel) return null;
            if (maskTexture.width <= 0 || maskTexture.height <= 0) return null;

            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + uvChannel)) return null;

            var vertexCount = mesh.vertexCount;
            if (vertexCount == 0) return null;

            var uvs = new List<Vector2>(vertexCount);
            mesh.GetUVs(uvChannel, uvs);
            if (uvs.Count != vertexCount) return null;

            var vertices = new List<Vector3>(vertexCount);
            mesh.GetVertices(vertices);
            if (vertices.Count != vertexCount) return null;

            var weights = SampleTexture(maskTexture, uvs);
            ApplyDarkestForCoLocatedVertices(vertices, weights);
            return weights;
        }

        private static float[] SampleTexture(Texture2D maskTexture, List<Vector2> uvs)
        {
            var count = uvs.Count;
            var weights = new float[count];

            if (maskTexture.isReadable)
            {
                SampleFromReadable(maskTexture, uvs, weights);
                return weights;
            }

            // Read/Write Enabled が無効なテクスチャは GPU 経由で一時的に読み取り可能なコピーを作る
            var prevActive = RenderTexture.active;
            var rtReadWrite = maskTexture.isDataSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            var rt = RenderTexture.GetTemporary(
                maskTexture.width, maskTexture.height, 0, RenderTextureFormat.ARGB32, rtReadWrite);
            Texture2D readable = null;

            try
            {
                Graphics.Blit(maskTexture, rt);
                RenderTexture.active = rt;

                readable = new Texture2D(
                    maskTexture.width, maskTexture.height, TextureFormat.RGBA32, false, !maskTexture.isDataSRGB)
                {
                    wrapMode = maskTexture.wrapMode,
                    filterMode = maskTexture.filterMode
                };
                readable.ReadPixels(new Rect(0, 0, maskTexture.width, maskTexture.height), 0, 0, false);
                readable.Apply(false, false);

                SampleFromReadable(readable, uvs, weights);
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (readable != null) Object.DestroyImmediate(readable);
            }

            return weights;
        }

        private static void SampleFromReadable(Texture2D texture, List<Vector2> uvs, float[] weights)
        {
            var count = uvs.Count;
            for (var i = 0; i < count; i++)
            {
                var uv = uvs[i];
                var color = texture.GetPixelBilinear(uv.x, uv.y);
                weights[i] = Mathf.Clamp01(color.grayscale);
            }
        }

        /// <summary>
        /// 同一座標を共有する頂点（UV シーム等）には一番暗い色（最小倍率）を適用し、
        /// シームが裂けるのを防ぐ。
        /// </summary>
        private static void ApplyDarkestForCoLocatedVertices(List<Vector3> vertices, float[] weights)
        {
            var count = vertices.Count;
            var minByPos = new Dictionary<Vector3, float>(count);

            for (var i = 0; i < count; i++)
            {
                var pos = NormalizeZero(vertices[i]);
                var w = weights[i];
                if (minByPos.TryGetValue(pos, out var existing))
                {
                    if (w < existing) minByPos[pos] = w;
                }
                else
                {
                    minByPos.Add(pos, w);
                }
            }

            for (var i = 0; i < count; i++)
            {
                weights[i] = minByPos[NormalizeZero(vertices[i])];
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

