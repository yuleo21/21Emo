using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace yulEmo
{
    /// <summary>
    /// 21Emo が生成する「21Emo」オブジェクトに自動で付く、NDMF（ビルド時処理）用の設定コンポーネント。
    ///
    /// 役割:
    ///   1. FaceFix のメニューアイコンの撮影し直し（インスペクターのボタン、またはビルド時）
    ///   2. 生成したコントローラー内のアニメーションクリップの差し替え（インスペクターのボタン。アセットを直接書き換える）
    ///
    /// 実際の処理は Editor 側の yulEmoNDMFPlugin / yulEmoNDMFComponentEditor が行う。
    /// IEditorOnly なので、アップロードされるアバターには含まれない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("21tools/21Emo NDMF Settings")]
    public class yulEmoNDMFComponent : MonoBehaviour, IEditorOnly
    {
        public const int FaceFixSlotCount = 8;

        /// <summary>差し替えるクリップ 1 組。source が replacement に置き換わる。replacement が空の組は無視する。</summary>
        [Serializable]
        public class ClipSwap
        {
            public AnimationClip source;
            public AnimationClip replacement;
        }

        // ── アイコン ───────────────────────────────────────────────
        [Tooltip("オンにすると、アバターのビルドのたびに FaceFix のアイコンを撮影し直します（撮影は 1 回につき数秒かかります）。")]
        public bool recaptureOnBuild = false;

        [Tooltip("撮影用の複製に NDMF を適用してから撮影するか。\n「今すぐ撮影し直す」と、VRChat SDK からのビルド・アップロード時の撮影で有効です。")]
        public bool captureWithNDMF = true;

        // ビルド前（NDMF の処理が始まる前）に撮影したアイコン。保存されない一時データ。
        [NonSerialized] public Texture2D[] preCapturedIcons;

        [Tooltip("「今すぐ撮影し直す」ボタンで PNG を保存するフォルダ（Assets 以下）。")]
        public string iconFolder = string.Empty;

        // カメラ位置（yulEmoCameraSettings と同じ意味。単位はメートル、画角は度）
        public float cameraRight = 0f;
        public float cameraUp = 0f;
        public float cameraForward = 0f;
        public float cameraFov = 20f;

        [Tooltip("アイコンを撮影するクリップ。並びは FaceFix の値の順（インデックス = 値 - 1）。\nジェスチャ表情メニューを使うときは、先頭がジェスチャ表情で、その後ろに FaceFix のスロット 1〜8 が続きます。")]
        public AnimationClip[] faceFixClips = new AnimationClip[FaceFixSlotCount];

        // ── 作成時の設定 ───────────────────────────────────────────
        [Tooltip("アニメーターを作成したときの、21Emo のウィンドウの設定。ウィンドウに読み込み直すために使います。")]
        public yulEmoSettingsSnapshot settings = new yulEmoSettingsSnapshot();

        [Tooltip("settings に、作成時の設定が保存されているか。古いバージョンで作成したものは false です。")]
        public bool hasSettings = false;

        // ── アニメーション差し替え ──────────────────────────────────
        [Tooltip("source のクリップを replacement に差し替えます。「差し替えを実行」ボタンで、コントローラーのアセットを直接書き換えます。")]
        public List<ClipSwap> clipSwaps = new List<ClipSwap>();

        [Tooltip("オンにすると、差し替え後のクリップを最終フレームだけの 0 フレームのクリップに変換してから差し替えます。")]
        public bool flattenToZeroFrame = false;
    }
}
