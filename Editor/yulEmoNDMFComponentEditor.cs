#if UNITY_EDITOR
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>
    /// yulEmoNDMFComponent のインスペクター。
    /// アイコンの撮影し直し（今すぐ / ビルド時）、カメラ位置、アニメーションの差し替えを設定する。
    /// </summary>
    [CustomEditor(typeof(yulEmoNDMFComponent))]
    public class yulEmoNDMFComponentEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("FaceFix アイコン", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("recaptureOnBuild"),
                new GUIContent("ビルド時に撮影し直す", "オンにすると、ビルドのたびにアイコンを撮影し直してメニューに設定します。\n下の「撮影時に NDMF を適用する」がオンなら、VRChat SDK からのビルド・アップロードでは NDMF を適用した状態で撮影します。\nそれ以外の場面（NDMF の手動実行など）では、NDMF を適用せずに撮影します。"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("captureWithNDMF"),
                new GUIContent("撮影時に NDMF を適用する", "「今すぐ撮影し直す」と、VRChat SDK からのビルド・アップロード時の撮影で、NDMF を適用した状態で撮影します。\nメッシュを統合する最適化ツールを使っていて表情が反映されないときは、オフにしてください。"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("iconFolder"),
                new GUIContent("保存先フォルダ", "「今すぐ撮影し直す」で PNG を保存するフォルダ（Assets 以下）。"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("faceFixClips"),
                new GUIContent("撮影するクリップ（FaceFix の値の順）", "ジェスチャ表情メニューを使うときは、先頭がジェスチャ表情で、その後ろに FaceFix のスロット 1〜8 が続きます。"), true);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("カメラ位置", EditorStyles.boldLabel);
            DrawSlider("cameraRight", "左右", -yulEmoCameraSettings.MaxPan, yulEmoCameraSettings.MaxPan);
            DrawSlider("cameraUp", "上下", -yulEmoCameraSettings.MaxPan, yulEmoCameraSettings.MaxPan);
            DrawSlider("cameraForward", "前後", -yulEmoCameraSettings.MaxDolly, yulEmoCameraSettings.MaxDolly);
            DrawSlider("cameraFov", "画角 (FoV)", yulEmoCameraSettings.MinFov, yulEmoCameraSettings.MaxFov);

            serializedObject.ApplyModifiedProperties();

            var component = (yulEmoNDMFComponent)target;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("カメラプレビューを開く"))
                {
                    var avatar = FindAvatar(component);
                    EditorApplication.delayCall += () => yulEmoCameraPreviewWindow.Open(avatar);
                }
                if (GUILayout.Button("プレビューの設定を取り込む"))
                {
                    var current = yulEmoCameraSettings.Current;
                    Undo.RecordObject(component, "21Emo: カメラ位置");
                    component.cameraRight = current.Right;
                    component.cameraUp = current.Up;
                    component.cameraForward = current.Forward;
                    component.cameraFov = current.Fov;
                    EditorUtility.SetDirty(component);
                }
            }

            if (GUILayout.Button("アイコンを今すぐ撮影し直す"))
            {
                // ダイアログや撮影を OnGUI の途中で行うとレイアウトエラーになるため、OnGUI の後に実行する
                EditorApplication.delayCall += () => RecaptureNow(component);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("アニメーションの差し替え", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "「差し替えを実行」を押すと、コントローラーのアセットの中の「差し替え前」のクリップを「差し替え後」のクリップに直接書き換えます（元に戻せません）。\n" +
                "「差し替え後」が空の行は無視されます。実行後は、差し替え一覧の「差し替え前」が新しいクリップに更新されます。",
                MessageType.None);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("clipSwaps"),
                new GUIContent("差し替え一覧（差し替え前 → 差し替え後）"), true);
            serializedObject.ApplyModifiedProperties();

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("flattenToZeroFrame"),
                new GUIContent("0 フレームにする", "オンにすると、差し替え後のクリップを最終フレームだけの 0 フレームのクリップに変換してから差し替えます。\n変換したクリップは、コントローラーと同じ場所の「〜_Swapped」フォルダに保存されます。"));
            serializedObject.ApplyModifiedProperties();

            if (GUILayout.Button("コントローラーの使用クリップを一覧に追加"))
            {
                AddControllerClips(component);
            }

            if (GUILayout.Button("差し替えを実行"))
            {
                EditorApplication.delayCall += () => ExecuteSwap(component);
            }
        }

        private void DrawSlider(string propertyName, string label, float min, float max)
        {
            var property = serializedObject.FindProperty(propertyName);
            property.floatValue = EditorGUILayout.Slider(label, property.floatValue, min, max);
        }

        private static GameObject FindAvatar(yulEmoNDMFComponent component)
        {
            var descriptor = component.GetComponentInParent<VRCAvatarDescriptor>();
            return descriptor != null ? descriptor.gameObject : null;
        }

        /// <summary>同じオブジェクトの Merge Animator のコントローラーが使うクリップのうち、一覧にないものを「差し替え前」として追加する。</summary>
        private static void AddControllerClips(yulEmoNDMFComponent component)
        {
            var added = 0;
            Undo.RecordObject(component, "21Emo: 差し替え一覧に追加");

            var existing = new HashSet<AnimationClip>();
            foreach (var swap in component.clipSwaps)
            {
                if (swap != null && swap.source != null) existing.Add(swap.source);
            }

            foreach (var merge in component.GetComponents<ModularAvatarMergeAnimator>())
            {
                if (merge.animator == null) continue;

                foreach (var clip in merge.animator.animationClips)
                {
                    if (clip == null || !existing.Add(clip)) continue;
                    component.clipSwaps.Add(new yulEmoNDMFComponent.ClipSwap { source = clip });
                    added++;
                }
            }

            EditorUtility.SetDirty(component);
            if (added == 0)
            {
                Debug.Log("[21Emo] 差し替え一覧に追加できるクリップはありませんでした（Merge Animator のコントローラーが未設定、または追加済みです）。");
            }
        }

        private static void ExecuteSwap(yulEmoNDMFComponent component)
        {
            if (component == null) return;

            if (!EditorUtility.DisplayDialog(
                    "21Emo",
                    "コントローラーのアセットを直接書き換えます。元に戻せません。\n実行しますか？",
                    "実行する", "キャンセル"))
            {
                return;
            }

            var success = yulEmoClipSwapper.TryExecute(component, out var message);
            if (success)
            {
                Debug.Log("[21Emo] " + message);
            }
            else
            {
                Debug.LogWarning("[21Emo] " + message);
            }
            EditorUtility.DisplayDialog("21Emo", message, "OK");
        }

        private static void RecaptureNow(yulEmoNDMFComponent component)
        {
            if (component == null) return;

            var avatar = FindAvatar(component);
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("21Emo", "VRCAvatarDescriptor が見つかりませんでした。", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(component.iconFolder) || !component.iconFolder.StartsWith("Assets"))
            {
                EditorUtility.DisplayDialog("21Emo", "保存先フォルダを Assets 以下のパスで指定してください。", "OK");
                return;
            }

            var clips = component.faceFixClips;
            var hasClip = false;
            foreach (var clip in clips)
            {
                if (clip != null) hasClip = true;
            }
            if (!hasClip)
            {
                EditorUtility.DisplayDialog("21Emo", "撮影する FaceFix のクリップが設定されていません。", "OK");
                return;
            }

            EnsureFolderExists(component.iconFolder.TrimEnd('/'));

            var settings = new yulEmoCameraSettings
            {
                Right = component.cameraRight,
                Up = component.cameraUp,
                Forward = component.cameraForward,
                Fov = component.cameraFov,
            };

            var icons = yulEmoIconCapturer.CaptureFaceFixIcons(
                avatar, clips, component.iconFolder, settings, component.captureWithNDMF, out var message);
            yulEmoModularAvatarSetup.ApplyFaceFixIcons(component.transform, icons, true);

            if (avatar.scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.scene);
            }
            EditorUtility.DisplayDialog("21Emo", message ?? "アイコンを撮影し直しました。", "OK");
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
    }
}
#endif
