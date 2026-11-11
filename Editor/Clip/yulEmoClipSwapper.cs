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
    /// yulEmoNDMFComponent の差し替え一覧に従って、AnimatorController の中のクリップを差し替える。
    ///
    /// ・対象は、同じオブジェクトの Modular Avatar Merge Animator が指す AnimatorController アセット。
    ///   全レイヤーのステートと、BlendTree の中のクリップを調べる。
    /// ・元の コントローラーは書き換えず、複製（「元の名前_SwappedCopy」）を作ってそちらを書き換え、
    ///   Merge Animator の参照を複製へ差し替える。元のコントローラーはそのまま残る。
    ///   既に複製を指している場合は、その複製自を書き換える（複製の複製は作らない）。
    /// ・差し替えられる箇所が 1 つも無い場合は、複製も 0 フレーム化クリップも作らず、何も変更しない。
    /// ・flattenToZeroFrame がオンのときは、差し替え後のクリップを yulEmoClipFlattener で
    ///   最終フレームだけの 0 フレームのクリップにしてから差し替える（コントローラーと同じ場所の「〜_Swapped」フォルダに保存）。
    /// ・実行後、コンポーネントの FaceFix クリップ（アイコン撮影用）と差し替え一覧の「差し替え前」を新しいクリップに更新する。
    ///   これにより、同じ一覧をもう一度実行しても結果が変わらない。
    /// ・コンポーネントと Merge Animator の変更は Undo で戻せるが、作られた複製やクリップのアセット自体は Undo の対象外。
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

            // 検証だけを先に行う。差し替えられる箇所が無いのに、複製やクリップを作って保存する形にしないため
            var found = 0;
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                found += CountInStateMachine(layer.stateMachine, map);
            }
            if (found == 0)
            {
                message = "コントローラーの中に、差し替え前のクリップが見つかりませんでした。";
                return false;
            }

            // 0 フレーム化
            var flattenedCount = 0;
            if (component.flattenToZeroFrame)
            {
                var folder = Path.GetDirectoryName(controllerPath).Replace('\\', '/') + "/" + controller.name + "_Swapped";
                yulEmoEditorUtility.EnsureFolder(folder);

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

            // 元のコントローラーは書き換えず、複製を作ってそちらを書き換える
            var target = PrepareCopy(controller, controllerPath, out var isNewCopy);
            if (target == null)
            {
                message = "コントローラーの複製を作成できませんでした。";
                return false;
            }

            // 複製側の書き換え
            var replaced = 0;
            foreach (var layer in target.layers)
            {
                if (layer.stateMachine == null) continue;
                replaced += ReplaceInStateMachine(layer.stateMachine, map);
            }
            EditorUtility.SetDirty(target);

            // Merge Animator の参照を複製へ差し替える（既に複製を指している場合はそのまま）
            if (isNewCopy)
            {
                Undo.RecordObject(merge, "21Emo: アニメーション差し替え");
                merge.animator = target;
                EditorUtility.SetDirty(merge);
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

            // 保存は差し替えた複製だけに絞る。プロジェクト全体の保存はしない
            AssetDatabase.SaveAssetIfDirty(target);

            message = $"{replaced} 箇所のクリップを差し替えました。"
                + (isNewCopy ? $"\n（複製「{target.name}」を作成し、Merge Animator の参照を差し替えました。元のコントローラーは変更していません）" : string.Empty)
                + (component.flattenToZeroFrame ? $"\n（0 フレームのクリップを {flattenedCount} 個作成しました）" : string.Empty);
            return true;
        }

        private const string CopySuffix = "_SwappedCopy";

        /// <summary>
        /// 書き換え対象の複製を用意する。既に複製（名前が _SwappedCopy で終わる）を指している場合は、
        /// 複製の複製を作らず、そのコントローラー自身を返す。
        /// 以前の実行で作られた複製が同じ場所にある場合は、作り直す（生成物なので、元のデータではない）。
        /// </summary>
        private static AnimatorController PrepareCopy(AnimatorController controller, string controllerPath, out bool isNewCopy)
        {
            // 失敗して return する経路でも out 引数を確定させるため、最初に初期化する（CS0177 対策）
            isNewCopy = false;
            if (controller.name.EndsWith(CopySuffix))
            {
                isNewCopy = false;
                return controller;
            }

            var folder = Path.GetDirectoryName(controllerPath).Replace('\\', '/');
            var copyPath = $"{folder}/{controller.name}{CopySuffix}.controller";

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(copyPath) != null)
            {
                AssetDatabase.DeleteAsset(copyPath);
            }
            if (!AssetDatabase.CopyAsset(controllerPath, copyPath))
            {
                return null;
            }
            isNewCopy = true;
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(copyPath);
        }

        private static int CountInStateMachine(AnimatorStateMachine stateMachine, Dictionary<AnimationClip, AnimationClip> map)
        {
            var count = 0;

            foreach (var childState in stateMachine.states)
            {
                var state = childState.state;
                if (state == null) continue;
                count += CountMotion(state.motion, map);
            }

            foreach (var child in stateMachine.stateMachines)
            {
                if (child.stateMachine != null) count += CountInStateMachine(child.stateMachine, map);
            }

            return count;
        }

        private static int CountMotion(Motion motion, Dictionary<AnimationClip, AnimationClip> map)
        {
            if (motion is AnimationClip clip)
            {
                return map.ContainsKey(clip) ? 1 : 0;
            }

            if (motion is BlendTree tree)
            {
                var count = 0;
                foreach (var child in tree.children)
                {
                    count += CountMotion(child.motion, map);
                }
                return count;
            }

            return 0;
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
    }
}
#endif
