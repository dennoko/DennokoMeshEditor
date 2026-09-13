using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// ヒエラルキー上の 1 オブジェクトを一時的に色付けし、フェードで消す。
    ///
    /// 複数選択からコンポーネントを追加すると、どのオブジェクトに付いたのかが
    /// 選択のハイライトだけでは読み取りにくい。追加直後に一瞬だけ色を乗せて視線を誘導する。
    /// </summary>
    internal static class HierarchyHighlight
    {
        /// <summary>色を乗せてから消え切るまでの秒数。</summary>
        private const double DurationSeconds = 1.8;

        /// <summary>フェードを始めるまで濃さを保つ割合（全体の長さに対する比）。</summary>
        private const double HoldRatio = 0.25;

        /// <summary>最も濃いときの不透明度。行のアイコンと名前が読める範囲に留める。</summary>
        private const float MaxAlpha = 0.45f;

        /// <summary>dennokoworks カラースキーマの成功色 (#4caf50)。選択行の青と重なっても見分けが付く。</summary>
        private static readonly Color HighlightColor = new Color(0.298f, 0.686f, 0.314f);

        private static int _instanceId;
        private static double _startTime;
        private static bool _hooked;

        /// <summary>
        /// <paramref name="target"/> の行を光らせる。表示中でなければ（畳まれている等）何も起きない。
        /// </summary>
        internal static void Flash(GameObject target)
        {
            if (target == null) return;

            _instanceId = target.GetInstanceID();
            _startTime = EditorApplication.timeSinceStartup;

            if (!_hooked)
            {
                EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGui;

                // hierarchyWindowItemOnGUI は再描画のときしか呼ばれない。
                // フェードさせるには毎エディタ更新で再描画を要求し続ける必要がある
                EditorApplication.update += OnUpdate;
                _hooked = true;
            }

            EditorApplication.RepaintHierarchyWindow();
        }

        private static void OnUpdate()
        {
            if (Progress() >= 1.0)
            {
                Stop();
            }

            // 消え切った直後の 1 回も描き直す必要があるので、停止した後でも要求する
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void OnHierarchyItemGui(int instanceId, Rect selectionRect)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (instanceId != _instanceId) return;

            var alpha = AlphaAt(Progress());
            if (alpha <= 0f) return;

            // selectionRect は行のうち名前まわりだけなので、行全体へ広げてから塗る
            var rect = new Rect(0f, selectionRect.y, EditorGUIUtility.currentViewWidth, selectionRect.height);

            EditorGUI.DrawRect(rect, new Color(HighlightColor.r, HighlightColor.g, HighlightColor.b, alpha));
        }

        /// <summary>0 で開始直後、1 以上で終了。</summary>
        private static double Progress()
        {
            if (!_hooked) return 1.0;

            return (EditorApplication.timeSinceStartup - _startTime) / DurationSeconds;
        }

        private static float AlphaAt(double progress)
        {
            if (progress >= 1.0) return 0f;
            if (progress <= HoldRatio) return MaxAlpha;

            var t = (float)((progress - HoldRatio) / (1.0 - HoldRatio));

            // 直線的に薄くすると消え際が唐突に見えるので、両端を緩めた曲線を使う
            return MaxAlpha * (1f - Mathf.SmoothStep(0f, 1f, t));
        }

        private static void Stop()
        {
            if (!_hooked) return;

            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyItemGui;
            EditorApplication.update -= OnUpdate;

            _hooked = false;
            _instanceId = 0;
        }
    }
}
