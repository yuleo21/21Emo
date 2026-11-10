#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 既存の AnimatorController（アバターの FX レイヤー）を解析して、21Emo のウィンドウに取り込める形にする。
    /// ジェスチャーのステートと FaceFix の検出、ジェスチャーとは別の条件で切り替わるプリセットの検出を行う。
    /// 画面への反映とダイアログは yulEmoAnimatorWindow 側が行う。ここでは解析だけをする。
    /// </summary>
    internal static class yulEmoControllerImporter
    {
        /// <summary>検出したプリセット 1 つ分のクリップ（左右）。</summary>
        public sealed class ImportedPreset
        {
            public AnimationClip[] Left;
            public AnimationClip[] Right;
        }

        /// <summary>解析の結果。</summary>
        public sealed class Result
        {
            public yulEmoGenerator.HandMode HandMode;

            /// <summary>検出した Idle クリップ。検出できなければ null。</summary>
            public AnimationClip IdleClip;

            public AnimationClip[] Left;
            public AnimationClip[] Right;
            public AnimationClip[] FaceFixClips;

            /// <summary>元の表情レイヤーのクリップ（デフォルト表情レイヤーの対象シェイプキー収集用）。</summary>
            public List<AnimationClip> OriginalFacialClips;

            /// <summary>検出したプリセット（セットの値の小さい順）。検出できなければ空。</summary>
            public List<ImportedPreset> Presets;

            /// <summary>プリセットの切り替え方法の説明（ダイアログ表示用）。</summary>
            public string PresetDescription;
        }

        /// <summary>
        /// controller を解析する。gestureNames はジェスチャーのステート名の並び（gestureCount 個）。
        /// controller が null のときは null を返す。
        /// </summary>
        public static Result Analyze(AnimatorController controller, string[] gestureNames, int gestureCount, int faceFixSlotCount)
        {
            if (controller == null) return null;
            return new Worker(gestureNames, gestureCount, faceFixSlotCount).Run(controller);
        }

        private sealed class Worker
        {
            private readonly string[] _gestureNames;
            private readonly int _gestureCount;
            private readonly int _faceFixSlotCount;

            internal Worker(string[] gestureNames, int gestureCount, int faceFixSlotCount)
            {
                _gestureNames = gestureNames;
                _gestureCount = gestureCount;
                _faceFixSlotCount = faceFixSlotCount;
            }

            public Result Run(AnimatorController controller)
            {
                if (controller == null) return null;

                // 1) 候補レイヤーを収集
                var candidateLayers = new List<AnimatorControllerLayer>();
                foreach (var layer in controller.layers)
                {
                    if (layer.stateMachine == null) continue;
                    if (layer.name.Equals("21Facial", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateLayers.Insert(0, layer);
                        continue;
                    }

                    if (LayerHasGestureTransitions(layer.stateMachine))
                    {
                        candidateLayers.Add(layer);
                    }
                }

                var usedFallback = false;
                if (candidateLayers.Count == 0)
                {
                    usedFallback = true;
                    // ジェスチャー条件が見つからない場合、全レイヤーを対象にする
                    foreach (var layer in controller.layers)
                    {
                        if (layer.stateMachine != null)
                        {
                            candidateLayers.Add(layer);
                        }
                    }
                }

                // 元の表情レイヤーのクリップを控えておく（デフォルト表情レイヤーの対象シェイプキー収集用）。
                // 全レイヤーを対象にしたフォールバック時は、表情以外のレイヤーを巻き込むため収集しない。
                var originalFacialClips = new List<AnimationClip>();
                if (!usedFallback)
                {
                    var originalClips = new HashSet<AnimationClip>();
                    foreach (var layer in candidateLayers)
                    {
                        CollectMotionClips(layer.stateMachine, originalClips);
                    }
                    originalFacialClips.AddRange(originalClips);
                }

                var leftStates = new AnimatorState[_gestureCount];
                var rightStates = new AnimatorState[_gestureCount];
                var faceFixClips = new AnimationClip[_faceFixSlotCount];
                AnimationClip detectedIdleClip = null;

                // ジェスチャーとは別の条件で切り替わる、元からあるプリセットを検出する
                var presetContext = DetectOriginalPresets(candidateLayers);

                foreach (var layer in candidateLayers)
                {
                    CollectStatesFromStateMachine(
                        layer.stateMachine,
                        leftStates,
                        rightStates,
                        faceFixClips,
                        ref detectedIdleClip,
                        presetContext);
                }

                // クリップの取得
                var leftClips = new AnimationClip[_gestureCount];
                var rightClips = new AnimationClip[_gestureCount];
                for (var i = 0; i < _gestureCount; i++)
                {
                    leftClips[i] = leftStates[i] != null ? leftStates[i].motion as AnimationClip : null;
                    rightClips[i] = rightStates[i] != null ? rightStates[i].motion as AnimationClip : null;
                }

                // Both / Either の判定:
                // 左右の両方にクリップがあり、かつ「異なるアニメーションファイル」であるジェスチャーが
                // 1つでもあれば Both。ステートが別でも、遷移先のクリップが左右で完全に同じなら Either とみなす。
                bool isBoth = false;
                for (var i = 0; i < _gestureCount; i++)
                {
                    if (leftClips[i] != null && rightClips[i] != null && leftClips[i] != rightClips[i])
                    {
                        isBoth = true;
                        break;
                    }
                }

                // 検出したプリセットのクリップ（セットの値の小さい順）
                var presetClipSets = new List<(AnimationClip[] Left, AnimationClip[] Right)>();
                foreach (var pair in presetContext.Sets)
                {
                    var presetLeft = new AnimationClip[_gestureCount];
                    var presetRight = new AnimationClip[_gestureCount];
                    for (var i = 0; i < _gestureCount; i++)
                    {
                        presetLeft[i] = pair.Value.Left[i] != null ? pair.Value.Left[i].motion as AnimationClip : null;
                        presetRight[i] = pair.Value.Right[i] != null ? pair.Value.Right[i].motion as AnimationClip : null;
                        if (presetLeft[i] != null && presetRight[i] != null && presetLeft[i] != presetRight[i])
                        {
                            isBoth = true;
                        }
                    }
                    presetClipSets.Add((presetLeft, presetRight));
                }

                return new Result
                {
                    HandMode = isBoth ? yulEmoGenerator.HandMode.Both : yulEmoGenerator.HandMode.Either,
                    IdleClip = detectedIdleClip,
                    Left = leftClips,
                    Right = rightClips,
                    FaceFixClips = faceFixClips,
                    OriginalFacialClips = originalFacialClips,
                    Presets = presetClipSets.ConvertAll(set => new ImportedPreset { Left = set.Left, Right = set.Right }),
                    PresetDescription = presetContext.Describe(),
                };
            }

            private void CollectMotionClips(AnimatorStateMachine sm, HashSet<AnimationClip> clips)
            {
                foreach (var childState in sm.states)
                {
                    if (childState.state != null) CollectMotionClips(childState.state.motion, clips);
                }
                foreach (var subSm in sm.stateMachines)
                {
                    if (subSm.stateMachine != null) CollectMotionClips(subSm.stateMachine, clips);
                }
            }

            private void CollectMotionClips(Motion motion, HashSet<AnimationClip> clips)
            {
                if (motion is AnimationClip clip)
                {
                    clips.Add(clip);
                }
                else if (motion is BlendTree tree)
                {
                    foreach (var child in tree.children)
                    {
                        CollectMotionClips(child.motion, clips);
                    }
                }
            }

            private bool LayerHasGestureTransitions(AnimatorStateMachine sm)
            {
                foreach (var transition in sm.anyStateTransitions)
                {
                    if (HasGestureCondition(transition)) return true;
                }
                foreach (var childState in sm.states)
                {
                    if (childState.state == null) continue;
                    foreach (var transition in childState.state.transitions)
                    {
                        if (HasGestureCondition(transition)) return true;
                    }
                }
                foreach (var subSm in sm.stateMachines)
                {
                    if (subSm.stateMachine != null && LayerHasGestureTransitions(subSm.stateMachine))
                    {
                        return true;
                    }
                }
                return false;
            }

            private bool HasGestureCondition(AnimatorTransitionBase transition)
            {
                if (transition.conditions == null) return false;
                foreach (var cond in transition.conditions)
                {
                    if (cond.parameter == "GestureLeft" || cond.parameter == "GestureRight")
                    {
                        return true;
                    }
                }
                return false;
            }

            private bool IsPresetStateName(string stateName)
            {
                return System.Text.RegularExpressions.Regex.IsMatch(stateName, @"^P\d+_");
            }

            /// <summary>FacePreset が「基本以外」（Bool: True / Int: 0 以外）を要求する遷移かどうか。</summary>
            private bool IsPresetTransition(AnimatorStateTransition transition)
            {
                if (transition.conditions == null) return false;
                foreach (var cond in transition.conditions)
                {
                    if (cond.parameter != yulEmoGenerator.FacePresetParameter) continue;

                    if (cond.mode == AnimatorConditionMode.If) return true;
                    if (cond.mode == AnimatorConditionMode.Equals && Mathf.RoundToInt(cond.threshold) != 0) return true;
                }
                return false;
            }

            // ─────────────────────────────────────────────────────────────
            // 元からあるプリセットの検出
            // ─────────────────────────────────────────────────────────────

            /// <summary>プリセットの切り替えに使えない（別の役割で使われる）パラメータ。</summary>
            private readonly HashSet<string> NonSelectorParameters = new HashSet<string>
            {
                "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight", "FaceFix", "FaceLock",
            };

            /// <summary>プリセット 1 つ（基本以外の 1 つの値）分の、左右のジェスチャーのステート。</summary>
            private sealed class PresetStateSet
            {
                public readonly AnimatorState[] Left;
                public readonly AnimatorState[] Right;

                public PresetStateSet(int gestureCount)
                {
                    Left = new AnimatorState[gestureCount];
                    Right = new AnimatorState[gestureCount];
                }
            }

            /// <summary>
            /// ジェスチャーとは別の条件で切り替わるプリセットの検出結果。
            /// Selector が切り替えに使われているパラメータ名（無ければ null）。
            /// Bool: False = 基本、True = プリセット 1。Int: 0 = 基本、0 以外の値ごとにプリセット（値の小さい順）。
            /// </summary>
            private sealed class PresetImportContext
            {
                public string Selector;
                public bool IsBool;
                public readonly HashSet<AnimatorState> PresetStates = new HashSet<AnimatorState>();
                public readonly SortedDictionary<int, PresetStateSet> Sets = new SortedDictionary<int, PresetStateSet>();

                public string Describe()
                {
                    if (Selector == null) return string.Empty;
                    if (IsBool) return $"切り替えのパラメータ: {Selector}（Bool。False = 基本、True = Preset 1）";

                    var parts = new List<string> { "0 = 基本" };
                    var n = 1;
                    foreach (var value in Sets.Keys)
                    {
                        parts.Add($"{value} = Preset {n}");
                        n++;
                    }
                    return $"切り替えのパラメータ: {Selector}（Int。{string.Join("、", parts)}）";
                }
            }

            private void CollectAllTransitions(AnimatorStateMachine sm, List<AnimatorStateTransition> list)
            {
                list.AddRange(sm.anyStateTransitions);
                foreach (var childState in sm.states)
                {
                    if (childState.state != null && childState.state.transitions != null)
                    {
                        list.AddRange(childState.state.transitions);
                    }
                }
                foreach (var subSm in sm.stateMachines)
                {
                    if (subSm.stateMachine != null) CollectAllTransitions(subSm.stateMachine, list);
                }
            }

            /// <summary>遷移の条件から、GestureLeft / GestureRight == 値 を読み取る。ジェスチャー 1〜7 のどちらかがあれば true。</summary>
            private bool TryGetGestureValues(AnimatorStateTransition transition, out int gestureLeft, out int gestureRight)
            {
                gestureLeft = -1;
                gestureRight = -1;
                if (transition.conditions == null) return false;

                foreach (var cond in transition.conditions)
                {
                    if (cond.mode != AnimatorConditionMode.Equals) continue;

                    var value = Mathf.RoundToInt(cond.threshold);
                    if (cond.parameter == "GestureLeft") gestureLeft = value;
                    else if (cond.parameter == "GestureRight") gestureRight = value;
                }

                return (gestureLeft >= 1 && gestureLeft <= _gestureCount) || (gestureRight >= 1 && gestureRight <= _gestureCount);
            }

            /// <summary>
            /// 遷移が、パラメータ selector についてどのプリセットの値を要求しているか。
            /// Bool: If = 1 / IfNot = 0。Int: Equals の値。条件が無い（または読み取れない）ときは 0（基本）。
            /// </summary>
            private int GetPresetSetValue(AnimatorStateTransition transition, string selector)
            {
                if (transition.conditions == null) return 0;

                foreach (var cond in transition.conditions)
                {
                    if (cond.parameter != selector) continue;

                    switch (cond.mode)
                    {
                        case AnimatorConditionMode.If:
                            return 1;
                        case AnimatorConditionMode.IfNot:
                            return 0;
                        case AnimatorConditionMode.Equals:
                            return Mathf.Max(0, Mathf.RoundToInt(cond.threshold));
                    }
                }
                return 0;
            }

            private bool IsSelectorCondition(AnimatorCondition cond)
            {
                if (NonSelectorParameters.Contains(cond.parameter)) return false;
                return cond.mode == AnimatorConditionMode.If
                    || cond.mode == AnimatorConditionMode.IfNot
                    || cond.mode == AnimatorConditionMode.Equals;
            }

            /// <summary>
            /// ジェスチャーへの遷移に、ジェスチャーとは別の条件（Bool / Int）が付いていて、その値で表情が切り替わるものを探す。
            /// 例: パラメータ P が False のときの表情と、True のときの表情が別々にあれば、False = 基本、True = プリセット 1。
            /// 条件を満たすパラメータが複数あるときは、基本以外の遷移が一番多いものを選ぶ。
            /// 全ての遷移に付いている条件（基本の側が無いもの）は、切り替えではないとして無視する。
            /// </summary>
            private PresetImportContext DetectOriginalPresets(List<AnimatorControllerLayer> layers)
            {
                var context = new PresetImportContext();

                var transitions = new List<AnimatorStateTransition>();
                foreach (var layer in layers)
                {
                    if (layer.stateMachine != null) CollectAllTransitions(layer.stateMachine, transitions);
                }

                // ジェスチャーへの遷移だけを対象にする
                var gestureTransitions = new List<AnimatorStateTransition>();
                foreach (var t in transitions)
                {
                    if (t.destinationState != null && TryGetGestureValues(t, out _, out _))
                    {
                        gestureTransitions.Add(t);
                    }
                }
                if (gestureTransitions.Count == 0) return context;

                // パラメータごとに、「基本以外」の値を要求する遷移の数を数える
                var nonBaseCount = new Dictionary<string, int>();
                var boolVotes = new Dictionary<string, int>();
                var intVotes = new Dictionary<string, int>();
                foreach (var t in gestureTransitions)
                {
                    var names = new HashSet<string>();
                    foreach (var cond in t.conditions)
                    {
                        if (!IsSelectorCondition(cond)) continue;
                        if (!names.Add(cond.parameter)) continue;

                        if (cond.mode == AnimatorConditionMode.Equals)
                        {
                            intVotes[cond.parameter] = intVotes.TryGetValue(cond.parameter, out var iv) ? iv + 1 : 1;
                        }
                        else
                        {
                            boolVotes[cond.parameter] = boolVotes.TryGetValue(cond.parameter, out var bv) ? bv + 1 : 1;
                        }
                    }

                    foreach (var name in names)
                    {
                        if (GetPresetSetValue(t, name) != 0)
                        {
                            nonBaseCount[name] = nonBaseCount.TryGetValue(name, out var c) ? c + 1 : 1;
                        }
                    }
                }

                // 基本の側の遷移が 1 つ以上ある（= 全ての遷移が基本以外ではない）パラメータだけを候補にする
                string best = null;
                var bestCount = 0;
                foreach (var pair in nonBaseCount)
                {
                    if (pair.Value >= gestureTransitions.Count) continue;

                    var better = pair.Value > bestCount
                        || (pair.Value == bestCount && best != null
                            && (pair.Key == yulEmoGenerator.FacePresetParameter
                                || (best != yulEmoGenerator.FacePresetParameter && string.CompareOrdinal(pair.Key, best) < 0)));
                    if (better)
                    {
                        best = pair.Key;
                        bestCount = pair.Value;
                    }
                }
                if (best == null) return context;

                context.Selector = best;
                // Equals の条件があれば Int、無ければ Bool
                context.IsBool = !(intVotes.TryGetValue(best, out var intCount) && intCount > 0);

                // 基本以外の値ごとに、ジェスチャーごとのステートを集める
                foreach (var t in gestureTransitions)
                {
                    var setValue = GetPresetSetValue(t, best);
                    if (setValue == 0) continue;

                    TryGetGestureValues(t, out var gLeft, out var gRight);
                    var dest = t.destinationState;

                    if (!context.Sets.TryGetValue(setValue, out var set))
                    {
                        set = new PresetStateSet(_gestureCount);
                        context.Sets[setValue] = set;
                    }
                    if (gLeft >= 1 && gLeft <= _gestureCount) set.Left[gLeft - 1] = dest;
                    if (gRight >= 1 && gRight <= _gestureCount) set.Right[gRight - 1] = dest;
                    context.PresetStates.Add(dest);
                }

                // 切り替えの値が 1 つも読み取れなかったときは、検出なしにする
                if (context.Sets.Count == 0)
                {
                    context.Selector = null;
                    context.PresetStates.Clear();
                }

                return context;
            }

            private void CollectStatesFromStateMachine(
                AnimatorStateMachine sm,
                AnimatorState[] leftStates,
                AnimatorState[] rightStates,
                AnimationClip[] faceFixClips,
                ref AnimationClip detectedIdleClip,
                PresetImportContext presetContext)
            {
                // デフォルトステートを Idle 候補に
                if (detectedIdleClip == null && sm.defaultState != null && sm.defaultState.motion is AnimationClip defClip)
                {
                    detectedIdleClip = defClip;
                }

                // ステート名からの判定
                foreach (var childState in sm.states)
                {
                    var state = childState.state;
                    if (state == null) continue;

                    var clip = state.motion as AnimationClip;
                    var sName = state.name;

                    // 21Emo が作ったプリセット用ステート ("P1_Fist_L" など) は基本の表情として読み込まない
                    if (IsPresetStateName(sName)) continue;

                    // 検出したプリセットのステート（基本以外の値で選ばれるもの）も、基本の表情として読み込まない
                    if (presetContext.PresetStates.Contains(state)) continue;

                    // ジェスチャ表情メニュー用のステート ("GestureFix_Fist_L" など) も、基本の表情・FaceFix として読み込まない
                    if (sName.StartsWith(yulEmoGenerator.GestureFixStatePrefix, StringComparison.Ordinal)) continue;

                    // Idle 判定
                    if (detectedIdleClip == null && sName.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 && clip != null)
                    {
                        detectedIdleClip = clip;
                    }

                    // ステート名に _L / _R が含まれる場合の割り当て
                    bool isLeftNamed = sName.EndsWith("_L", StringComparison.OrdinalIgnoreCase) || sName.IndexOf("_Left", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isRightNamed = sName.EndsWith("_R", StringComparison.OrdinalIgnoreCase) || sName.IndexOf("_Right", StringComparison.OrdinalIgnoreCase) >= 0;

                    for (var i = 0; i < _gestureCount; i++)
                    {
                        var info = _gestureNames[i];
                        if (sName.IndexOf(info, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (isLeftNamed && leftStates[i] == null) leftStates[i] = state;
                            else if (isRightNamed && rightStates[i] == null) rightStates[i] = state;
                            else if (!isLeftNamed && !isRightNamed)
                            {
                                // 左右の区別がないステート名（例: "Point"）
                                if (leftStates[i] == null) leftStates[i] = state;
                                if (rightStates[i] == null) rightStates[i] = state;
                            }
                        }
                    }

                    // FaceFix ステート名フォールバック ("1"〜"8", "FaceFix_1"〜"FaceFix_8")
                    for (var i = 1; i <= _faceFixSlotCount; i++)
                    {
                        if (sName == i.ToString() || sName.Equals($"FaceFix_{i}", StringComparison.OrdinalIgnoreCase))
                        {
                            if (faceFixClips[i - 1] == null && clip != null) faceFixClips[i - 1] = clip;
                        }
                    }
                }

                // トランジションの条件を走査
                var allTransitions = new List<AnimatorStateTransition>();
                allTransitions.AddRange(sm.anyStateTransitions);

                foreach (var childState in sm.states)
                {
                    if (childState.state != null && childState.state.transitions != null)
                    {
                        allTransitions.AddRange(childState.state.transitions);
                    }
                }

                foreach (var t in allTransitions)
                {
                    var dest = t.destinationState;
                    if (dest == null) continue;

                    // ジェスチャ表情メニュー用ステートへの遷移（FaceFix == value）は、FaceFix の固定表情として読み込まない
                    if (dest.name.StartsWith(yulEmoGenerator.GestureFixStatePrefix, StringComparison.Ordinal)) continue;

                    // プリセット用の遷移（基本以外の値で選ばれるもの）は基本の表情として読み込まない。
                    // プリセットを検出できたときはその条件で、できなかったときは FacePreset の条件で判定する。
                    if (presetContext.Selector != null)
                    {
                        if (TryGetGestureValues(t, out _, out _) && GetPresetSetValue(t, presetContext.Selector) != 0) continue;
                    }
                    else if (IsPresetTransition(t))
                    {
                        continue;
                    }

                    int gLeft = -1;
                    int gRight = -1;
                    int fix = -1;

                    if (t.conditions != null)
                    {
                        foreach (var cond in t.conditions)
                        {
                            if (cond.mode == AnimatorConditionMode.Equals)
                            {
                                var val = Mathf.RoundToInt(cond.threshold);
                                if (cond.parameter == "GestureLeft") gLeft = val;
                                else if (cond.parameter == "GestureRight") gRight = val;
                                else if (cond.parameter == "FaceFix") fix = val;
                            }
                        }
                    }

                    // Idle 判定
                    if (gLeft == 0 && gRight == 0 && dest.motion is AnimationClip idleClip && detectedIdleClip == null)
                    {
                        detectedIdleClip = idleClip;
                    }

                    // ジェスチャー番号 1〜7
                    if (gLeft >= 1 && gLeft <= _gestureCount)
                    {
                        leftStates[gLeft - 1] = dest;
                    }

                    if (gRight >= 1 && gRight <= _gestureCount)
                    {
                        rightStates[gRight - 1] = dest;
                    }

                    // FaceFix 1〜8
                    // ジェスチャ表情メニューを使って作られたコントローラーでは、FaceFix の値がずれている。
                    // ステート名はスロット番号（"1"〜"8"）なので、名前で読み取れるときはそちらを優先する。
                    var fixSlot = fix;
                    if (fix >= 1 && int.TryParse(dest.name, out var namedSlot) && namedSlot >= 1 && namedSlot <= _faceFixSlotCount)
                    {
                        fixSlot = namedSlot;
                    }
                    if (fixSlot >= 1 && fixSlot <= _faceFixSlotCount)
                    {
                        if (dest.motion is AnimationClip fixClip)
                        {
                            faceFixClips[fixSlot - 1] = fixClip;
                        }
                    }
                }

                // サブステートマシンも再帰走査
                foreach (var subSm in sm.stateMachines)
                {
                    if (subSm.stateMachine != null)
                    {
                        CollectStatesFromStateMachine(subSm.stateMachine, leftStates, rightStates, faceFixClips, ref detectedIdleClip, presetContext);
                    }
                }
            }
        }
    }
}
#endif
