using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// ローカルの version.json を読み、GitHub 上の最新版と比較する。
    /// インポート時（コンパイル完了時）や起動時に自動でチェックが走る。
    ///
    /// ただし実際にリクエストを飛ばすのは前回から <see cref="CheckIntervalHours"/> 以上
    /// 経過したときだけで、その間は前回結果（EditorPrefs キャッシュ）を表示に使う。
    /// ドメインリロードのたびに取得しに行くと GitHub のレート制限に掛かるため。
    /// </summary>
    [InitializeOnLoad]
    internal static class DenMeshEditorVersion
    {
        // version.json をどうしても読めなかった場合の最終フォールバック（通常は使われない）
        private const string FallbackVersion = "0.0.0";
        private static string _currentCache;

        internal static string Current
        {
            get
            {
                // 失敗（null）はキャッシュしない。インポート直後で version.json が
                // まだ読めなかった場合でも、次回アクセス時に再試行できるようにする。
                if (string.IsNullOrEmpty(_currentCache))
                {
                    _currentCache = LoadLocalVersion();
                }
                return string.IsNullOrEmpty(_currentCache) ? FallbackVersion : _currentCache;
            }
        }

        // チェック先（このプロジェクトのリモートリポジトリ）
        private const string RepoOwner = "dennoko";
        private const string RepoName = "DennokoMeshEditor";
        private const string RepoBranch = "main";
        private const string VersionFilePath = "version.json";

        // セッションキー。State（比較結果）は保存しない — ローカル版が後から正しく解決され得るため、
        // 表示のたびに「保存した最新版 vs 現在のローカル版」で更新有無を再計算する。
        private const string VerCheckDoneKey = "DennokoMeshEditor_VerCheck_Done";
        private const string VerCheckErrorKey = "DennokoMeshEditor_VerCheck_Error";
        private const string VerCheckLatestKey = "DennokoMeshEditor_VerCheck_Latest";
        private const string VerCheckUrlKey = "DennokoMeshEditor_VerCheck_Url";
        private const string VerCheckMessageKey = "DennokoMeshEditor_VerCheck_Message";

        // 以下はエディタ再起動をまたいで保持する必要があるため EditorPrefs に置く。
        // SessionState だと再起動のたびにリセットされ、レート制限中でも撃ち続けてしまう。
        private const string VerCheckLastAttemptKey = "DennokoMeshEditor_VerCheck_LastAttemptUtc";
        private const string VerCheckCachedLatestKey = "DennokoMeshEditor_VerCheck_CachedLatest";
        private const string VerCheckCachedUrlKey = "DennokoMeshEditor_VerCheck_CachedUrl";
        private const string VerCheckCachedMessageKey = "DennokoMeshEditor_VerCheck_CachedMessage";

        // 前回リクエストからこの時間が経つまで自動チェックを行わない。ドメインリロード
        // （スクリプト保存・Play mode 出入りごとに走る）で無制限に再試行すると、GitHub 側の
        // レート制限を自分で悪化させ「制限 → エラー → 即再試行」のループに入るため。
        private const double CheckIntervalHours = 6.0;

        static DenMeshEditorVersion()
        {
            // 静的コンストラクタはドメインリロード中に走り、この時点では version.json が
            // AssetDatabase 未登録のことがある。delayCall で 1 tick 遅らせてから開始する。
            EditorApplication.delayCall += StartCheckBackgroundTask;
        }

        // 同一ドメイン内での二重リクエスト防止（ドメインリロードで false に戻る）
        private static bool _checking;

        internal static void StartCheckBackgroundTask()
        {
            // 成功済みなら再取得しない。だがエラー時は「インポート直後の一時的な失敗
            // （パッケージ取り込み時のドメインリロードでリクエストが中断される等）」を想定し、
            // 次のトリガー（Inspector を開く / ドメインリロード）で再試行する。
            bool done = SessionState.GetBool(VerCheckDoneKey, false);
            bool error = SessionState.GetBool(VerCheckErrorKey, false);
            if (done && !error) return;
            if (_checking) return;

            // クールダウン中はリクエストを送らず、前回取得できた結果をそのまま表示に使う。
            // ここで done を立てないと「確認中…」のまま固まってしまう。
            if (IsInCheckInterval())
            {
                ApplyCachedResult();
                return;
            }

            _checking = true;
            EditorPrefs.SetString(VerCheckLastAttemptKey, DateTime.UtcNow.ToString("o"));

            DennokoVersionChecker.CheckAsync(
                RepoOwner, RepoName, RepoBranch, VersionFilePath, Current, OnVersionChecked);
        }

        /// <summary>前回リクエストから <see cref="CheckIntervalHours"/> 経っていなければ true。</summary>
        private static bool IsInCheckInterval()
        {
            var last = EditorPrefs.GetString(VerCheckLastAttemptKey, string.Empty);
            if (string.IsNullOrEmpty(last)) return false;
            if (!DateTime.TryParse(last, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var lastUtc)) return false;

            // 端末時刻が巻き戻された場合に永久に待ち続けないよう、未来の記録は無効扱いにする
            var elapsed = DateTime.UtcNow - lastUtc.ToUniversalTime();
            if (elapsed < TimeSpan.Zero) return false;

            return elapsed.TotalHours < CheckIntervalHours;
        }

        /// <summary>EditorPrefs に残っている前回の取得結果をセッションへ反映する。</summary>
        private static void ApplyCachedResult()
        {
            var latest = EditorPrefs.GetString(VerCheckCachedLatestKey, string.Empty);
            SessionState.SetBool(VerCheckDoneKey, true);
            // 前回も取得できていなければエラー表示のまま（次のクールダウン明けに再試行される）
            SessionState.SetBool(VerCheckErrorKey, string.IsNullOrEmpty(latest));
            SessionState.SetString(VerCheckLatestKey, latest);
            SessionState.SetString(VerCheckUrlKey, EditorPrefs.GetString(VerCheckCachedUrlKey, string.Empty));
            SessionState.SetString(VerCheckMessageKey, EditorPrefs.GetString(VerCheckCachedMessageKey, string.Empty));

            RefreshOpenInspectors();
        }

        /// <summary>手動での再取得。前回結果（成功/失敗・ローカル版キャッシュ）を破棄して再チェックする。</summary>
        internal static void ForceRecheck()
        {
            if (_checking) return; // 進行中なら何もしない
            _currentCache = null;  // ローカル版も読み直す（version.json を直したケースに対応）
            SessionState.SetBool(VerCheckDoneKey, false);
            SessionState.SetBool(VerCheckErrorKey, false);
            // 明示的なユーザー操作なのでクールダウンは無視する（自動チェックのみ抑制対象）
            EditorPrefs.DeleteKey(VerCheckLastAttemptKey);
            StartCheckBackgroundTask();
        }

        private static void OnVersionChecked(DennokoVersionChecker.Result result)
        {
            _checking = false;
            bool failed = result.State == DennokoVersionChecker.State.Error;

            SessionState.SetBool(VerCheckDoneKey, true);
            SessionState.SetBool(VerCheckErrorKey, failed);
            SessionState.SetString(VerCheckLatestKey, result.LatestVersion ?? string.Empty);
            SessionState.SetString(VerCheckUrlKey, result.Url ?? string.Empty);
            SessionState.SetString(VerCheckMessageKey, result.Message ?? string.Empty);

            // 成功時のみ永続キャッシュを更新する。失敗で上書きすると、クールダウン中の
            // 表示から「前回取得できていた最新版」が消えてしまうため。
            if (!failed)
            {
                EditorPrefs.SetString(VerCheckCachedLatestKey, result.LatestVersion ?? string.Empty);
                EditorPrefs.SetString(VerCheckCachedUrlKey, result.Url ?? string.Empty);
                EditorPrefs.SetString(VerCheckCachedMessageKey, result.Message ?? string.Empty);
            }

            RefreshOpenInspectors();
        }

        /// <summary>すでに開かれている Inspector に取得結果を反映させる。</summary>
        private static void RefreshOpenInspectors()
        {
            var inspectors = Resources.FindObjectsOfTypeAll<DenMeshEditorInspector>();
            if (inspectors == null) return;
            foreach (var inspector in inspectors)
            {
                if (inspector != null) inspector.ReloadVersionResult();
            }
        }

        /// <summary>
        /// セッションに保存された取得結果から表示用の Result を組み立てる。
        /// State（更新有無）はキャッシュせず、常に「現在のローカル版 vs 取得済みの最新版」で
        /// 再計算する。こうしないと、取得時のローカル版が後から正しく解決された場合に
        /// 「v3.0.0 更新あり 3.0.0」のような矛盾表示が残ってしまう。
        /// </summary>
        internal static DennokoVersionChecker.Result LoadResultFromSessionState()
        {
            string local = Current;
            string latest = SessionState.GetString(VerCheckLatestKey, string.Empty);
            bool done = SessionState.GetBool(VerCheckDoneKey, false);
            bool error = SessionState.GetBool(VerCheckErrorKey, false);

            DennokoVersionChecker.State state;
            if (!done)
                state = DennokoVersionChecker.State.Checking;
            else if (error || string.IsNullOrEmpty(latest))
                state = DennokoVersionChecker.State.Error;
            else if (DennokoVersionChecker.IsUpdateAvailable(latest, local))
                state = DennokoVersionChecker.State.UpdateAvailable;
            else
                state = DennokoVersionChecker.State.UpToDate;

            return new DennokoVersionChecker.Result
            {
                State = state,
                LocalVersion = local,
                LatestVersion = latest,
                Url = SessionState.GetString(VerCheckUrlKey, string.Empty),
                Message = SessionState.GetString(VerCheckMessageKey, string.Empty),
            };
        }

        /// <summary>更新ページ（version.json の url、無ければリリース一覧）を開く。</summary>
        internal static void OpenUpdatePage(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                url = $"https://github.com/{RepoOwner}/{RepoName}/releases";
            }
            Application.OpenURL(url);
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
        }

        /// <summary>ローカルの version.json を読む。読めなければ null（呼び出し側で
        /// フォールバックし、次回アクセス時に再試行する）。</summary>
        private static string LoadLocalVersion()
        {
            // 1) スクリプト位置からの相対探索。
            //    [CallerFilePath] はコンパイル時パスなので、他プロジェクトへインポートして
            //    再コンパイルされれば、そのプロジェクト内の正しいパスに解決される
            //    （AssetDatabase のインポート完了状況に依存しない）。
            //    このリポジトリは .meta を管理外にしているため GUID が固定できず、
            //    GUID 経由ではなくこちらを主経路にしている。
            var v = TryReadVersion(ResolveVersionJsonByScriptPath());
            if (v != null) return v;

            // 2) AssetDatabase 経由での保険（コンパイル時パスが解決できなかった場合）。
            //    同名の version.json は他ツールにもあるので、「隣に本スクリプトがある」
            //    ものだけを自分のものとみなす（フォルダ名を変えられても効く）。
            foreach (var guid in AssetDatabase.FindAssets("version t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith("/version.json", StringComparison.OrdinalIgnoreCase)) continue;

                var dir = path.Substring(0, path.Length - "/version.json".Length);
                if (!File.Exists(Path.Combine(dir, "Editor", "Version", "DenMeshEditorVersion.cs")) &&
                    !File.Exists(Path.Combine(dir, "Editor", "DenMeshEditorVersion.cs")) &&
                    !File.Exists(Path.Combine(dir, "Runtime", "DenMeshEditor.cs"))) continue;

                v = TryReadVersion(path);
                if (v != null) return v;
            }

            return null;
        }

        private static string TryReadVersion(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var info = JsonUtility.FromJson<VersionInfo>(File.ReadAllText(path));
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dennoko Mesh Editor] version.json の読み込みに失敗しました ({path}): {e.Message}");
            }
            return null;
        }

        /// <summary>このスクリプトの位置を起点に上位フォルダを辿って version.json を探す。</summary>
        private static string ResolveVersionJsonByScriptPath([CallerFilePath] string scriptPath = null)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            var dir = Path.GetDirectoryName(scriptPath);
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "version.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
