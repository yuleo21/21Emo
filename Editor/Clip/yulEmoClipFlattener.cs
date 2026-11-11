#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 2 フレームなどの複数フレームを持つアニメーションクリップから、
    /// 最終フレームの値だけを持つ「0 フレーム（キーが 1 つだけ）」の新しいクリップを作る。
    /// 元のクリップは変更しない。
    /// クリップ設定（AnimationClipSettings）は引き継ぐが、startTime / stopTime だけは 0 に戻す。
    /// アニメーションイベントは引き継がない（最終フレームの姿勢だけを持つクリップのため）。
    /// 同名のクリップが保存先にある場合は、新規作成ではなく中身を上書きして再利用する。
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

            // クリップ設定（Loop Time など）は引き継ぐ。引き継がないと、ループが必要な表情がフラット化で壊れるため
            // ただし startTime / stopTime は元の長さ前提の値で、そのまま写すと長さ 0 のクリップの再生範囲が
            // 元クリップの長さになってしまうので、この 2 つだけ 0 に戻す
            var settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.startTime = 0f;
            settings.stopTime = 0f;
            AnimationUtility.SetAnimationClipSettings(result, settings);

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

            // 同名のクリップが既にある場合は、中身を上書きして再利用する
            // 実行のたびに新しいファイルを作ると、使われていないクリップがフォルダに溜まっていくため
            var path = $"{folder.TrimEnd('/')}/{result.name}.anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null && existing != source)
            {
                EditorUtility.CopySerialized(result, existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(result);
                return existing;
            }

            if (existing == source)
            {
                // 元クリップ自身と衝突する場合は、元を上書きしないよう別名で作る
                path = AssetDatabase.GenerateUniqueAssetPath(path);
            }
            AssetDatabase.CreateAsset(result, path);
            return result;
        }
    }
}
#endif
