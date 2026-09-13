using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// ヒエラルキーで直近に右クリックされた行を覚えておく。
    ///
    /// 複数選択中に GameObject メニューを実行すると、Unity は選択の数だけメニュー関数を呼び、
    /// <see cref="MenuCommand.context"/> にはその都度別の選択オブジェクトを渡してくる。
    /// 呼ばれる順は右クリックした行と関係がないので、context からは「どの行で右クリックしたか」が
    /// 分からない。そこでヒエラルキーの行 GUI でマウスイベントを覗き見て自前で記録する。
    /// イベントは消費しないので、Unity 標準の選択やメニュー表示には影響しない。
    /// </summary>
    [InitializeOnLoad]
    internal static class HierarchyContextClick
    {
        /// <summary>
        /// 記録を有効とみなす秒数。メニューを開いてからサブメニューを辿って実行するまでの
        /// 時間を見込みつつ、キャンセルされた右クリックの記録がいつまでも残らない程度に留める。
        /// </summary>
        private const double ValidSeconds = 30.0;

        private static int _instanceId;
        private static double _time = double.NegativeInfinity;

        static HierarchyContextClick()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGui;
        }

        /// <summary>
        /// 直近に右クリックされ、かつ今も選択に含まれている GameObject を返す。
        /// 取得できた記録は消費する。同じ右クリックを後の別の実行（Undo してからの再実行など）で
        /// 使い回さないため。複数選択による連続呼び出しは呼び出し側で 1 回目以外を捨てているので、
        /// 1 回目で消費しても差し支えない。
        /// </summary>
        internal static bool TryConsumeSelected(out GameObject gameObject)
        {
            gameObject = null;

            if (_instanceId == 0) return false;
            if (EditorApplication.timeSinceStartup - _time > ValidSeconds) return false;

            var go = EditorUtility.InstanceIDToObject(_instanceId) as GameObject;
            if (go == null || !Selection.Contains(go)) return false;

            _instanceId = 0;
            gameObject = go;
            return true;
        }

        private static void OnHierarchyItemGui(int instanceId, Rect selectionRect)
        {
            var e = Event.current;

            var isRightClick = (e.type == EventType.MouseDown && e.button == 1) || e.type == EventType.ContextClick;
            var isLeftClick = e.type == EventType.MouseDown && e.button == 0;
            if (!isRightClick && !isLeftClick) return;

            // selectionRect は行のうち名前まわりだけなので、行全体へ広げて判定する
            var row = new Rect(0f, selectionRect.y, EditorGUIUtility.currentViewWidth, selectionRect.height);
            if (!row.Contains(e.mousePosition)) return;

            if (isRightClick)
            {
                _instanceId = instanceId;
                _time = EditorApplication.timeSinceStartup;
            }
            else
            {
                // 左クリックで選択を操作し直したら、それ以前の右クリックは意図を表さない
                _instanceId = 0;
            }
        }
    }
}
