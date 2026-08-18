using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenMeshEditor.Editor
{
    internal partial class EditSession
    {
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

            // ボーン情報を読めなかったときは頂点位置の取得と同じ変換で代用する。
            // ここだけ localToWorldMatrix を使うと、スケールの扱いが
            // WorldVertices と食い違ってデルタがずれる
            var fallback = MeshToWorld(target);

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

            NotifyInfluencesChanged();
        }

        /// <summary>
        /// 現在の編集内容を「次のドラッグの基準」として確定する。
        /// </summary>
        private void CommitSnapshot()
        {
            foreach (var target in _targets)
            {
                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
            }
        }

        /// <summary>
        /// ハンドルの変位に対応する、ミラー側の変位を求める。
        /// 中心だけでなく変位ベクトルも反射する。これを忘れると反対側が同じ向きに動く。
        /// </summary>
        private Vector3 MirrorDisplacement(Vector3 displacement)
        {
            if (!_mirrorActive) return Vector3.zero;

            var root = MirrorRoot;
            var inRoot = root.worldToLocalMatrix.MultiplyVector(displacement);
            return root.localToWorldMatrix.MultiplyVector(Reflect(inRoot, _component.mirrorAxis));
        }

        /// <summary>
        /// ハンドルの変位を各頂点へ配分し、メッシュローカルのデルタとして保存する。
        /// </summary>
        private void ApplyDisplacement()
        {
            var displacement = _handlePosition - _centerWorld;
            var mirrorDisplacement = MirrorDisplacement(displacement);

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
            CopyDeltas(target.Snapshot, target.Working);
        }

        /// <summary>
        /// デルタ辞書の内容を移す。インスタンスは作り直さず、確保済みの容量を使い回す。
        /// </summary>
        private static void CopyDeltas(Dictionary<int, Vector3> source, Dictionary<int, Vector3> destination)
        {
            destination.Clear();
            foreach (var pair in source)
            {
                destination.Add(pair.Key, pair.Value);
            }
        }
    }
}
