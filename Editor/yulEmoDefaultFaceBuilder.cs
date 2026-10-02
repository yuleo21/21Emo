#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>
    /// 「デフォルト表情レイヤー」用に、顔のブレンドシェイプとそのデフォルト値を集める。
    ///
    /// 元の表情レイヤーと 21Facial の干渉を、FXレイヤーを削除せずに防ぐための仕組み。
    /// 21Facial の直前に「全シェイプキーをデフォルト値に戻し続ける」レイヤーを置くことで、
    /// 元の表情レイヤーが動かしたシェイプキーを毎フレーム上書きして打ち消す。
    ///
    /// 対象は次のクリップに含まれる SkinnedMeshRenderer の blendShape.* カーブ:
    ///   - アバターの元の表情レイヤーが使っているクリップ
    ///   - 21Emo で設定したクリップ（Idle / ジェスチャー / FaceFix）
    /// </summary>
    public static class yulEmoDefaultFaceBuilder
    {
        private const string BlendShapePrefix = "blendShape.";

        public static yulEmoGenerator.DefaultFaceBinding[] Build(
            GameObject avatarObject,
            IEnumerable<AnimationClip> clips,
            bool excludeLipSyncAndBlink)
        {
            var result = new List<yulEmoGenerator.DefaultFaceBinding>();
            if (avatarObject == null || clips == null) return result.ToArray();

            var descriptor = avatarObject.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                descriptor = avatarObject.GetComponentInChildren<VRCAvatarDescriptor>();
            }
            if (descriptor == null) return result.ToArray();

            var root = descriptor.transform;
            var excluded = excludeLipSyncAndBlink
                ? CollectLipSyncAndBlinkShapes(descriptor)
                : new HashSet<(string, string)>();

            var seenClips = new HashSet<AnimationClip>();
            var seenBindings = new HashSet<(string, string)>();
            var meshCache = new Dictionary<string, SkinnedMeshRenderer>();

            foreach (var clip in clips)
            {
                if (clip == null || !seenClips.Add(clip)) continue;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer)) continue;
                    if (!binding.propertyName.StartsWith(BlendShapePrefix)) continue;

                    var key = (binding.path, binding.propertyName);
                    if (excluded.Contains(key)) continue;
                    if (!seenBindings.Add(key)) continue;

                    if (!meshCache.TryGetValue(binding.path, out var mesh))
                    {
                        var target = root.Find(binding.path);
                        mesh = target != null ? target.GetComponent<SkinnedMeshRenderer>() : null;
                        meshCache[binding.path] = mesh;
                    }
                    if (mesh == null || mesh.sharedMesh == null) continue;

                    var shapeName = binding.propertyName.Substring(BlendShapePrefix.Length);
                    var index = mesh.sharedMesh.GetBlendShapeIndex(shapeName);
                    if (index < 0) continue;

                    result.Add(new yulEmoGenerator.DefaultFaceBinding(
                        binding.path,
                        binding.propertyName,
                        mesh.GetBlendShapeWeight(index)));
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// リップシンク（Viseme）とまばたき（Eyelids）で使われているシェイプキーを集める。
        /// これらは VRChat 側で制御されるため、デフォルト表情レイヤーでは触らない。
        /// </summary>
        private static HashSet<(string, string)> CollectLipSyncAndBlinkShapes(VRCAvatarDescriptor descriptor)
        {
            var set = new HashSet<(string, string)>();
            var root = descriptor.transform;

            var visemeMesh = descriptor.VisemeSkinnedMesh;
            if (visemeMesh != null && descriptor.VisemeBlendShapes != null)
            {
                var path = AnimationUtility.CalculateTransformPath(visemeMesh.transform, root);
                foreach (var name in descriptor.VisemeBlendShapes)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    set.Add((path, BlendShapePrefix + name));
                }
            }

            var eye = descriptor.customEyeLookSettings;
            if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes &&
                eye.eyelidsSkinnedMesh != null &&
                eye.eyelidsSkinnedMesh.sharedMesh != null &&
                eye.eyelidsBlendshapes != null)
            {
                var mesh = eye.eyelidsSkinnedMesh;
                var path = AnimationUtility.CalculateTransformPath(mesh.transform, root);
                foreach (var index in eye.eyelidsBlendshapes)
                {
                    if (index < 0 || index >= mesh.sharedMesh.blendShapeCount) continue;
                    set.Add((path, BlendShapePrefix + mesh.sharedMesh.GetBlendShapeName(index)));
                }
            }

            return set;
        }
    }
}
#endif