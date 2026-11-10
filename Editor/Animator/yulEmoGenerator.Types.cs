#if UNITY_EDITOR
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// yulEmoGenerator の入力に使う型の定義（ジェスチャー・プリセット・無効化ルール・設定）。
    /// 生成の処理は yulEmoGenerator.cs にある。
    /// </summary>
    public static partial class yulEmoGenerator
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
            /// 表情を強制的に Idle にするルール（複数可）。null / 空なら使用しない。
            /// いずれかのルールが成立している間は、Idle からジェスチャーステートへ遷移せず、
            /// ジェスチャーステートにいる場合も Exit（→ Idle）へ強制的に戻る。
            /// </summary>
            public DisableRule[] DisableRules;

            /// <summary>
            /// 表情変更を無効にするルール（複数可）。null / 空なら使用しない。
            /// ジェスチャーステートから Exit する遷移（Gesture != value など）に、各ルールの「成立しない」条件を AND で追加する。
            /// 例: AllowFaceChange を Bool / False で指定すると、Exit の条件は「Gesture != value」に加えて「AllowFaceChange == True」になる。
            /// いずれかのルールが成立している間は Exit できず、表情が固定される。
            /// </summary>
            public DisableRule[] FreezeRules;

            /// <summary>
            /// true の場合、Idle を経由せず、Entry から各ジェスチャーステート・ジェスチャ表情ステートへ直接遷移する（Entry/Exit 方式）。
            ///   Entry → 各表情（Idle → 表情 と同じ条件）。どの条件も成立しないときだけ、デフォルトステートの Idle に入る。
            ///   各表情 → Exit → Entry なので、表情から別の表情へは Idle を挟まずに切り替わる。
            ///   Idle は、Entry の条件が成立したら Exit する（Idle → Exit に、各 Entry 遷移と同じ条件を付ける）。
            /// Entry の遷移は作成順に評価されるので、ジェスチャ表情メニュー（FaceFix）がジェスチャーより優先される。
            /// </summary>
            public bool SkipIdle;
        }
    }
}
#endif
