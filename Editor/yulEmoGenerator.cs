#if UNITY_EDITOR
using System.Collections.Generic;
using AnimatorAsCode.V1;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace yulEmo
{
    /// <summary>
    /// Animator As Code (AacV1) を使って、21Emo の表情アニメーターコントローラーを
    /// 実際に生成する処理。UI (yulEmoWindow) からのみ呼び出される想定。
    ///
    /// 生成されるパラメータ:
    ///   GestureLeft  (Int)  VRChat標準パラメータ。左手のジェスチャー(0-7)。
    ///   GestureRight (Int)  VRChat標準パラメータ。右手のジェスチャー(0-7)。
    ///   FaceFix      (Int)  固定表情の選択(0=なし, 1-8=対応する表情)。
    ///   FaceLock     (Bool) 表情固定中などの制御用パラメータ。
    ///   FacePreset   (Bool / Int) ジェスチャー表情のプリセット切り替え。
    ///                  追加プリセットが 1 個のとき Bool（False=基本, True=プリセット1）。
    ///                  2 個以上のとき Int（0=基本, 1〜N=各プリセット）。0 個のときは作らない。
    ///
    /// レイヤー名は "21Facial" に固定される。
    ///
    /// 遷移方式:
    ///   ジェスチャーステート: Entry/Exit 方式
    ///     - Entry → Idle（デフォルトステート）
    ///     - Idle → 各ジェスチャーステート（条件: Gesture == value && FaceLock == false）
    ///     - 各ジェスチャーステート → Exit（条件: Gesture != value && FaceLock == false, Duration: 0.1s）
    ///     - Exit → 自動的に Entry (Idle) へ復帰
    ///   表情変更の無効化ルール（任意・複数可。型 Bool / Int / Float と条件を指定）:
    ///     - いずれかのルールが成立している間は表情変更を無効にする（OR）
    ///     - Idle → 各ジェスチャーステートの条件に、全ルールの「成立しない」条件を追加（AND）
    ///     - 各ジェスチャーステート → Exit に、ルールごとに「成立」の遷移を追加（FaceLock 条件なし）
    ///   ジェスチャープリセット:
    ///     - 基本のジェスチャー表情と、各プリセットのジェスチャー表情で、それぞれ別のステート群を作る
    ///     - Idle → 各ジェスチャーステートの条件に FacePreset の条件を追加（基本=0/False, プリセットN=N/True）
    ///   FaceFix ステート (1〜8):
    ///     - Any State → 各 FaceFix ステート（条件: FaceFix == value, Duration: 0.1s） ※FaceLock条件は不要
    ///     - 各 FaceFix ステート → Exit（条件: FaceFix != value, Duration: 0.1s）
    ///     - 各 FaceFix ステートに Parameter Driver を付け、FaceLock を False にする
    ///   ジェスチャ表情メニュー（Config.UseGestureFaceMenu）:
    ///     - FaceFix パラメータを流用する。値はジェスチャ表情が 1 から先に使い、既存の FaceFix スロット（1〜8）はその後ろにずれる
    ///       （GetGestureFaceEntries / GetGestureFaceOffset 参照）
    ///     - Idle → 各ジェスチャ表情ステート（条件: FaceFix == value [&& 無効化ルールが全て不成立], Duration: 0.1s）
    ///     - 各ジェスチャ表情ステート → Exit（条件: FaceFix != value, Duration: 0.1s）
    ///     - 無効化ルールが成立したら Exit（→ Idle）
    ///     - 各ジェスチャ表情ステートに Parameter Driver を付け、FaceLock を False にする
    /// </summary>
    public static class yulEmoGenerator
    {
        public enum HandMode
        {
            Both,   // 両手用: 左右の状態を別々に作成する
            Either  // 片手用: 左右どちらでも同じ状態を使う
        }

        /// <summary>
        /// ジェスチャー1つ分の定義。
        /// Both モード時は ClipLeft と ClipRight が独立して使用される。
        /// Either モード時は Clip (= ClipLeft) のみ使用される。
        /// </summary>
        public readonly struct GestureDef
        {
            public readonly string Name;
            public readonly int Value;
            /// <summary>Either モード用、または Both モードの左手用クリップ。</summary>
            public readonly AnimationClip Clip;
            /// <summary>Both モードの右手用クリップ。null の場合は Clip を流用する。</summary>
            public readonly AnimationClip ClipRight;

            /// <summary>Either モード用コンストラクタ（単一クリップ）。</summary>
            public GestureDef(string name, int value, AnimationClip clip)
            {
                Name = name;
                Value = value;
                Clip = clip;
                ClipRight = null;
            }

            /// <summary>Both モード用コンストラクタ（左右独立クリップ）。</summary>
            public GestureDef(string name, int value, AnimationClip clipLeft, AnimationClip clipRight)
            {
                Name = name;
                Value = value;
                Clip = clipLeft;
                ClipRight = clipRight;
            }
        }

        /// <summary>
        /// ジェスチャー表情のプリセット 1 つ分。基本のジェスチャー表情とは別に用意する表情セット。
        /// </summary>
        public readonly struct GesturePreset
        {
            /// <summary>メニューに表示する名前。</summary>
            public readonly string Name;
            /// <summary>基本の Config.Gestures と同じ形式（HandMode に応じて使われるクリップが変わる）。</summary>
            public readonly GestureDef[] Gestures;

            public GesturePreset(string name, GestureDef[] gestures)
            {
                Name = name;
                Gestures = gestures;
            }
        }

        public enum DisableParameterType
        {
            Bool,
            Int,
            Float
        }

        /// <summary>
        /// 無効化ルールの条件。
        ///   Bool : True / False
        ///   Int  : Greater / Less / Equals / NotEqual
        ///   Float: Greater / Less
        /// </summary>
        public enum DisableCondition
        {
            True,
            False,
            Greater,
            Less,
            Equals,
            NotEqual
        }

        /// <summary>表情変更を無効にするルール 1 件分。ルールが成立している間は表情変更が無効になる。</summary>
        public readonly struct DisableRule
        {
            public readonly string Name;
            public readonly DisableParameterType Type;
            public readonly DisableCondition Condition;
            /// <summary>Int / Float 用のしきい値（Int は四捨五入して使用）。Bool では無視。</summary>
            public readonly float Value;

            public DisableRule(string name, DisableParameterType type, DisableCondition condition, float value)
            {
                Name = name;
                Type = type;
                Condition = condition;
                Value = value;
            }
        }

        /// <summary>
        /// デフォルト表情レイヤーで毎フレーム書き込むブレンドシェイプ 1 件分。
        /// Path はアバタールートからの相対パス、PropertyName は "blendShape.xxx"。
        /// </summary>
        public readonly struct DefaultFaceBinding
        {
            public readonly string Path;
            public readonly string PropertyName;
            public readonly float Value;

            public DefaultFaceBinding(string path, string propertyName, float value)
            {
                Path = path;
                PropertyName = propertyName;
                Value = value;
            }
        }

        /// <summary>ジェスチャ表情メニューのエントリが、左手・右手のどちらの表情か（Either モードでは None）。</summary>
        public enum GestureFaceSide
        {
            None,
            Left,
            Right
        }

        /// <summary>
        /// ジェスチャ表情メニューの 1 項目（Idle 以外のジェスチャの表情 1 つ）。
        /// FixValue は、この表情を選ぶときの FaceFix パラメータの値（1 から順に割り当てる）。
        /// </summary>
        public readonly struct GestureFaceEntry
        {
            /// <summary>0 = 基本のジェスチャー表情、1〜N = 各プリセット。</summary>
            public readonly int SetIndex;
            /// <summary>基本なら "Default"、プリセットならプリセット名。</summary>
            public readonly string SetName;
            public readonly string GestureName;
            public readonly int GestureValue;
            public readonly GestureFaceSide Side;
            public readonly AnimationClip Clip;
            public readonly int FixValue;

            public GestureFaceEntry(
                int setIndex, string setName, string gestureName, int gestureValue,
                GestureFaceSide side, AnimationClip clip, int fixValue)
            {
                SetIndex = setIndex;
                SetName = setName;
                GestureName = gestureName;
                GestureValue = gestureValue;
                Side = side;
                Clip = clip;
                FixValue = fixValue;
            }
        }

        public class Config
        {
            public HandMode HandMode;
            public bool WriteDefaults = true;
            public bool UseGestureWeight;
            public AnimationClip IdleClip;
            public GestureDef[] Gestures;

            /// <summary>
            /// 基本のジェスチャー表情に加えて切り替えられるプリセット。null / 空なら使用しない。
            /// クリップが 1 つも設定されていないプリセットは無視される（GetActivePresets 参照）。
            /// </summary>
            public GesturePreset[] Presets;

            public bool UseFaceFix;
            public AnimationClip[] FaceFixClips;

            /// <summary>
            /// FaceFix トグル（1〜8）のメニューアイコン。FaceFixClips と同じ並びで、null の要素はアイコンなし。
            /// Generator 自体は使わず、yulEmoModularAvatarSetup がメニューに設定する。
            /// </summary>
            public Texture2D[] FaceFixIcons;

            /// <summary>
            /// FaceFix トグル（1〜8）のメニュー表示名。FaceFixClips と同じ並びで、null / 空の要素は「1」〜「8」の番号になる。
            /// Generator 自体は使わず、yulEmoModularAvatarSetup がメニューに設定する。
            /// </summary>
            public string[] FaceFixNames;

            /// <summary>
            /// true の場合、Idle 以外の各ジェスチャの表情を FaceFix パラメータで固定できるステートとメニューを追加する。
            /// FaceFix の値はジェスチャ表情が 1 から先に使い、既存の FaceFix スロット（1〜8）はその後ろにずれる。
            /// </summary>
            public bool UseGestureFaceMenu;

            /// <summary>
            /// 元の表情レイヤーとの干渉防止用。null / 空の場合はデフォルト表情レイヤーを作らない。
            /// </summary>
            public DefaultFaceBinding[] DefaultFace;

            /// <summary>
            /// true の場合、ジェスチャーの各表情ステートに VRCAnimatorTrackingControl (Eyes & Eyelids = Animation) を付け、
            /// Idle に Tracking へ戻す VRCAnimatorTrackingControl を付ける。
            /// </summary>
            public bool UseTrackingControl;

            /// <summary>
            /// 表情変更を無効にするルール（複数可）。null / 空なら使用しない。
            /// いずれかのルールが成立している間は、Idle からジェスチャーステートへ遷移せず、
            /// ジェスチャーステートにいる場合も Exit（→ Idle）へ強制的に戻る。
            /// </summary>
            public DisableRule[] DisableRules;
        }

        public const string FacePresetParameter = "FacePreset";

        /// <summary>ジェスチャ表情メニュー用ステートの名前の接頭辞（アバターからの読み込みで、通常のジェスチャーステートと区別するため）。</summary>
        public const string GestureFixStatePrefix = "GestureFix_";

        /// <summary>基本のジェスチャー表情（プリセットではないもの）のメニュー名。</summary>
        public const string DefaultGestureSetName = "Default";

        private const string SystemName = "21Facial";
        private const string DefaultFaceLayerName = "21Facial_Default";

        /// <summary>
        /// 実際に使われるプリセット（クリップが 1 つ以上設定されているもの）を、設定順のまま返す。
        /// リストの i 番目（0 始まり）が FacePreset = i + 1 に対応する。
        /// Generator とメニュー作成（yulEmoModularAvatarSetup）の両方で同じ判定を使うための関数。
        /// </summary>
        public static List<GesturePreset> GetActivePresets(Config config)
        {
            var result = new List<GesturePreset>();
            if (config == null || config.Presets == null) return result;

            foreach (var preset in config.Presets)
            {
                if (preset.Gestures == null) continue;

                var hasClip = false;
                foreach (var gesture in preset.Gestures)
                {
                    if (gesture.Clip != null || gesture.ClipRight != null)
                    {
                        hasClip = true;
                        break;
                    }
                }
                if (hasClip) result.Add(preset);
            }

            return result;
        }

        /// <summary>
        /// ジェスチャ表情メニューの項目を、FaceFix の値（1 から順）つきで返す。UseGestureFaceMenu が false なら空。
        /// 並びは「基本（Default）→ プリセット 1 → …」で、各セットの中は
        ///   Both モード: 左手のジェスチャー 1〜7 → 右手のジェスチャー 1〜7
        ///   Either モード: ジェスチャー 1〜7
        /// クリップが無いジェスチャーは項目にならず、値も使わない。
        /// Generator・メニュー作成・アイコン撮影の全てで同じ割り当てを使うための関数。
        /// </summary>
        public static List<GestureFaceEntry> GetGestureFaceEntries(Config config)
        {
            var result = new List<GestureFaceEntry>();
            if (config == null || !config.UseGestureFaceMenu) return result;

            var nextValue = 1;
            AddGestureFaceSet(result, config.HandMode, config.Gestures, 0, DefaultGestureSetName, ref nextValue);

            var presets = GetActivePresets(config);
            for (var p = 0; p < presets.Count; p++)
            {
                var presetName = string.IsNullOrWhiteSpace(presets[p].Name) ? $"Preset {p + 1}" : presets[p].Name.Trim();
                AddGestureFaceSet(result, config.HandMode, presets[p].Gestures, p + 1, presetName, ref nextValue);
            }

            return result;
        }

        private static void AddGestureFaceSet(
            List<GestureFaceEntry> result,
            HandMode handMode,
            GestureDef[] gestures,
            int setIndex,
            string setName,
            ref int nextValue)
        {
            if (gestures == null) return;

            if (handMode == HandMode.Both)
            {
                // 左手: Clip (= ClipLeft)
                foreach (var gesture in gestures)
                {
                    if (gesture.Clip == null) continue;
                    result.Add(new GestureFaceEntry(setIndex, setName, gesture.Name, gesture.Value,
                        GestureFaceSide.Left, gesture.Clip, nextValue++));
                }
                // 右手: ClipRight が null なら Clip を流用（BuildBothMode と同じ）
                foreach (var gesture in gestures)
                {
                    var clipR = gesture.ClipRight ?? gesture.Clip;
                    if (clipR == null) continue;
                    result.Add(new GestureFaceEntry(setIndex, setName, gesture.Name, gesture.Value,
                        GestureFaceSide.Right, clipR, nextValue++));
                }
            }
            else
            {
                foreach (var gesture in gestures)
                {
                    if (gesture.Clip == null) continue;
                    result.Add(new GestureFaceEntry(setIndex, setName, gesture.Name, gesture.Value,
                        GestureFaceSide.None, gesture.Clip, nextValue++));
                }
            }
        }

        /// <summary>ジェスチャ表情メニューが使う FaceFix の値の個数。既存の FaceFix スロット（1〜8）の値は、この数だけ後ろにずれる。</summary>
        public static int GetGestureFaceOffset(Config config)
        {
            return GetGestureFaceEntries(config).Count;
        }

        /// <summary>
        /// FaceFix の値でクリップを引ける配列を返す（インデックス = FaceFix の値 - 1）。
        /// 先頭がジェスチャ表情、その後ろが既存の FaceFix スロット。クリップが無い位置は null。
        /// アイコン撮影（FaceFix_1.png〜）とメニューのアイコン設定が、この並びを使う。
        /// </summary>
        public static AnimationClip[] GetFixClipSlots(Config config)
        {
            var entries = GetGestureFaceEntries(config);
            var fixClips = config != null ? config.FaceFixClips : null;
            var slotCount = fixClips != null ? fixClips.Length : yulEmoNDMFComponent.FaceFixSlotCount;

            var result = new AnimationClip[entries.Count + slotCount];
            foreach (var entry in entries)
            {
                result[entry.FixValue - 1] = entry.Clip;
            }

            if (config != null && config.UseFaceFix && fixClips != null)
            {
                for (var i = 0; i < fixClips.Length; i++)
                {
                    result[entries.Count + i] = fixClips[i];
                }
            }

            return result;
        }

        /// <summary>
        /// assetPath (例: "Assets/21Emo/Generated/21Facial_Both.controller") に
        /// 新しい AnimatorController を作成し、設定内容に従ってレイヤー・パラメータ・
        /// ステート・トランジションを構築する。
        /// </summary>
        public static void Generate(Config config, string assetPath)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);

            // デフォルトで作成される "Base Layer" 等を除去し、
            // 21Facial レイヤーのみを持つ状態にする。
            while (controller.layers.Length > 0)
            {
                controller.RemoveLayer(0);
            }

            var aac = AacV1.Create(new AacConfiguration
            {
                SystemName = SystemName,
                AnimatorRoot = null,
                DefaultValueRoot = null,
                AssetKey = GUID.Generate().ToString(),
                AssetContainer = controller,
                ContainerMode = AacConfiguration.Container.Everything,
                DefaultsProvider = new AacDefaultsProvider(writeDefaults: config.WriteDefaults)
            });

            var fx = aac.CreateMainArbitraryControllerLayer(controller);
            EnsureLayerName(controller, SystemName);

            var gestureLeft  = fx.IntParameter("GestureLeft");
            var gestureRight = fx.IntParameter("GestureRight");
            var faceFix      = fx.IntParameter("FaceFix");
            var faceLock     = fx.BoolParameter("FaceLock");

            // ジェスチャープリセット用パラメータ（1 個: Bool / 2 個以上: Int / 0 個: 作らない）
            var presets = GetActivePresets(config);
            AacFlBoolParameter presetBool = null;
            AacFlIntParameter presetInt = null;
            if (presets.Count == 1)
            {
                presetBool = fx.BoolParameter(FacePresetParameter);
            }
            else if (presets.Count >= 2)
            {
                presetInt = fx.IntParameter(FacePresetParameter);
            }

            // index 0 = 基本、1〜N = プリセット。プリセットが無い場合は null（条件を付けない）。
            IAacFlCondition PresetCondition(int index)
            {
                if (presetBool != null)
                {
                    if (index == 0) return presetBool.IsFalse();
                    return presetBool.IsTrue();
                }
                if (presetInt != null)
                {
                    return presetInt.IsEqualTo(index);
                }
                return null;
            }

            AacFlFloatParameter weightLeft  = null;
            AacFlFloatParameter weightRight = null;
            if (config.UseGestureWeight)
            {
                weightLeft  = fx.FloatParameter("GestureLeftWeight");
                weightRight = fx.FloatParameter("GestureRightWeight");
            }

            // ─────────────────────────────────────────────────
            // Idle（デフォルトステート = Entry から自動遷移される）
            // ─────────────────────────────────────────────────
            var idle = fx.NewState("Idle");
            if (config.IdleClip != null)
            {
                idle.WithAnimation(config.IdleClip);
            }
            // ジェスチャーステートで Animation にした Eyes & Eyelids を、Idle で Tracking に戻す
            if (config.UseTrackingControl)
            {
                SetEyesTracking(idle, animation: false);
            }

            // 表情変更を無効にするルール（任意・複数）
            var disableRules = ResolveDisableRules(fx, config.DisableRules);

            // ─────────────────────────────────────────────────
            // ジェスチャ表情メニュー用ステート（Idle → State → Exit、FaceFix で選ぶ）
            // Idle からの遷移は先に作った方が優先されるため、通常のジェスチャーステートより前に作る
            // （FaceFix で選んだ表情を、ジェスチャーより優先する）。
            // ─────────────────────────────────────────────────
            var gestureFaceEntries = GetGestureFaceEntries(config);
            foreach (var entry in gestureFaceEntries)
            {
                BuildGestureFaceState(fx, config, idle, entry, faceFix, disableRules);
            }

            // ─────────────────────────────────────────────────
            // ジェスチャーステート（Entry/Exit 方式: Idle → State → Exit）
            // ─────────────────────────────────────────────────
            // 基本のジェスチャー表情（FacePreset = 0 / False）
            BuildGestureSet(fx, config, idle, config.Gestures, string.Empty, PresetCondition(0),
                gestureLeft, gestureRight, faceLock, disableRules, weightLeft, weightRight);

            // 追加プリセット（FacePreset = 1〜N / True）。ステート名は "P1_Fist_L" のように接頭辞を付ける
            for (var p = 0; p < presets.Count; p++)
            {
                BuildGestureSet(fx, config, idle, presets[p].Gestures, $"P{p + 1}_", PresetCondition(p + 1),
                    gestureLeft, gestureRight, faceLock, disableRules, weightLeft, weightRight);
            }

            // ─────────────────────────────────────────────────
            // FaceFix ステート (1〜8)（Any State → State → Exit）
            // ─────────────────────────────────────────────────
            if (config.UseFaceFix && config.FaceFixClips != null)
            {
                // ジェスチャ表情メニューを使う場合、FaceFix の値はジェスチャ表情が先に使うので、その分だけ後ろにずらす
                var faceFixOffset = gestureFaceEntries.Count;

                for (var i = 0; i < config.FaceFixClips.Length; i++)
                {
                    var clip = config.FaceFixClips[i];
                    if (clip == null) continue;

                    var value = faceFixOffset + i + 1;
                    // ステート名は画像に合わせてスロット番号 (1〜8)。ずらした値ではなくスロット番号なので、読み込み時に判別できる
                    var state = fx.NewState((i + 1).ToString()).WithAnimation(clip);

                    // 固定表情を選んだら FaceLock を False に戻す（Parameter Driver）
                    SetBoolDriver(state, "FaceLock", false);

                    // Any State からの遷移（FaceFix == value のみ。FaceLock.IsTrue は不要）
                    state.TransitionsFromAny()
                        .WithTransitionDurationSeconds(0.1f)
                        .When(faceFix.IsEqualTo(value));

                    // Exit への遷移（FaceFix != value）
                    state.Exits()
                        .WithTransitionDurationSeconds(0.1f)
                        .When(faceFix.IsNotEqualTo(value));
                }
            }

            // ─────────────────────────────────────────────────
            // デフォルト表情レイヤー（元の表情レイヤーとの干渉防止）
            // ─────────────────────────────────────────────────
            if (config.DefaultFace != null && config.DefaultFace.Length > 0)
            {
                BuildDefaultFaceLayer(aac, controller, config);
            }

            EditorUtility.SetDirty(controller);
        }

        /// <summary>
        /// 21Facial の直前に「顔のシェイプキーをデフォルト値に戻し続けるレイヤー」を作る。
        /// MA で FX の末尾に結合されるため、元の表情レイヤーが動かしたシェイプキーを上書きして打ち消せる。
        /// </summary>
        private static void BuildDefaultFaceLayer(AacFlBase aac, AnimatorController controller, Config config)
        {
            var layer = aac.CreateSupportingArbitraryControllerLayer(controller, "DefaultFace");

            var clip = aac.NewClip();
            clip.Animating(edit =>
            {
                foreach (var binding in config.DefaultFace)
                {
                    edit.Animates(binding.Path, typeof(SkinnedMeshRenderer), binding.PropertyName)
                        .WithOneFrame(binding.Value);
                }
            });
            layer.NewState("DefaultFace").WithAnimation(clip);

            // AAC は新しいレイヤーを末尾に追加するので、21Facial より前（先頭）へ移動する。
            // 名前も分かりやすいものに固定し、ウェイトは 1 にしておく。
            var layers = controller.layers;
            var last = layers.Length - 1;
            var defaultLayer = layers[last];
            for (var i = last; i > 0; i--)
            {
                layers[i] = layers[i - 1];
            }
            layers[0] = defaultLayer;

            defaultLayer.name = DefaultFaceLayerName;
            defaultLayer.stateMachine.name = DefaultFaceLayerName;
            foreach (var l in layers)
            {
                l.defaultWeight = 1f;
            }
            controller.layers = layers;
        }

        /// <summary>
        /// ジェスチャ表情メニューの 1 項目分のステートと遷移を作る。
        ///   Idle → State: FaceFix == value [&& 無効化ルールが全て不成立]
        ///   State → Exit: FaceFix != value（と、無効化ルールが成立したとき）
        /// </summary>
        private static void BuildGestureFaceState(
            AacFlLayer fx,
            Config config,
            AacFlState idle,
            GestureFaceEntry entry,
            AacFlIntParameter faceFix,
            IReadOnlyList<DisableConditionPair> disableRules)
        {
            // ステート名: "GestureFix_Fist_L" / プリセットは "GestureFix_P1_Fist_L" / Either は左右の接尾辞なし
            var name = GestureFixStatePrefix
                + (entry.SetIndex > 0 ? $"P{entry.SetIndex}_" : string.Empty)
                + entry.GestureName
                + (entry.Side == GestureFaceSide.Left ? "_L" : entry.Side == GestureFaceSide.Right ? "_R" : string.Empty);

            var state = fx.NewState(name).WithAnimation(entry.Clip);
            if (config.UseTrackingControl) SetEyesTracking(state, animation: true);

            // 固定表情を選んだら FaceLock を False に戻す（FaceFix ステートと同じ）
            SetBoolDriver(state, "FaceLock", false);

            // Idle から（FaceFix == value。FaceLock の条件は FaceFix ステートと同じく不要）
            var to = idle.TransitionsTo(state)
                .WithTransitionDurationSeconds(0.1f)
                .When(faceFix.IsEqualTo(entry.FixValue));
            AddDisableGuards(to, disableRules);

            // Exit へ（FaceFix != value）
            state.Exits()
                .WithTransitionDurationSeconds(0.1f)
                .When(faceFix.IsNotEqualTo(entry.FixValue));

            // 無効化パラメータが True なら強制的に Exit（→ Idle）
            AddDisableExit(state, disableRules);
        }

        /// <summary>Idle → ジェスチャーステートの遷移に、プリセットの条件を AND で追加する（条件が null なら何もしない）。</summary>
        private static void AddPresetGuard(AacFlTransitionContinuation transition, IAacFlCondition presetCondition)
        {
            if (presetCondition == null) return;
            transition.And(presetCondition);
        }

        /// <summary>
        /// ジェスチャー表情 1 セット分（基本 or プリセット 1 つ）のステートと遷移を作る。
        /// namePrefix はステート名の接頭辞、presetCondition は Idle からの遷移に足す FacePreset の条件。
        /// </summary>
        private static void BuildGestureSet(
            AacFlLayer fx,
            Config config,
            AacFlState idle,
            GestureDef[] gestures,
            string namePrefix,
            IAacFlCondition presetCondition,
            AacFlIntParameter gestureLeft,
            AacFlIntParameter gestureRight,
            AacFlBoolParameter faceLock,
            IReadOnlyList<DisableConditionPair> disableRules,
            AacFlFloatParameter weightLeft,
            AacFlFloatParameter weightRight)
        {
            if (gestures == null) return;

            if (config.HandMode == HandMode.Both)
            {
                BuildBothMode(fx, config, idle, gestures, namePrefix, presetCondition,
                    gestureLeft, gestureRight, faceLock, disableRules, weightLeft, weightRight);
            }
            else
            {
                BuildEitherMode(fx, config, idle, gestures, namePrefix, presetCondition,
                    gestureLeft, gestureRight, faceLock, disableRules, weightLeft);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Both モード: Idle → 各L/Rステート → Exit
        // ─────────────────────────────────────────────────────────────
        private static void BuildBothMode(
            AacFlLayer fx,
            Config config,
            AacFlState idle,
            GestureDef[] gestures,
            string namePrefix,
            IAacFlCondition presetCondition,
            AacFlIntParameter gestureLeft,
            AacFlIntParameter gestureRight,
            AacFlBoolParameter faceLock,
            IReadOnlyList<DisableConditionPair> disableRules,
            AacFlFloatParameter weightLeft,
            AacFlFloatParameter weightRight)
        {
            foreach (var gesture in gestures)
            {
                // 左手ステート: Clip (= ClipLeft) が null なら作成しない
                var clipL = gesture.Clip;
                if (clipL != null)
                {
                    var stateL = fx.NewState(namePrefix + gesture.Name + "_L").WithAnimation(clipL);
                    if (weightLeft != null) stateL.WithMotionTime(weightLeft);
                    if (config.UseTrackingControl) SetEyesTracking(stateL, animation: true);

                    // Idle からこのステートへの遷移（GestureLeft == value && FaceLock == false [&& 無効化ルールが全て不成立]）
                    var toL = idle.TransitionsTo(stateL)
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureLeft.IsEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());
                    AddPresetGuard(toL, presetCondition);
                    AddDisableGuards(toL, disableRules);

                    // このステートから Exit（GestureLeft != value && FaceLock == false）
                    stateL.Exits()
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureLeft.IsNotEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());

                    // 無効化パラメータが True なら強制的に Exit（→ Idle）
                    AddDisableExit(stateL, disableRules);
                }

                // 右手ステート: ClipRight が null なら Clip (= ClipLeft) を流用。
                var clipR = gesture.ClipRight ?? gesture.Clip;
                if (clipR != null)
                {
                    var stateR = fx.NewState(namePrefix + gesture.Name + "_R").WithAnimation(clipR);
                    if (weightRight != null) stateR.WithMotionTime(weightRight);
                    if (config.UseTrackingControl) SetEyesTracking(stateR, animation: true);

                    // Idle からこのステートへの遷移（GestureRight == value && FaceLock == false [&& 無効化ルールが全て不成立]）
                    var toR = idle.TransitionsTo(stateR)
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureRight.IsEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());
                    AddPresetGuard(toR, presetCondition);
                    AddDisableGuards(toR, disableRules);

                    // このステートから Exit（GestureRight != value && FaceLock == false）
                    stateR.Exits()
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureRight.IsNotEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());

                    // 無効化パラメータが True なら強制的に Exit（→ Idle）
                    AddDisableExit(stateR, disableRules);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Either モード: Idle → 各ステート → Exit
        // ─────────────────────────────────────────────────────────────
        private static void BuildEitherMode(
            AacFlLayer fx,
            Config config,
            AacFlState idle,
            GestureDef[] gestures,
            string namePrefix,
            IAacFlCondition presetCondition,
            AacFlIntParameter gestureLeft,
            AacFlIntParameter gestureRight,
            AacFlBoolParameter faceLock,
            IReadOnlyList<DisableConditionPair> disableRules,
            AacFlFloatParameter weightLeft)
        {
            foreach (var gesture in gestures)
            {
                var clip = gesture.Clip;
                if (clip == null) continue;

                var state = fx.NewState(namePrefix + gesture.Name).WithAnimation(clip);
                if (weightLeft != null) state.WithMotionTime(weightLeft);
                if (config.UseTrackingControl) SetEyesTracking(state, animation: true);

                // Idle から: 左手 OR 右手がこのジェスチャー値（FaceLock == false [&& 無効化ルールが全て不成立]）
                var fromLeft = idle.TransitionsTo(state)
                    .WithTransitionDurationSeconds(0.1f)
                    .When(gestureLeft.IsEqualTo(gesture.Value))
                    .And(faceLock.IsFalse());
                AddPresetGuard(fromLeft, presetCondition);
                AddDisableGuards(fromLeft, disableRules);

                var fromRight = idle.TransitionsTo(state)
                    .WithTransitionDurationSeconds(0.1f)
                    .When(gestureRight.IsEqualTo(gesture.Value))
                    .And(faceLock.IsFalse());
                AddPresetGuard(fromRight, presetCondition);
                AddDisableGuards(fromRight, disableRules);

                // Exit へ: 左右ともジェスチャーが変わった かつ FaceLock == false
                state.Exits()
                    .WithTransitionDurationSeconds(0.1f)
                    .When(gestureLeft.IsNotEqualTo(gesture.Value))
                    .And(gestureRight.IsNotEqualTo(gesture.Value))
                    .And(faceLock.IsFalse());

                // 無効化パラメータが True なら強制的に Exit（→ Idle）
                AddDisableExit(state, disableRules);
            }
        }

        /// <summary>1 つの無効化ルールについて、「成立」条件とその否定（不成立）条件の組。</summary>
        public readonly struct DisableConditionPair
        {
            /// <summary>ルールが成立している（= 表情変更が無効）ときに真になる条件。</summary>
            public readonly IAacFlCondition Disabled;
            /// <summary>ルールが成立していない（= 表情変更が有効）ときに真になる条件。</summary>
            public readonly IAacFlCondition Enabled;

            public DisableConditionPair(IAacFlCondition disabled, IAacFlCondition enabled)
            {
                Disabled = disabled;
                Enabled = enabled;
            }
        }

        /// <summary>
        /// 設定されたルールからパラメータを作成し、成立／不成立の条件ペアに変換する。
        /// 名前が空のもの、同名で型が異なるもの（Animator では 1 つの名前に 1 つの型しか持てない）、
        /// 型と条件の組み合わせが不正なものは無視する。
        /// </summary>
        private static List<DisableConditionPair> ResolveDisableRules(AacFlLayer fx, DisableRule[] rules)
        {
            var result = new List<DisableConditionPair>();
            if (rules == null) return result;

            var boolParams = new Dictionary<string, AacFlBoolParameter>();
            var intParams = new Dictionary<string, AacFlIntParameter>();
            var floatParams = new Dictionary<string, AacFlFloatParameter>();
            var typeByName = new Dictionary<string, DisableParameterType>();

            foreach (var rule in rules)
            {
                var name = rule.Name != null ? rule.Name.Trim() : null;
                if (string.IsNullOrEmpty(name)) continue;

                if (typeByName.TryGetValue(name, out var existingType) && existingType != rule.Type)
                {
                    Debug.LogWarning($"[21Emo] 無効化パラメータ \"{name}\" は型が重複しているため無視しました。");
                    continue;
                }
                typeByName[name] = rule.Type;

                switch (rule.Type)
                {
                    case DisableParameterType.Bool:
                    {
                        if (!boolParams.TryGetValue(name, out var p))
                        {
                            p = fx.BoolParameter(name);
                            boolParams[name] = p;
                        }
                        switch (rule.Condition)
                        {
                            case DisableCondition.True:
                                result.Add(new DisableConditionPair(p.IsTrue(), p.IsFalse()));
                                continue;
                            case DisableCondition.False:
                                result.Add(new DisableConditionPair(p.IsFalse(), p.IsTrue()));
                                continue;
                        }
                        break;
                    }
                    case DisableParameterType.Int:
                    {
                        if (!intParams.TryGetValue(name, out var p))
                        {
                            p = fx.IntParameter(name);
                            intParams[name] = p;
                        }
                        var v = Mathf.RoundToInt(rule.Value);
                        switch (rule.Condition)
                        {
                            // 否定は整数なので境界を ±1 して「以下」「以上」を表現する
                            case DisableCondition.Greater:
                                result.Add(new DisableConditionPair(p.IsGreaterThan(v), p.IsLessThan(v + 1)));
                                continue;
                            case DisableCondition.Less:
                                result.Add(new DisableConditionPair(p.IsLessThan(v), p.IsGreaterThan(v - 1)));
                                continue;
                            case DisableCondition.Equals:
                                result.Add(new DisableConditionPair(p.IsEqualTo(v), p.IsNotEqualTo(v)));
                                continue;
                            case DisableCondition.NotEqual:
                                result.Add(new DisableConditionPair(p.IsNotEqualTo(v), p.IsEqualTo(v)));
                                continue;
                        }
                        break;
                    }
                    case DisableParameterType.Float:
                    {
                        if (!floatParams.TryGetValue(name, out var p))
                        {
                            p = fx.FloatParameter(name);
                            floatParams[name] = p;
                        }
                        // Animator の Float 条件は Greater / Less のみ。
                        // 値がしきい値とちょうど等しい場合は、成立・不成立のどちらにもならない。
                        switch (rule.Condition)
                        {
                            case DisableCondition.Greater:
                                result.Add(new DisableConditionPair(p.IsGreaterThan(rule.Value), p.IsLessThan(rule.Value)));
                                continue;
                            case DisableCondition.Less:
                                result.Add(new DisableConditionPair(p.IsLessThan(rule.Value), p.IsGreaterThan(rule.Value)));
                                continue;
                        }
                        break;
                    }
                }

                Debug.LogWarning($"[21Emo] 無効化パラメータ \"{name}\" の型 ({rule.Type}) と条件 ({rule.Condition}) の組み合わせが不正なため無視しました。");
            }

            return result;
        }

        /// <summary>Idle → ジェスチャーステートの遷移に、全ルールの「不成立」条件を AND で追加する。</summary>
        private static void AddDisableGuards(AacFlTransitionContinuation transition, IReadOnlyList<DisableConditionPair> rules)
        {
            if (rules == null) return;

            foreach (var rule in rules)
            {
                transition.And(rule.Enabled);
            }
        }

        /// <summary>ジェスチャーステートに、ルールごとの「成立」で Exit する遷移を追加する（OR になる）。</summary>
        private static void AddDisableExit(AacFlState state, IReadOnlyList<DisableConditionPair> rules)
        {
            if (rules == null) return;

            foreach (var rule in rules)
            {
                state.Exits()
                    .WithTransitionDurationSeconds(0.1f)
                    .When(rule.Disabled);
            }
        }

        /// <summary>
        /// ステートに VRCAvatarParameterDriver を追加し、Bool パラメータを指定の値にする（Set）。
        /// AAC のバージョン差を避けるため、SetEyesTracking と同様に Behaviour を直接追加している。
        /// </summary>
        private static void SetBoolDriver(AacFlState state, string parameterName, bool value)
        {
            var driver = state.State.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                type = VRC_AvatarParameterDriver.ChangeType.Set,
                name = parameterName,
                value = value ? 1f : 0f,
            });
        }

        /// <summary>
        /// ステートに VRCAnimatorTrackingControl を追加し、Eyes & Eyelids のみを制御する。
        /// animation = true: Animation（アニメーションで制御） / false: Tracking（トラッキングに戻す）。
        /// それ以外の部位は NoChange。
        /// </summary>
        private static void SetEyesTracking(AacFlState state, bool animation)
        {
            var behaviour = state.State.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
            behaviour.trackingEyes = animation
                ? VRC_AnimatorTrackingControl.TrackingType.Animation
                : VRC_AnimatorTrackingControl.TrackingType.Tracking;
        }

        private static void EnsureLayerName(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            if (layers.Length == 0) return;
            if (layers[0].name == name) return;
            layers[0].name = name;
            controller.layers = layers;
        }
    }
}
#endif
