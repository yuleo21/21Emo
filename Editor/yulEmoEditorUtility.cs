#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>21Emo の Editor 側で共通に使う小さな処理。</summary>
    internal static class yulEmoEditorUtility
    {
        /// <summary>
        /// avatar の親、なければ子から VRCAvatarDescriptor を探す。見つからなければ null。
        /// </summary>
        public static VRCAvatarDescriptor FindDescriptor(GameObject avatar)
        {
            if (avatar == null) return null;

            var descriptor = avatar.GetComponentInParent<VRCAvatarDescriptor>();
            return descriptor != null ? descriptor : avatar.GetComponentInChildren<VRCAvatarDescriptor>();
        }

        /// <summary>Assets から始まるフォルダのパスを、途中のフォルダも含めて作る（存在すれば何もしない）。</summary>
        public static void EnsureFolder(string folder)
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
    }
}
#endif
