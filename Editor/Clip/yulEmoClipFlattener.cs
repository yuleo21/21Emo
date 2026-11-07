#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 2 フレームなどの複数フレームを持つアニメーションクリップから、
    /// 最終フレームの値だけを持つ「0 フレーム（キーが 1 つだけ）」の新しいクリップを作る。
    /// 元のクリップは変更しない。
    /// </summary>
    public static class yulEmoClipFlattener
    {
        private const string Suffix = "_LastFrame";

        /// <summary>
        /// source の最終フレームだけを持つ新しいクリップを folder に保存して返す。
        /// source が null の場合は null、既に 1 フレームだけ（長さ 0）の場合は source をそのまま返す。
        /// folder は Assets から始まる、存在するフォルダのパス。
        /// </summary>
        public static AnimationClip Create(AnimationClip source, string folder)
        {
            if (source == null) return null;
            if (source.length <= 0f) return source;

            var result = new AnimationClip
            {
                name = source.name + Suffix,
                frameRate = source.frameRate,
            };

            // 数値カーブ（ブレンドシェイプ・Transform・有効/無効 など）
            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                var curve = AnimationUtility.GetEditorCurve(source, binding);
                if (curve == null || curve.length == 0) continue;

                var last = curve.keys[curve.length - 1];
                AnimationUtility.SetEditorCurve(result, binding, new AnimationCurve(new Keyframe(0f, last.value)));
            }

            // オブジェクト参照カーブ（マテリアル差し替えなど）
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
            {
                var frames = AnimationUtility.GetObjectReferenceCurve(source, binding);
                if (frames == null || frames.Length == 0) continue;

                var last = frames[frames.Length - 1];
                AnimationUtility.SetObjectReferenceCurve(
                    result,
                    binding,
                    new[] { new ObjectReferenceKeyframe { time = 0f, value = last.value } });
            }

            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder.TrimEnd('/')}/{result.name}.anim");
            AssetDatabase.CreateAsset(result, path);
            return result;
        }
    }
}
#endif
