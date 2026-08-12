using System;
using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// シーンビューでのメッシュ編集セッション。
    ///
    /// 編集の基準となる頂点位置は、NDMF プレビューのプロキシ Renderer から取得する
    /// （<see cref="ProxyRegistry"/>）。プロキシは他ツールのメッシュ変形とボーン操作の両方を
    /// 反映しているため、SkinnedMeshRenderer.BakeMesh でスキニング結果を得ることで
    /// 「実際に見えている形状」を編集できる。
    ///
    /// ドラッグはワールド空間で発生するが、デルタはメッシュローカル空間で保存するため、
    /// スキニング行列の逆行列で変換する。
    /// </summary>
    internal class EditSession
    {
        private const float PickThresholdPixels = 24f;
        private const float RefreshIntervalSeconds = 0.1f;
        private const int MaxDrawnVertices = 3000;

        /// <summary>クリック位置に近い順に、最大でいくつまで遮蔽判定を試すか。</summary>
        private const int MaxPickCandidates = 8;

        /// <summary>遮蔽判定用の三角形ハッシュグリッドのセルサイズ（GUI ポイント）。</summary>
        private const float TriangleCellSize = 48f;

        /// <summary>ハッシュグリッドのバケット数。2 のべき乗であること。</summary>
        private const int TriangleBuckets = 8192;

        /// <summary>1 つの三角形を登録するセル数の上限。超えたものはオーバーフロー側へ回す。</summary>
        private const int MaxCellsPerTriangle = 12;

        internal const float MinBrushRadius = 0.001f;
        internal const float MaxBrushRadius = 0.5f;

        /// <summary>ホイール 1 目盛りあたりの半径の倍率。加算ではなく乗算にすることで、
        /// 半径が小さいときは細かく、大きいときは粗く変化させる。</summary>
        private const float RadiusScrollStep = 1.05f;

        /// <summary>
        /// プロキシ未取得の警告を出すまでの猶予。パイプライン再構築の数フレームで
        /// 警告が明滅しないようにする。
        /// </summary>
        private const double FallbackWarningDelaySeconds = 1.0;

        private static EditSession _active;
        private static bool _toolsHiddenBefore;

        /// <summary>サブメッシュごとのインデックス読み出しに使う共有バッファ。</summary>
        private static readonly List<int> IndexScratch = new List<int>();

        /// <summary>
        /// 編集中のコンポーネント。NDMF プレビューフィルタがこの値を監視し、
        /// 編集セッションの開始・終了でプロキシの生成対象を切り替える。
        /// </summary>
        internal static readonly PublishedValue<DenMeshEditor> ActiveComponent =
            new PublishedValue<DenMeshEditor>(null, "DenMeshEditor.ActiveComponent");

        internal static EditSession Active => _active;

        internal static bool IsActive(DenMeshEditor component)
        {
            return _active != null && _active._component == component;
        }

        /// <summary>
        /// エディタのライフサイクルに合わせてセッションを確実に閉じる。
        ///
        /// これが無いと、ドメインリロード時に <see cref="Cleanup"/> が走らず
        /// BakeScratch（HideAndDontSave な Mesh）がエディタ再起動まで残り、
        /// Tools.hidden も戻らない。さらに Enter Play Mode Options でドメインリロードを
        /// 無効にしている環境では static が生き残るため、プレイモード中もセッションが
        /// 動き続けてコンポーネントを書き換えてしまう。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void InstallLifecycleHooks()
        {
            AssemblyReloadEvents.beforeAssemblyReload += End;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            End();
        }

        private static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            End();
        }

        private static void OnUndoRedoPerformed()
        {
            _active?.ResyncFromComponent();
        }

        internal static void Begin(DenMeshEditor component)
        {
            End();
            if (component == null) return;

            _active = new EditSession(component);
            SceneView.duringSceneGui += _active.OnSceneGui;

            // シーンビューの再描画は描画ループの外側から要求する（理由は OnEditorUpdate）
            EditorApplication.update += _active.OnEditorUpdate;

            _toolsHiddenBefore = Tools.hidden;
            Tools.hidden = true;

            // プレビューフィルタへ「編集開始」を伝え、プロキシを生成させる
            ActiveComponent.Value = component;

            SceneView.RepaintAll();
        }

        internal static void End()
        {
            if (_active == null) return;

            SceneView.duringSceneGui -= _active.OnSceneGui;
            EditorApplication.update -= _active.OnEditorUpdate;
            _active.Cleanup();
            _active = null;

            // 開始前の状態へ戻す（ユーザーが自分でツールを隠していた場合を潰さない）
            Tools.hidden = _toolsHiddenBefore;
            ActiveComponent.Value = null;

            SceneView.RepaintAll();
        }

        internal static void NotifySettingsChanged()
        {
            if (_active == null) return;

            if (_active._hasSelection)
            {
                _active.RecomputeMirrorCenter();
                _active.BuildInfluences();
            }

            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------

        private sealed class TargetState
        {
            public Renderer Original;
            public Renderer Proxy;
            public SkinnedMeshRenderer Skinned;
            public Mesh Mesh;
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

            public Dictionary<int, Vector3> Working = new Dictionary<int, Vector3>();
            public Dictionary<int, Vector3> Snapshot = new Dictionary<int, Vector3>();
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

        private readonly DenMeshEditor _component;
        private readonly List<TargetState> _targets = new List<TargetState>();
        private readonly List<Influence> _influences = new List<Influence>();
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<TriangleRef> _nearbyTriangles = new List<TriangleRef>();

        private double _lastRefresh;
        private bool _hasSelection;
        private TargetState _selectedTarget;
        private int _selectedIndex = -1;
        private Vector3 _centerWorld;
        private Vector3 _mirrorCenterWorld;
        private bool _mirrorActive;
        private Vector3 _handlePosition;
        private bool _dragging;

        /// <summary>
        /// 選択頂点の「自分のデルタを除いた」ワールド位置と、そのスキニング行列。
        ///
        /// ハンドル位置は常に <c>_selectedBaseWorld + skin * delta</c> で表せる。
        /// これを持っておくと、Undo でデルタが巻き戻ったときに、NDMF プレビューの
        /// 再構築（非同期）を待たずにハンドルを正しい位置へ戻せる。
        /// </summary>
        private Vector3 _selectedBaseWorld;

        private Matrix4x4 _selectedSkin = Matrix4x4.identity;

        // ドラッグ中にホイールで変更した半径。確定するまでコンポーネントには書かない
        private float _pendingRadius;
        private bool _hasPendingRadius;

        private TargetState _hoverTarget;
        private int _hoverIndex = -1;

        private Rect _overlayRect;

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

        // シーンビューの定期再描画
        private double _lastRepaint;

        // プロキシ未取得の状態がどれだけ続いているか
        private double _fallbackSince = -1;
        private bool _showFallbackWarning;

        /// <summary>オーバーレイのレイアウトに影響する状態。Layout イベント時に固定する。</summary>
        private bool _overlayShowsWarning;

        /// <summary>オーバーレイのスライダー操作を Undo 1 段にまとめるためのグループ番号。</summary>
        private int _settingsUndoGroup = -1;

        /// <summary>プロキシを取得できていない対象があるか（生の状態）。</summary>
        internal bool AnyFallback { get; private set; }

        /// <summary>
        /// ユーザーへ警告を出すべきか。パイプライン再構築の数フレームで明滅しないよう、
        /// フォールバック状態が一定時間続いたときだけ true になる。
        /// </summary>
        internal bool ShowFallbackWarning => _showFallbackWarning;

        /// <summary>
        /// 現在有効なブラシ半径。ドラッグ中にホイールで変更した未確定値を優先する。
        /// </summary>
        private float BrushRadius => _hasPendingRadius ? _pendingRadius : _component.brushRadius;

        private EditSession(DenMeshEditor component)
        {
            _component = component;
            Refresh(true);
        }

        private void Cleanup()
        {
            // 未確定のドラッグ内容は破棄し、プレビューをコンポーネントの内容へ戻す
            LiveEdits.Clear();

            DisposeTargets();
            _influences.Clear();
            _candidates.Clear();
            _nearbyTriangles.Clear();

            ProxyRegistry.Prune();
        }

        private void DisposeTargets()
        {
            foreach (var target in _targets)
            {
                if (target.BakeScratch != null) Object.DestroyImmediate(target.BakeScratch);
            }

            _targets.Clear();
            _screenCacheValid = false;
        }

        /// <summary>
        /// Undo / Redo の後に、作業状態をコンポーネントの現在値から作り直す。
        ///
        /// これを行わないと、巻き戻ったコンポーネントに対して古い Working / Snapshot が
        /// 残ったままになり、次のドラッグの <see cref="Commit"/> で「取り消したはずの編集」を
        /// 書き戻してしまう。
        /// </summary>
        private void ResyncFromComponent()
        {
            if (_component == null)
            {
                End();
                return;
            }

            LiveEdits.Clear();
            _dragging = false;
            _hasPendingRadius = false;
            _settingsUndoGroup = -1;

            // 選択の復元用に控えておく（Refresh で解除されうるため、読むのは先）
            var hadSelection = _hasSelection;
            var selectedRenderer = _selectedTarget?.Original;
            var selectedIndex = _selectedIndex;

            // MeshEdit のインスタンスが差し替わっていれば、SyncTargetList が
            // ターゲットごと作り直す（このとき選択も解除される）
            Refresh(true);

            // インスタンスが維持された場合は作業状態だけを作り直す
            foreach (var target in _targets)
            {
                target.Working = target.Edit != null
                    ? target.Edit.ToDictionary()
                    : new Dictionary<int, Vector3>();
                target.Snapshot = new Dictionary<int, Vector3>(target.Working);
                target.Touched = false;
            }

            // Undo で変わるのはデルタだけで、どの頂点を掴んでいるかは変わらない。
            // 対象が作り直されて選択が落ちた場合は選び直す
            if (hadSelection && !_hasSelection)
            {
                RestoreSelection(selectedRenderer, selectedIndex);
            }

            if (_hasSelection)
            {
                // ハンドルを巻き戻ったデルタの位置へ戻す。
                // プレビューメッシュの再構築を待たずに決まるので、ここで確定できる
                SyncHandleToSelection();
                RecomputeMirrorCenter();
                BuildInfluences();
            }

            SceneView.RepaintAll();
        }

        /// <summary>
        /// シーンビューの定期再描画。
        ///
        /// Layout イベントの中で <c>sceneView.Repaint()</c> を呼ぶと
        /// Repaint → OnGUI → Layout → Repaint の無限ループになり、編集中ずっと
        /// 全力で再描画し続けてしまう。描画ループの外側から一定間隔で要求する。
        /// </summary>
        private void OnEditorUpdate()
        {
            // ドラッグ中はハンドル操作自体が再描画を駆動するので不要
            if (_dragging) return;

            if (EditorApplication.timeSinceStartup - _lastRepaint < RefreshIntervalSeconds) return;
            _lastRepaint = EditorApplication.timeSinceStartup;

            SceneView.RepaintAll();
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

            foreach (var target in _targets)
            {
                var proxy = ProxyRegistry.ResolveOrOriginal(target.Original, out var usingProxy);
                if (!usingProxy) anyFallback = true;

                if (target.Proxy != proxy)
                {
                    target.Proxy = proxy;
                    target.Skinned = proxy as SkinnedMeshRenderer;
                }

                // プレビュー用メッシュはパイプライン再構築のたびに作り直されるため、
                // プロキシが同じでもメッシュのインスタンスは変わりうる。毎回読み直す。
                var mesh = MeshDeltaApplier.GetSharedMesh(proxy);
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
                _targets.Add(new TargetState
                {
                    Original = edit.target,
                    Edit = edit,
                    Working = edit.ToDictionary(),
                });
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

        /// <summary>
        /// 頂点 index のメッシュローカル空間 → ワールド空間のスキニング行列を求める。
        /// M = Σ wi * (bones[i].localToWorldMatrix * bindposes[i])
        /// </summary>
        private static Matrix4x4 SkinMatrix(TargetState target, int index)
        {
            // BuildInfluences はホイールでの半径変更やオーバーレイ操作から
            // Refresh を経由せずに呼ばれる。ドラッグ中は Refresh が止まっているため、
            // ここへ来た時点でプロキシが破棄済み（パイプライン再構築）ということがありうる。
            if (target.Proxy == null) return Matrix4x4.identity;

            var fallback = target.Proxy.transform.localToWorldMatrix;

            if (target.Skinned == null || target.Bones == null || target.BindPoses.Count == 0 ||
                index >= target.BoneWeights.Count)
            {
                return fallback;
            }

            var bw = target.BoneWeights[index];
            var accumulated = new Matrix4x4();
            var total = 0f;

            total += Accumulate(ref accumulated, target, bw.boneIndex0, bw.weight0);
            total += Accumulate(ref accumulated, target, bw.boneIndex1, bw.weight1);
            total += Accumulate(ref accumulated, target, bw.boneIndex2, bw.weight2);
            total += Accumulate(ref accumulated, target, bw.boneIndex3, bw.weight3);

            if (total <= 1e-6f) return fallback;

            if (!Mathf.Approximately(total, 1f))
            {
                var scale = 1f / total;
                for (var i = 0; i < 16; i++) accumulated[i] *= scale;
            }

            return accumulated;
        }

        private static float Accumulate(ref Matrix4x4 accumulated, TargetState target, int boneIndex, float weight)
        {
            if (weight <= 0f) return 0f;
            if (boneIndex < 0 || boneIndex >= target.Bones.Length || boneIndex >= target.BindPoses.Count) return 0f;

            var bone = target.Bones[boneIndex];
            if (bone == null) return 0f;

            var m = bone.localToWorldMatrix * target.BindPoses[boneIndex];
            for (var i = 0; i < 16; i++) accumulated[i] += m[i] * weight;

            return weight;
        }

        private static Matrix4x4 SafeInverse(Matrix4x4 m)
        {
            return Mathf.Abs(m.determinant) < 1e-12f ? Matrix4x4.identity : m.inverse;
        }

        // ------------------------------------------------------------------
        // ミラー基準空間

        private Transform MirrorRoot
        {
            get
            {
                var avatarRoot = nadena.dev.ndmf.runtime.RuntimeUtil.FindAvatarInParents(_component.transform);
                return avatarRoot != null ? avatarRoot : _component.transform;
            }
        }

        private static Vector3 Reflect(Vector3 v, MirrorAxis axis)
        {
            switch (axis)
            {
                case MirrorAxis.Y: return new Vector3(v.x, -v.y, v.z);
                case MirrorAxis.Z: return new Vector3(v.x, v.y, -v.z);
                default: return new Vector3(-v.x, v.y, v.z);
            }
        }

        private static float AxisComponent(Vector3 v, MirrorAxis axis)
        {
            switch (axis)
            {
                case MirrorAxis.Y: return v.y;
                case MirrorAxis.Z: return v.z;
                default: return v.x;
            }
        }

        // ------------------------------------------------------------------
        // シーンビュー

        private void OnSceneGui(SceneView sceneView)
        {
            if (_component == null)
            {
                End();
                return;
            }

            // 別のオブジェクトを選択したら編集モードを抜ける（ツール状態を残さないため）
            if (!Selection.Contains(_component.gameObject))
            {
                End();
                return;
            }

            var current = Event.current;

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                if (_hasSelection) ClearSelection();
                else End();
                current.Use();
                return;
            }

            if (!_dragging) Refresh(false);

            // 何もヒットしなかった場合に拾うためのフォールバックコントロール。
            // MouseDown 時にこれが nearestControl であれば、移動ハンドル上ではないと判断できる。
            var defaultControl = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(defaultControl);

            DrawOverlay();
            HandleSelection(current, defaultControl);
            HandleRadiusScroll(current);
            HandleDrag(current);
            DrawGizmos();

            // Layout イベントで Repaint を呼ぶと無限再描画になるため、ここではホバー追従が
            // 必要なマウス移動時だけにする。定期更新は OnEditorUpdate が担当する。
            if (current.type == EventType.MouseMove || current.type == EventType.MouseDrag)
            {
                sceneView.Repaint();
            }
        }

        private void HandleSelection(Event current, int defaultControl)
        {
            if (_overlayRect.Contains(current.mousePosition)) return;

            if (current.type == EventType.MouseMove && !_dragging)
            {
                TryPick(current.mousePosition, out _hoverTarget, out _hoverIndex);
            }

            if (current.type != EventType.MouseDown || current.button != 0 || current.alt) return;

            // 移動ハンドルを掴もうとしているときは選択処理を行わない
            if (HandleUtility.nearestControl != defaultControl) return;

            if (TryPick(current.mousePosition, out var target, out var index))
            {
                BeginSelection(target, index);
                current.Use();
            }
            else
            {
                ClearSelection();
            }
        }

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

        private void BeginSelection(TargetState target, int index)
        {
            _hasSelection = true;
            _selectedTarget = target;
            _selectedIndex = index;

            // デルタを差し引いた基準位置を控えておく（Undo でハンドルを戻すために使う）
            _selectedSkin = SkinMatrix(target, index);
            target.Working.TryGetValue(index, out var delta);
            _selectedBaseWorld = target.WorldVertices[index] - _selectedSkin.MultiplyVector(delta);

            _centerWorld = target.WorldVertices[index];
            _handlePosition = _centerWorld;

            RecomputeMirrorCenter();
            BuildInfluences();
            CommitSnapshot();
        }

        /// <summary>
        /// 現在のデルタからハンドル位置を求め直す。
        ///
        /// プレビューメッシュを読まずに決まるため、NDMF のパイプライン再構築が
        /// 非同期であることに影響されない。
        /// </summary>
        private void SyncHandleToSelection()
        {
            if (!_hasSelection || _selectedTarget == null) return;

            _selectedTarget.Working.TryGetValue(_selectedIndex, out var delta);
            _centerWorld = _selectedBaseWorld + _selectedSkin.MultiplyVector(delta);
            _handlePosition = _centerWorld;
        }

        /// <summary>
        /// 対象の作り直しで解除された選択を、同じ Renderer・同じ頂点番号で選び直す。
        /// </summary>
        private void RestoreSelection(Renderer original, int index)
        {
            if (original == null || index < 0) return;

            foreach (var target in _targets)
            {
                if (target.Original != original) continue;
                if (target.WorldVertices == null || index >= target.WorldVertices.Length) return;

                _hasSelection = true;
                _selectedTarget = target;
                _selectedIndex = index;
                return;
            }
        }

        private void RecomputeMirrorCenter()
        {
            _mirrorActive = false;
            if (!_component.mirror) return;

            var root = MirrorRoot;
            var centerInRoot = root.worldToLocalMatrix.MultiplyPoint3x4(_centerWorld);

            // 中心線のごく近くではミラー適用をスキップする（影響球の重なりによる二重適用を避ける）
            var epsilon = Mathf.Max(1e-4f, BrushRadius * 0.05f);
            if (Mathf.Abs(AxisComponent(centerInRoot, _component.mirrorAxis)) < epsilon) return;

            _mirrorActive = true;
            _mirrorCenterWorld = root.localToWorldMatrix.MultiplyPoint3x4(
                Reflect(centerInRoot, _component.mirrorAxis));
        }

        private void BuildInfluences()
        {
            _influences.Clear();

            var radius = Mathf.Max(1e-5f, BrushRadius);
            var radiusSq = radius * radius;
            var inverseRadius = 1f / radius;
            var falloff = _component.falloff;

            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = vertices[i];

                    // 影響圏の外がほとんどなので、まず二乗距離で弾く。
                    // 平方根は圏内の頂点にだけ計算する
                    var weight = 0f;
                    var distanceSq = (world - _centerWorld).sqrMagnitude;
                    if (distanceSq < radiusSq)
                    {
                        weight = FalloffUtil.Weight(Mathf.Sqrt(distanceSq) * inverseRadius, falloff);
                    }

                    var mirrorWeight = 0f;
                    if (_mirrorActive)
                    {
                        var mirrorDistanceSq = (world - _mirrorCenterWorld).sqrMagnitude;
                        if (mirrorDistanceSq < radiusSq)
                        {
                            mirrorWeight = FalloffUtil.Weight(Mathf.Sqrt(mirrorDistanceSq) * inverseRadius, falloff);
                        }
                    }

                    if (weight <= 0f && mirrorWeight <= 0f) continue;

                    _influences.Add(new Influence
                    {
                        Target = target,
                        Index = i,
                        Weight = weight,
                        MirrorWeight = mirrorWeight,
                        InverseSkin = SafeInverse(SkinMatrix(target, i)),
                    });
                }
            }
        }

        /// <summary>
        /// 現在の編集内容を「次のドラッグの基準」として確定する。
        /// </summary>
        private void CommitSnapshot()
        {
            foreach (var target in _targets)
            {
                target.Snapshot = new Dictionary<int, Vector3>(target.Working);
                target.Touched = false;
            }
        }

        private void ClearSelection()
        {
            // ドラッグ中に Esc で解除されうる。ここで降ろさないと _dragging が立ちっぱなしになり、
            // 頂点位置の更新（Refresh）が二度と走らなくなる
            _dragging = false;
            _hasPendingRadius = false;

            _hasSelection = false;
            _mirrorActive = false;
            _selectedTarget = null;
            _selectedIndex = -1;
            _influences.Clear();
            _hoverTarget = null;
            _hoverIndex = -1;
        }

        /// <summary>
        /// ドラッグ中のホイール操作でブラシ半径を変更する。
        ///
        /// 影響範囲の再計算に使う頂点位置（<see cref="TargetState.WorldVertices"/>）は
        /// ドラッグ中は更新されない＝ドラッグ開始時の形状のままなので、
        /// 選択時と同じ基準で影響範囲を計算し直せる。
        ///
        /// 半径を縮めた場合、範囲から外れた頂点は <see cref="ApplyDisplacement"/> が
        /// スナップショットから作り直すことで元に戻る。
        /// </summary>
        private void HandleRadiusScroll(Event current)
        {
            // ドラッグ中のみ横取りする。それ以外ではシーンビューのズームを妨げない
            if (!_dragging || !_hasSelection) return;
            if (current.type != EventType.ScrollWheel) return;

            // ホイール上方向で delta.y が負になる。上で拡大、下で縮小
            var notches = Mathf.Clamp(current.delta.y, -10f, 10f);
            _pendingRadius = Mathf.Clamp(
                BrushRadius * Mathf.Pow(RadiusScrollStep, -notches),
                MinBrushRadius, MaxBrushRadius);
            _hasPendingRadius = true;

            RecomputeMirrorCenter();
            BuildInfluences();
            ApplyDisplacement();

            // シーンビューのズームへ渡さない
            current.Use();
        }

        private void HandleDrag(Event current)
        {
            if (!_hasSelection) return;

            EditorGUI.BeginChangeCheck();

            // Tools.pivotRotation（Global / Local）に追従させる
            var moved = Handles.PositionHandle(_handlePosition, Tools.handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                _handlePosition = moved;
                _dragging = true;
                ApplyDisplacement();
            }

            if (!_dragging) return;

            // Handles.PositionHandle は hotControl を持った状態で MouseUp を受け取ると
            // evt.Use() を呼ぶ。Event.current は同一インスタンスなので、ここへ来た時点で
            // current.type は EventType.Used になっている。
            // つまり type == MouseUp で判定すると確定処理が永久に走らず、
            //   - ドラッグ結果がコンポーネントへ書き込まれない
            //   - _dragging が立ちっぱなしで Refresh も止まる
            //   - 編集終了時に Cleanup の LiveEdits.Clear() で編集が消える
            // という壊れ方をする。Use() の影響を受けない rawType と、
            // hotControl が落ちたことの両方で検出する（後者はウィンドウ外での
            // リリースやフォーカス喪失も拾える）。
            if (GUIUtility.hotControl != 0 && current.rawType != EventType.MouseUp) return;

            _dragging = false;
            Commit();

            // プレビューの再構築は非同期なので、プロキシの更新を待たずに
            // 「ハンドルの現在位置」を次の基準にする（タイミングに依存させない）。
            CommitSnapshot();
            _centerWorld = _handlePosition;
            RecomputeMirrorCenter();
        }

        /// <summary>
        /// ドラッグ結果をコンポーネントへ書き込んで確定する。
        ///
        /// 「ハンドルを掴んで動かす 1 動作 = Undo 1 段」にするため、書き込みは
        /// マウスを離したときの 1 回だけにし（ドラッグ中は <see cref="LiveEdits"/> 経由）、
        /// さらに Undo グループを明示的に切る。
        /// </summary>
        private void Commit()
        {
            if (_component == null) return;

            // 書き込むものが無ければ Undo エントリも作らない。
            // 空の段が積まれると、Ctrl+Z を押しても何も起きないように見える
            var hasChanges = _hasPendingRadius;
            foreach (var target in _targets)
            {
                if (!target.Touched || target.Mesh == null) continue;
                hasChanges = true;
                break;
            }

            if (!hasChanges)
            {
                LiveEdits.Clear();
                return;
            }

            // Unity は「同じ Undo グループ・同じ名前」の RecordObject を 1 段にまとめる。
            // グループが自動で進むのは限られたタイミングだけなので、明示的に切らないと
            // 複数回のドラッグが 1 段に潰れ、Ctrl+Z で一気に巻き戻る。
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Den Mesh Editor");

            // 記録より先に半径を書くと変更前の値が取れなくなるので、記録が先
            Undo.RecordObject(_component, "Den Mesh Editor");

            if (_hasPendingRadius)
            {
                _component.brushRadius = _pendingRadius;
                _hasPendingRadius = false;
            }

            foreach (var target in _targets)
            {
                if (!target.Touched || target.Mesh == null) continue;
                target.Edit.SetFrom(target.Working, target.Mesh.vertexCount);
            }

            EditorUtility.SetDirty(_component);

            // SerializedObject を経由せずフィールドを直接書き換えているため、
            // Prefab インスタンス上ではオーバーライドとして記録されるよう明示しておく
            if (PrefabUtility.IsPartOfPrefabInstance(_component))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(_component);
            }

            // RecordObject の差分は通常 MouseUp の直後に自動で確定するが、その MouseUp は
            // Handles.PositionHandle が既に消費している。自動フラッシュのタイミングに
            // 頼らず、この場で差分を確定させる
            Undo.FlushUndoRecordObjects();

            // 直後の無関係な操作がこのグループへ入り込まないように閉じる
            Undo.IncrementCurrentGroup();

            // 確定したので、プレビューはコンポーネントの内容を読むようになる
            LiveEdits.Clear();
        }

        /// <summary>
        /// ハンドルの変位を各頂点へ配分し、メッシュローカルのデルタとして保存する。
        /// </summary>
        private void ApplyDisplacement()
        {
            var displacement = _handlePosition - _centerWorld;

            var mirrorDisplacement = Vector3.zero;
            if (_mirrorActive)
            {
                var root = MirrorRoot;
                var inRoot = root.worldToLocalMatrix.MultiplyVector(displacement);
                // 中心だけでなく変位ベクトルも反射する。これを忘れると反対側が同じ向きに動く。
                mirrorDisplacement = root.localToWorldMatrix.MultiplyVector(Reflect(inRoot, _component.mirrorAxis));
            }

            // 前フレームの寄与を打ち消すため、確定済みスナップショットから作り直す
            foreach (var target in _targets)
            {
                if (target.Touched) ResetWorkingToSnapshot(target);
            }

            foreach (var influence in _influences)
            {
                var target = influence.Target;
                if (!target.Touched)
                {
                    ResetWorkingToSnapshot(target);
                    target.Touched = true;
                }

                var worldDelta = displacement * influence.Weight + mirrorDisplacement * influence.MirrorWeight;
                var localDelta = influence.InverseSkin.MultiplyVector(worldDelta);

                target.Snapshot.TryGetValue(influence.Index, out var baseDelta);
                target.Working[influence.Index] = baseDelta + localDelta;
            }

            // ドラッグ中はコンポーネントを書き換えない。毎フレーム dirty にすると
            // NDMF がその都度プレビューパイプラインを作り直してしまうため、
            // 未確定データとしてプレビューへ直接渡す。
            foreach (var target in _targets)
            {
                if (!target.Touched) continue;
                LiveEdits.Publish(target.Edit, target.Working);
            }

            SceneView.RepaintAll();
        }

        /// <summary>
        /// Working をスナップショットの内容へ戻す。
        ///
        /// ドラッグ中は毎フレーム呼ばれるので、辞書インスタンスは作り直さず中身だけ入れ替える
        /// （Working は LiveEdits へ渡してあるが、プレビュー側は読み取りしか行わない）。
        /// </summary>
        private static void ResetWorkingToSnapshot(TargetState target)
        {
            target.Working.Clear();
            foreach (var pair in target.Snapshot)
            {
                target.Working.Add(pair.Key, pair.Value);
            }
        }

        // ------------------------------------------------------------------
        // 描画

        private void DrawGizmos()
        {
            if (Event.current.type != EventType.Repaint) return;

            if (!_hasSelection)
            {
                if (_hoverTarget?.WorldVertices == null || _hoverIndex < 0 ||
                    _hoverIndex >= _hoverTarget.WorldVertices.Length) return;

                var hovered = _hoverTarget.WorldVertices[_hoverIndex];
                Handles.color = new Color(1f, 0.8f, 0.2f, 0.9f);
                Handles.DotHandleCap(0, hovered, Quaternion.identity,
                    HandleUtility.GetHandleSize(hovered) * 0.03f, EventType.Repaint);
                DrawBrushCircle(hovered, new Color(1f, 0.8f, 0.2f, 0.6f));
                return;
            }

            DrawBrushCircle(_centerWorld, new Color(0.3f, 0.8f, 1f, 0.8f));
            if (_mirrorActive) DrawBrushCircle(_mirrorCenterWorld, new Color(1f, 0.4f, 0.6f, 0.8f));

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

                var world = vertices[influence.Index];
                Handles.DotHandleCap(0, world, Quaternion.identity,
                    HandleUtility.GetHandleSize(world) * 0.012f, EventType.Repaint);
            }
        }

        private void DrawBrushCircle(Vector3 center, Color color)
        {
            var camera = Camera.current;
            if (camera == null) return;

            Handles.color = color;
            Handles.DrawWireDisc(center, camera.transform.forward, BrushRadius);
        }

        private void DrawOverlay()
        {
            // Layout と Repaint で GUILayout の構成が変わると
            // 「Getting control N's position in a group with only M controls」で例外になる。
            // Refresh は毎イベント走って警告状態を書き換えるため、レイアウトに影響する状態は
            // Layout イベント時に固定してから両イベントで使い回す。
            if (Event.current.type == EventType.Layout)
            {
                _overlayShowsWarning = _showFallbackWarning;
            }

            _overlayRect = new Rect(10, 10, 280, _overlayShowsWarning ? 152 : 116);

            Handles.BeginGUI();
            GUILayout.BeginArea(_overlayRect, GUI.skin.box);

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70f;

            GUILayout.Label("Den Mesh Editor — 編集中", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            // ドラッグ中にホイールで変えた未確定の半径もそのまま表示する
            var radius = EditorGUILayout.Slider("半径", BrushRadius, MinBrushRadius, MaxBrushRadius);
            var falloff = (FalloffType)EditorGUILayout.EnumPopup("減衰", _component.falloff);
            var mirror = EditorGUILayout.Toggle("ミラー", _component.mirror);
            if (EditorGUI.EndChangeCheck())
            {
                // スライダーのドラッグは毎フレーム変更を出すので、
                // 最初の変更時のグループ番号を覚えておき、離したときに 1 段へまとめる。
                // ここでグループを切らないと、連続した設定変更どうしが 1 段に潰れる
                if (_settingsUndoGroup < 0)
                {
                    Undo.IncrementCurrentGroup();
                    _settingsUndoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Den Mesh Editor Settings");
                }

                Undo.RecordObject(_component, "Den Mesh Editor Settings");
                _hasPendingRadius = false;
                _component.brushRadius = radius;
                _component.falloff = falloff;
                _component.mirror = mirror;
                EditorUtility.SetDirty(_component);

                // 半径・減衰・ミラーが変わったら影響範囲を計算し直す
                if (_hasSelection)
                {
                    RecomputeMirrorCenter();
                    BuildInfluences();
                }
            }

            if (_settingsUndoGroup >= 0 && Event.current.rawType == EventType.MouseUp)
            {
                Undo.CollapseUndoOperations(_settingsUndoGroup);
                _settingsUndoGroup = -1;
            }

            if (_overlayShowsWarning)
            {
                EditorGUILayout.HelpBox("NDMF プレビュー未取得。他ツールの影響は反映されていません。",
                    MessageType.Warning);
            }

            EditorGUIUtility.labelWidth = previousLabelWidth;

            GUILayout.EndArea();
            Handles.EndGUI();
        }
    }
}
