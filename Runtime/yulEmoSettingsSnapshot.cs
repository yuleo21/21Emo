using System;
using System.Collections.Generic;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 21Emo のウィンドウの設定一式の控え。「21Emo」オブジェクトの yulEmoNDMFComponent に保存され、
    /// アニメーターを作成したときの設定を、あとからウィンドウへ読み込み直すために使う。
    ///
    /// 列挙型（手のモード・無効化ルールの型と条件）は、Editor 側の定義を参照できないため整数で持つ。
    /// クリップは、ウィンドウで指定した元のクリップ（0 フレーム化する前のもの）を保存する。
    /// </summary>
    [Serializable]
    public class yulEmoSettingsSnapshot
    {
        public const int GestureCount = 7;
        public const int FaceFixSlotCount = 8;

        [Serializable]
        public class PresetData
        {
            public string name = string.Empty;
            public AnimationClip[] left = new AnimationClip[GestureCount];
            public AnimationClip[] right = new AnimationClip[GestureCount];
            public AnimationClip[] either = new AnimationClip[GestureCount];
        }

        [Serializable]
        public class DisableRuleData
        {
            public string name = string.Empty;
            public int type;
            public int condition;
            public float value;
        }

        // 手のモード・WD
        public int handMode;
        public bool writeDefaults = true;

        // ジェスチャー
        public AnimationClip idleClip;
        public AnimationClip[] clipsLeft = new AnimationClip[GestureCount];
        public AnimationClip[] clipsRight = new AnimationClip[GestureCount];
        public AnimationClip[] clipsEither = new AnimationClip[GestureCount];
        public bool useGestureWeight;
        public bool useTrackingControl = true;
        public bool flattenClips;

        // プリセット・ジェスチャ表情メニュー
        public List<PresetData> presets = new List<PresetData>();
        public bool useGestureFaceMenu;

        // FaceFix
        public bool useFaceFix;
        public AnimationClip[] faceFixClips = new AnimationClip[FaceFixSlotCount];
        public string[] faceFixNames = new string[FaceFixSlotCount];

        // 表情変更を無効にするルール
        public List<DisableRuleData> disableRules = new List<DisableRuleData>();

        // 元の表情との干渉対策
        public bool resetOriginalFace = true;
        public bool excludeLipSyncAndBlink = true;
        public List<AnimationClip> originalFacialClips = new List<AnimationClip>();

        // 出力・アイコン・Modular Avatar
        public string outputFolder = string.Empty;
        public string controllerName = string.Empty;
        public bool captureFaceFixIcons = true;
        public bool captureWithNDMF = true;
        public bool setupModularAvatar = true;
    }
}
