using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
        private sealed class TargetState
        {
            public Renderer Original;
            public Renderer Proxy;
            public SkinnedMeshRenderer Skinned;
            public Mesh Mesh;

            /// <summary>
            /// 最後に読めたメッシュの頂点数。
            ///
            /// <see cref="Mesh"/> はプレビューパイプラインが所有するインスタンスで、
            /// パイプラインが作り直されると破棄される（＝ Unity の null 比較で null になる）。
            /// ドラッグ中は <see cref="Refresh"/> を止めているため、確定時には
            /// 破棄済みの参照しか残っていないことがある。頂点数だけは控えておく。
            /// </summary>
            public int VertexCount;

            public Mesh BakeScratch;
            public Vector3[] WorldVertices;
            public MeshEdit Edit;

            /// <summary>BakeMesh / Mesh から頂点を読み出すための使い回しバッファ。</summary>
            public readonly List<Vector3> LocalVertices = new List<Vector3>();

            /// <summary>遮蔽判定用のインデックス配列。メッシュが変わったときだけ取り直す。</summary>
            public readonly List<int> Triangles = new List<int>();

            public bool HasTriangles;

            // ピック時に計算し直す視点依存のキャッシュ
            public Vector2[] ScreenPositions;
            public float[] ViewDepths;
            public bool[] InFront;

            // 遮蔽三角形のスクリーン空間ハッシュグリッド（CSR 形式）。
            // 視点か形状が変わったときだけ作り直す
            public int[] BucketStart;
            public int[] BucketItems;
            public int[] BucketCursor;
            public readonly List<int> Overflow = new List<int>();
            public bool GridValid;

            /// <summary>同じ三角形を二重に評価しないためのマーク。</summary>
            public int[] Stamp;

            public int StampGeneration;

            public Transform[] Bones;
            public readonly List<Matrix4x4> BindPoses = new List<Matrix4x4>();
            public readonly List<BoneWeight> BoneWeights = new List<BoneWeight>();

            // Undo やドラッグのたびに作り直さず、中身だけ入れ替えて使う。
            // Working は LiveEdits へ参照のまま渡してあるので、インスタンスを差し替えない
            // ことが「公開済みの参照が古くならない」ことの保証にもなっている
            public readonly Dictionary<int, Vector3> Working = new Dictionary<int, Vector3>();
            public readonly Dictionary<int, Vector3> Snapshot = new Dictionary<int, Vector3>();
            public bool Touched;
        }

        private struct Influence
        {
            public TargetState Target;
            public int Index;
            public float Weight;
            public float MirrorWeight;
            public Matrix4x4 InverseSkin;
        }

        /// <summary>クリック位置に近い頂点の候補。近い順に並べる。</summary>
        private struct Candidate
        {
            public TargetState Target;
            public int Index;
            public float ScreenDistanceSq;
        }

        /// <summary>遮蔽判定の対象になりうる三角形への参照。</summary>
        private struct TriangleRef
        {
            public TargetState Target;
            public int Start;
        }

        /// <summary>
        /// スクリーン座標キャッシュの有効性を判断するための視点情報。
        /// これが変わらない限り、頂点のスクリーン座標は再計算しなくてよい。
        /// </summary>
        private struct ViewKey
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float FieldOfView;
            public bool Orthographic;
            public float OrthographicSize;
            public Rect PixelRect;
            public int GeometryGeneration;

            public bool Matches(ViewKey other)
            {
                return GeometryGeneration == other.GeometryGeneration
                       && Orthographic == other.Orthographic
                       && Position == other.Position
                       && Rotation == other.Rotation
                       && FieldOfView == other.FieldOfView
                       && OrthographicSize == other.OrthographicSize
                       && PixelRect == other.PixelRect;
            }

            public static ViewKey From(Camera camera, int geometryGeneration)
            {
                var t = camera.transform;
                return new ViewKey
                {
                    Position = t.position,
                    Rotation = t.rotation,
                    FieldOfView = camera.fieldOfView,
                    Orthographic = camera.orthographic,
                    OrthographicSize = camera.orthographicSize,
                    PixelRect = camera.pixelRect,
                    GeometryGeneration = geometryGeneration,
                };
            }
        }

        private readonly List<TargetState> _targets = new List<TargetState>();
        private readonly List<Influence> _influences = new List<Influence>();
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<TriangleRef> _nearbyTriangles = new List<TriangleRef>();

        /// <summary>Unity が選択中の Renderer へ既定で使う描画状態。</summary>
        private const EditorSelectedRenderState DefaultSelectedRenderState =
            EditorSelectedRenderState.Highlight | EditorSelectedRenderState.Wireframe;

        /// <summary>
        /// 編集対象の選択時表示（選択ワイヤーフレームなど）を止める／戻す。
        ///
        /// 編集中に見えている形状は NDMF のプロキシで、元 Renderer は
        /// <c>forceRenderingOff</c> で描画されていない。それでもこれらは元 Renderer の形状
        /// （＝編集前の形状）で描かれるため、編集結果の上にずれた線が重なって見づらくなる。
        ///
        /// なお選択アウトライン（オレンジの輪郭）はこの状態を見ておらず、ここでは消せない。
        /// そちらは <see cref="SelectionOutline"/> が担当する。
        ///
        /// 現在の状態を読み出す API は無いため、戻すときは Unity の既定値を書く。
        /// </summary>
        private static void SetSelectedRenderState(Renderer renderer, bool visible)
        {
            if (renderer == null) return;

            EditorUtility.SetSelectedRenderState(
                renderer,
                visible ? DefaultSelectedRenderState : EditorSelectedRenderState.Hidden);
        }

        private void DisposeTargets()
        {
            foreach (var target in _targets)
            {
                SetSelectedRenderState(target.Original, true);
                if (target.Proxy != null && target.Proxy != target.Original)
                {
                    SetSelectedRenderState(target.Proxy, true);
                }

                if (target.BakeScratch != null) Object.DestroyImmediate(target.BakeScratch);
            }

            _targets.Clear();
            _screenCacheValid = false;
        }

        // ------------------------------------------------------------------
        // プロキシからの頂点位置取得

        private void Refresh(bool force)
        {
            if (!force && EditorApplication.timeSinceStartup - _lastRefresh < RefreshIntervalSeconds) return;
            _lastRefresh = EditorApplication.timeSinceStartup;

            if (_component == null) return;

            // 形状が実際に変わったときだけ世代を進める。
            // Refresh は 0.1 秒ごとに走るので、ここで無条件に進めるとスクリーン座標キャッシュが
            // 秒 10 回必ず捨てられ、ホバー中の O(頂点数) 射影とグリッド構築が毎回走ってしまう
            var geometryChanged = SyncTargetList();

            var anyFallback = false;
            var anyVertexCountMismatch = false;

            foreach (var target in _targets)
            {
                var proxy = ProxyRegistry.ResolveOrOriginal(target.Original, out var usingProxy);

                // 選択時表示の状態は、選択内容が変わると Unity 側で既定値へ戻る。
                // 追加選択などでセッションを抜けないまま復活しうるので、毎回書き直しておく
                SetSelectedRenderState(target.Original, false);
                if (proxy != null && proxy != target.Original)
                {
                    SetSelectedRenderState(proxy, false);
                }

                // 下流の NDMF フィルタが頂点数を変えている場合、プロキシから読んだ頂点位置と
                // 元メッシュのインデックスで保存するデルタの対応が取れない。
                // そのまま編集させると無関係な頂点が動くので、プロキシを捨てて元 Renderer へ退避する。
                // （Avatar Optimizer の Remove Mesh 系は三角形インデックスしか書き換えないので該当しない）
                if (usingProxy && DownstreamGuard.HasVertexCountMismatch(target.Original))
                {
                    proxy = target.Original;
                    usingProxy = false;
                    anyVertexCountMismatch = true;
                }

                if (!usingProxy) anyFallback = true;

                if (target.Proxy != proxy)
                {
                    target.Proxy = proxy;
                    target.Skinned = proxy as SkinnedMeshRenderer;
                }

                // プレビュー用メッシュはパイプライン再構築のたびに作り直されるため、
                // プロキシが同じでもメッシュのインスタンスは変わりうる。毎回読み直す。
                var mesh = MeshDeltaApplier.GetSharedMesh(proxy);

                // 確定時にメッシュが破棄済みでも頂点数を書けるよう、読めたときに控えておく
                if (mesh != null) target.VertexCount = mesh.vertexCount;

                if (target.Mesh != mesh)
                {
                    target.Mesh = mesh;
                    geometryChanged = true;

                    // List 版の取得 API を使い、パイプライン再構築のたびに
                    // ボーンウェイト（頂点数 × 32 バイト）や三角形配列を確保し直さないようにする
                    if (mesh != null)
                    {
                        mesh.GetBindposes(target.BindPoses);
                        mesh.GetBoneWeights(target.BoneWeights);

                        // 遮蔽判定に使う。トポロジは変わらないが、プレビュー用メッシュは
                        // パイプライン再構築のたびに別インスタンスになるので取り直す
                        ReadTriangles(mesh, target.Triangles);
                        target.HasTriangles = target.Triangles.Count >= 3;
                    }
                    else
                    {
                        target.BindPoses.Clear();
                        target.BoneWeights.Clear();
                        target.Triangles.Clear();
                        target.HasTriangles = false;
                    }

                    target.GridValid = false;
                    target.Stamp = null;
                }

                // Scale Adjuster などがシャドウボーンを差し替えることがあるので毎回読み直す
                target.Bones = target.Skinned != null ? target.Skinned.bones : null;

                if (UpdateWorldVertices(target)) geometryChanged = true;
            }

            if (geometryChanged)
            {
                unchecked
                {
                    _geometryGeneration++;
                }
            }

            AnyFallback = anyFallback;
            AnyVertexCountMismatch = anyVertexCountMismatch;

            // 編集開始直後やパイプライン再構築の数フレームは、正常でも一時的に
            // フォールバック状態になる。状態が続いたときにだけ警告する（明滅させない）
            if (!anyFallback)
            {
                _fallbackSince = -1;
            }
            else if (_fallbackSince < 0)
            {
                _fallbackSince = EditorApplication.timeSinceStartup;
            }

            _showFallbackWarning = _fallbackSince >= 0
                                   && EditorApplication.timeSinceStartup - _fallbackSince
                                   > FallbackWarningDelaySeconds;
        }

        /// <summary>
        /// 編集データに添える頂点数を決める。取得できなければ 0。
        ///
        /// プレビュー用メッシュはパイプライン再構築のたびに破棄されるため、
        /// <see cref="TargetState.Mesh"/> をそのまま参照すると確定時に null になっていることがある
        /// （下流フィルタ併用時はドラッグ中に毎回作り直されるので、ほぼ必ずそうなる）。
        /// 控えておいた頂点数、それも無ければ元 Renderer のメッシュへ順に退避する。
        /// </summary>
        private static int ResolveVertexCount(TargetState target)
        {
            if (target.Mesh != null) return target.Mesh.vertexCount;
            if (target.VertexCount > 0) return target.VertexCount;

            var mesh = MeshDeltaApplier.GetSharedMesh(target.Original);
            return mesh != null ? mesh.vertexCount : 0;
        }

        /// <summary>対象リストを作り直したら true。</summary>
        private bool SyncTargetList()
        {
            var wanted = new List<MeshEdit>();
            foreach (var edit in _component.edits)
            {
                if (edit?.target == null) continue;
                if (MeshDeltaApplier.GetSharedMesh(edit.target) == null) continue;
                wanted.Add(edit);
            }

            if (_targets.Count == wanted.Count)
            {
                var same = true;
                for (var i = 0; i < wanted.Count; i++)
                {
                    if (_targets[i].Original == wanted[i].target && _targets[i].Edit == wanted[i]) continue;
                    same = false;
                    break;
                }

                if (same) return false;
            }

            DisposeTargets();
            ClearSelection();

            foreach (var edit in wanted)
            {
                var state = new TargetState
                {
                    Original = edit.target,
                    Edit = edit,
                };

                edit.CopyTo(state.Working);
                _targets.Add(state);
            }

            return true;
        }

        /// <summary>
        /// 全サブメッシュの三角形インデックスを 1 本のリストへ読み出す。
        /// <c>Mesh.triangles</c> と違い、呼び出しごとに int 配列を確保しない。
        /// </summary>
        private static void ReadTriangles(Mesh mesh, List<int> destination)
        {
            destination.Clear();

            var subMeshCount = mesh.subMeshCount;
            for (var s = 0; s < subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;

                mesh.GetTriangles(IndexScratch, s);
                destination.AddRange(IndexScratch);
            }
        }

        /// <summary>
        /// プロキシからワールド空間の頂点位置を取り直す。実際に位置が変わったら true。
        /// </summary>
        private static bool UpdateWorldVertices(TargetState target)
        {
            if (target.Proxy == null || target.Mesh == null) return false;

            var localToWorld = target.Proxy.transform.localToWorldMatrix;
            var source = target.LocalVertices;

            if (target.Skinned != null)
            {
                if (target.BakeScratch == null)
                {
                    target.BakeScratch = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }

                // プロキシの bones（Scale Adjuster のシャドウボーンを含む）を用いてスキニング結果を得る
                target.Skinned.BakeMesh(target.BakeScratch);

                // Mesh.vertices は呼び出しごとに Vector3[] を確保する。
                // 0.1 秒ごとに走る経路なので List 版を使い回す
                target.BakeScratch.GetVertices(source);
            }
            else
            {
                target.Mesh.GetVertices(source);
            }

            var count = source.Count;
            var changed = target.WorldVertices == null || target.WorldVertices.Length != count;
            if (changed) target.WorldVertices = new Vector3[count];

            var world = target.WorldVertices;
            for (var i = 0; i < count; i++)
            {
                var position = localToWorld.MultiplyPoint3x4(source[i]);

                // Vector3 の == は近似比較。微小な数値誤差でキャッシュを捨てないので都合がよい
                if (!changed && position != world[i]) changed = true;
                world[i] = position;
            }

            return changed;
        }
    }
}
