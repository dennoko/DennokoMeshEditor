using System.Diagnostics;
using Unity.Profiling;
#if DEN_MESH_EDITOR_DEBUG
using UnityEditor;
#endif

namespace Dennokoworks.DenMeshEditor.Editor
{
    /// <summary>
    /// プレビュー処理の計測用マーカー。
    ///
    /// Profiler で「どの処理が Renderer 数に比例して重くなっているか」を切り分けるために置く。
    /// マーカーは Profiler が記録していないときはほぼ無コストなので、常時残しておく。
    /// </summary>
    internal static class PreviewMarkers
    {
        internal static readonly ProfilerMarker OnFrame = new ProfilerMarker("DenMeshEditor.OnFrame");
        internal static readonly ProfilerMarker ProbeUpstream = new ProfilerMarker("DenMeshEditor.ProbeUpstream");
        internal static readonly ProfilerMarker Rebuild = new ProfilerMarker("DenMeshEditor.Rebuild");
        internal static readonly ProfilerMarker GatherEdits = new ProfilerMarker("DenMeshEditor.GatherEdits");
        internal static readonly ProfilerMarker Instantiate = new ProfilerMarker("DenMeshEditor.Instantiate");
    }

    /// <summary>
    /// プレビュー処理の回数カウンタ。<c>DEN_MESH_EDITOR_DEBUG</c> が定義されているときだけ動く。
    ///
    /// 呼び出しは <see cref="ConditionalAttribute"/> によって、シンボルが無いビルドでは
    /// 呼び出し元ごと取り除かれる。通常の利用者には一切コストがかからない。
    /// </summary>
    internal static class PreviewStats
    {
        private static int _rebuilds;
        private static int _fullReads;
        private static int _sampleProbes;
        private static int _generatedAlive;
        private static int _generatedCreated;
        private static int _nodesCreated;
        private static int _nodeRefreshes;
        private static int _nodeReused;

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountRebuild() => _rebuilds++;

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountFullRead() => _fullReads++;

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountSampleProbe() => _sampleProbes++;

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountGeneratedCreated()
        {
            _generatedCreated++;
            _generatedAlive++;
        }

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountGeneratedDestroyed() => _generatedAlive--;

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountNodeCreated() => _nodesCreated++;

        [Conditional("DEN_MESH_EDITOR_DEBUG")]
        internal static void CountNodeRefresh(bool reused)
        {
            _nodeRefreshes++;
            if (reused) _nodeReused++;
        }

#if DEN_MESH_EDITOR_DEBUG
        private const string MenuRoot = "Tools/dennokoworks/Dennoko Mesh Editor/Debug/";

        [MenuItem(MenuRoot + "Log Preview Stats")]
        private static void LogStats()
        {
            UnityEngine.Debug.Log(
                "[Dennoko Mesh Editor] Preview stats — "
                + $"Rebuild: {_rebuilds}, FullRead: {_fullReads}, SampleProbe: {_sampleProbes}, "
                + $"Generated alive: {_generatedAlive} (created {_generatedCreated}), "
                + $"Node created: {_nodesCreated}, Node refresh: {_nodeRefreshes} (reused {_nodeReused})");
        }

        [MenuItem(MenuRoot + "Reset Preview Stats")]
        private static void ResetStats()
        {
            // 生存数は実在するメッシュの数なのでリセットしない
            _rebuilds = 0;
            _fullReads = 0;
            _sampleProbes = 0;
            _generatedCreated = 0;
            _nodesCreated = 0;
            _nodeRefreshes = 0;
            _nodeReused = 0;
        }
#endif
    }
}
