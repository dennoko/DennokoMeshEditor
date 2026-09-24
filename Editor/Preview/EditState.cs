using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 1 つの Renderer に寄与する <see cref="MeshEdit"/> 1 件分の「内容が変わったか」の比較値。
    ///
    /// プレビューノードは、最後に反映したときの値の列と現在の値の列を比べて、
    /// 違っていればメッシュを作り直す。通知（「変わったよ」という合図）に頼らず
    /// 状態そのものを比べるので、合図の出し忘れが更新漏れにならない。
    ///
    /// 比較はハッシュではなく値の完全一致で行う。衝突による更新漏れを構造的に起こさないため。
    /// </summary>
    internal readonly struct EditState : IEquatable<EditState>
    {
        /// <summary>
        /// 編集データのインスタンス。Undo のデシリアライズでインスタンスが差し替わった場合を
        /// 検出するため、参照で比較する。
        /// </summary>
        public readonly MeshEdit Edit;

        /// <summary>確定・クリア・Undo による内容の変化。</summary>
        public readonly int Revision;

        /// <summary>Revision を通らない書き換えに対する安全網。</summary>
        public readonly int Count;

        /// <summary>頂点数の不一致による「適用 / スキップ」の切り替わり。</summary>
        public readonly int VertexCount;

        /// <summary>編集セッション中の未確定データ（<see cref="LiveEdits.GetStamp"/>）。</summary>
        public readonly int LiveStamp;

        private EditState(MeshEdit edit, int liveStamp)
        {
            Edit = edit;
            Revision = edit.Revision;
            Count = edit.Count;
            VertexCount = edit.vertexCount;
            LiveStamp = liveStamp;
        }

        public bool Equals(EditState other)
        {
            return ReferenceEquals(Edit, other.Edit)
                   && Revision == other.Revision
                   && Count == other.Count
                   && VertexCount == other.VertexCount
                   && LiveStamp == other.LiveStamp;
        }

        public override bool Equals(object obj)
        {
            return obj is EditState other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Edit != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Edit) : 0;
                hash = hash * 31 + Revision;
                hash = hash * 31 + Count;
                hash = hash * 31 + VertexCount;
                return hash * 31 + LiveStamp;
            }
        }

        /// <summary>
        /// <paramref name="target"/> に寄与する編集の比較値を <paramref name="destination"/> へ集める。
        ///
        /// 収集の順序と対象の絞り込みは <see cref="DenMeshEditorPreviewFilter.GatherEdits"/> と
        /// 同じにする（コンポーネントの順 → 各コンポーネントの edits の順、target の比較は
        /// Unity の <c>!=</c>）。ここで拾う集合が合成側と食い違うと、変化を見落としうる。
        /// </summary>
        /// <param name="includeLive">
        /// 未確定データのスタンプを含めるか。コンポーネントそのものの監視
        /// （セッション外の変更の検出）では含めない。
        /// </param>
        internal static void Collect(IReadOnlyList<DenMeshEditor> components, Renderer target,
            List<EditState> destination, bool includeLive)
        {
            destination.Clear();

            for (var c = 0; c < components.Count; c++)
            {
                AppendFrom(components[c], target, destination, includeLive);
            }
        }

        /// <summary>
        /// 1 コンポーネント分の比較値を末尾へ追加する。
        /// </summary>
        internal static void AppendFrom(DenMeshEditor component, Renderer target,
            List<EditState> destination, bool includeLive)
        {
            if (component == null) return;

            var edits = component.edits;
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (edit == null || edit.target != target) continue;

                destination.Add(new EditState(edit, includeLive ? LiveEdits.GetStamp(edit) : 0));
            }
        }

        /// <summary>
        /// 1 コンポーネント分のうち、<paramref name="targets"/> に含まれる Renderer を対象にする
        /// 編集の比較値を末尾へ追加する。NDMF の監視（1 ノードが複数 Renderer を持ちうる）用。
        /// </summary>
        internal static void AppendFrom(DenMeshEditor component, HashSet<Renderer> targets,
            List<EditState> destination, bool includeLive)
        {
            if (component == null) return;

            var edits = component.edits;
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (edit == null) continue;

                // 破棄済みの Renderer は、合成側（target との != 比較）と同様に対象外とする
                var target = edit.target;
                if (target == null || !targets.Contains(target)) continue;

                destination.Add(new EditState(edit, includeLive ? LiveEdits.GetStamp(edit) : 0));
            }
        }

        internal static bool SequenceEqual(List<EditState> a, List<EditState> b)
        {
            if (a.Count != b.Count) return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// NDMF の監視（<c>ComputeContext.Observe</c>）に渡す、<see cref="EditState"/> 列の不変スナップショット。
    ///
    /// NDMF は抽出値を保持して後の抽出値と比較関数で比べるため、抽出値は後から書き換わらない
    /// オブジェクトでなければならない。比較は要素ごとの完全一致で行い、ハッシュだけで判定しない。
    /// </summary>
    internal sealed class EditSnapshot
    {
        private readonly EditState[] _states;

        private EditSnapshot(List<EditState> states)
        {
            _states = states.ToArray();
        }

        internal static EditSnapshot From(List<EditState> states)
        {
            return new EditSnapshot(states);
        }

        /// <summary>内容が <paramref name="states"/> と完全に一致するか。確保しない。</summary>
        internal bool Matches(List<EditState> states)
        {
            if (_states.Length != states.Count) return false;

            for (var i = 0; i < _states.Length; i++)
            {
                if (!_states[i].Equals(states[i])) return false;
            }

            return true;
        }

        internal static bool AreEqual(EditSnapshot a, EditSnapshot b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a._states.Length != b._states.Length) return false;

            for (var i = 0; i < a._states.Length; i++)
            {
                if (!a._states[i].Equals(b._states[i])) return false;
            }

            return true;
        }
    }
}
