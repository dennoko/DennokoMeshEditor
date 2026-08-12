using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor
{
    /// <summary>
    /// プロポーショナル編集の減衰カーブ。
    /// </summary>
    public enum FalloffType
    {
        Smooth,
        Linear,
        Sharp,
        Constant,
    }

    /// <summary>
    /// ミラー編集の対称軸。アバタールートのローカル空間で解釈する。
    /// </summary>
    public enum MirrorAxis
    {
        X,
        Y,
        Z,
    }

    /// <summary>
    /// 1 つの Renderer に対する編集データ。
    ///
    /// 動かした頂点だけを疎に保持する。適用は元の頂点座標への単純な加算であり、
    /// 上流の NDMF ツールがどのようなメッシュ変形を行っていても、
    /// 頂点数と頂点順序さえ保たれていれば正しく動作する（シェイプキーと同じ意味論）。
    /// デルタはメッシュローカル（バインドポーズ）空間で保持する。
    ///
    /// 保持形式は <c>byte[]</c> 1 本（v2 形式）。理由は 2 つある。
    ///   - テキスト YAML では <c>List&lt;Vector3&gt;</c> が 1 頂点 4 行に展開されるため、
    ///     大量のコンポーネントを置いたシーンのロードが線形に遅くなる。byte[] は
    ///     1 個のスカラー値として書き出されるためパースが桁違いに軽い
    ///   - Prefab インスタンス上でのオーバーライドがプロパティ 1 件にまとまる
    ///     （<c>Array.data[i]</c> が頂点数 × 2 件積まれない）
    ///
    /// 旧形式（<see cref="indices"/> / <see cref="deltas"/>）のデータは初回アクセス時に
    /// 自動で移行する。フィールドを残しているのは、既存シーンのデシリアライズを壊さないため。
    /// </summary>
    [Serializable]
    public class MeshEdit
    {
        /// <summary>1 頂点分のバイト数。int32 インデックス + float32 x/y/z。</summary>
        private const int Stride = 16;

        /// <summary>float とそのビット表現を相互変換するための共用体。BitConverter と違い確保が起きない。</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        public Renderer target;

        [Tooltip("編集時点の頂点数。適用前の整合性チェックに使用します。")]
        public int vertexCount;

        [SerializeField, HideInInspector] private int count;
        [SerializeField, HideInInspector] private byte[] blob;

        /// <summary>
        /// 編集データの世代番号。書き込みのたびに増える。
        ///
        /// NDMF プレビューの変更検出（<c>ComputeContext.Observe</c>）はフィンガープリントを
        /// <b>毎フレーム</b>再評価する（NDMF: PropertyMonitor.CheckAllObjectsLoop）。
        /// デルタ全体を走査していると、コンポーネント数 × 編集頂点数のコストが常時かかるため、
        /// この値だけを見て済むようにしている。
        ///
        /// シリアライズ対象なので Undo / Prefab / ドメインリロードと自然に整合する
        /// （static なカウンタと違い、巻き戻ればこの値も巻き戻る）。
        /// </summary>
        [SerializeField, HideInInspector] private int revision;

        // ---- 旧形式（v1）。読み込み時に blob へ移行し、以後は使わない ----
        [SerializeField, HideInInspector] private List<int> indices;
        [SerializeField, HideInInspector] private List<Vector3> deltas;

        /// <summary>編集された頂点数。</summary>
        public int Count
        {
            get
            {
                MigrateIfNeeded();
                return count;
            }
        }

        /// <summary>
        /// 編集データの世代番号。プレビューの変更検出に使う。
        /// </summary>
        public int Revision
        {
            get
            {
                MigrateIfNeeded();
                return revision;
            }
        }

        public bool HasEdits => Count > 0;

        /// <summary><paramref name="i"/> 番目の編集頂点のインデックス。</summary>
        public int GetIndex(int i)
        {
            return ReadInt(blob, i * Stride);
        }

        /// <summary><paramref name="i"/> 番目の編集頂点のデルタ。</summary>
        public Vector3 GetDelta(int i)
        {
            var offset = i * Stride + 4;
            return new Vector3(
                ReadFloat(blob, offset),
                ReadFloat(blob, offset + 4),
                ReadFloat(blob, offset + 8));
        }

        public Dictionary<int, Vector3> ToDictionary()
        {
            var n = Count;
            var dict = new Dictionary<int, Vector3>(n);
            for (var i = 0; i < n; i++)
            {
                dict[GetIndex(i)] = GetDelta(i);
            }

            return dict;
        }

        /// <summary>
        /// 辞書の内容で上書きする。ゼロに戻った頂点は保存しない（データを膨らませないため）。
        /// </summary>
        public void SetFrom(Dictionary<int, Vector3> dict, int newVertexCount)
        {
            DiscardLegacy();

            var buffer = new byte[dict.Count * Stride];
            var written = 0;

            foreach (var kv in dict)
            {
                if (kv.Value.sqrMagnitude <= 0f) continue;

                var offset = written * Stride;
                WriteInt(buffer, offset, kv.Key);
                WriteFloat(buffer, offset + 4, kv.Value.x);
                WriteFloat(buffer, offset + 8, kv.Value.y);
                WriteFloat(buffer, offset + 12, kv.Value.z);
                written++;
            }

            // 末尾に未使用領域が残るとそのぶん YAML が膨らむので切り詰める
            blob = Trim(buffer, written * Stride);
            count = written;
            vertexCount = newVertexCount;

            unchecked
            {
                revision++;
            }
        }

        public void Clear()
        {
            DiscardLegacy();

            if (count == 0 && (blob == null || blob.Length == 0)) return;

            count = 0;
            blob = null;

            unchecked
            {
                revision++;
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 旧形式のデータを byte[] へ移行する。移行済み・空データなら即座に返る（O(1)）。
        ///
        /// 毎フレーム走るフィンガープリント計算から呼ばれるため、
        /// 「何もしない」経路が確実に軽いことが要件になる。
        /// </summary>
        private void MigrateIfNeeded()
        {
            if (indices == null && deltas == null) return;

            var legacyCount = indices != null && deltas != null
                ? Mathf.Min(indices.Count, deltas.Count)
                : 0;

            // v2 データが既にあるなら、そちらを正として旧形式を捨てる
            if (legacyCount == 0 || count > 0)
            {
                indices = null;
                deltas = null;
                return;
            }

            var buffer = new byte[legacyCount * Stride];
            var written = 0;

            for (var i = 0; i < legacyCount; i++)
            {
                var delta = deltas[i];
                if (delta.sqrMagnitude <= 0f) continue;

                var offset = written * Stride;
                WriteInt(buffer, offset, indices[i]);
                WriteFloat(buffer, offset + 4, delta.x);
                WriteFloat(buffer, offset + 8, delta.y);
                WriteFloat(buffer, offset + 12, delta.z);
                written++;
            }

            blob = Trim(buffer, written * Stride);
            count = written;

            indices = null;
            deltas = null;

            // 内容は変わっていないので revision は進めない
        }

        private void DiscardLegacy()
        {
            MigrateIfNeeded();
            indices = null;
            deltas = null;
        }

        private static byte[] Trim(byte[] buffer, int used)
        {
            if (buffer.Length == used) return buffer;

            var trimmed = new byte[used];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, used);
            return trimmed;
        }

        private static int ReadInt(byte[] buffer, int offset)
        {
            return buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);
        }

        private static float ReadFloat(byte[] buffer, int offset)
        {
            return new FloatBits { Bits = ReadInt(buffer, offset) }.Value;
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            WriteInt(buffer, offset, new FloatBits { Value = value }.Bits);
        }
    }

    /// <summary>
    /// VRChat アバター改変向けの非破壊メッシュ編集コンポーネント。
    ///
    /// 編集結果は頂点ごとのデルタとして本コンポーネントに保持され、
    /// NDMF プレビューへの反映とビルド時の適用が行われる。元メッシュアセットは書き換えない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("dennokoworks/Den Mesh Editor")]
    public class DenMeshEditor : MonoBehaviour
#if DEN_MESH_EDITOR_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Tooltip("編集対象の Renderer。複数指定できます。")]
        public List<MeshEdit> edits = new List<MeshEdit>();

        [Header("ブラシ設定")]
        [Tooltip("プロポーショナル編集の影響半径（ワールド単位）。")]
        public float brushRadius = 0.03f;

        public FalloffType falloff = FalloffType.Smooth;

        [Header("ミラー")]
        [Tooltip("有効な間に行った操作のみがミラーされます。")]
        public bool mirror;

        public MirrorAxis mirrorAxis = MirrorAxis.X;

        [Header("ベイク")]
        [Tooltip("ON にすると、元の形状を保ったまま編集分をシェイプキーとして追加します。")]
        public bool bakeAsBlendShape;

        /// <summary>
        /// コンポーネント追加時に呼ばれる。Renderer を持つオブジェクトに付与された場合、
        /// その Renderer を最初の編集対象として自動登録する。
        /// </summary>
        private void Reset()
        {
            if (edits.Count > 0) return;

            var renderer = GetComponent<Renderer>();
            if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
            {
                edits.Add(new MeshEdit { target = renderer });
            }
        }

        public MeshEdit FindEdit(Renderer target)
        {
            foreach (var edit in edits)
            {
                if (edit != null && edit.target == target) return edit;
            }

            return null;
        }
    }
}
