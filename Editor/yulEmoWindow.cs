#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>
    /// 21Emo のメインウィンドウ。
    /// VRChat アバター用の表情アニメーターコントローラーを、
    /// Animator As Code (AacV1) を使って生成するためのエディタ拡張。
    /// </summary>
    public class yulEmoWindow : EditorWindow
    {
        private const int FaceFixSlotCount = 8;
        private const int GestureCount = 7;

        // アバター読み込み用
        private GameObject _avatarObject;
        private GameObject _lastLoadedAvatar;

        // 手のモード & WD設定
        private yulEmoGenerator.HandMode _handMode = yulEmoGenerator.HandMode.Both;
        private bool _writeDefaults = true;

        // Idle（待機用）
        private AnimationClip _idleClip;

        // Both モード用: 左手・右手それぞれのクリップ
        private readonly AnimationClip[] _clipsLeft  = new AnimationClip[GestureCount];
        private readonly AnimationClip[] _clipsRight = new AnimationClip[GestureCount];

        // Either モード用: 左右共通クリップ
        private readonly AnimationClip[] _clipsEither = new AnimationClip[GestureCount];

        private bool _useGestureWeight;

        // ジェスチャー表情のプリセット（基本の表情とは別に、FacePreset で切り替える表情セット）
        private sealed class PresetEntry
        {
            public string Name = "";
            public bool Foldout = true;
            public readonly AnimationClip[] Left   = new AnimationClip[GestureCount];
            public readonly AnimationClip[] Right  = new AnimationClip[GestureCount];
            public readonly AnimationClip[] Either = new AnimationClip[GestureCount];
        }

        private readonly List<PresetEntry> _presets = new List<PresetEntry>();

        // ジェスチャ表情メニュー（Idle 以外の各ジェスチャの表情を、FaceFix パラメータで固定するメニュー）
        private bool _useGestureFaceMenu;

        private bool _useFaceFix;
        private readonly AnimationClip[] _faceFixClips = new AnimationClip[FaceFixSlotCount];
        // FaceFix のメニュー表示名（既定は番号）
        private readonly string[] _faceFixNames = { "1", "2", "3", "4", "5", "6", "7", "8" };

        private string _outputFolder = "Assets/21Emo/Generated";
        // 空欄のときは「アバター名_21Facial_日時」を自動で付ける（作成のたびに別の名前になる）
        private string _controllerName = string.Empty;

        // ジェスチャー表情ステートで Eyes & Eyelids を Animation にする (VRCAnimatorTrackingControl)
        private bool _useTrackingControl = true;

        // 表情クリップを「最終フレームだけの 0 フレームアニメーション」に差し替えて使う
        private bool _flattenClips;
        private string _flattenFolder;
        private readonly Dictionary<AnimationClip, AnimationClip> _flattenCache = new Dictionary<AnimationClip, AnimationClip>();

        // 表情変更を無効にするルール（複数可。いずれかが成立している間は無効）
        private sealed class DisableRuleEntry
        {
            public string Name = "";
            public yulEmoGenerator.DisableParameterType Type = yulEmoGenerator.DisableParameterType.Bool;
            public yulEmoGenerator.DisableCondition Condition = yulEmoGenerator.DisableCondition.True;
            public float Value;
        }

        private readonly List<DisableRuleEntry> _disableRules = new List<DisableRuleEntry>();

        // 型ごとに選べる条件（Animator の仕様: Bool=If/IfNot, Int=Greater/Less/Equals/NotEqual, Float=Greater/Less）
        private static readonly yulEmoGenerator.DisableCondition[] BoolConditions =
        {
            yulEmoGenerator.DisableCondition.True,
            yulEmoGenerator.DisableCondition.False,
        };
        private static readonly yulEmoGenerator.DisableCondition[] IntConditions =
        {
            yulEmoGenerator.DisableCondition.Greater,
            yulEmoGenerator.DisableCondition.Less,
            yulEmoGenerator.DisableCondition.Equals,
            yulEmoGenerator.DisableCondition.NotEqual,
        };
        private static readonly yulEmoGenerator.DisableCondition[] FloatConditions =
        {
            yulEmoGenerator.DisableCondition.Greater,
            yulEmoGenerator.DisableCondition.Less,
        };
        private static readonly string[] BoolConditionLabels = { "True", "False" };
        private static readonly string[] IntConditionLabels = { "Greater", "Less", "Equals", "Not Equal" };
        private static readonly string[] FloatConditionLabels = { "Greater", "Less" };

        // 元の表情レイヤーとの干渉防止（デフォルト表情レイヤー）
        private bool _resetOriginalFace = true;
        private bool _excludeLipSyncAndBlink = true;
        // アバターから読み込んだ際に検出した、元の表情レイヤーのクリップ
        private readonly List<AnimationClip> _originalFacialClips = new List<AnimationClip>();

        // FaceFix のメニューアイコンを、アニメーター作成時に撮影して設定する
        private bool _captureFaceFixIcons = true;
        // 撮影用に複製したアバターへ NDMF の全プラグインを適用する（ビルド時と同じ状態で撮影）
        private bool _captureWithNDMF = true;

        // 生成後に Modular Avatar (Merge Animator / Menu Installer / Menu Item) を自動セットアップするか
        private bool _setupModularAvatar = true;

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
            var window = GetWindow<yulEmoWindow>("21Emo");
            window.minSize = new Vector2(480, 640);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("21Emo", EditorStyles.boldLabel);

            DrawAvatarSection();
            DrawModeSection();
            DrawGestureSection();
            DrawPresetSection();
            DrawFaceFixSection();
            DrawConflictSection();
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
                var droppedEmo = newAvatar != null ? newAvatar.GetComponent<yulEmoNDMFComponent>() : null;
                if (droppedEmo != null)
                {
                    // 21Emo のオブジェクト（シーン上、またはプレハブ）が渡された: 設定をそのオブジェクトから読み込む。
                    // シーン上のときは、親のアバターをアバター欄に設定する。
                    var owner = newAvatar.GetComponentInParent<VRCAvatarDescriptor>();
                    if (owner != null)
                    {
                        _avatarObject = owner.gameObject;
                        _lastLoadedAvatar = _avatarObject;
                    }
                    var emo = droppedEmo;
                    EditorApplication.delayCall += () => LoadFromEmoObject(emo, true);
                }
                else
                {
                    _avatarObject = newAvatar;
                    if (_avatarObject != null && _avatarObject != _lastLoadedAvatar)
                    {
                        LoadFromAvatar(_avatarObject, showSuccessDialog: true);
                    }
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

            // このアバターに作成済みの「21Emo」があれば、その設定を読み込めるようにする
            var existingEmo = FindEmoComponent(_avatarObject);
            using (new EditorGUI.DisabledScope(existingEmo == null))
            {
                if (GUILayout.Button(new GUIContent(
                        "作成済みの 21Emo から設定を読み込む",
                        "アバターの「21Emo」オブジェクトに保存された、作成時の設定をウィンドウに読み込みます。\n" +
                        "「21Emo」オブジェクトやそのプレハブを、上のアバター欄にドラッグ＆ドロップしても読み込めます。")))
                {
                    var emo = existingEmo;
                    EditorApplication.delayCall += () => LoadFromEmoObject(emo, true);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 1. モード & WD 設定
        // ─────────────────────────────────────────────────────────────
        private void DrawModeSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. モード・設定", EditorStyles.boldLabel);

            var newMode = (yulEmoGenerator.HandMode)EditorGUILayout.EnumPopup("手のモード", _handMode);
            if (newMode != _handMode)
            {
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

            if (_handMode == yulEmoGenerator.HandMode.Both)
            {
                DrawBothGestureFields(_clipsLeft, _clipsRight);
            }
            else
            {
                DrawEitherGestureFields(_clipsEither);
            }

            EditorGUILayout.Space(4);
            _useGestureWeight = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "GestureWeight を Motion Time として使用する",
                    "GestureLeftWeight / GestureRightWeight を使い、握り込みの深さに応じて再生します。"),
                _useGestureWeight);

            _useTrackingControl = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Eyes & Eyelids をアニメーション制御にする (Tracking Control)",
                    "各ジェスチャー表情ステートに VRC Animator Tracking Control を追加し、Eyes & Eyelids を Animation にします。\n" +
                    "Idle には Tracking へ戻す設定を追加します。FaceFix ステートには追加されません。"),
                _useTrackingControl);

            _flattenClips = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "クリップを最終フレームだけの 0 フレームアニメーションに差し替える",
                    "Idle・ジェスチャー・プリセット・FaceFix のクリップごとに、最終フレームの値だけを持つ新しいクリップを作り、\n" +
                    "生成するコントローラーではそちらを使います。元のクリップは変更しません。\n" +
                    "新しいクリップは「保存先フォルダ/コントローラー名_Clips」に保存されます（作り直すたびに中身は入れ替わります）。\n" +
                    "既に 1 フレームだけのクリップは、そのまま使います。"),
                _flattenClips);

            EditorGUILayout.Space(4);
            DrawDisableRules();
        }

        // ─────────────────────────────────────────────────────────────
        // 表情変更を無効にするパラメータ（複数・型/条件指定）
        // ─────────────────────────────────────────────────────────────
        private void DrawDisableRules()
        {
            EditorGUILayout.LabelField(
                new GUIContent(
                    "表情変更を無効にするパラメータ",
                    "いずれかの条件が成立している間は、Idle からジェスチャーステートへ遷移せず、\n" +
                    "ジェスチャーステートにいる場合も Idle へ戻されます（OR 条件）。\n" +
                    "※ パラメータの宣言（Expression Parameters 等）は行いません。\n" +
                    "※ Float は Animator の仕様上 Greater / Less のみです。しきい値とちょうど同じ値のときは、\n" +
                    "   無効にも有効にもならず、Idle から動かなくなります。"),
                EditorStyles.boldLabel);

            var removeIndex = -1;
            for (var i = 0; i < _disableRules.Count; i++)
            {
                var rule = _disableRules[i];

                EditorGUILayout.BeginHorizontal();
                rule.Name = EditorGUILayout.TextField(rule.Name);

                var newType = (yulEmoGenerator.DisableParameterType)EditorGUILayout.EnumPopup(rule.Type, GUILayout.Width(60));
                if (newType != rule.Type)
                {
                    rule.Type = newType;
                }

                // 型に合わない条件が入っていたらその型の先頭の条件に補正する
                var conditions = ConditionsFor(rule.Type);
                var labels = ConditionLabelsFor(rule.Type);
                var condIndex = System.Array.IndexOf(conditions, rule.Condition);
                if (condIndex < 0)
                {
                    condIndex = 0;
                    rule.Condition = conditions[0];
                }
                condIndex = EditorGUILayout.Popup(condIndex, labels, GUILayout.Width(80));
                rule.Condition = conditions[condIndex];

                if (rule.Type == yulEmoGenerator.DisableParameterType.Int)
                {
                    rule.Value = EditorGUILayout.IntField(Mathf.RoundToInt(rule.Value), GUILayout.Width(60));
                }
                else if (rule.Type == yulEmoGenerator.DisableParameterType.Float)
                {
                    rule.Value = EditorGUILayout.FloatField(rule.Value, GUILayout.Width(60));
                }

                if (GUILayout.Button("−", GUILayout.Width(24)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (removeIndex >= 0)
            {
                _disableRules.RemoveAt(removeIndex);
            }

            if (GUILayout.Button("＋ パラメータを追加", GUILayout.Width(160)))
            {
                _disableRules.Add(new DisableRuleEntry());
            }

            if (_disableRules.Count == 0)
            {
                EditorGUILayout.LabelField("※ 未設定の場合、表情変更の無効化は行いません。", EditorStyles.miniLabel);
            }
        }

        private static yulEmoGenerator.DisableCondition[] ConditionsFor(yulEmoGenerator.DisableParameterType type)
        {
            switch (type)
            {
                case yulEmoGenerator.DisableParameterType.Int: return IntConditions;
                case yulEmoGenerator.DisableParameterType.Float: return FloatConditions;
                default: return BoolConditions;
            }
        }

        private static string[] ConditionLabelsFor(yulEmoGenerator.DisableParameterType type)
        {
            switch (type)
            {
                case yulEmoGenerator.DisableParameterType.Int: return IntConditionLabels;
                case yulEmoGenerator.DisableParameterType.Float: return FloatConditionLabels;
                default: return BoolConditionLabels;
            }
        }

        private yulEmoGenerator.DisableRule[] BuildDisableRules()
        {
            var rules = new List<yulEmoGenerator.DisableRule>();
            foreach (var entry in _disableRules)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                rules.Add(new yulEmoGenerator.DisableRule(entry.Name.Trim(), entry.Type, entry.Condition, entry.Value));
            }
            return rules.ToArray();
        }

        private void DrawBothGestureFields(AnimationClip[] clipsLeft, AnimationClip[] clipsRight)
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
                clipsLeft[i] = (AnimationClip)EditorGUILayout.ObjectField(clipsLeft[i], typeof(AnimationClip), false);
                clipsRight[i] = (AnimationClip)EditorGUILayout.ObjectField(clipsRight[i], typeof(AnimationClip), false);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawEitherGestureFields(AnimationClip[] clipsEither)
        {
            for (var i = 0; i < GestureCount; i++)
            {
                var info = GestureInfos[i];
                clipsEither[i] = ClipField(info.Label, clipsEither[i]);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 3. ジェスチャープリセット（FacePreset で切り替える追加の表情セット）
        // ─────────────────────────────────────────────────────────────
        private void DrawPresetSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                new GUIContent(
                    "3. ジェスチャープリセット",
                    "「2. アニメーション設定」を基本の表情（FacePreset が False / 0 のとき）として、\n" +
                    "メニューから切り替えられる別のジェスチャー表情セットを追加します。\n" +
                    "追加が 1 個なら FacePreset は Bool、2 個以上なら Int になります。\n" +
                    "クリップが 1 つも設定されていないプリセットは無視されます。"),
                EditorStyles.boldLabel);

            var removeIndex = -1;
            for (var i = 0; i < _presets.Count; i++)
            {
                var preset = _presets[i];

                EditorGUILayout.BeginHorizontal();
                preset.Foldout = EditorGUILayout.Foldout(preset.Foldout, $"プリセット {i + 1}（FacePreset = {i + 1}）", true);
                preset.Name = EditorGUILayout.TextField(preset.Name, GUILayout.MinWidth(80));
                if (GUILayout.Button("−", GUILayout.Width(24)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();

                if (preset.Foldout)
                {
                    EditorGUI.indentLevel++;
                    if (_handMode == yulEmoGenerator.HandMode.Both)
                    {
                        DrawBothGestureFields(preset.Left, preset.Right);
                    }
                    else
                    {
                        DrawEitherGestureFields(preset.Either);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            if (removeIndex >= 0)
            {
                _presets.RemoveAt(removeIndex);
            }

            if (GUILayout.Button("＋ プリセットを追加", GUILayout.Width(160)))
            {
                _presets.Add(new PresetEntry { Name = $"Preset {_presets.Count + 1}" });
            }

            var activeCount = 0;
            foreach (var preset in _presets)
            {
                if (PresetHasAnyClip(preset)) activeCount++;
            }

            if (activeCount == 0)
            {
                EditorGUILayout.LabelField("※ クリップを設定したプリセットが無い場合、FacePreset は作成されません。", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    activeCount == 1
                        ? "FacePreset: Bool（False = 基本 / True = プリセット1）"
                        : $"FacePreset: Int（0 = 基本 / 1〜{activeCount} = 各プリセット）",
                    EditorStyles.miniLabel);
            }
        }

        private bool PresetHasAnyClip(PresetEntry preset)
        {
            var left = _handMode == yulEmoGenerator.HandMode.Both ? preset.Left : preset.Either;
            var right = _handMode == yulEmoGenerator.HandMode.Both ? preset.Right : null;
            for (var i = 0; i < GestureCount; i++)
            {
                if (left[i] != null) return true;
                if (right != null && right[i] != null) return true;
            }
            return false;
        }

        private AnimationClip Flat(AnimationClip clip)
        {
            if (!_flattenClips || clip == null || _flattenFolder == null) return clip;

            if (!_flattenCache.TryGetValue(clip, out var result))
            {
                result = yulEmoClipFlattener.Create(clip, _flattenFolder);
                _flattenCache[clip] = result;
            }
            return result;
        }

        private yulEmoGenerator.GestureDef[] MakeGestureDefs(
            yulEmoGenerator.HandMode handMode,
            AnimationClip[] left,
            AnimationClip[] right,
            AnimationClip[] either)
        {
            var gestures = new yulEmoGenerator.GestureDef[GestureCount];
            for (var i = 0; i < GestureCount; i++)
            {
                var info = GestureInfos[i];
                gestures[i] = handMode == yulEmoGenerator.HandMode.Both
                    ? new yulEmoGenerator.GestureDef(info.Name, info.Value, Flat(left[i]), Flat(right[i]))
                    : new yulEmoGenerator.GestureDef(info.Name, info.Value, Flat(either[i]));
            }
            return gestures;
        }

        // ─────────────────────────────────────────────────────────────
        // 3. FaceFixオプション
        // ─────────────────────────────────────────────────────────────
        private void DrawFaceFixSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("4. FaceFixオプション", EditorStyles.boldLabel);

            // ジェスチャ表情メニュー（メニューは Modular Avatar で作るため、アバターの指定と MA のセットアップが必要）
            var gestureMenuAvailable = _avatarObject != null && _setupModularAvatar;
            using (new EditorGUI.DisabledScope(!gestureMenuAvailable))
            {
                var useGestureMenu = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "ジェスチャ表情メニューを追加する",
                        "「Lock」の後に「ジェスチャ表情」サブメニューを追加し、Idle 以外の各ジェスチャの表情を選んで固定できるようにします。\n" +
                        "・ジェスチャープリセットがあるときは、「Default」（基本）とプリセット名のサブメニューに分けます。\n" +
                        "・両手用 (Both) のときは、さらに Left / Right のサブメニューに分けます。\n" +
                        "・FaceFix パラメータを流用し、ジェスチャ表情が 1 から先に使います。下の FaceFix の値はその分だけ後ろにずれます。\n" +
                        "・Idle から各表情への FaceFix による遷移を追加し、表情変更を無効にするパラメータの設定も適用します。\n" +
                        "・メニューのアイコンは、FaceFix と同じように撮影して設定します。\n" +
                        "※ メニューは Modular Avatar で作るため、アバターの指定と、出力先の「Modular Avatar でアバターに組み込む」が必要です。"),
                    _useGestureFaceMenu && gestureMenuAvailable);
                if (gestureMenuAvailable) _useGestureFaceMenu = useGestureMenu;
            }
            if (!gestureMenuAvailable)
            {
                EditorGUILayout.LabelField("※ アバターの指定と Modular Avatar のセットアップが必要です。", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            _useFaceFix = EditorGUILayout.ToggleLeft(
                "FaceFix パラメータによる固定表情を使用する（最大8個）",
                _useFaceFix);
            if (_useFaceFix && _useGestureFaceMenu && _avatarObject != null && _setupModularAvatar)
            {
                EditorGUILayout.LabelField(
                    "※ ジェスチャ表情メニューを使うため、FaceFix の値はジェスチャ表情の個数だけ後ろにずれます。",
                    EditorStyles.miniLabel);
            }

            if (_useFaceFix)
            {
                EditorGUI.indentLevel++;
                for (var i = 0; i < FaceFixSlotCount; i++)
                {
                    _faceFixClips[i] = ClipField($"表情 {i + 1}（FaceFix = {i + 1}）", _faceFixClips[i]);
                    EditorGUI.indentLevel++;
                    _faceFixNames[i] = EditorGUILayout.TextField(
                        new GUIContent("メニュー名", "Modular Avatar のメニューに表示される名前です。空欄にすると番号（" + (i + 1) + "）になります。"),
                        _faceFixNames[i]);
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 4. 元の表情との干渉対策
        // ─────────────────────────────────────────────────────────────
        private void DrawConflictSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("5. 元の表情との干渉対策", EditorStyles.boldLabel);

            var hasAvatar = _avatarObject != null;
            using (new EditorGUI.DisabledScope(!hasAvatar))
            {
                var reset = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "デフォルト表情レイヤーを追加する",
                        "21Facial の直前に、顔のシェイプキーをデフォルト値に戻し続けるレイヤーを追加します。\n" +
                        "元の表情レイヤーが動かしたシェイプキーを上書きして打ち消すため、FXレイヤーを削除せずに干渉を防げます。\n" +
                        "対象は、アバターの元の表情レイヤーと 21Emo のクリップが使っているシェイプキーです。"),
                    _resetOriginalFace && hasAvatar);
                if (hasAvatar) _resetOriginalFace = reset;

                using (new EditorGUI.DisabledScope(!_resetOriginalFace || !hasAvatar))
                {
                    EditorGUI.indentLevel++;
                    _excludeLipSyncAndBlink = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "リップシンク・まばたきのシェイプキーを除外する",
                            "VRChat のリップシンク(Viseme)とまばたき(Eyelids)に使われているシェイプキーは、デフォルト表情レイヤーで触りません。"),
                        _excludeLipSyncAndBlink);
                    EditorGUI.indentLevel--;
                }
            }

            if (!hasAvatar)
            {
                EditorGUILayout.LabelField("※ アバターが未指定のため、デフォルト表情レイヤーは作成されません。", EditorStyles.miniLabel);
            }
            else if (_resetOriginalFace)
            {
                EditorGUILayout.LabelField(
                    _originalFacialClips.Count > 0
                        ? $"元の表情レイヤーのクリップを {_originalFacialClips.Count} 個検出しています。"
                        : "元の表情レイヤーは未検出です（21Emo のクリップのみが対象になります）。",
                    EditorStyles.miniLabel);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 5. 出力先
        // ─────────────────────────────────────────────────────────────
        private void DrawOutputSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("6. 出力先", EditorStyles.boldLabel);

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

            _controllerName = EditorGUILayout.TextField(
                new GUIContent("コントローラー名", "空欄のときは「アバター名_21Facial_日時」を自動で付けます。"),
                _controllerName);
            if (string.IsNullOrWhiteSpace(_controllerName))
            {
                EditorGUILayout.LabelField("　空欄のため作成時に自動で付けます（例: " + BuildAutoControllerName() + "）", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            var hasAvatar = _avatarObject != null;
            using (new EditorGUI.DisabledScope(!hasAvatar))
            {
                var toggled = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Modular Avatar でアバターに組み込む",
                        "アバター直下に「21Emo」オブジェクトを作成し、Merge Animator で FX レイヤーに結合します。\n" +
                        "また Menu Installer / Menu Item で「21Emo」サブメニュー（プリセット・Lock・ジェスチャ表情・FaceFix 1〜8 のトグル）を追加します。\n" +
                        "※ 既に同名のオブジェクトがある場合は作り直します。"),
                    _setupModularAvatar && hasAvatar);
                if (hasAvatar) _setupModularAvatar = toggled;
            }
            using (new EditorGUI.DisabledScope(!hasAvatar || !_setupModularAvatar || !(_useFaceFix || _useGestureFaceMenu)))
            {
                EditorGUI.indentLevel++;
                _captureFaceFixIcons = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "FaceFix・ジェスチャ表情のメニューアイコンを撮影して設定する",
                        "アニメーター作成時に、アバターを複製して FaceFix（とジェスチャ表情メニュー）の各表情（最終フレーム）を適用し、顔を正面から撮影します。\n" +
                        "画像は「保存先フォルダ/コントローラー名_Icons」に FaceFix_1.png〜（FaceFix の値の順。ジェスチャ表情が先）として保存し、メニューのトグルのアイコンに設定します。\n" +
                        "元のアバターは変更されません。FaceFix またはジェスチャ表情メニューを使用する設定で、Modular Avatar のセットアップが有効なときだけ動きます。"),
                    _captureFaceFixIcons);
                using (new EditorGUI.DisabledScope(!_captureFaceFixIcons))
                {
                    _captureWithNDMF = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "NDMF を適用した状態で撮影する",
                            "撮影用に複製したアバターへ NDMF の全プラグイン（Modular Avatar など）を適用してから撮影します。\n" +
                            "衣装の切り替えなどがビルド後と同じ状態になります。\n" +
                            "メッシュを統合する最適化ツールを使っている場合は、表情が反映されないことがあります。その場合はオフにしてください。"),
                        _captureWithNDMF);
                    var cameraSettings = yulEmoCameraSettings.Current;
                    EditorGUILayout.LabelField(
                        new GUIContent("カメラ位置", "アイコン撮影のカメラの左右・上下・前後・画角です。プレビューウィンドウで調整します。"),
                        new GUIContent(cameraSettings.IsDefault ? "標準" : cameraSettings.ToString()));
                    if (GUILayout.Button("カメラ位置をプレビューして調整..."))
                    {
                        yulEmoCameraPreviewWindow.Open(_avatarObject);
                    }
                }
                EditorGUI.indentLevel--;
            }

            if (!hasAvatar)
            {
                EditorGUILayout.LabelField("※ アバターが未指定のため、Modular Avatar のセットアップは行われません。", EditorStyles.miniLabel);
            }
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
                // ダイアログ表示やシーン操作を OnGUI の途中で行うと
                // 「EndLayoutGroup: BeginLayoutGroup must be called first」が出るため、OnGUI の後に実行する
                EditorApplication.delayCall += Generate;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 作成済みの 21Emo からの読み込み・設定の保存
        // ─────────────────────────────────────────────────────────────
        private static yulEmoNDMFComponent FindEmoComponent(GameObject avatar)
        {
            if (avatar == null) return null;

            var descriptor = avatar.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null) descriptor = avatar.GetComponentInChildren<VRCAvatarDescriptor>();
            if (descriptor == null) return null;

            var root = descriptor.transform.Find(yulEmoModularAvatarSetup.RootObjectName);
            return root != null ? root.GetComponent<yulEmoNDMFComponent>() : null;
        }

        private static void CopyClips(AnimationClip[] from, AnimationClip[] to)
        {
            for (var i = 0; i < to.Length; i++)
            {
                to[i] = from != null && i < from.Length ? from[i] : null;
            }
        }

        /// <summary>今のウィンドウの設定を、「21Emo」オブジェクトに保存できる形にまとめる。</summary>
        private yulEmoSettingsSnapshot BuildSnapshot()
        {
            var s = new yulEmoSettingsSnapshot
            {
                handMode = (int)_handMode,
                writeDefaults = _writeDefaults,
                idleClip = _idleClip,
                useGestureWeight = _useGestureWeight,
                useTrackingControl = _useTrackingControl,
                flattenClips = _flattenClips,
                useGestureFaceMenu = _useGestureFaceMenu,
                useFaceFix = _useFaceFix,
                resetOriginalFace = _resetOriginalFace,
                excludeLipSyncAndBlink = _excludeLipSyncAndBlink,
                outputFolder = _outputFolder,
                controllerName = _controllerName,
                captureFaceFixIcons = _captureFaceFixIcons,
                captureWithNDMF = _captureWithNDMF,
                setupModularAvatar = _setupModularAvatar,
            };
            CopyClips(_clipsLeft, s.clipsLeft);
            CopyClips(_clipsRight, s.clipsRight);
            CopyClips(_clipsEither, s.clipsEither);
            CopyClips(_faceFixClips, s.faceFixClips);
            for (var i = 0; i < FaceFixSlotCount; i++)
            {
                s.faceFixNames[i] = _faceFixNames[i];
            }

            foreach (var preset in _presets)
            {
                var data = new yulEmoSettingsSnapshot.PresetData { name = preset.Name };
                CopyClips(preset.Left, data.left);
                CopyClips(preset.Right, data.right);
                CopyClips(preset.Either, data.either);
                s.presets.Add(data);
            }

            foreach (var rule in _disableRules)
            {
                s.disableRules.Add(new yulEmoSettingsSnapshot.DisableRuleData
                {
                    name = rule.Name,
                    type = (int)rule.Type,
                    condition = (int)rule.Condition,
                    value = rule.Value,
                });
            }

            s.originalFacialClips.AddRange(_originalFacialClips);
            return s;
        }

        /// <summary>保存された設定を、ウィンドウに反映する。</summary>
        private void ApplySnapshot(yulEmoSettingsSnapshot s)
        {
            _handMode = (yulEmoGenerator.HandMode)s.handMode;
            _writeDefaults = s.writeDefaults;
            _idleClip = s.idleClip;
            _useGestureWeight = s.useGestureWeight;
            _useTrackingControl = s.useTrackingControl;
            _flattenClips = s.flattenClips;
            _useGestureFaceMenu = s.useGestureFaceMenu;
            _useFaceFix = s.useFaceFix;
            _resetOriginalFace = s.resetOriginalFace;
            _excludeLipSyncAndBlink = s.excludeLipSyncAndBlink;
            if (!string.IsNullOrEmpty(s.outputFolder)) _outputFolder = s.outputFolder;
            _controllerName = s.controllerName ?? string.Empty;
            _captureFaceFixIcons = s.captureFaceFixIcons;
            _captureWithNDMF = s.captureWithNDMF;
            _setupModularAvatar = s.setupModularAvatar;

            CopyClips(s.clipsLeft, _clipsLeft);
            CopyClips(s.clipsRight, _clipsRight);
            CopyClips(s.clipsEither, _clipsEither);
            CopyClips(s.faceFixClips, _faceFixClips);
            for (var i = 0; i < FaceFixSlotCount; i++)
            {
                var name = s.faceFixNames != null && i < s.faceFixNames.Length ? s.faceFixNames[i] : null;
                _faceFixNames[i] = string.IsNullOrEmpty(name) ? (i + 1).ToString() : name;
            }

            _presets.Clear();
            foreach (var data in s.presets)
            {
                var entry = new PresetEntry { Name = data.name ?? string.Empty };
                CopyClips(data.left, entry.Left);
                CopyClips(data.right, entry.Right);
                CopyClips(data.either, entry.Either);
                _presets.Add(entry);
            }

            _disableRules.Clear();
            foreach (var data in s.disableRules)
            {
                _disableRules.Add(new DisableRuleEntry
                {
                    Name = data.name ?? string.Empty,
                    Type = (yulEmoGenerator.DisableParameterType)data.type,
                    Condition = (yulEmoGenerator.DisableCondition)data.condition,
                    Value = data.value,
                });
            }

            _originalFacialClips.Clear();
            _originalFacialClips.AddRange(s.originalFacialClips);
        }

        /// <summary>
        /// 作成済みの「21Emo」オブジェクトから設定を読み込む。
        /// 作成時の設定が保存されていればそれを使い、無い（古いバージョンで作成した）ときは、
        /// コントローラーとメニューから読み取れる範囲を読み込む。
        /// </summary>
        private void LoadFromEmoObject(yulEmoNDMFComponent component, bool showDialog)
        {
            if (component == null) return;

            string message;
            if (component.hasSettings && component.settings != null)
            {
                ApplySnapshot(component.settings);
                message = "「21Emo」オブジェクトに保存された、作成時の設定を読み込みました。";
            }
            else
            {
                var merge = component.GetComponent<ModularAvatarMergeAnimator>();
                var controller = merge != null ? merge.animator as AnimatorController : null;
                if (controller == null)
                {
                    EditorUtility.DisplayDialog("21Emo", "設定を読み込めませんでした。\n作成時の設定が保存されておらず、Merge Animator にコントローラーも設定されていません。", "OK");
                    return;
                }

                AnalyzeAndImportController(controller, false);
                ApplyMenuNames(component.transform);
                message = "作成時の設定が保存されていなかったため、コントローラーとメニューから読み取りました。\n"
                    + "ジェスチャー・プリセット・FaceFix のクリップとメニュー名は読み込みましたが、無効化ルールなどの細かい設定は読み込めません。";
            }

            Repaint();
            if (showDialog)
            {
                EditorUtility.DisplayDialog("21Emo", message, "OK");
            }
        }

        /// <summary>メニューのトグルの名前から、プリセット名と FaceFix のメニュー名を読み取る（古いバージョンで作成したとき用）。</summary>
        private void ApplyMenuNames(Transform emoRoot)
        {
            // プリセット名: FacePreset パラメータのトグル（値 1〜N）
            foreach (var item in emoRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item.Control == null || item.Control.parameter == null) continue;
                if (item.Control.parameter.name != yulEmoGenerator.FacePresetParameter) continue;

                var index = Mathf.RoundToInt(item.Control.value) - 1;
                if (index >= 0 && index < _presets.Count)
                {
                    _presets[index].Name = item.gameObject.name;
                }
            }

            // FaceFix のメニュー名: 直下の FaceFix トグルを、クリップが設定されているスロットの順に割り当てる
            var slots = new List<int>();
            for (var i = 0; i < FaceFixSlotCount; i++)
            {
                if (_faceFixClips[i] != null) slots.Add(i);
            }

            var k = 0;
            foreach (Transform child in emoRoot)
            {
                var item = child.GetComponent<ModularAvatarMenuItem>();
                if (item == null || item.Control == null || item.Control.parameter == null) continue;
                if (item.Control.parameter.name != yulEmoModularAvatarSetup.FaceFixParameter) continue;
                if (item.Control.type != VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle) continue;

                if (k < slots.Count) _faceFixNames[slots[k]] = child.name;
                k++;
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
            _originalFacialClips.Clear();
            if (!usedFallback)
            {
                var originalClips = new HashSet<AnimationClip>();
                foreach (var layer in candidateLayers)
                {
                    CollectMotionClips(layer.stateMachine, originalClips);
                }
                _originalFacialClips.AddRange(originalClips);
            }

            var leftStates = new AnimatorState[GestureCount];
            var rightStates = new AnimatorState[GestureCount];
            var faceFixClips = new AnimationClip[FaceFixSlotCount];
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
            var leftClips = new AnimationClip[GestureCount];
            var rightClips = new AnimationClip[GestureCount];
            for (var i = 0; i < GestureCount; i++)
            {
                leftClips[i] = leftStates[i] != null ? leftStates[i].motion as AnimationClip : null;
                rightClips[i] = rightStates[i] != null ? rightStates[i].motion as AnimationClip : null;
            }

            // Both / Either の判定:
            // 左右の両方にクリップがあり、かつ「異なるアニメーションファイル」であるジェスチャーが
            // 1つでもあれば Both。ステートが別でも、遷移先のクリップが左右で完全に同じなら Either とみなす。
            bool isBoth = false;
            for (var i = 0; i < GestureCount; i++)
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
                var presetLeft = new AnimationClip[GestureCount];
                var presetRight = new AnimationClip[GestureCount];
                for (var i = 0; i < GestureCount; i++)
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

            // UI に適用
            _handMode = isBoth ? yulEmoGenerator.HandMode.Both : yulEmoGenerator.HandMode.Either;

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

            // 検出したプリセットで「3. ジェスチャープリセット」を置き換える（検出できたときだけ）
            if (presetClipSets.Count > 0)
            {
                _presets.Clear();
                for (var p = 0; p < presetClipSets.Count; p++)
                {
                    var entry = new PresetEntry { Name = $"Preset {p + 1}" };
                    for (var i = 0; i < GestureCount; i++)
                    {
                        entry.Left[i] = presetClipSets[p].Left[i];
                        entry.Right[i] = presetClipSets[p].Right[i];
                        entry.Either[i] = presetClipSets[p].Left[i] ?? presetClipSets[p].Right[i];
                    }
                    _presets.Add(entry);
                }
            }

            if (showSuccessDialog)
            {
                var modeText = isBoth ? "両手用 (Both)" : "片手用 (Either)";
                var dialogText = $"アバターの FX レイヤーからアニメーションを読み込みました。\n判定モード: {modeText}";
                if (presetClipSets.Count > 0)
                {
                    dialogText += $"\n\nジェスチャーとは別の条件で切り替わるプリセットを検出しました。\n{presetContext.Describe()}\n"
                        + "「3. ジェスチャープリセット」に読み込みました（名前は変更できます）。\n"
                        + "※ 生成されるアニメーターは FacePreset パラメータで切り替わります。元のメニューの切り替えは使われません。";
                }
                EditorUtility.DisplayDialog("21Emo", dialogText, "OK");
            }
        }

        private static void CollectMotionClips(AnimatorStateMachine sm, HashSet<AnimationClip> clips)
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

        private static void CollectMotionClips(Motion motion, HashSet<AnimationClip> clips)
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

        private static bool IsPresetStateName(string stateName)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(stateName, @"^P\d+_");
        }

        /// <summary>FacePreset が「基本以外」（Bool: True / Int: 0 以外）を要求する遷移かどうか。</summary>
        private static bool IsPresetTransition(AnimatorStateTransition transition)
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
        private static readonly HashSet<string> NonSelectorParameters = new HashSet<string>
        {
            "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight", "FaceFix", "FaceLock",
        };

        /// <summary>プリセット 1 つ（基本以外の 1 つの値）分の、左右のジェスチャーのステート。</summary>
        private sealed class PresetStateSet
        {
            public readonly AnimatorState[] Left = new AnimatorState[GestureCount];
            public readonly AnimatorState[] Right = new AnimatorState[GestureCount];
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

        private static void CollectAllTransitions(AnimatorStateMachine sm, List<AnimatorStateTransition> list)
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
        private static bool TryGetGestureValues(AnimatorStateTransition transition, out int gestureLeft, out int gestureRight)
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

            return (gestureLeft >= 1 && gestureLeft <= GestureCount) || (gestureRight >= 1 && gestureRight <= GestureCount);
        }

        /// <summary>
        /// 遷移が、パラメータ selector についてどのプリセットの値を要求しているか。
        /// Bool: If = 1 / IfNot = 0。Int: Equals の値。条件が無い（または読み取れない）ときは 0（基本）。
        /// </summary>
        private static int GetPresetSetValue(AnimatorStateTransition transition, string selector)
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

        private static bool IsSelectorCondition(AnimatorCondition cond)
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
        private static PresetImportContext DetectOriginalPresets(List<AnimatorControllerLayer> layers)
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
                    set = new PresetStateSet();
                    context.Sets[setValue] = set;
                }
                if (gLeft >= 1 && gLeft <= GestureCount) set.Left[gLeft - 1] = dest;
                if (gRight >= 1 && gRight <= GestureCount) set.Right[gRight - 1] = dest;
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

        private static void CollectStatesFromStateMachine(
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
                if (gLeft >= 1 && gLeft <= 7)
                {
                    leftStates[gLeft - 1] = dest;
                }

                if (gRight >= 1 && gRight <= 7)
                {
                    rightStates[gRight - 1] = dest;
                }

                // FaceFix 1〜8
                // ジェスチャ表情メニューを使って作られたコントローラーでは、FaceFix の値がずれている。
                // ステート名はスロット番号（"1"〜"8"）なので、名前で読み取れるときはそちらを優先する。
                var fixSlot = fix;
                if (fix >= 1 && int.TryParse(dest.name, out var namedSlot) && namedSlot >= 1 && namedSlot <= FaceFixSlotCount)
                {
                    fixSlot = namedSlot;
                }
                if (fixSlot >= 1 && fixSlot <= FaceFixSlotCount)
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

        /// <summary>「アバター名_21Facial_日時」の形式で、作成のたびに別になるコントローラー名を作る。</summary>
        private string BuildAutoControllerName()
        {
            var avatarName = _avatarObject != null ? _avatarObject.name : string.Empty;
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                avatarName = avatarName.Replace(invalid, '_');
            }
            avatarName = avatarName.Trim();

            var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return string.IsNullOrEmpty(avatarName)
                ? $"21Facial_{stamp}"
                : $"{avatarName}_21Facial_{stamp}";
        }

        private void Generate()
        {
            var controllerName = string.IsNullOrWhiteSpace(_controllerName)
                ? BuildAutoControllerName()
                : _controllerName.Trim();

            if (string.IsNullOrWhiteSpace(_outputFolder) || !_outputFolder.StartsWith("Assets"))
            {
                EditorUtility.DisplayDialog("21Emo", "保存先フォルダは Assets 以下のパスで指定してください。", "OK");
                return;
            }

            EnsureFolderExists(_outputFolder.TrimEnd('/'));

            var assetPath = $"{_outputFolder.TrimEnd('/')}/{controllerName}.controller";

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

            // 最終フレームだけのクリップを作る場合は、専用フォルダを作り直してから BuildConfig で差し替える
            _flattenCache.Clear();
            _flattenFolder = null;
            if (_flattenClips)
            {
                _flattenFolder = $"{_outputFolder.TrimEnd('/')}/{controllerName}_Clips";
                if (AssetDatabase.IsValidFolder(_flattenFolder))
                {
                    AssetDatabase.DeleteAsset(_flattenFolder);
                }
                EnsureFolderExists(_flattenFolder);
            }

            var config = BuildConfig();

            string iconMessage = null;

            yulEmoGenerator.Generate(config, assetPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var result = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (result != null)
            {
                EditorGUIUtility.PingObject(result);
                Selection.activeObject = result;
            }

            var dialogMessage = $"作成が完了しました。\n{assetPath}";

            // Modular Avatar のセットアップ（Merge Animator + Menu Installer + Menu Item）
            if (_setupModularAvatar && _avatarObject != null)
            {
                var iconFolderPath = $"{_outputFolder.TrimEnd('/')}/{controllerName}_Icons";
                var setupOptions = new yulEmoModularAvatarSetup.NDMFOptions
                {
                    IconFolder = iconFolderPath,
                    Camera = yulEmoCameraSettings.Current,
                    CaptureWithNDMF = _captureWithNDMF,
                    Snapshot = BuildSnapshot(),
                };
                if (yulEmoModularAvatarSetup.TrySetup(_avatarObject, result, config, out var maMessage, setupOptions))
                {
                    dialogMessage += $"\n\n{maMessage}";

                    // FaceFix のメニューアイコンを撮影して設定する。
                    // 新しいコントローラーを組み込んだ後に撮影するので、NDMF の最適化でシェイプキーが消えない。
                    // クリップは FaceFix の値の順（ジェスチャ表情が先、その後ろに FaceFix のスロット 1〜8）
                    var iconClips = yulEmoGenerator.GetFixClipSlots(config);
                    var hasIconClip = System.Array.Exists(iconClips, c => c != null);
                    if (_captureFaceFixIcons && hasIconClip)
                    {
                        var iconFolder = iconFolderPath;
                        EnsureFolderExists(iconFolder);
                        var icons = yulEmoIconCapturer.CaptureFaceFixIcons(
                            _avatarObject, iconClips, iconFolder, yulEmoCameraSettings.Current, _captureWithNDMF, out iconMessage);
                        yulEmoModularAvatarSetup.ApplyFaceFixIcons(_avatarObject, icons);
                    }
                    else if (_captureFaceFixIcons)
                    {
                        iconMessage = "アイコンの撮影を行いませんでした（「4. FaceFixオプション」で FaceFix またはジェスチャ表情メニューを使用する設定になっていない、またはクリップが設定されていません）。";
                        Debug.LogWarning("[21Emo] アイコン撮影: " + iconMessage);
                    }
                }
                else
                {
                    dialogMessage += $"\n\n※ Modular Avatar のセットアップに失敗しました。\n{maMessage}";
                }
            }

            if (!string.IsNullOrEmpty(iconMessage))
            {
                dialogMessage += $"\n\n※ {iconMessage}";
            }

            EditorUtility.DisplayDialog("21Emo", dialogMessage, "OK");
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

        private yulEmoGenerator.Config BuildConfig()
        {
            var gestures = MakeGestureDefs(_handMode, _clipsLeft, _clipsRight, _clipsEither);

            // プリセット（クリップが 1 つも無いものは Generator 側で無視される）
            var presetDefs = new List<yulEmoGenerator.GesturePreset>();
            for (var i = 0; i < _presets.Count; i++)
            {
                var entry = _presets[i];
                var presetName = string.IsNullOrWhiteSpace(entry.Name) ? $"Preset {i + 1}" : entry.Name.Trim();
                presetDefs.Add(new yulEmoGenerator.GesturePreset(
                    presetName,
                    MakeGestureDefs(_handMode, entry.Left, entry.Right, entry.Either)));
            }

            // Idle が空の場合は強制的に WD オン
            var actualWriteDefaults = _idleClip == null ? true : _writeDefaults;

            // デフォルト表情レイヤー用のシェイプキー収集
            yulEmoGenerator.DefaultFaceBinding[] defaultFace = null;
            if (_resetOriginalFace && _avatarObject != null)
            {
                var targetClips = new List<AnimationClip>(_originalFacialClips) { _idleClip };
                if (_handMode == yulEmoGenerator.HandMode.Both)
                {
                    targetClips.AddRange(_clipsLeft);
                    targetClips.AddRange(_clipsRight);
                }
                else
                {
                    targetClips.AddRange(_clipsEither);
                }
                foreach (var entry in _presets)
                {
                    if (_handMode == yulEmoGenerator.HandMode.Both)
                    {
                        targetClips.AddRange(entry.Left);
                        targetClips.AddRange(entry.Right);
                    }
                    else
                    {
                        targetClips.AddRange(entry.Either);
                    }
                }
                if (_useFaceFix)
                {
                    targetClips.AddRange(_faceFixClips);
                }

                defaultFace = yulEmoDefaultFaceBuilder.Build(_avatarObject, targetClips, _excludeLipSyncAndBlink);
            }

            return new yulEmoGenerator.Config
            {
                HandMode         = _handMode,
                WriteDefaults    = actualWriteDefaults,
                UseGestureWeight = _useGestureWeight,
                IdleClip         = Flat(_idleClip),
                Gestures         = gestures,
                Presets          = presetDefs.ToArray(),
                UseFaceFix       = _useFaceFix,
                FaceFixClips     = System.Array.ConvertAll(_faceFixClips, Flat),
                FaceFixNames     = (string[])_faceFixNames.Clone(),
                UseGestureFaceMenu = _useGestureFaceMenu && _setupModularAvatar && _avatarObject != null,
                DefaultFace      = defaultFace,
                UseTrackingControl = _useTrackingControl,
                DisableRules     = BuildDisableRules(),
            };
        }
    }
}
#endif
