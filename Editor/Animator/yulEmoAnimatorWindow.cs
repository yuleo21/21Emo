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
    public class yulEmoAnimatorWindow : EditorWindow
    {
        private const int FaceFixSlotCount = 8;
        private const int GestureCount = 7;

        // ウィンドウの入力状態は SerializeField で保持する
        // ドメインリロード（再コンパイルやインポート）で編集中の内容が飛ばないようにするため
        // readonly のフィールドは Unity がシリアライズしないので、保持したいものには付けない

        // アバター読み込み用
        [SerializeField] private GameObject _avatarObject;
        private GameObject _lastLoadedAvatar;

        // 手のモード & WD設定
        [SerializeField] private yulEmoGenerator.HandMode _handMode = yulEmoGenerator.HandMode.Both;
        [SerializeField] private bool _writeDefaults = true;

        // Idle（待機用）
        [SerializeField] private AnimationClip _idleClip;

        // Both モード用: 左手・右手それぞれのクリップ
        [SerializeField] private AnimationClip[] _clipsLeft  = new AnimationClip[GestureCount];
        [SerializeField] private AnimationClip[] _clipsRight = new AnimationClip[GestureCount];

        // Either モード用: 左右共通クリップ
        [SerializeField] private AnimationClip[] _clipsEither = new AnimationClip[GestureCount];

        [SerializeField] private bool _useGestureWeight;

        // ジェスチャー表情のプリセット（基本の表情とは別に、FacePreset で切り替える表情セット）
        [Serializable]
        private sealed class PresetEntry
        {
            public string Name = "";
            public bool Foldout = true;
            public AnimationClip[] Left   = new AnimationClip[GestureCount];
            public AnimationClip[] Right  = new AnimationClip[GestureCount];
            public AnimationClip[] Either = new AnimationClip[GestureCount];
        }

        [SerializeField] private List<PresetEntry> _presets = new List<PresetEntry>();

        // ジェスチャ表情メニュー（Idle 以外の各ジェスチャの表情を、FaceFix パラメータで固定するメニュー）
        [SerializeField] private bool _useGestureFaceMenu;

        [SerializeField] private bool _useFaceFix;
        [SerializeField] private AnimationClip[] _faceFixClips = new AnimationClip[FaceFixSlotCount];
        // FaceFix のメニュー表示名（既定は番号）
        [SerializeField] private string[] _faceFixNames = { "1", "2", "3", "4", "5", "6", "7", "8" };

        [SerializeField] private string _outputFolder = "Assets/21Emo/Generated";
        // 空欄のときは「アバター名_21Facial_日時」を自動で付ける（作成のたびに別の名前になる）
        [SerializeField] private string _controllerName = string.Empty;

        // ジェスチャー表情ステートで Eyes & Eyelids を Animation にする (VRCAnimatorTrackingControl)
        [SerializeField] private bool _useTrackingControl = true;

        // Idle を経由せず、Any State から各表情へ直接遷移する
        [SerializeField] private bool _skipIdle;

        // 表情クリップを「最終フレームだけの 0 フレームアニメーション」に差し替えて使う
        [SerializeField] private bool _flattenClips;
        private string _flattenFolder;
        private readonly Dictionary<AnimationClip, AnimationClip> _flattenCache = new Dictionary<AnimationClip, AnimationClip>();

        // 表情変更を無効にするルール（複数可。いずれかが成立している間は無効）
        [Serializable]
        private sealed class DisableRuleEntry
        {
            public string Name = "";
            public yulEmoGenerator.DisableParameterType Type = yulEmoGenerator.DisableParameterType.Bool;
            public yulEmoGenerator.DisableCondition Condition = yulEmoGenerator.DisableCondition.True;
            public float Value;
        }

        // 表情を強制的に Idle にするルール
        [SerializeField] private List<DisableRuleEntry> _disableRules = new List<DisableRuleEntry>();

        // 表情変更を無効にする（Exit の条件に加える）ルール
        [SerializeField] private List<DisableRuleEntry> _freezeRules = new List<DisableRuleEntry>();

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
        [SerializeField] private bool _resetOriginalFace = true;
        [SerializeField] private bool _excludeLipSyncAndBlink = true;
        // アバターから読み込んだ際に検出した、元の表情レイヤーのクリップ
        [SerializeField] private List<AnimationClip> _originalFacialClips = new List<AnimationClip>();

        // FaceFix のメニューアイコンを、アニメーター作成時に撮影して設定する
        [SerializeField] private bool _captureFaceFixIcons = true;
        // 撮影用に複製したアバターへ NDMF の全プラグインを適用する（ビルド時と同じ状態で撮影）
        [SerializeField] private bool _captureWithNDMF = true;

        // 生成後に Modular Avatar (Merge Animator / Menu Installer / Menu Item) を自動セットアップするか
        [SerializeField] private bool _setupModularAvatar = true;

        [SerializeField] private Vector2 _scroll;

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
            var window = GetWindow<yulEmoAnimatorWindow>("21Emo");
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

            _skipIdle = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Idle を挟まずに表情を切り替える（Any State から直接遷移）",
                    "オフ（標準）: Entry → Idle → 各表情 → Exit（→ Idle）\n" +
                    "オン: 各表情へ Any State から直接遷移します。ある表情から別の表情へ、Idle を経由せずに切り替わります。\n" +
                    "・何も押していないとき（ジェスチャー 0）は Exit → Idle に戻ります。\n" +
                    "・FaceFix で使っている値のときは、ジェスチャー表情へ遷移しません（FaceFix が優先されます）。\n" +
                    "・Both のとき左右のジェスチャーが同時に出たら、左が優先されます（単一レイヤーでは同時に 1 つの表情しか持てないため）。\n" +
                    "・FaceLock / 無効化ルール / FacePreset の条件は標準と同じように働きます。"),
                _skipIdle);

            _flattenClips = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "クリップを最終フレームだけの 0 フレームアニメーションに差し替える",
                    "Idle・ジェスチャー・プリセット・FaceFix のクリップごとに、最終フレームの値だけを持つ新しいクリップを作り、\n" +
                    "生成するコントローラーではそちらを使います。元のクリップは変更しません。\n" +
                    "新しいクリップは「保存先フォルダ/コントローラー名_Clips」に保存されます（作り直すたびに中身は入れ替わります）。\n" +
                    "既に 1 フレームだけのクリップは、そのまま使います。"),
                _flattenClips);

            EditorGUILayout.Space(4);
            DrawRuleList(
                _disableRules,
                "表情を強制的にIdleにするパラメータ",
                "いずれかの条件が成立している間は、Idle からジェスチャーステートへ遷移せず、\n" +
                "ジェスチャーステートにいる場合も Idle へ戻されます（OR 条件）。\n" +
                "※ パラメータの宣言（Expression Parameters 等）は行いません。\n" +
                "※ Float は Animator の仕様上 Greater / Less のみです。しきい値とちょうど同じ値のときは、\n" +
                "   「成立していない」側として扱います。",
                "※ 未設定の場合、強制的に Idle にする処理は行いません。");

            EditorGUILayout.Space(4);
            DrawRuleList(
                _freezeRules,
                "表情変更を無効にするパラメータ",
                "ここで指定した条件が成立している間は、ジェスチャーの表情ステートから Exit する遷移に\n" +
                "「その条件が成立しない」という条件が追加され、表情が切り替わらなくなります（表情が固定されます）。\n" +
                "例: AllowFaceChange を「Bool / False」にすると、Exit の条件は\n" +
                "「GestureRight != 値」に加えて「AllowFaceChange == True」になります。\n" +
                "複数指定したときは、どれか 1 つでも成立している間は固定されます。\n" +
                "※ パラメータの宣言（Expression Parameters 等）は行いません。",
                "※ 未設定の場合、表情の固定は行いません。");
        }

        // ─────────────────────────────────────────────────────────────
        // パラメータ条件のリスト（複数・型/条件指定）
        // ─────────────────────────────────────────────────────────────
        private void DrawRuleList(List<DisableRuleEntry> rules, string title, string tooltip, string emptyNote)
        {
            EditorGUILayout.LabelField(new GUIContent(title, tooltip), EditorStyles.boldLabel);

            var removeIndex = -1;
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];

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
                rules.RemoveAt(removeIndex);
            }

            if (GUILayout.Button("＋ パラメータを追加", GUILayout.Width(160)))
            {
                rules.Add(new DisableRuleEntry());
            }

            if (rules.Count == 0)
            {
                EditorGUILayout.LabelField(emptyNote, EditorStyles.miniLabel);
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

        private static yulEmoGenerator.DisableRule[] BuildDisableRules(List<DisableRuleEntry> entries)
        {
            var rules = new List<yulEmoGenerator.DisableRule>();
            foreach (var entry in entries)
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
                        "・FaceFix パラメータを流用し、1 番からジェスチャ表情に割り当てます。下の FaceFix の値はその分だけ後ろにずれます。\n" +
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

            var descriptor = yulEmoEditorUtility.FindDescriptor(avatar);
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
                skipIdle = _skipIdle,
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

            foreach (var rule in _freezeRules)
            {
                s.freezeRules.Add(new yulEmoSettingsSnapshot.DisableRuleData
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
            _skipIdle = s.skipIdle;
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

            _freezeRules.Clear();
            if (s.freezeRules != null)
            {
                foreach (var data in s.freezeRules)
                {
                    _freezeRules.Add(new DisableRuleEntry
                    {
                        Name = data.name ?? string.Empty,
                        Type = (yulEmoGenerator.DisableParameterType)data.type,
                        Condition = (yulEmoGenerator.DisableCondition)data.condition,
                        Value = data.value,
                    });
                }
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

            // 他の場所と同じ探し方にそろえる（親 → 子）。アバター配下のオブジェクトを渡されても見つかるようになる
            var descriptor = yulEmoEditorUtility.FindDescriptor(avatarObj);

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

            // 解析そのものは yulEmoControllerImporter が行い、ここでは結果を画面の状態へ反映する
            var result = yulEmoControllerImporter.Analyze(
                controller,
                System.Array.ConvertAll(GestureInfos, info => info.Name),
                GestureCount,
                FaceFixSlotCount);
            if (result == null) return;

            _originalFacialClips.Clear();
            _originalFacialClips.AddRange(result.OriginalFacialClips);

            _handMode = result.HandMode;

            if (result.IdleClip != null)
            {
                _idleClip = result.IdleClip;
            }

            for (var i = 0; i < GestureCount; i++)
            {
                _clipsLeft[i] = result.Left[i];
                _clipsRight[i] = result.Right[i];
                _clipsEither[i] = result.Left[i] ?? result.Right[i];
            }

            bool hasFaceFix = false;
            for (var i = 0; i < FaceFixSlotCount; i++)
            {
                _faceFixClips[i] = result.FaceFixClips[i];
                if (result.FaceFixClips[i] != null)
                {
                    hasFaceFix = true;
                }
            }
            _useFaceFix = hasFaceFix;

            // 検出したプリセットで「3. ジェスチャープリセット」を置き換える（検出できたときだけ）
            if (result.Presets.Count > 0)
            {
                _presets.Clear();
                for (var p = 0; p < result.Presets.Count; p++)
                {
                    var entry = new PresetEntry { Name = $"Preset {p + 1}" };
                    for (var i = 0; i < GestureCount; i++)
                    {
                        entry.Left[i] = result.Presets[p].Left[i];
                        entry.Right[i] = result.Presets[p].Right[i];
                        entry.Either[i] = result.Presets[p].Left[i] ?? result.Presets[p].Right[i];
                    }
                    _presets.Add(entry);
                }
            }

            if (showSuccessDialog)
            {
                var modeText = result.HandMode == yulEmoGenerator.HandMode.Both ? "両手用 (Both)" : "片手用 (Either)";
                var dialogText = $"アバターの FX レイヤーからアニメーションを読み込みました。\n判定モード: {modeText}";
                if (result.Presets.Count > 0)
                {
                    dialogText += $"\n\nジェスチャーとは別の条件で切り替わるプリセットを検出しました。\n{result.PresetDescription}\n"
                        + "「3. ジェスチャープリセット」に読み込みました（名前は変更できます）。\n"
                        + "※ 生成されるアニメーターは FacePreset パラメータで切り替わります。元のメニューの切り替えは使われません。";
                }
                EditorUtility.DisplayDialog("21Emo", dialogText, "OK");
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

        /// <summary>アバター直下に、セットアップ済みの「21Emo」オブジェクトがあるか（TrySetup が作り直す対象と同じ判定）。</summary>
        private static bool HasExistingEmoObject(GameObject avatar)
        {
            if (avatar == null) return false;

            var descriptor = yulEmoEditorUtility.FindDescriptor(avatar);
            if (descriptor == null) return false;

            return descriptor.transform.Find(yulEmoModularAvatarSetup.RootObjectName) != null;
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

            // 既にアバターに「21Emo」オブジェクトがある場合は、作り直す（上書きする）前に確認する。
            // コントローラーを作る前に聞くので、キャンセルしても何も変更されない。
            if (_setupModularAvatar && _avatarObject != null && HasExistingEmoObject(_avatarObject))
            {
                var overwriteEmo = EditorUtility.DisplayDialog(
                    "21Emo",
                    $"このアバターには既に「{yulEmoModularAvatarSetup.RootObjectName}」オブジェクトがあります。\n" +
                    "上書きすると、「21Emo」オブジェクトを削除して作り直します。\n" +
                    "（手で追加したコンポーネントやメニューの変更、手動で設定したアイコンは失われます。Undo で元に戻せます）\n\n" +
                    "上書きしますか？",
                    "上書きする",
                    "キャンセル");
                if (!overwriteEmo) return;
            }

            // コントローラー名の検証は、削除や生成より前に済ませる
            // ファイル名に使えない文字（/ や : など）が混ざると、旧コントローラーを消した後に作成だけ失敗する形になるため
            if (controllerName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                EditorUtility.DisplayDialog("21Emo", $"コントローラー名「{controllerName}」に、ファイル名として使えない文字が含まれています。", "OK");
                return;
            }

            yulEmoEditorUtility.EnsureFolder(_outputFolder.TrimEnd('/'));

            var assetPath = $"{_outputFolder.TrimEnd('/')}/{controllerName}.controller";

            var replacing = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null;
            if (replacing)
            {
                var overwrite = EditorUtility.DisplayDialog(
                    "21Emo",
                    $"{assetPath} は既に存在します。上書きしますか？",
                    "上書きする",
                    "キャンセル");
                if (!overwrite) return;
                // ここではまだ消さない。生成が成功してから置き換える
            }

            // 最終フレームだけのクリップを作る場合は、専用フォルダ内の同名クリップを BuildConfig で差し替える
            // フォルダごと消すと、ユーザーが置いた別のアセットまで巻き込んで消えるので、フォルダ削除はしない
            // 同名クリップの上書き・再利用は ClipFlattener 側で行う
            _flattenCache.Clear();
            _flattenFolder = null;
            if (_flattenClips)
            {
                _flattenFolder = $"{_outputFolder.TrimEnd('/')}/{controllerName}_Clips";
                yulEmoEditorUtility.EnsureFolder(_flattenFolder);
            }

            // 生成は一時パスで先に行い、成功したら既存のコントローラーと置き換える
            // 途中で例外が出ても、既存のコントローラーはそのまま残る
            var tempPath = $"{_outputFolder.TrimEnd('/')}/{controllerName}_tmp_{System.Guid.NewGuid().ToString("N").Substring(0, 8)}.controller";

            yulEmoGenerator.Config config = null;
            string iconMessage = null;
            try
            {
                config = BuildConfig();
                yulEmoGenerator.Generate(config, tempPath);
            }
            catch (System.Exception e)
            {
                AssetDatabase.DeleteAsset(tempPath);
                Debug.LogException(e);
                EditorUtility.DisplayDialog("21Emo", $"作成に失敗しました。既存のコントローラーは変更していません。\n\n{e.Message}", "OK");
                return;
            }

            if (replacing)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
            var moveError = AssetDatabase.MoveAsset(tempPath, assetPath);
            if (!string.IsNullOrEmpty(moveError))
            {
                // 置き換えに失敗した場合は、生成済みのファイルを消さずに場所を知らせる
                EditorUtility.DisplayDialog("21Emo", $"コントローラーの置き換えに失敗しました。\n生成したファイルは一時ファイルとして残しています。\n{tempPath}\n\n{moveError}", "OK");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var result = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (result != null)
            {
                // 一時ファイル名がアセット名に残る場合は、本来の名前に戻す
                if (result.name != controllerName)
                {
                    result.name = controllerName;
                    EditorUtility.SetDirty(result);
                }
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
                        yulEmoEditorUtility.EnsureFolder(iconFolder);
                        var icons = yulEmoIconCapturer.CaptureFaceFixIcons(
                            _avatarObject, iconClips, iconFolder, yulEmoCameraSettings.Current, _captureWithNDMF, out iconMessage);
                        yulEmoModularAvatarSetup.ApplyFaceFixIcons(_avatarObject, icons);
                    }
                    else if (_captureFaceFixIcons)
                    {
                        iconMessage = "アイコンを撮影しませんでした（「4. FaceFixオプション」で FaceFix またはジェスチャ表情メニューを使用する設定になっていないか、クリップが設定されていません）。";
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
                SkipIdle         = _skipIdle,
                DisableRules     = BuildDisableRules(_disableRules),
                FreezeRules      = BuildDisableRules(_freezeRules),
            };
        }
    }
}
#endif
