#if UNITY_EDITOR
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace yulEmo
{
    /// <summary>
    /// VRChat SDK からのビルド・アップロードで、NDMF の処理が始まる前にアイコンを撮影する。
    ///
    /// 「ビルド時に撮影し直す」と「撮影時に NDMF を適用する」の両方がオンのコンポーネントが対象。
    /// NDMF の処理の最中に NDMF を再度適用すると不安定なため、NDMF の処理が始まる前（コールバックの順番を早くして）に、
    /// ビルド用の複製をもう一度複製して NDMF を適用し、撮影する。
    /// 撮影した画像は yulEmoNDMFComponent.preCapturedIcons に入れ、NDMF 側の処理（yulEmoNDMFPlugin）がメニューに設定する。
    ///
    /// このコールバックは VRChat SDK 経由のビルドでだけ呼ばれる。NDMF の手動実行などでは呼ばれないため、
    /// その場合は NDMF を適用しない撮影になる。
    /// </summary>
    public class yulEmoNDMFPreprocess : IVRCSDKPreprocessAvatarCallback
    {
        // NDMF の処理（-11000 のはず）より前に動かす（推測です）
        public int callbackOrder => -12000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            foreach (var component in avatarGameObject.GetComponentsInChildren<yulEmoNDMFComponent>(true))
            {
                if (!component.recaptureOnBuild || !component.captureWithNDMF) continue;

                var clips = component.faceFixClips;
                var hasClip = false;
                foreach (var clip in clips)
                {
                    if (clip != null) hasClip = true;
                }
                if (!hasClip) continue;

                yulEmoNDMFPlugin.SuppressProcessing = true;
                try
                {
                    component.preCapturedIcons = yulEmoIconCapturer.CaptureFaceFixIcons(
                        avatarGameObject,
                        clips,
                        null,
                        yulEmoNDMFPlugin.BuildCameraSettings(component),
                        true,
                        out var message);
                    Debug.Log("[21Emo] NDMF: ビルド前の撮影: " + message);
                }
                catch (System.Exception e)
                {
                    // 撮影に失敗してもビルドは止めない（NDMF 側の処理が、NDMF なしで撮影し直す）
                    Debug.LogException(e);
                    component.preCapturedIcons = null;
                }
                finally
                {
                    yulEmoNDMFPlugin.SuppressProcessing = false;
                }
            }

            return true;
        }
    }
}
#endif
