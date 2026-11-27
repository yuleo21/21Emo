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
    /// 元の表情レイヤーと 21Facial の干渉を、FXレイヤーを削除せずに防ぐ。
    /// 21Facial の直前に「全シェイプキーをデフォルト値に戻し続ける」レイヤーを置くことで、
    /// 元の表情レイヤーが動かしたシェイプキーを毎フレーム上書きして打ち消す。
    ///
    /// 対象は、次のクリップに blendShape.* カーブが現れる SkinnedMeshRenderer の全ブレンドシェイプ:
    ///   - アバターの元の表情レイヤーが使っているクリップ
    ///   - 21Emo で設定したクリップ（Idle / ジェスチャー / FaceFix）
    /// クリップに含まれるシェイプキーだけでは、元の表情管理が別の経路で動かすシェイプキーを
    /// 書き漏らして打ち消せないため、レンダラー単位で全てを対象にする。
    /// デフォルト値は、生成時点のアバターのシェイプキーの値（GetBlendShapeWeight）を使う。
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

            var descriptor = yulEmoEditorUtility.FindDescriptor(avatarObject);
            if (descriptor == null) return result.ToArray();

            var root = descriptor.transform;
            var excluded = excludeLipSyncAndBlink
                ? CollectLipSyncAndBlinkShapes(descriptor)
                : new HashSet<(string, string)>();

            // クリップに blendShape.* カーブが現れるレンダラーを集める
            var seenClips = new HashSet<AnimationClip>();
            var paths = new List<string>();
            var seenPaths = new HashSet<string>();

            foreach (var clip in clips)
            {
                if (clip == null || !seenClips.Add(clip)) continue;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer)) continue;
                    if (!binding.propertyName.StartsWith(BlendShapePrefix)) continue;
                    if (seenPaths.Add(binding.path)) paths.Add(binding.path);
                }
            }

            // 集めたレンダラーの全ブレンドシェイプを、現在の値で対象にする
            foreach (var path in paths)
            {
                var target = root.Find(path);
                var mesh = target != null ? target.GetComponent<SkinnedMeshRenderer>() : null;
                if (mesh == null || mesh.sharedMesh == null) continue;

                var sharedMesh = mesh.sharedMesh;
                for (var i = 0; i < sharedMesh.blendShapeCount; i++)
                {
                    var propertyName = BlendShapePrefix + sharedMesh.GetBlendShapeName(i);
                    if (excluded.Contains((path, propertyName))) continue;

                    result.Add(new yulEmoGenerator.DefaultFaceBinding(
                        path,
                        propertyName,
                        mesh.GetBlendShapeWeight(i)));
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