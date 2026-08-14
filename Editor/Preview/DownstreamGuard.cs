using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 「自分より下流の NDMF フィルタがプロキシのメッシュを差し替えているか」を検出する。
    ///
    /// <b>なぜ必要か</b>：
    /// <see cref="DenMeshEditorPreviewNode.OnFrame"/> は毎フレーム生成済みメッシュを
    /// プロキシへ差し込む。これは「自分がフィルタチェーンの最後尾である」ことに依存した実装で、
    /// メッシュを書き換える NDMF フィルタが下流に居ると、そのノードの <c>OnFrame</c> に
    /// 毎フレーム上書きされてドラッグ中の更新が一切見えなくなる
    /// （例：Avatar Optimizer の Remove Mesh 系。Optimizing フェーズなので必ず下流になる）。
    ///
    /// <b>なぜ検出漏れが無いか</b>：
    /// NDMF は毎フレーム <c>ProxyObjectController.OnPreFrame</c> でプロキシの sharedMesh を
    /// 元 Renderer のものへ戻す。つまりメッシュを書き換えるフィルタは、毎フレーム
    /// <c>OnFrame</c> で sharedMesh を代入し直す以外に動作する方法が無い。
    /// 「こちらを壊しうるフィルタ」と「sharedMesh を代入し直すフィルタ」は定義上一致するので、
    /// 描画後に sharedMesh を読み戻すこの方式には構造的な死角が無い。
    ///
    /// 書き込みから読み戻しまでの間に sharedMesh を触るコードが無いことも確認済み
    /// （<c>ProxyObjectController.FinishPreFrame</c> は読むだけ、
    /// <c>ProxyManager.ResetStates</c> は forceRenderingOff しか触らない）。
    ///
    /// <b>タイミング上の揺らぎへの対処</b>：
    /// NDMF の <c>FrameTimeLimiter</c> はノードのループをフレーム途中で打ち切ることがあり、
    /// プレビュー UI が無効なときは 1 ノードも走らない。そこで
    ///   - 実際に代入したフレームだけ判定する（<see cref="Expect"/> が呼ばれた分だけ検証する）
    ///   - 一度検出したらノードが破棄されるまでラッチする
    /// の 2 点で、偽陽性・偽陰性のどちらも無害化している。
    /// </summary>
    [InitializeOnLoad]
    internal static class DownstreamGuard
    {
        private sealed class State
        {
            /// <summary>登録したノード。パイプライン再構築時の取り違えを防ぐ。</summary>
            public object Owner;

            public Renderer Proxy;

            /// <summary>このフレームで代入したメッシュ。検証で消費して null に戻す。</summary>
            public Mesh Expected;

            /// <summary>代入したメッシュの頂点数。下流が頂点数を変えたかの判定に使う。</summary>
            public int ExpectedVertexCount;

            /// <summary>下流に上書きされたか（ラッチ）。</summary>
            public bool Overridden;

            /// <summary>上書きしてきたメッシュの頂点数。一致しなければインデックス対応が壊れている。</summary>
            public int ObservedVertexCount;
        }

        private static readonly Dictionary<Renderer, State> States = new Dictionary<Renderer, State>();

        /// <summary>ラッチ済みの件数。<see cref="AnyOverridden"/> を O(1) にするため持つ。</summary>
        private static int _overriddenCount;

        /// <summary>
        /// 下流に上書きされている対象が 1 つでもあるか。
        /// <see cref="LiveEdits"/> がこれを見て、高速パスと同期パスを切り替える。
        /// </summary>
        internal static bool AnyOverridden => _overriddenCount > 0;

        /// <summary>
        /// ラッチが新たに成立するたびに進む世代番号。
        ///
        /// <see cref="IsOverridden"/> は素の bool なので、これを見ているだけでは
        /// 「検出した瞬間」にノードを作り直させることができない。フィルタ側はこの値を
        /// 常時 <c>Observe</c> しておき、検出と同時にノードを作り直して
        /// <see cref="LiveEdits.SyncedVersion"/> の監視を張れるようにする。
        ///
        /// ラッチは解除されないので、この値が進むのは併用構成を組んだ直後の 1 回だけ。
        /// </summary>
        internal static readonly PublishedValue<int> OverrideGeneration =
            new PublishedValue<int>(0, "DenMeshEditor.DownstreamOverrideGeneration");

        static DownstreamGuard()
        {
            // NDMF 自身が使っているのと同じフック（ProxyManager）。
            // ノードの OnFrame（OnPreCull 経路）より確実に後に呼ばれる。
            Camera.onPostRender -= OnPostRender;
            Camera.onPostRender += OnPostRender;
        }

        /// <summary>
        /// このフレームでプロキシへ代入したメッシュを申告する。
        /// 次の <see cref="OnPostRender"/> で読み戻して、下流の上書きを検出する。
        /// </summary>
        /// <param name="assigned">
        /// 自分が代入したメッシュ。編集が無くて何も代入しなかった場合は、
        /// 上流メッシュ（＝ OnPreFrame が戻した元メッシュ）を渡す。こうすると
        /// 編集が 1 つも無い状態でも下流フィルタを検出できる。
        /// </param>
        internal static void Expect(object owner, Renderer original, Renderer proxy, Mesh assigned)
        {
            if (original == null || proxy == null || assigned == null) return;

            if (!States.TryGetValue(original, out var state))
            {
                state = new State();
                States.Add(original, state);
            }

            state.Owner = owner;
            state.Proxy = proxy;
            state.Expected = assigned;
            state.ExpectedVertexCount = assigned.vertexCount;
        }

        /// <summary>
        /// 下流フィルタに上書きされている対象か。
        /// 一度検出したら、そのノードが破棄されるまで true を返し続ける。
        /// </summary>
        internal static bool IsOverridden(Renderer original)
        {
            return original != null && States.TryGetValue(original, out var state) && state.Overridden;
        }

        /// <summary>
        /// 下流フィルタが頂点数を変えているか。
        ///
        /// 変えられていると、シーンビュー編集側が「プロキシから読んだ頂点位置」と
        /// 「元メッシュのインデックスで保存するデルタ」の対応を取れなくなる。
        /// Avatar Optimizer の Remove Mesh 系は三角形インデックスしか書き換えないので
        /// ここには該当しないが、マージ系のフィルタが下流に来た場合に備える。
        /// </summary>
        internal static bool HasVertexCountMismatch(Renderer original)
        {
            if (original == null || !States.TryGetValue(original, out var state)) return false;
            return state.Overridden && state.ObservedVertexCount != state.ExpectedVertexCount;
        }

        /// <summary>
        /// ノードの破棄時に登録を解除する。パイプライン再構築では新しいノードが登録した後に
        /// 古いノードの Dispose が走ることがあるため、自分が登録したものだけを消す。
        /// </summary>
        internal static void Forget(object owner, Renderer original)
        {
            if (original == null) return;
            if (!States.TryGetValue(original, out var state)) return;
            if (!ReferenceEquals(state.Owner, owner)) return;

            if (state.Overridden) _overriddenCount--;
            States.Remove(original);
        }

        private static void OnPostRender(Camera camera)
        {
            if (States.Count == 0) return;

            List<Renderer> stale = null;
            var newlyLatched = false;

            foreach (var pair in States)
            {
                var state = pair.Value;

                // 代入していないフレームは判定しない。
                // NDMF がノードのループを打ち切った場合や、プレビューが無効な場合に
                // 「上書きされた」と誤検出しないため
                if (state.Expected == null) continue;

                var expected = state.Expected;
                state.Expected = null;

                if (pair.Key == null || state.Proxy == null)
                {
                    // 破棄済み。ここで件数を戻しておく（削除ループでは Unity の null 比較が
                    // 効いてしまい、ラッチ済みかどうかを引き直せない）
                    if (state.Overridden)
                    {
                        state.Overridden = false;
                        _overriddenCount--;
                    }

                    stale ??= new List<Renderer>();
                    stale.Add(pair.Key);
                    continue;
                }

                var current = MeshDeltaApplier.GetSharedMesh(state.Proxy);
                if (current == expected) continue;

                // 下流のノードが差し替えた。以降はラッチしたままにする
                state.ObservedVertexCount = current != null ? current.vertexCount : 0;

                if (state.Overridden) continue;

                state.Overridden = true;
                _overriddenCount++;
                newlyLatched = true;
            }

            if (stale != null)
            {
                foreach (var key in stale)
                {
                    States.Remove(key);
                }
            }

            // 検出した瞬間にノードを作り直させ、同期パスへ切り替える。
            // これを進めないと、フィルタが SyncedVersion の監視を張る機会が無いまま
            // 「上書きされているのに誰も無効化しない」状態で固まる。
            //
            // 値の変更はリスナを同期的に呼ぶので、States の列挙を抜けてから行う
            if (!newlyLatched) return;

            unchecked
            {
                OverrideGeneration.Value = OverrideGeneration.Value + 1;
            }
        }
    }
}
