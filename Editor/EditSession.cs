using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

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

        private static EditSession _active;

        internal static EditSession Active => _active;

        internal static bool IsActive(DenMeshEditor component)
        {
            return _active != null && _active._component == component;
        }

        internal static void Begin(DenMeshEditor component)
        {
            End();
            if (component == null) return;

            _active = new EditSession(component);
            SceneView.duringSceneGui += _active.OnSceneGui;
            Tools.hidden = true;
            SceneView.RepaintAll();
        }

        internal static void End()
        {
            if (_active == null) return;

            SceneView.duringSceneGui -= _active.OnSceneGui;
            _active.Cleanup();
            _active = null;
            Tools.hidden = false;
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

            public Transform[] Bones;
            public Matrix4x4[] BindPoses;
            public BoneWeight[] BoneWeights;

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

        private readonly DenMeshEditor _component;
        private readonly List<TargetState> _targets = new List<TargetState>();
        private readonly List<Influence> _influences = new List<Influence>();

        private double _lastRefresh;
        private bool _hasSelection;
        private TargetState _selectedTarget;
        private int _selectedIndex = -1;
        private Vector3 _centerWorld;
        private Vector3 _mirrorCenterWorld;
        private bool _mirrorActive;
        private Vector3 _handlePosition;
        private bool _dragging;

        private TargetState _hoverTarget;
        private int _hoverIndex = -1;

        private Rect _overlayRect;

        internal bool AnyFallback { get; private set; }

        private EditSession(DenMeshEditor component)
        {
            _component = component;
            Refresh(true);
        }

        private void Cleanup()
        {
            // 未確定のドラッグ内容は破棄し、プレビューをコンポーネントの内容へ戻す
            LiveEdits.Clear();

            foreach (var target in _targets)
            {
                if (target.BakeScratch != null) Object.DestroyImmediate(target.BakeScratch);
            }

            _targets.Clear();
            _influences.Clear();
        }

        // ------------------------------------------------------------------
        // プロキシからの頂点位置取得

        private void Refresh(bool force)
        {
            if (!force && EditorApplication.timeSinceStartup - _lastRefresh < RefreshIntervalSeconds) return;
            _lastRefresh = EditorApplication.timeSinceStartup;

            if (_component == null) return;

            SyncTargetList();

            AnyFallback = false;

            foreach (var target in _targets)
            {
                var proxy = ProxyRegistry.ResolveOrOriginal(target.Original, out var usingProxy);
                if (!usingProxy) AnyFallback = true;

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
                    target.BindPoses = mesh != null ? mesh.bindposes : null;
                    target.BoneWeights = mesh != null ? mesh.boneWeights : null;
                }

                // Scale Adjuster などがシャドウボーンを差し替えることがあるので毎回読み直す
                target.Bones = target.Skinned != null ? target.Skinned.bones : null;

                UpdateWorldVertices(target);
            }
        }

        private void SyncTargetList()
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

                if (same) return;
            }

            foreach (var target in _targets)
            {
                if (target.BakeScratch != null) Object.DestroyImmediate(target.BakeScratch);
            }

            _targets.Clear();
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
        }

        private void UpdateWorldVertices(TargetState target)
        {
            if (target.Proxy == null || target.Mesh == null) return;

            var localToWorld = target.Proxy.transform.localToWorldMatrix;
            Vector3[] source;

            if (target.Skinned != null)
            {
                if (target.BakeScratch == null)
                {
                    target.BakeScratch = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }

                // プロキシの bones（Scale Adjuster のシャドウボーンを含む）を用いてスキニング結果を得る
                target.Skinned.BakeMesh(target.BakeScratch);
                source = target.BakeScratch.vertices;
            }
            else
            {
                source = target.Mesh.vertices;
            }

            if (target.WorldVertices == null || target.WorldVertices.Length != source.Length)
            {
                target.WorldVertices = new Vector3[source.Length];
            }

            for (var i = 0; i < source.Length; i++)
            {
                target.WorldVertices[i] = localToWorld.MultiplyPoint3x4(source[i]);
            }
        }

        /// <summary>
        /// 頂点 index のメッシュローカル空間 → ワールド空間のスキニング行列を求める。
        /// M = Σ wi * (bones[i].localToWorldMatrix * bindposes[i])
        /// </summary>
        private static Matrix4x4 SkinMatrix(TargetState target, int index)
        {
            var fallback = target.Proxy.transform.localToWorldMatrix;

            if (target.Skinned == null || target.Bones == null || target.BindPoses == null ||
                target.BoneWeights == null || index >= target.BoneWeights.Length)
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
            if (boneIndex < 0 || boneIndex >= target.Bones.Length || boneIndex >= target.BindPoses.Length) return 0f;

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
            HandleDrag(current);
            DrawGizmos();

            if (current.type == EventType.Layout || current.type == EventType.MouseMove)
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

        private bool TryPick(Vector2 mousePosition, out TargetState picked, out int pickedIndex)
        {
            picked = null;
            pickedIndex = -1;

            var camera = Camera.current;
            if (camera == null) return false;

            var cameraPosition = camera.transform.position;
            var cameraForward = camera.transform.forward;
            var best = PickThresholdPixels * PickThresholdPixels;

            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = vertices[i];

                    // カメラ後方の頂点は除外
                    if (Vector3.Dot(world - cameraPosition, cameraForward) <= 0f) continue;

                    var distance = (HandleUtility.WorldToGUIPoint(world) - mousePosition).sqrMagnitude;
                    if (distance >= best) continue;

                    best = distance;
                    picked = target;
                    pickedIndex = i;
                }
            }

            return picked != null;
        }

        private void BeginSelection(TargetState target, int index)
        {
            _hasSelection = true;
            _selectedTarget = target;
            _selectedIndex = index;
            _centerWorld = target.WorldVertices[index];
            _handlePosition = _centerWorld;

            RecomputeMirrorCenter();
            BuildInfluences();
            CommitSnapshot();
        }

        private void RecomputeMirrorCenter()
        {
            _mirrorActive = false;
            if (!_component.mirror) return;

            var root = MirrorRoot;
            var centerInRoot = root.worldToLocalMatrix.MultiplyPoint3x4(_centerWorld);

            // 中心線のごく近くではミラー適用をスキップする（影響球の重なりによる二重適用を避ける）
            var epsilon = Mathf.Max(1e-4f, _component.brushRadius * 0.05f);
            if (Mathf.Abs(AxisComponent(centerInRoot, _component.mirrorAxis)) < epsilon) return;

            _mirrorActive = true;
            _mirrorCenterWorld = root.localToWorldMatrix.MultiplyPoint3x4(
                Reflect(centerInRoot, _component.mirrorAxis));
        }

        private void BuildInfluences()
        {
            _influences.Clear();

            var radius = Mathf.Max(1e-5f, _component.brushRadius);
            var falloff = _component.falloff;

            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = vertices[i];

                    var weight = FalloffUtil.Weight(Vector3.Distance(world, _centerWorld) / radius, falloff);
                    var mirrorWeight = _mirrorActive
                        ? FalloffUtil.Weight(Vector3.Distance(world, _mirrorCenterWorld) / radius, falloff)
                        : 0f;

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
            _hasSelection = false;
            _mirrorActive = false;
            _selectedTarget = null;
            _selectedIndex = -1;
            _influences.Clear();
            _hoverTarget = null;
            _hoverIndex = -1;
        }

        private void HandleDrag(Event current)
        {
            if (!_hasSelection) return;

            EditorGUI.BeginChangeCheck();
            var moved = Handles.PositionHandle(_handlePosition, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                _handlePosition = moved;
                _dragging = true;
                ApplyDisplacement();
            }

            if (!_dragging || current.type != EventType.MouseUp) return;

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
        /// Undo の記録と書き換えを同じフレームで行うため、1 ドラッグにつき Undo 1 段になる。
        /// </summary>
        private void Commit()
        {
            Undo.RecordObject(_component, "Den Mesh Editor");

            foreach (var target in _targets)
            {
                if (!target.Touched || target.Mesh == null) continue;
                target.Edit.SetFrom(target.Working, target.Mesh.vertexCount);
            }

            EditorUtility.SetDirty(_component);

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
                if (target.Touched) target.Working = new Dictionary<int, Vector3>(target.Snapshot);
            }

            foreach (var influence in _influences)
            {
                var target = influence.Target;
                if (!target.Touched)
                {
                    target.Working = new Dictionary<int, Vector3>(target.Snapshot);
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
            Handles.DrawWireDisc(center, camera.transform.forward, _component.brushRadius);
        }

        private void DrawOverlay()
        {
            _overlayRect = new Rect(10, 10, 280, AnyFallback ? 152 : 116);

            Handles.BeginGUI();
            GUILayout.BeginArea(_overlayRect, GUI.skin.box);

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70f;

            GUILayout.Label("Den Mesh Editor — 編集中", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var radius = EditorGUILayout.Slider("半径", _component.brushRadius, 0.001f, 0.5f);
            var falloff = (FalloffType)EditorGUILayout.EnumPopup("減衰", _component.falloff);
            var mirror = EditorGUILayout.Toggle("ミラー", _component.mirror);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_component, "Den Mesh Editor Settings");
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

            if (AnyFallback)
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
