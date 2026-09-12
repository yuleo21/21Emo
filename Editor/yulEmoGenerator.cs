#if UNITY_EDITOR
using AnimatorAsCode.V1;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

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
    ///
    /// レイヤー名は "21Facial" に固定される。
    ///
    /// 遷移方式:
    ///   ジェスチャーステート: Entry/Exit 方式
    ///     - Entry → Idle（デフォルトステート）
    ///     - Idle → 各ジェスチャーステート（条件: Gesture == value && FaceLock == false）
    ///     - 各ジェスチャーステート → Exit（条件: Gesture != value && FaceLock == false, Duration: 0.1s）
    ///     - Exit → 自動的に Entry (Idle) へ復帰
    ///   FaceFix ステート (1〜8):
    ///     - Any State → 各 FaceFix ステート（条件: FaceFix == value, Duration: 0.1s） ※FaceLock条件は不要
    ///     - 各 FaceFix ステート → Exit（条件: FaceFix != value, Duration: 0.1s）
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

        public class Config
        {
            public HandMode HandMode;
            public bool WriteDefaults = true;
            public bool UseGestureWeight;
            public AnimationClip IdleClip;
            public GestureDef[] Gestures;
            public bool UseFaceFix;
            public AnimationClip[] FaceFixClips;
        }

        private const string SystemName = "21Facial";

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

            // ─────────────────────────────────────────────────
            // ジェスチャーステート（Entry/Exit 方式: Idle → State → Exit）
            // ─────────────────────────────────────────────────
            if (config.HandMode == HandMode.Both)
            {
                BuildBothMode(fx, config, idle, gestureLeft, gestureRight, faceLock, weightLeft, weightRight);
            }
            else
            {
                BuildEitherMode(fx, config, idle, gestureLeft, gestureRight, faceLock, weightLeft);
            }

            // ─────────────────────────────────────────────────
            // FaceFix ステート (1〜8)（Any State → State → Exit）
            // ─────────────────────────────────────────────────
            if (config.UseFaceFix && config.FaceFixClips != null)
            {
                for (var i = 0; i < config.FaceFixClips.Length; i++)
                {
                    var clip = config.FaceFixClips[i];
                    if (clip == null) continue;

                    var value = i + 1;
                    // ステート名は画像に合わせて数字 (1〜8)
                    var state = fx.NewState(value.ToString()).WithAnimation(clip);

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

            EditorUtility.SetDirty(controller);
        }

        // ─────────────────────────────────────────────────────────────
        // Both モード: Idle → 各L/Rステート → Exit
        // ─────────────────────────────────────────────────────────────
        private static void BuildBothMode(
            AacFlLayer fx,
            Config config,
            AacFlState idle,
            AacFlIntParameter gestureLeft,
            AacFlIntParameter gestureRight,
            AacFlBoolParameter faceLock,
            AacFlFloatParameter weightLeft,
            AacFlFloatParameter weightRight)
        {
            foreach (var gesture in config.Gestures)
            {
                // 左手ステート: Clip (= ClipLeft) が null なら作成しない
                var clipL = gesture.Clip;
                if (clipL != null)
                {
                    var stateL = fx.NewState(gesture.Name + "_L").WithAnimation(clipL);
                    if (weightLeft != null) stateL.WithMotionTime(weightLeft);

                    // Idle からこのステートへの遷移（GestureLeft == value && FaceLock == false）
                    idle.TransitionsTo(stateL)
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureLeft.IsEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());

                    // このステートから Exit（GestureLeft != value && FaceLock == false）
                    stateL.Exits()
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureLeft.IsNotEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());
                }

                // 右手ステート: ClipRight が null なら Clip (= ClipLeft) を流用。
                var clipR = gesture.ClipRight ?? gesture.Clip;
                if (clipR != null)
                {
                    var stateR = fx.NewState(gesture.Name + "_R").WithAnimation(clipR);
                    if (weightRight != null) stateR.WithMotionTime(weightRight);

                    // Idle からこのステートへの遷移（GestureRight == value && FaceLock == false）
                    idle.TransitionsTo(stateR)
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureRight.IsEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());

                    // このステートから Exit（GestureRight != value && FaceLock == false）
                    stateR.Exits()
                        .WithTransitionDurationSeconds(0.1f)
                        .When(gestureRight.IsNotEqualTo(gesture.Value))
                        .And(faceLock.IsFalse());
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
            AacFlIntParameter gestureLeft,
            AacFlIntParameter gestureRight,
            AacFlBoolParameter faceLock,
            AacFlFloatParameter weightLeft)
        {
            foreach (var gesture in config.Gestures)
            {
                var clip = gesture.Clip;
                if (clip == null) continue;

                var state = fx.NewState(gesture.Name).WithAnimation(clip);
                if (weightLeft != null) state.WithMotionTime(weightLeft);

                // Idle から: 左手 OR 右手がこのジェスチャー値（FaceLock == false）
                idle.TransitionsTo(state)
                    .WithTransitionDurationSeconds(0.1f)
                    .When(gestureLeft.IsEqualTo(gesture.Value))
                    .And(faceLock.IsFalse());
                idle.TransitionsTo(state)
                    .WithTransitionDurationSeconds(0.1f)
                    .When(gestureRight.IsEqualTo(gesture.Value))
                    .And(faceLock.IsFalse());

                // Exit へ: 左右ともジェスチャーが変わった かつ FaceLock == false
                state.Exits()
                    .WithTransitionDurationSeconds(0.1f)
                    .When(gestureLeft.IsNotEqualTo(gesture.Value))
                    .And(gestureRight.IsNotEqualTo(gesture.Value))
                    .And(faceLock.IsFalse());
            }
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
