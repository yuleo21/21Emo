#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace 21Emo
{
    /// <summary>
    /// 21Emo のメインウィンドウ。
    /// VRChat アバター用の表情アニメーターコントローラーを、
    /// Animator As Code (AacV1) を使って生成するためのエディタ拡張。
    /// </summary>
    public class 21EmoWindow : EditorWindow
    {
        private const int FaceFixSlotCount = 8;
        private const int GestureCount = 7;

        // アバター読み込み用
        private GameObject _avatarObject;
        private GameObject _lastLoadedAvatar;

        // 手のモード & WD設定
        private 21EmoGenerator.HandMode _handMode = 21EmoGenerator.HandMode.Both;
        private bool _writeDefaults = true;

        // Idle（待機用）
        private AnimationClip _idleClip;

        // Both モード用: 左手・右手それぞれのクリップ
        private readonly AnimationClip[] _clipsLeft  = new AnimationClip[GestureCount];
        private readonly AnimationClip[] _clipsRight = new AnimationClip[GestureCount];

        // Either モード用: 左右共通クリップ
        private readonly AnimationClip[] _clipsEither = new AnimationClip[GestureCount];

        private bool _useGestureWeight;

        private bool _useFaceFix;
        private readonly AnimationClip[] _faceFixClips = new AnimationClip[FaceFixSlotCount];

        private string _outputFolder = "Assets/21Emo/Generated";
        private string _controllerName = "21Facial_Both";

        private Vector2 _scroll;

        // ジェスチャー情報（名前・値は固定）
        private static readonly (string Label, string Name, int Value)[] GestureInfos =
        {
            ("Fist",              "Fist",      1),
            ("Open",              "Open",      2),
            ("Point",           "Point",     3),
            ("Peace",           "Peace",     4),
            ("RockNRoll", "RockNRoll", 5),
            ("Gun",          "Gun",       6),
            ("Thumbs up",      "Thumbs up", 7),
        };

        [MenuItem("21tools/21Emo")]
        public static void Open()
        {
            var window = GetWindow<21EmoWindow>("21Emo");
            window.minSize = new Vector2(480, 640);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("21Emo", EditorStyles.boldLabel);

            DrawAvatarSection();
            DrawModeSection();
            DrawGestureSection();
            DrawFaceFixSection();
            DrawOutputSection();
            DrawCreateButton();

            EditorGUILayout.EndScrollView();
        }

        // ─────────────────────────────────────────────────────────────
        // 0. アバター読み込みセクション
        // ─────────────────────────────────────────────────────────────
        private void DrawAvatarSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("アバターから読み込み", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            var newAvatar = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("アバター", "VRCAvatarDescriptor を持つアバターの GameObject を指定してください。"),
                _avatarObject,
                typeof(GameObject),
                true);

            if (newAvatar != _avatarObject)
            {
                _avatarObject = newAvatar;
                if (_avatarObject != null && _avatarObject != _lastLoadedAvatar)
                {
                    LoadFromAvatar(_avatarObject, showSuccessDialog: true);
                }
            }

            using (new EditorGUI.DisabledScope(_avatarObject == null))
            {
                if (GUILayout.Button("読み込む", GUILayout.Width(70)))
                {
                    LoadFromAvatar(_avatarObject, showSuccessDialog: true);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        // ─────────────────────────────────────────────────────────────
        // 1. モード & WD 設定
        // ─────────────────────────────────────────────────────────────
        private void DrawModeSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. モード・設定", EditorStyles.boldLabel);

            var newMode = (21EmoGenerator.HandMode)EditorGUILayout.EnumPopup("手のモード", _handMode);
            if (newMode != _handMode)
            {
                var oldDefaultName = _handMode == 21EmoGenerator.HandMode.Both ? "21Facial_Both" : "21Facial_Either";
                if (_controllerName == oldDefaultName)
                {
                    _controllerName = newMode == 21EmoGenerator.HandMode.Both ? "21Facial_Both" : "21Facial_Either";
                }
                _handMode = newMode;
            }

            // WD設定
            // Idle が空の場合は強制的に WD オンになり、操作不可
            var isIdleEmpty = _idleClip == null;
            if (isIdleEmpty)
            {
                _writeDefaults = true;
            }

            using (new EditorGUI.DisabledScope(isIdleEmpty))
            {
                _writeDefaults = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "WDを有効にする",
                        "各ステートの Write Defaults を ON にします。OFF にすると WD オフ対応になります。\n" +
                        "※ Idle が空の場合は表情がリセットされなくなるため、強制的に ON に固定されます。"),
                    _writeDefaults);
            }
            if (isIdleEmpty)
            {
                EditorGUILayout.LabelField("※ Idle が未設定のため、Write Defaults は強制的に ON に固定されます。", EditorStyles.miniLabel);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. ジェスチャーごとのアニメーション
        // ─────────────────────────────────────────────────────────────
        private void DrawGestureSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. アニメーション設定", EditorStyles.boldLabel);

            _idleClip = ClipField("Idle", _idleClip);

            EditorGUILayout.Space(4);

            if (_handMode == 21EmoGenerator.HandMode.Both)
            {
                DrawBothGestureFields();
            }
            else
            {
                DrawEitherGestureFields();
            }

            EditorGUILayout.Space(4);
            _useGestureWeight = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "GestureWeight を Motion Time として使用する",
                    "GestureLeftWeight / GestureRightWeight を使い、握り込みの深さに応じて再生します。"),
                _useGestureWeight);
        }

        private void DrawBothGestureFields()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("ジェスチャー", EditorStyles.boldLabel, GUILayout.Width(170));
            EditorGUILayout.LabelField("左手", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("右手", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            for (var i = 0; i < GestureCount; i++)
            {
                var info = GestureInfos[i];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(info.Label, GUILayout.Width(170));
                _clipsLeft[i] = (AnimationClip)EditorGUILayout.ObjectField(_clipsLeft[i], typeof(AnimationClip), false);
                _clipsRight[i] = (AnimationClip)EditorGUILayout.ObjectField(_clipsRight[i], typeof(AnimationClip), false);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawEitherGestureFields()
        {
            for (var i = 0; i < GestureCount; i++)
            {
                var info = GestureInfos[i];
                _clipsEither[i] = ClipField(info.Label, _clipsEither[i]);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 3. FaceFixオプション
        // ─────────────────────────────────────────────────────────────
        private void DrawFaceFixSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. FaceFixオプション", EditorStyles.boldLabel);

            _useFaceFix = EditorGUILayout.ToggleLeft(
                "FaceFix パラメータによる固定表情を使用する（最大8個）",
                _useFaceFix);

            if (_useFaceFix)
            {
                EditorGUI.indentLevel++;
                for (var i = 0; i < FaceFixSlotCount; i++)
                {
                    _faceFixClips[i] = ClipField($"表情 {i + 1}（FaceFix = {i + 1}）", _faceFixClips[i]);
                }
                EditorGUI.indentLevel--;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 4. 出力先
        // ─────────────────────────────────────────────────────────────
        private void DrawOutputSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("4. 出力先", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _outputFolder = EditorGUILayout.TextField("保存先フォルダ", _outputFolder);
            if (GUILayout.Button("参照...", GUILayout.Width(70)))
            {
                var selected = EditorUtility.OpenFolderPanel("保存先フォルダを選択", Application.dataPath, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    var relative = ToProjectRelativePath(selected);
                    if (relative != null)
                    {
                        _outputFolder = relative;
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("21Emo", "プロジェクトの Assets フォルダの中を選択してください。", "OK");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            _controllerName = EditorGUILayout.TextField("コントローラー名", _controllerName);
        }

        // ─────────────────────────────────────────────────────────────
        // 作成ボタン
        // ─────────────────────────────────────────────────────────────
        private void DrawCreateButton()
        {
            EditorGUILayout.Space();
            EditorGUILayout.Space();

            if (GUILayout.Button("アニメーターを作成", GUILayout.Height(36)))
            {
                Generate();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // アバターからの読み込み処理
        // ─────────────────────────────────────────────────────────────
        private void LoadFromAvatar(GameObject avatarObj, bool showSuccessDialog = true)
        {
            if (avatarObj == null)
            {
                EditorUtility.DisplayDialog("21Emo", "アバターの GameObject を指定してください。", "OK");
                return;
            }

            var descriptor = avatarObj.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                descriptor = avatarObj.GetComponentInChildren<VRCAvatarDescriptor>();
            }

            if (descriptor == null)
            {
                EditorUtility.DisplayDialog("21Emo", "指定されたオブジェクトに VRCAvatarDescriptor が見つかりませんでした。", "OK");
                return;
            }

            AnimatorController fxController = null;
            if (descriptor.baseAnimationLayers != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                {
                    if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && layer.animatorController != null)
                    {
                        fxController = layer.animatorController as AnimatorController;
                        break;
                    }
                }
            }

            if (fxController == null)
            {
                var animator = descriptor.GetComponent<Animator>();
                if (animator != null && animator.runtimeAnimatorController != null)
                {
                    fxController = animator.runtimeAnimatorController as AnimatorController;
                }
            }

            if (fxController == null)
            {
                EditorUtility.DisplayDialog("21Emo", "VRCAvatarDescriptor の FX レイヤーに AnimatorController が設定されていません。", "OK");
                return;
            }

            _lastLoadedAvatar = avatarObj;
            AnalyzeAndImportController(fxController, showSuccessDialog);
        }

        private void AnalyzeAndImportController(AnimatorController controller, bool showSuccessDialog)
        {
            if (controller == null) return;

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

            if (candidateLayers.Count == 0)
            {
                // ジェスチャー条件が見つからない場合、全レイヤーを対象にする
                foreach (var layer in controller.layers)
                {
                    if (layer.stateMachine != null)
                    {
                        candidateLayers.Add(layer);
                    }
                }
            }

            var leftStates = new AnimatorState[GestureCount];
            var rightStates = new AnimatorState[GestureCount];
            var faceFixClips = new AnimationClip[FaceFixSlotCount];
            AnimationClip detectedIdleClip = null;

            foreach (var layer in candidateLayers)
            {
                CollectStatesFromStateMachine(
                    layer.stateMachine,
                    leftStates,
                    rightStates,
                    faceFixClips,
                    ref detectedIdleClip);
            }

            // Both / Either の判定:
            // 左右それぞれ別ステートが存在していれば Both、そうでなければ Either
            bool isBoth = false;
            for (var i = 0; i < GestureCount; i++)
            {
                if (leftStates[i] != null && rightStates[i] != null && leftStates[i] != rightStates[i])
                {
                    isBoth = true;
                    break;
                }
            }

            // クリップの取得
            var leftClips = new AnimationClip[GestureCount];
            var rightClips = new AnimationClip[GestureCount];
            for (var i = 0; i < GestureCount; i++)
            {
                leftClips[i] = leftStates[i] != null ? leftStates[i].motion as AnimationClip : null;
                rightClips[i] = rightStates[i] != null ? rightStates[i].motion as AnimationClip : null;
            }

            // UI に適用
            _handMode = isBoth ? 21EmoGenerator.HandMode.Both : 21EmoGenerator.HandMode.Either;
            _controllerName = isBoth ? "21Facial_Both" : "21Facial_Either";

            if (detectedIdleClip != null)
            {
                _idleClip = detectedIdleClip;
            }

            for (var i = 0; i < GestureCount; i++)
            {
                _clipsLeft[i] = leftClips[i];
                _clipsRight[i] = rightClips[i];
                _clipsEither[i] = leftClips[i] ?? rightClips[i];
            }

            bool hasFaceFix = false;
            for (var i = 0; i < FaceFixSlotCount; i++)
            {
                _faceFixClips[i] = faceFixClips[i];
                if (faceFixClips[i] != null)
                {
                    hasFaceFix = true;
                }
            }
            _useFaceFix = hasFaceFix;

            if (showSuccessDialog)
            {
                var modeText = isBoth ? "両手用 (Both)" : "片手用 (Either)";
                EditorUtility.DisplayDialog("21Emo", $"アバターの FX レイヤーからアニメーションを読み込みました。\n判定モード: {modeText}", "OK");
            }
        }

        private static bool LayerHasGestureTransitions(AnimatorStateMachine sm)
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

        private static bool HasGestureCondition(AnimatorTransitionBase transition)
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

        private static void CollectStatesFromStateMachine(
            AnimatorStateMachine sm,
            AnimatorState[] leftStates,
            AnimatorState[] rightStates,
            AnimationClip[] faceFixClips,
            ref AnimationClip detectedIdleClip)
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

                // Idle 判定
                if (detectedIdleClip == null && sName.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 && clip != null)
                {
                    detectedIdleClip = clip;
                }

                // ステート名に _L / _R が含まれる場合の割り当て
                bool isLeftNamed = sName.EndsWith("_L", StringComparison.OrdinalIgnoreCase) || sName.IndexOf("_Left", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isRightNamed = sName.EndsWith("_R", StringComparison.OrdinalIgnoreCase) || sName.IndexOf("_Right", StringComparison.OrdinalIgnoreCase) >= 0;

                for (var i = 0; i < GestureCount; i++)
                {
                    var info = GestureInfos[i];
                    if (sName.IndexOf(info.Name, StringComparison.OrdinalIgnoreCase) >= 0)
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
                for (var i = 1; i <= FaceFixSlotCount; i++)
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
                if (gLeft >= 1 && gLeft <= 7)
                {
                    leftStates[gLeft - 1] = dest;
                }

                if (gRight >= 1 && gRight <= 7)
                {
                    rightStates[gRight - 1] = dest;
                }

                // FaceFix 1〜8
                if (fix >= 1 && fix <= 8)
                {
                    if (dest.motion is AnimationClip fixClip)
                    {
                        faceFixClips[fix - 1] = fixClip;
                    }
                }
            }

            // サブステートマシンも再帰走査
            foreach (var subSm in sm.stateMachines)
            {
                if (subSm.stateMachine != null)
                {
                    CollectStatesFromStateMachine(subSm.stateMachine, leftStates, rightStates, faceFixClips, ref detectedIdleClip);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // ユーティリティ & 生成
        // ─────────────────────────────────────────────────────────────
        private static AnimationClip ClipField(string label, AnimationClip clip)
        {
            return (AnimationClip)EditorGUILayout.ObjectField(label, clip, typeof(AnimationClip), false);
        }

        private static string ToProjectRelativePath(string absolutePath)
        {
            absolutePath = absolutePath.Replace('\\', '/');
            var dataPath = Application.dataPath.Replace('\\', '/');
            if (!absolutePath.StartsWith(dataPath)) return null;
            return "Assets" + absolutePath.Substring(dataPath.Length);
        }

        private void Generate()
        {
            if (string.IsNullOrWhiteSpace(_outputFolder) || !_outputFolder.StartsWith("Assets"))
            {
                EditorUtility.DisplayDialog("21Emo", "保存先フォルダは Assets 以下のパスで指定してください。", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(_controllerName))
            {
                EditorUtility.DisplayDialog("21Emo", "コントローラー名を入力してください。", "OK");
                return;
            }

            EnsureFolderExists(_outputFolder.TrimEnd('/'));

            var assetPath = $"{_outputFolder.TrimEnd('/')}/{_controllerName}.controller";

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null)
            {
                var overwrite = EditorUtility.DisplayDialog(
                    "21Emo",
                    $"{assetPath} は既に存在します。上書きしますか？",
                    "上書きする",
                    "キャンセル");
                if (!overwrite) return;
                AssetDatabase.DeleteAsset(assetPath);
            }

            var config = BuildConfig();
            21EmoGenerator.Generate(config, assetPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var result = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (result != null)
            {
                EditorGUIUtility.PingObject(result);
                Selection.activeObject = result;
            }

            EditorUtility.DisplayDialog("21Emo", $"作成が完了しました。\n{assetPath}", "OK");
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

        private 21EmoGenerator.Config BuildConfig()
        {
            var gestures = new 21EmoGenerator.GestureDef[GestureCount];

            if (_handMode == 21EmoGenerator.HandMode.Both)
            {
                for (var i = 0; i < GestureCount; i++)
                {
                    var info = GestureInfos[i];
                    gestures[i] = new 21EmoGenerator.GestureDef(
                        info.Name, info.Value,
                        _clipsLeft[i],
                        _clipsRight[i]);
                }
            }
            else
            {
                for (var i = 0; i < GestureCount; i++)
                {
                    var info = GestureInfos[i];
                    gestures[i] = new 21EmoGenerator.GestureDef(
                        info.Name, info.Value,
                        _clipsEither[i]);
                }
            }

            // Idle が空の場合は強制的に WD オン
            var actualWriteDefaults = _idleClip == null ? true : _writeDefaults;

            return new 21EmoGenerator.Config
            {
                HandMode         = _handMode,
                WriteDefaults    = actualWriteDefaults,
                UseGestureWeight = _useGestureWeight,
                IdleClip         = _idleClip,
                Gestures         = gestures,
                UseFaceFix       = _useFaceFix,
                FaceFixClips     = (AnimationClip[])_faceFixClips.Clone(),
            };
        }
    }
}
#endif
