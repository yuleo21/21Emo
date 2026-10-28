#if UNITY_EDITOR
using nadena.dev.ndmf;
using UnityEngine;
using yulEmo;

[assembly: ExportsPlugin(typeof(yulEmoNDMFPlugin))]

namespace yulEmo
{
    /// <summary>
    /// 「21Emo」オブジェクトの yulEmoNDMFComponent を、アバターのビルド時に処理する NDMF プラグイン。
    /// （NDMF を適用した状態でのアイコン撮影は、NDMF の処理が始まる前に yulEmoNDMFPreprocess が行う）
    ///
    /// Modular Avatar より前（Transforming フェーズ）に、アイコンの撮影し直しを行う（recaptureOnBuild がオンのときだけ）。
    ///      FaceFix のクリップで撮影し、メニューのトグルのアイコンに設定する。
    ///      ビルド中のアバターに NDMF を再度適用すると不安定なため、撮影は NDMF を適用しない状態で行う。
    ///      この時点では Modular Avatar による衣装の統合などは済んでいない。
    ///      NDMF を適用した状態の撮影が必要なときは、yulEmoNDMFPreprocess が先に撮影した画像を使う。
    /// </summary>
    public class yulEmoNDMFPlugin : Plugin<yulEmoNDMFPlugin>
    {
        private const string LogPrefix = "[21Emo] NDMF: ";

        /// <summary>
        /// true の間は処理を行わない。ビルド前の撮影で NDMF を適用した複製にも、このプラグインが走るため、
        /// それを止めて二重の撮影を防ぐ。
        /// </summary>
        internal static bool SuppressProcessing;

        internal static yulEmoCameraSettings BuildCameraSettings(yulEmoNDMFComponent component)
        {
            return new yulEmoCameraSettings
            {
                Right = component.cameraRight,
                Up = component.cameraUp,
                Forward = component.cameraForward,
                Fov = component.cameraFov,
            };
        }

        public override string QualifiedName => "net.yuleo21.21emo";
        public override string DisplayName => "21Emo";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("21Emo: アイコン撮影", Process);
        }

        private static void Process(BuildContext context)
        {
            if (SuppressProcessing) return;

            var components = context.AvatarRootObject.GetComponentsInChildren<yulEmoNDMFComponent>(true);
            foreach (var component in components)
            {
                if (component.recaptureOnBuild)
                {
                    RecaptureIcons(context, component);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // アイコンの撮影し直し
        // ─────────────────────────────────────────────────────────────
        private static void RecaptureIcons(BuildContext context, yulEmoNDMFComponent component)
        {
            var clips = component.faceFixClips;
            var hasClip = false;
            foreach (var clip in clips)
            {
                if (clip != null) hasClip = true;
            }
            if (!hasClip) return;

            // ビルド前の撮影（NDMF 適用あり）が済んでいれば、その画像を使う。
            // 無ければ、ここで NDMF を適用せずに撮影する（ビルド中のアバターに NDMF を再度適用すると不安定なため）。
            var icons = component.preCapturedIcons;
            component.preCapturedIcons = null;
            string message;
            if (icons != null)
            {
                message = "ビルド前に NDMF を適用して撮影した画像を使用しました。";
            }
            else
            {
                // folder = null: ファイルに保存せず、メモリ上のテクスチャを受け取る。
                icons = yulEmoIconCapturer.CaptureFaceFixIcons(
                    context.AvatarRootObject, clips, null, BuildCameraSettings(component), false, out message);
            }

            foreach (var icon in icons)
            {
                if (icon == null) continue;
                try
                {
                    context.AssetSaver.SaveAsset(icon);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"{LogPrefix}アイコンをビルド用に登録できませんでした: {e.Message}");
                }
            }

            yulEmoModularAvatarSetup.ApplyFaceFixIcons(component.transform, icons, false);
            Debug.Log($"{LogPrefix}{message}");
        }
    }
}
#endif
