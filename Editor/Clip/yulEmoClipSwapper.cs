#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// yulEmoNDMFComponent の差し替え一覧に従って、生成済みコントローラー（アセット）の中のクリップを直接差し替える。
    ///
    /// ・対象は、同じオブジェクトの Modular Avatar Merge Animator が指す AnimatorController アセット。
    ///   全レイヤーのステートと、BlendTree の中のクリップを調べる。
    /// ・flattenToZeroFrame がオンのときは、差し替え後のクリップを yulEmoClipFlattener で
    ///   最終フレームだけの 0 フレームのクリップにしてから差し替える（コントローラーと同じ場所の「〜_Swapped」フォルダに保存）。
    /// ・実行後、コンポーネントの FaceFix クリップ（アイコン撮影用）と差し替え一覧の「差し替え前」を新しいクリップに更新する。
    ///   これにより、同じ一覧をもう一度実行しても結果が変わらない。
    /// ・元に戻す（Undo）には対応していない。
    /// </summary>
    public static class yulEmoClipSwapper
    {
        public static bool TryExecute(yulEmoNDMFComponent component, out string message)
        {
            if (component == null)
            {
                message = "コンポーネントが見つかりません。";
                return false;
            }

            var merge = component.GetComponent<ModularAvatarMergeAnimator>();
            var controller = merge != null ? merge.animator as AnimatorController : null;
            if (controller == null)
            {
                message = "同じオブジェクトの Merge Animator に、AnimatorController が設定されていません。";
                return false;
            }

            var controllerPath = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(controllerPath))
            {
                message = "コントローラーがアセットとして保存されていません。";
                return false;
            }

            // 差し替える組（差し替え前・差し替え後の両方が設定されているもの）
            var map = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var swap in component.clipSwaps)
            {
                if (swap == null || swap.source == null || swap.replacement == null) continue;
                if (swap.source == swap.replacement) continue;
                map[swap.source] = swap.replacement;
            }
            if (map.Count == 0)
            {
                message = "差し替える組がありません。「差し替え前」と「差し替え後」の両方を設定してください。";
                return false;
            }

            // 0 フレーム化
            var flattenedCount = 0;
            if (component.flattenToZeroFrame)
            {
                var folder = Path.GetDirectoryName(controllerPath).Replace('\\', '/') + "/" + controller.name + "_Swapped";
                EnsureFolderExists(folder);

                var cache = new Dictionary<AnimationClip, AnimationClip>();
                var keys = new List<AnimationClip>(map.Keys);
                foreach (var key in keys)
                {
                    var replacement = map[key];
                    if (!cache.TryGetValue(replacement, out var flat))
                    {
                        flat = yulEmoClipFlattener.Create(replacement, folder);
                        cache[replacement] = flat;
                        if (flat != replacement) flattenedCount++;
                    }
                    map[key] = flat;
                }
            }

            // コントローラーの書き換え
            var replaced = 0;
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                replaced += ReplaceInStateMachine(layer.stateMachine, map);
            }

            // コンポーネント側を更新（アイコン撮影用のクリップと、差し替え一覧の「差し替え前」）
            Undo.RecordObject(component, "21Emo: アニメーション差し替え");
            if (component.faceFixClips != null)
            {
                for (var i = 0; i < component.faceFixClips.Length; i++)
                {
                    var clip = component.faceFixClips[i];
                    if (clip != null && map.TryGetValue(clip, out var newClip)) component.faceFixClips[i] = newClip;
                }
            }
            foreach (var swap in component.clipSwaps)
            {
                if (swap == null || swap.source == null || swap.replacement == null) continue;
                if (!map.TryGetValue(swap.source, out var newClip)) continue;
                swap.source = newClip;
                swap.replacement = null;
            }
            EditorUtility.SetDirty(component);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            if (replaced == 0)
            {
                message = "コントローラーの中に、差し替え前のクリップが見つかりませんでした。";
                return false;
            }

            message = $"{replaced} 箇所のクリップを差し替えました。"
                + (component.flattenToZeroFrame ? $"\n（0 フレームのクリップを {flattenedCount} 個作成しました）" : string.Empty);
            return true;
        }

        private static int ReplaceInStateMachine(AnimatorStateMachine stateMachine, Dictionary<AnimationClip, AnimationClip> map)
        {
            var count = 0;

            foreach (var childState in stateMachine.states)
            {
                var state = childState.state;
                if (state == null) continue;

                var motion = ReplaceMotion(state.motion, map, ref count);
                if (motion != state.motion)
                {
                    state.motion = motion;
                    EditorUtility.SetDirty(state);
                }
            }

            foreach (var child in stateMachine.stateMachines)
            {
                if (child.stateMachine != null) count += ReplaceInStateMachine(child.stateMachine, map);
            }

            return count;
        }

        private static Motion ReplaceMotion(Motion motion, Dictionary<AnimationClip, AnimationClip> map, ref int count)
        {
            if (motion is AnimationClip clip)
            {
                if (map.TryGetValue(clip, out var replacement))
                {
                    count++;
                    return replacement;
                }
                return motion;
            }

            if (motion is BlendTree tree)
            {
                var children = tree.children;
                var changed = false;
                for (var i = 0; i < children.Length; i++)
                {
                    var newMotion = ReplaceMotion(children[i].motion, map, ref count);
                    if (newMotion != children[i].motion)
                    {
                        children[i].motion = newMotion;
                        changed = true;
                    }
                }
                if (changed)
                {
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                }
            }

            return motion;
        }

        private static void EnsureFolderExists(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }
    }
}
#endif
