#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 21Emo のエディター設定。Unity の Preferences の「21Emo」で変更でき、EditorPrefs に保存される。
    /// 今のところ、表情作成ウィンドウのプレビューに出す枠線の色だけを持つ。
    /// </summary>
    public static class yulEmoPreferences
    {
        private const string HoverColorKey = "yulEmo.Preferences.HoverBoxColor";
        private const string SelectedColorKey = "yulEmo.Preferences.SelectedBoxColor";
        private const string UseNdmfPreviewKey = "yulEmo.Preferences.UseNdmfPreview";

        public static readonly Color DefaultHoverBoxColor = new Color32(0xFF, 0x88, 0x21, 0xFF);
        public static readonly Color DefaultSelectedBoxColor = new Color32(0x21, 0x9D, 0xFF, 0xFF);

        /// <summary>シェイプキーに乗せたときの枠線の色。</summary>
        public static Color HoverBoxColor
        {
            get => Load(HoverColorKey, DefaultHoverBoxColor);
            set => Save(HoverColorKey, value);
        }

        /// <summary>編集中のシェイプキーの枠線の色。</summary>
        public static Color SelectedBoxColor
        {
            get => Load(SelectedColorKey, DefaultSelectedBoxColor);
            set => Save(SelectedColorKey, value);
        }

        /// <summary>表情作成のプレビューで NDMF Preview を使うか。</summary>
        public static bool UseNdmfPreview
        {
            get => EditorPrefs.GetBool(UseNdmfPreviewKey, true);
            set => EditorPrefs.SetBool(UseNdmfPreviewKey, value);
        }

        public static void ResetBoxColors()
        {
            EditorPrefs.DeleteKey(HoverColorKey);
            EditorPrefs.DeleteKey(SelectedColorKey);
        }

        private static Color Load(string key, Color fallback)
        {
            var html = EditorPrefs.GetString(key, string.Empty);
            if (!string.IsNullOrEmpty(html) && ColorUtility.TryParseHtmlString("#" + html, out var color))
            {
                color.a = 1f;
                return color;
            }
            return fallback;
        }

        private static void Save(string key, Color color)
        {
            EditorPrefs.SetString(key, ColorUtility.ToHtmlStringRGB(color));
        }
    }

    /// <summary>Unity の Preferences に「21Emo」の項目を追加する。</summary>
    internal static class yulEmoPreferencesProvider
    {
        public const string SettingsPath = "Preferences/21Emo";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.User)
            {
                label = "21Emo",
                guiHandler = _ => Draw(),
                keywords = new HashSet<string> { "21Emo", "枠線", "色", "プレビュー" },
            };
        }

        private static void Draw()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("プレビューのウィンドウ", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var useNdmf = EditorGUILayout.Toggle(
                new GUIContent("NDMF Preview を使う",
                    "オンにすると、プレビューのウィンドウにも NDMF Preview が効き、Scene ビューと同じ見た目になります。\n" +
                    "オフにすると、NDMF の処理を反映しない元のアバターを表示します。\n" +
                    "NDMF のメニューで NDMF Preview 自体がオフのときは、ここをオンにしても反映されません。"),
                yulEmoPreferences.UseNdmfPreview);
            if (EditorGUI.EndChangeCheck()) yulEmoPreferences.UseNdmfPreview = useNdmf;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("プレビューの枠線", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var hover = EditorGUILayout.ColorField(
                new GUIContent("乗せたシェイプキー", "シェイプキーに乗せたとき、動く範囲を囲む枠の色です。"),
                yulEmoPreferences.HoverBoxColor, true, false, false);
            var selected = EditorGUILayout.ColorField(
                new GUIContent("編集中のシェイプキー", "「編集中のシェイプキーの範囲も囲む」がオンのとき、その範囲を囲む枠の色です。"),
                yulEmoPreferences.SelectedBoxColor, true, false, false);
            if (EditorGUI.EndChangeCheck())
            {
                yulEmoPreferences.HoverBoxColor = hover;
                yulEmoPreferences.SelectedBoxColor = selected;
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("標準の色に戻す", GUILayout.Width(140f)))
            {
                yulEmoPreferences.ResetBoxColors();
            }
        }
    }
}
#endif
