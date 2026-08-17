using System;
using System.Collections.Generic;
using System.Linq; // List.Contains(value, IEqualityComparer) は LINQ の拡張メソッド
using UnityEngine;
using UnityEngine.Networking;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// GitHub Public リポジトリ上の version.json を取得し、ローカル版と比較する
    /// エディタ専用の自己完結アップデートチェッカー。
    ///
    /// version.json の形式:
    ///   { "version": "1.2.0", "url": "https://.../releases", "message": "" }
    ///
    /// owner / repo は呼び出し側（<see cref="DenMeshEditorVersion"/>）から渡す。
    /// 文言は返さず State だけ返すので、表示側で自由に言い回しを決められる。
    ///
    /// 名前空間はツールごとに変える（同一プロジェクトに複数ツールを入れたときの
    /// クラス重複 CS0101 を避けるため）。
    /// </summary>
    internal static class DennokoVersionChecker
    {
        internal enum State { Checking, UpToDate, UpdateAvailable, Error }

        /// <summary>応答が無い場合に待ち続けないための上限（秒）。</summary>
        private const int RequestTimeoutSeconds = 10;

        internal struct Result
        {
            public State State;
            public string LocalVersion;
            public string LatestVersion;
            public string Url;
            public string Message;
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
            public string url;
            public string message;
        }

        /// <summary>
        /// version.json を非同期取得して結果を onResult に渡す。例外は投げず、失敗時は
        /// State.Error を返す。onResult は Unity のメインスレッド上で呼ばれる。
        ///
        /// 指定 branch で取得できなかった場合は "main" にフォールバックして再取得する
        /// (デフォルトブランチが master / main のどちらでも動くように)。
        /// </summary>
        internal static void CheckAsync(
            string owner, string repo, string branch, string filePath,
            string localVersion, Action<Result> onResult)
        {
            if (onResult == null) return;

            // 候補ブランチ: 指定ブランチ → "main" (重複は除外)
            var branches = new List<string>();
            if (!string.IsNullOrEmpty(branch)) branches.Add(branch);
            if (!branches.Contains("main", StringComparer.OrdinalIgnoreCase)) branches.Add("main");

            TryBranch(owner, repo, branches, 0, filePath, localVersion, onResult);
        }

        /// <summary>候補ブランチを index から順に試す。エラーなら次の候補へフォールバックする。</summary>
        private static void TryBranch(
            string owner, string repo, List<string> branches, int index,
            string filePath, string localVersion, Action<Result> onResult)
        {
            if (index >= branches.Count)
            {
                onResult(Error(localVersion));
                return;
            }

            UnityWebRequest req;
            try
            {
                // api.github.com ではなく raw を使う。未認証の api.github.com は
                // IP あたり 60 req/hour と枠が狭く、レート制限に当たりやすいため。
                var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branches[index]}/{filePath}";
                req = UnityWebRequest.Get(url);
                // UnityWebRequest 既定の User-Agent は 403 の対象になり得るので明示する
                req.SetRequestHeader("User-Agent", $"{repo}-VersionChecker");
                req.timeout = RequestTimeoutSeconds;
            }
            catch (Exception e)
            {
                // URL 組み立て自体の失敗はブランチを変えても直らないため即エラー
                Debug.LogWarning($"[Dennoko Mesh Editor] version check: request build failed: {e.Message}");
                onResult(Error(localVersion));
                return;
            }

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                Result result;
                long httpCode = 0;
                try
                {
                    result = BuildResult(req, localVersion);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Dennoko Mesh Editor] version check: callback failed: {e.Message}");
                    result = Error(localVersion);
                }
                finally
                {
                    httpCode = req.responseCode;
                    req.Dispose();
                }

                // 403 / 429 はブランチを変えても解消せず、リクエストを増やして
                // レート制限を悪化させるだけなのでフォールバックしない
                bool rateLimited = httpCode == 403 || httpCode == 429;

                if (result.State == State.Error && !rateLimited && index + 1 < branches.Count)
                {
                    // 次の候補ブランチへフォールバック
                    TryBranch(owner, repo, branches, index + 1, filePath, localVersion, onResult);
                }
                else
                {
                    onResult(result);
                }
            };
        }

        private static Result BuildResult(UnityWebRequest req, string localVersion)
        {
            string url = req != null ? req.url : "(null)";
#if UNITY_2020_2_OR_NEWER
            bool hasError = req.result != UnityWebRequest.Result.Success;
#else
            bool hasError = req.isNetworkError || req.isHttpError;
#endif
            // 失敗は必ず一度警告する。URL・httpCode・error が「最新版を取得できません」の
            // 切り分け材料になる（owner/repo/branch・push 有無・回線）。
            if (hasError)
            {
                // 403 / 429 は本文に理由（レート制限か否か）が入るので一緒に出す
                var body = req.downloadHandler != null ? req.downloadHandler.text : null;
                if (!string.IsNullOrEmpty(body) && body.Length > 300) body = body.Substring(0, 300) + "...";
                Debug.LogWarning($"[Dennoko Mesh Editor] version check 取得失敗: url={url} httpCode={req.responseCode} error={req.error} body={body}");
                return Error(localVersion);
            }

            var json = req.downloadHandler != null ? req.downloadHandler.text : null;
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning($"[Dennoko Mesh Editor] version check 取得失敗: レスポンスが空。url={url} httpCode={req.responseCode}");
                return Error(localVersion);
            }

            VersionInfo info;
            try { info = JsonUtility.FromJson<VersionInfo>(json); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dennoko Mesh Editor] version check 取得失敗: JSON パース失敗: {e.Message} url={url}");
                return Error(localVersion);
            }

            if (info == null || string.IsNullOrEmpty(info.version))
            {
                Debug.LogWarning($"[Dennoko Mesh Editor] version check 取得失敗: version フィールドが空。url={url}");
                return Error(localVersion);
            }

            var state = IsNewer(info.version, localVersion) ? State.UpdateAvailable : State.UpToDate;
            return new Result
            {
                State = state,
                LocalVersion = localVersion,
                LatestVersion = info.version,
                Url = info.url,
                Message = info.message,
            };
        }

        private static Result Error(string localVersion) => new Result
        {
            State = State.Error,
            LocalVersion = localVersion,
            LatestVersion = null,
            Url = null,
            Message = null,
        };

        /// <summary>
        /// latest がローカル版より新しいか（＝更新あり）。State をキャッシュせず、表示側が
        /// 「保存した最新版 vs 現在のローカル版」で都度再計算できるよう公開する。
        /// </summary>
        internal static bool IsUpdateAvailable(string latestVersion, string localVersion)
            => IsNewer(latestVersion, localVersion);

        private static bool IsNewer(string latest, string local)
        {
            var l = Normalize(latest);
            var c = Normalize(local);
            if (Version.TryParse(l, out var vLatest) && Version.TryParse(c, out var vLocal))
                return vLatest > vLocal;
            return !string.Equals(l, c, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 比較用に正規化する。BOM / 先頭 v / プレリリース・ビルドメタデータを除去し、
        /// 2 桁以下（"3", "3.0"）は 3 桁（"3.0.0"）へゼロ埋めする。
        /// ゼロ埋めしないと Version 型で Build=-1 となり "3.0" &lt; "3.0.0" の誤判定が出る。
        /// </summary>
        private static string Normalize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            v = v.Trim().Trim('﻿').Trim(); // 空白と BOM を除去
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            // "1.2.0-beta" / "1.2.0+build" などのサフィックスは比較対象外
            int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) v = v.Substring(0, cut);
            if (string.IsNullOrEmpty(v)) return "0.0.0";

            var parts = v.Split('.');
            if (parts.Length >= 3) return v;
            var padded = new string[3];
            for (int i = 0; i < 3; i++)
                padded[i] = (i < parts.Length && !string.IsNullOrEmpty(parts[i])) ? parts[i] : "0";
            return string.Join(".", padded);
        }
    }
}
