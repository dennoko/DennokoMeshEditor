using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// 編集セッション中の Renderer の選択時表示（選択アウトライン・選択ワイヤーフレーム）を
    /// 一括管理するコントローラー。
    ///
    /// Unity 全体の設定（<c>AnnotationUtility.showSelectionOutline</c> 等）を変更せず、公開 API の
    /// <see cref="EditorUtility.SetSelectedRenderState"/> で編集対象だけを Hidden にする。
    /// 変更した状態はエディタのメモリ上にしか無いため、Unity が異常終了しても
    /// 永続設定（EditorPrefs）は汚染されず、再起動後の後始末そのものが不要になる。
    ///
    /// 後始末が要るのは「プロセスが生きたまま managed 状態だけが消える」ドメインリロードだけ。
    /// ネイティブ側の Hidden が残る経路に備え、触った Renderer の Instance ID を
    /// <see cref="SessionState"/> へ記録し、リロード後に解除を試みる。
    ///
    /// 抑制するかどうかの判断は常に <see cref="EditSession.CanContinueEditing"/> で行う。
    /// 台帳（<see cref="Suppressed"/>）は「自分が触ってまだ戻していない Renderer」という
    /// 後始末のリストであって、抑制の根拠ではない。
    /// </summary>
    [InitializeOnLoad]
    internal static class SelectionVisualController
    {
        private const string SessionStateKey = "Dennokoworks.DenMeshEditor.SuppressedRenderers";

        /// <summary>
        /// Hidden を貼り直す間隔。選択内容が変わると Unity 側で Renderer の状態が
        /// 既定値へ戻るため、台帳へ加えた 1 回だけでは足りない。
        /// </summary>
        private const double ReapplyIntervalSeconds = 0.1;

        /// <summary>
        /// リロード後の Instance ID 解決を諦めるまでの時間。コンパイルとアセット更新を
        /// 抜けてから数える（シーンが読めるようになるのを待つため）。
        /// </summary>
        private const double RestoreGiveUpSeconds = 30.0;

        /// <summary>Unity が選択中の Renderer へ既定で使う描画状態。</summary>
        internal const EditorSelectedRenderState DefaultSelectedRenderState =
            EditorSelectedRenderState.Highlight | EditorSelectedRenderState.Wireframe;

        /// <summary>自分が Hidden を試み、まだ解除していない Renderer の台帳。</summary>
        private static readonly HashSet<Renderer> Suppressed = new HashSet<Renderer>();

        /// <summary>Reconcile が使い回す desired の作業集合。</summary>
        private static readonly HashSet<Renderer> DesiredScratch = new HashSet<Renderer>();

        /// <summary>台帳から取り除くものを溜める作業リスト（列挙中に Remove しないため）。</summary>
        private static readonly List<Renderer> RemoveScratch = new List<Renderer>();

        /// <summary>ドメインリロード後に解決を試みる Instance ID。</summary>
        private static readonly List<int> PendingRestoreIds = new List<int>();

        /// <summary>SessionState へ書き出す ID の重複除去に使う作業集合。</summary>
        private static readonly HashSet<int> IdScratch = new HashSet<int>();

        private static readonly StringBuilder IdBuilder = new StringBuilder();

        /// <summary>
        /// 一度記録した失敗の Instance ID。Hidden の貼り直しは 0.1 秒ごとに走るので、
        /// 同じ失敗でログを毎回増やさない。
        /// </summary>
        private static readonly HashSet<int> LoggedFailures = new HashSet<int>();

        private static bool _updateSubscribed;
        private static bool _isReconciling;
        private static double _lastReapply;
        private static double _restoreReadySince = -1;
        private static bool _giveUpLogged;
        private static bool _sessionStateFailureLogged;

        static SelectionVisualController()
        {
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            LoadPendingIdsFromSessionState();
            UpdateSubscription();
        }

        // ------------------------------------------------------------------
        // イベント

        private static void OnSelectionChanged()
        {
            if (_isReconciling) return;

            var session = EditSession.Active;

            // Running でない間の選択変更（Begin が自分で行う Selection.activeGameObject の
            // 書き換えなど）は無視する。ここで終了扱いにすると、開始途中のセッションが
            // 自分の選択操作で落ちる
            if (session == null || !session.IsRunning) return;

            if (!EditSession.CanContinueEditing(session))
            {
                EditSession.End();
                return;
            }

            // 選択内容が変わると Renderer 側の状態は既定値へ戻る。台帳に載っていても貼り直す
            Reconcile(true);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode
                && change != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            // Scene Reload 無効時などは Renderer が生き残る。失敗分は必ず再試行する。
            RestoreAllActive();
        }

        private static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            // 閉じるシーンのオブジェクトしか消えないので、Play Mode 移行と違って台帳は捨てない。
            // 別シーンに残っている Renderer の ID を捨てると、Hidden のまま取り残す
            RestoreAllActive();
        }

        private static void OnBeforeAssemblyReload()
        {
            // リロード前に解除しきる。戻せなかった分だけが台帳に残り、
            // SyncSessionState 経由で ID がリロード後へ引き継がれる
            RestoreAllActive();
        }

        // ------------------------------------------------------------------
        // 整合

        /// <summary>
        /// 現在の編集セッションが望む集合（desired）と台帳を整合させる。
        /// </summary>
        /// <param name="reapply">
        /// 既に台帳に載っている Renderer へも Hidden を書き直すか。
        /// 選択変更後と定期更新では true にする（Unity 側で状態が既定値へ戻るため）。
        /// </param>
        internal static void Reconcile(bool reapply = false)
        {
            if (_isReconciling) return;
            _isReconciling = true;

            try
            {
                var session = EditSession.Active;
                DesiredScratch.Clear();

                if (session != null && session.IsRunning && EditSession.CanContinueEditing(session))
                {
                    session.CollectActiveRenderers(DesiredScratch);
                }

                var ledgerChanged = false;

                // 1. 台帳にあり desired にないものを通常表示へ戻す
                RemoveScratch.Clear();
                foreach (var renderer in Suppressed)
                {
                    if (renderer == null)
                    {
                        // 破棄済み。ネイティブ側の状態ごと消えているので戻すものは無い
                        RemoveScratch.Add(renderer);
                        ledgerChanged = true;
                        continue;
                    }

                    if (DesiredScratch.Contains(renderer)) continue;

                    try
                    {
                        EditorUtility.SetSelectedRenderState(renderer, DefaultSelectedRenderState);
                    }
                    catch (Exception ex)
                    {
                        // 台帳に残して次の機会に再試行する
                        LogFailureOnce(renderer, "restore selection visuals for", ex);
                        continue;
                    }

                    RemoveScratch.Add(renderer);
                    ledgerChanged = true;
                }

                for (var i = 0; i < RemoveScratch.Count; i++)
                {
                    Suppressed.Remove(RemoveScratch[i]);
                }

                RemoveScratch.Clear();

                // 2. desired のものを Hidden にする
                foreach (var renderer in DesiredScratch)
                {
                    if (renderer == null) continue;

                    // 台帳へ載せてから書く。逆順だと、書いた直後に落ちた場合に
                    // 「触ったのに台帳に無い」状態が生まれる
                    var added = Suppressed.Add(renderer);
                    if (added) ledgerChanged = true;
                    if (!added && !reapply) continue;

                    // managed 台帳だけではリロードを越えられない。記録できるまで触らない。
                    if (added && !TrySyncSessionState())
                    {
                        Suppressed.Remove(renderer);
                        continue;
                    }

                    try
                    {
                        EditorUtility.SetSelectedRenderState(renderer, EditorSelectedRenderState.Hidden);
                    }
                    catch (Exception ex)
                    {
                        LogFailureOnce(renderer, "hide selection visuals for", ex);
                    }
                }

                if (ledgerChanged)
                {
                    TrySyncSessionState();
                    SceneView.RepaintAll();
                }
            }
            finally
            {
                _isReconciling = false;
                _lastReapply = EditorApplication.timeSinceStartup;
                UpdateSubscription();
            }
        }

        /// <summary>
        /// NDMF がプロキシを登録した時点で、編集対象のものだけ初回描画前に Hidden にする。
        ///
        /// <see cref="ProxyRegistry.Report"/> は毎フレーム全 Renderer 分呼ばれるため、
        /// 台帳の参照だけで抜けられる順に判定する。
        /// </summary>
        internal static void OnProxyReported(Renderer original, Renderer proxy)
        {
            if (original == null || proxy == null) return;
            if (Suppressed.Contains(proxy)) return;

            var session = EditSession.Active;
            if (session == null || !session.IsRunning) return;
            if (!session.IsTargetOriginal(original)) return;
            if (!EditSession.CanContinueEditing(session)) return;

            Suppressed.Add(proxy);

            if (!TrySyncSessionState())
            {
                Suppressed.Remove(proxy);
                UpdateSubscription();
                return;
            }

            try
            {
                EditorUtility.SetSelectedRenderState(proxy, EditorSelectedRenderState.Hidden);
            }
            catch (Exception ex)
            {
                LogFailureOnce(proxy, "hide selection visuals for proxy", ex);
            }

            UpdateSubscription();
        }

        /// <summary>
        /// 台帳の Renderer をすべて通常表示へ戻す。戻せたものだけ台帳から落とす。
        /// </summary>
        private static void RestoreAllActive()
        {
            RemoveScratch.Clear();

            foreach (var renderer in Suppressed)
            {
                if (renderer == null)
                {
                    RemoveScratch.Add(renderer);
                    continue;
                }

                try
                {
                    EditorUtility.SetSelectedRenderState(renderer, DefaultSelectedRenderState);
                }
                catch (Exception ex)
                {
                    LogFailureOnce(renderer, "restore selection visuals for", ex);
                    continue;
                }

                RemoveScratch.Add(renderer);
            }

            for (var i = 0; i < RemoveScratch.Count; i++)
            {
                Suppressed.Remove(RemoveScratch[i]);
            }

            RemoveScratch.Clear();

            TrySyncSessionState();
            SceneView.RepaintAll();
            UpdateSubscription();
        }

        // ------------------------------------------------------------------
        // 駆動

        /// <summary>
        /// 「Running なセッションがある、または戻し待ちが残っている」間だけ購読する。
        /// セッションのインスタンスにぶら下げないので、セッションが無くても戻し待ちは進む。
        /// </summary>
        private static void OnEditorUpdate()
        {
            if (PendingRestoreIds.Count > 0) ProcessPendingRestores();

            var session = EditSession.Active;

            if (session != null && session.IsRunning)
            {
                if (!EditSession.CanContinueEditing(session))
                {
                    // Scene ビューが描画されていなくても、ここで終了と解除が進む
                    EditSession.End();
                }
                else if (EditorApplication.timeSinceStartup - _lastReapply >= ReapplyIntervalSeconds)
                {
                    Reconcile(true);
                }
            }
            else if (Suppressed.Count > 0)
            {
                Reconcile();
            }

            UpdateSubscription();
        }

        private static void ProcessPendingRestores()
        {
            // 「今解決できない」を「破棄済み」と混同しない。コンパイル中・アセット更新中は
            // シーンがまだ読めていないことがあり、ここで ID を捨てると生きている Renderer を
            // Hidden のまま取り残す
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            if (_restoreReadySince < 0) _restoreReadySince = EditorApplication.timeSinceStartup;

            var giveUp = EditorApplication.timeSinceStartup - _restoreReadySince > RestoreGiveUpSeconds;
            var changed = false;

            for (var i = PendingRestoreIds.Count - 1; i >= 0; i--)
            {
                var id = PendingRestoreIds[i];

                if (EditorUtility.InstanceIDToObject(id) is Renderer renderer)
                {
                    // 新しいセッションが同じ Renderer を抑制中なら、こちらが戻してはいけない。
                    // 所有権は現ドメインの台帳の側にある
                    if (!Suppressed.Contains(renderer))
                    {
                        try
                        {
                            EditorUtility.SetSelectedRenderState(renderer, DefaultSelectedRenderState);
                        }
                        catch (Exception ex)
                        {
                            LogFailureOnce(renderer, "restore selection visuals for", ex);
                            continue;
                        }
                    }

                    PendingRestoreIds.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!giveUp) continue;

                PendingRestoreIds.RemoveAt(i);
                changed = true;

                if (_giveUpLogged) continue;
                _giveUpLogged = true;
                Debug.LogWarning(
                    "[DennokoMeshEditor] Gave up restoring selection visuals for renderers that could "
                    + "not be resolved after a domain reload (instance id " + id + ").");
            }

            if (changed)
            {
                TrySyncSessionState();
                SceneView.RepaintAll();
            }
        }

        private static void UpdateSubscription()
        {
            var session = EditSession.Active;
            var needsUpdate = (session != null && session.IsRunning)
                              || Suppressed.Count > 0
                              || PendingRestoreIds.Count > 0;

            if (needsUpdate == _updateSubscribed) return;

            if (needsUpdate) EditorApplication.update += OnEditorUpdate;
            else EditorApplication.update -= OnEditorUpdate;

            _updateSubscribed = needsUpdate;
        }

        // ------------------------------------------------------------------
        // SessionState

        private static bool TrySyncSessionState()
        {
            try
            {
                SyncSessionState();
                _sessionStateFailureLogged = false;
                return true;
            }
            catch (Exception ex)
            {
                if (!_sessionStateFailureLogged)
                {
                    _sessionStateFailureLogged = true;
                    Debug.LogWarning("[DennokoMeshEditor] Could not record selection visual recovery state: "
                                     + ex.Message);
                }
                return false;
            }
        }

        /// <summary>
        /// 台帳と戻し待ちを一つのキーへまとめて書き出す。
        ///
        /// 二つの集合を別々に書くと、片方の書き込みがもう片方を消す。
        /// 「セッション中に戻し待ちが残っている」状況は実際に起こる（リロード直後に
        /// 編集を再開した場合）ので、必ずここで合成する。
        /// </summary>
        private static void SyncSessionState()
        {
            IdScratch.Clear();

            foreach (var renderer in Suppressed)
            {
                if (renderer != null) IdScratch.Add(renderer.GetInstanceID());
            }

            for (var i = 0; i < PendingRestoreIds.Count; i++)
            {
                IdScratch.Add(PendingRestoreIds[i]);
            }

            if (IdScratch.Count == 0)
            {
                SessionState.EraseString(SessionStateKey);
                return;
            }

            IdBuilder.Length = 0;
            foreach (var id in IdScratch)
            {
                if (IdBuilder.Length > 0) IdBuilder.Append(',');
                IdBuilder.Append(id);
            }

            SessionState.SetString(SessionStateKey, IdBuilder.ToString());
        }

        private static void LoadPendingIdsFromSessionState()
        {
            var raw = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return;

            var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var id)) PendingRestoreIds.Add(id);
            }

            _restoreReadySince = -1;
            _giveUpLogged = false;
        }

        /// <summary>
        /// 同じ対象の同じ失敗で毎フレームログを増やさない。
        /// </summary>
        private static void LogFailureOnce(Renderer renderer, string action, Exception ex)
        {
            int id;
            try
            {
                id = renderer.GetInstanceID();
            }
            catch
            {
                id = 0;
            }

            if (!LoggedFailures.Add(id)) return;

            string name;
            try
            {
                name = renderer.name;
            }
            catch
            {
                name = "<destroyed>";
            }

            Debug.LogWarning($"[DennokoMeshEditor] Failed to {action} '{name}': {ex.Message}");
        }
    }
}
