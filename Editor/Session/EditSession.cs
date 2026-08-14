using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
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
    internal partial class EditSession
    {
        private const float PickThresholdPixels = 24f;
        private const float RefreshIntervalSeconds = 0.1f;
        private const int MaxDrawnVertices = 3000;

        /// <summary>頂点ドットを面から浮かせる量。ドットの半径に対する倍率。</summary>
        private const float VertexDotDepthBias = 2f;

        /// <summary>クリック位置に近い順に、最大でいくつまで遮蔽判定を試すか。</summary>
        private const int MaxPickCandidates = 8;

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
            EditorApplication.quitting += End;
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
            if (_active == null) return;

            // 巻き戻し後の状態と食い違う未確定データを捨て、プレビューへ更新を促す。
            // パイプラインの再構築を挟まず、生成済みメッシュの頂点だけが書き換わる経路
            ClearLiveEdits();

            // 作業状態の作り直しは次のエディタ更新まで遅らせる。Ctrl+Z を押しっぱなしにして
            // 同一フレームに複数回届いても、重い作り直しは 1 回で済む
            _active._resyncPending = true;
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

            // 編集前の形状で描かれる選択アウトラインが編集結果に重なるのを避ける
            SelectionOutline.Suppress();

            // プレビューフィルタへ「編集開始」を伝え、プロキシを生成させる
            ActiveComponent.Value = component;

            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
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
            SelectionOutline.Restore();
            ActiveComponent.Value = null;

            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
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

        private readonly DenMeshEditor _component;
        private double _lastRefresh;

        /// <summary>Undo / Redo を受けて作業状態を作り直す必要があるか。</summary>
        private bool _resyncPending;

        private bool _hasSelection;
        private TargetState _selectedTarget;
        private int _selectedIndex = -1;
        private Vector3 _centerWorld;
        private Vector3 _mirrorCenterWorld;
        private bool _mirrorActive;
        private Vector3 _handlePosition;
        private bool _dragging;

        /// <summary>
        /// 移動ハンドルを掴んでいるか。<see cref="_dragging"/> と違い、掴んだだけで
        /// まだ動かしていない状態も含む。
        ///
        /// 半径のホイール変更はこちらを条件にする。掴んだ時点で「その頂点をどう動かすか」の
        /// 操作に入っているので、動かす前に影響範囲を決められないと手順が前後する
        /// （実際、掴んだままホイールを回すとシーンビューがズームしてしまい直観に反する）。
        /// </summary>
        private bool _handleGrabbed;

        /// <summary>
        /// 選択頂点の「自分のデルタを除いた」ワールド位置と、そのスキニング行列。
        ///
        /// ハンドル位置は常に <c>_selectedBaseWorld + skin * delta</c> で表せる。
        /// これを持っておくと、Undo でデルタが巻き戻ったときに、NDMF プレビューの
        /// 再構築（非同期）を待たずにハンドルを正しい位置へ戻せる。
        /// </summary>
        private Vector3 _selectedBaseWorld;

        private Matrix4x4 _selectedSkin = Matrix4x4.identity;

        // ハンドルを掴んでいる間にホイールで変更した半径。確定するまでコンポーネントには書かない
        private float _pendingRadius;
        private bool _hasPendingRadius;

        private TargetState _hoverTarget;
        private int _hoverIndex = -1;

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
        /// 下流の NDMF フィルタが頂点数を変えているためにプロキシを使えない対象があるか。
        /// この状態では他ツールの影響を反映した編集ができない。
        /// </summary>
        internal bool AnyVertexCountMismatch { get; private set; }

        /// <summary>
        /// ユーザーへ警告を出すべきか。パイプライン再構築の数フレームで明滅しないよう、
        /// フォールバック状態が一定時間続いたときだけ true になる。
        /// </summary>
        internal bool ShowFallbackWarning => _showFallbackWarning;

        /// <summary>
        /// 現在有効なブラシ半径。ハンドル操作中にホイールで変更した未確定値を優先する。
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
            _handleGrabbed = false;
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
                if (target.Edit != null) target.Edit.CopyTo(target.Working);
                else target.Working.Clear();

                CopyDeltas(target.Working, target.Snapshot);
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
            // Undo / Redo の後始末。描画ループの外側で、1 フレームにつき 1 回だけ行う
            if (_resyncPending)
            {
                _resyncPending = false;
                ResyncFromComponent();
            }

            // ドラッグ中はハンドル操作自体が再描画を駆動するので不要
            if (_dragging) return;

            if (EditorApplication.timeSinceStartup - _lastRepaint < RefreshIntervalSeconds) return;
            _lastRepaint = EditorApplication.timeSinceStartup;

            SceneView.RepaintAll();
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
                if (_hasSelection)
                {
                    ClearSelection();
                    sceneView.Repaint();
                }
                else
                {
                    End();
                }
                current.Use();
                return;
            }

            if (!_dragging) Refresh(false);

            // 何もヒットしなかった場合に拾うためのフォールバックコントロール。
            // MouseDown 時にこれが nearestControl であれば、移動ハンドル上ではないと判断できる。
            var defaultControl = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(defaultControl);

            DrawOverlay(sceneView);
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

        private void ClearSelection()
        {
            // ドラッグ中に Esc で解除されうる。ここで降ろさないと _dragging が立ちっぱなしになり、
            // 頂点位置の更新（Refresh）が二度と走らなくなる
            _dragging = false;
            _handleGrabbed = false;
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
        /// ハンドルを掴んでいる間のホイール操作でブラシ半径を変更する。
        ///
        /// 影響範囲の再計算に使う頂点位置（<see cref="TargetState.WorldVertices"/>）は、
        /// ドラッグ中は更新されない＝ドラッグ開始時の形状のままであり、掴んだだけで
        /// まだ動かしていない間は形状そのものが変わらない。どちらの場合も
        /// 選択時と同じ基準で影響範囲を計算し直せる。
        ///
        /// 半径を縮めた場合、範囲から外れた頂点は <see cref="ApplyDisplacement"/> が
        /// スナップショットから作り直すことで元に戻る。
        /// </summary>
        private void HandleRadiusScroll(Event current)
        {
            // ハンドルを掴んでいる間だけ横取りする。それ以外ではシーンビューのズームを妨げない
            if (!_hasSelection) return;
            if (!_dragging && !_handleGrabbed) return;
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

            // ハンドルが hotControl を取った瞬間＝掴んだ瞬間。PositionHandle の前後で比べる
            // ことで、掴んだのが自分のハンドルかどうかを取り違えずに判定できる
            var hotBefore = GUIUtility.hotControl;

            // Tools.pivotRotation（Global / Local）に追従させる
            var moved = Handles.PositionHandle(_handlePosition, Tools.handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                _handlePosition = moved;
                _dragging = true;
                ApplyDisplacement();
            }

            if (hotBefore == 0 && GUIUtility.hotControl != 0) _handleGrabbed = true;

            if (!_dragging && !_handleGrabbed) return;

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

            _handleGrabbed = false;

            // 掴んだだけで動かさなかった場合は確定するものが無い。ただしその間に
            // ホイールで半径を変えていれば、それは書き込む必要がある
            if (!_dragging && !_hasPendingRadius) return;

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
                ClearLiveEdits();
                return;
            }

            // Unity は「同じ Undo グループ・同じ名前」の RecordObject を 1 段にまとめる。
            // グループが自動で進むのは限られたタイミングだけなので、明示的に切らないと
            // 複数回のドラッグが 1 段に潰れ、Ctrl+Z で一気に巻き戻る。
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Dennoko Mesh Editor");

            // 記録より先に半径を書くと変更前の値が取れなくなるので、記録が先
            Undo.RecordObject(_component, "Dennoko Mesh Editor");

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
            ClearLiveEdits();
        }

        /// <summary>
        /// 未確定データを捨て、プレビューへ「読み直せ」と伝える。
        ///
        /// 編集セッション中のコンポーネントは NDMF から監視されていない
        /// （理由は <c>DenMeshEditorPreviewFilter.ObserveEdits</c>）ため、コンポーネントを
        /// 書き換えただけではプレビューが追従しない。更新の合図はこちらから出す。
        /// </summary>
        private static void ClearLiveEdits()
        {
            LiveEdits.Clear();
            LiveEdits.Invalidate();
        }
    }
}
