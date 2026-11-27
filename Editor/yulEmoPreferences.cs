#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 21Emo のエディター設定。Unity の Preferences の「21Emo」で変更でき、EditorPrefs に保存される。
    /// 表情作成ウィンドウのプレビュー設定と、撮影用複製から除外するコンポーネントの一覧を持つ。
    /// </summary>
    public static class yulEmoPreferences
    {
        private const string HoverColorKey = "yulEmo.Preferences.HoverBoxColor";
        private const string SelectedColorKey = "yulEmo.Preferences.SelectedBoxColor";
        private const string UseNdmfPreviewKey = "yulEmo.Preferences.UseNdmfPreview";
        private const string ExcludedNdmfComponentsKey = "yulEmo.Preferences.ExcludedNdmfComponents";
        private const string UsePreviewCaptureKey = "yulEmo.Preferences.UsePreviewCapture";

        public static readonly Color DefaultHoverBoxColor = new Color32(0xFF, 0x88, 0x21, 0xFF);
        public static readonly Color DefaultSelectedBoxColor = new Color32(0x21, 0x9D, 0xFF, 0xFF);

        /// <summary>マウスオーバー中のシェイプキーの枠線の色。</summary>
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

        /// <summary>
        /// アイコン撮影の高速モード（試作）。オンのとき、Generate と再撮影では、複製への NDMF 全適用の代わりに
        /// NDMF Preview で撮影する。ビルド時の撮影には使わない。
        /// プレビューに対応していない処理は反映されないため、結果が通常の撮影と食い違うことがある。
        /// </summary>
        public static bool UsePreviewCapture
        {
            get => EditorPrefs.GetBool(UsePreviewCaptureKey, false);
            set => EditorPrefs.SetBool(UsePreviewCaptureKey, value);
        }

        /// <summary>
        /// 撮影用複製から取り除くコンポーネントの型名の一覧。
        /// アイコン撮影とカメラプレビューで、NDMF を適用する前の複製から取り除かれる（元のアバターには影響しない）。
        /// 型の完全名（名前空間つき）で指定する。名前空間を含まない場合は、型の名前だけで一致させる。
        /// 返す配列は、空白を取り除き、重複を除いたもの。
        /// </summary>
        public static string[] ExcludedNdmfComponentTypes
        {
            get
            {
                var raw = EditorPrefs.GetString(ExcludedNdmfComponentsKey, string.Empty);
                return CleanEntries(raw.Split('\n'));
            }
            set => EditorPrefs.SetString(ExcludedNdmfComponentsKey, string.Join("\n", CleanEntries(value)));
        }

        private static string[] CleanEntries(IEnumerable<string> entries)
        {
            var result = new List<string>();
            if (entries == null) return result.ToArray();

            var seen = new HashSet<string>();
            foreach (var entry in entries)
            {
                var trimmed = entry != null ? entry.Trim() : string.Empty;
                if (trimmed.Length == 0) continue;
                if (seen.Add(trimmed)) result.Add(trimmed);
            }
            return result.ToArray();
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
                keywords = new HashSet<string> { "21Emo", "枠線", "色", "プレビュー", "NDMF", "除外", "コンポーネント" },
            };
        }

        // 除外一覧の編集中の内容。追加した直後の空の行を消さないよう、保存済みの値とは別に持つ
        private static List<string> _excludedEdit;

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
            EditorGUILayout.LabelField("撮影から除外する NDMF コンポーネント", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "アイコン撮影とカメラプレビューでは、NDMF を適用する前の複製から、ここに挙げたコンポーネントを取り除きます。元のアバターには影響しません。\n" +
                "型の完全名（例: nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator）で指定します。名前空間を含まない場合は、型の名前だけで一致します。",
                MessageType.None);
            DrawExcludedList();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("アイコン撮影の高速モード（試作）", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var usePreviewCapture = EditorGUILayout.ToggleLeft(
                new GUIContent("アイコン撮影に NDMF Preview を使う",
                    "オンにすると、Generate と再撮影のアイコン撮影で、複製に NDMF を全適用する代わりに NDMF Preview で撮影します。ビルド時の撮影には使いません。"),
                yulEmoPreferences.UsePreviewCapture);
            if (EditorGUI.EndChangeCheck()) yulEmoPreferences.UsePreviewCapture = usePreviewCapture;

            EditorGUILayout.HelpBox(
                "プレビューに対応していない処理は反映されないため、通常の撮影と見た目が食い違うことがあります。\n" +
                "NDMF Preview が使えないときは、通常の撮影に自動で切り替わります。",
                MessageType.None);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("プレビューの枠線", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var hover = EditorGUILayout.ColorField(
                new GUIContent("マウスオーバー中のシェイプキー", "シェイプキーにマウスオーバーしたとき、動く範囲を囲む枠の色です。"),
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

        private static void DrawExcludedList()
        {
            if (_excludedEdit == null)
            {
                _excludedEdit = new List<string>(yulEmoPreferences.ExcludedNdmfComponentTypes);
            }

            var changed = false;
            var removeIndex = -1;
            for (var i = 0; i < _excludedEdit.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                var value = EditorGUILayout.TextField(_excludedEdit[i]);
                if (value != _excludedEdit[i])
                {
                    _excludedEdit[i] = value;
                    changed = true;
                }

                if (GUILayout.Button("削除", GUILayout.Width(48f))) removeIndex = i;
                EditorGUILayout.EndHorizontal();
            }

            // 描画の途中で行を消すとレイアウトがずれることがあるので、削除は全行を描いてから行う
            if (removeIndex >= 0)
            {
                _excludedEdit.RemoveAt(removeIndex);
                changed = true;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("追加", GUILayout.Width(80f)))
            {
                _excludedEdit.Add(string.Empty);
            }
            if (GUILayout.Button("アバターから選んで追加", GUILayout.Width(160f)))
            {
                ShowComponentPicker();
            }
            EditorGUILayout.EndHorizontal();

            if (changed) yulEmoPreferences.ExcludedNdmfComponentTypes = _excludedEdit.ToArray();
        }

        /// <summary>
        /// 選択中のアバターに付いているコンポーネント（Unity 標準と 21Emo 自身を除く）の一覧を出し、
        /// 除外一覧への追加と取り消しを切り替える。
        /// </summary>
        private static void ShowComponentPicker()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
            {
                EditorUtility.DisplayDialog("21Emo", "先に、ヒエラルキーでアバター（またはその子）を選んでください。", "OK");
                return;
            }

            var root = selected.transform.root.gameObject;
            var names = new SortedSet<string>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;

                var fullName = component.GetType().FullName;
                if (string.IsNullOrEmpty(fullName)) continue;
                if (fullName.StartsWith("UnityEngine.", System.StringComparison.Ordinal)) continue;
                if (fullName.StartsWith("UnityEditor.", System.StringComparison.Ordinal)) continue;
                if (fullName.StartsWith("yulEmo.", System.StringComparison.Ordinal)) continue;
                names.Add(fullName);
            }

            if (names.Count == 0)
            {
                EditorUtility.DisplayDialog("21Emo", "追加できるコンポーネントが見つかりませんでした。", "OK");
                return;
            }

            if (_excludedEdit == null)
            {
                _excludedEdit = new List<string>(yulEmoPreferences.ExcludedNdmfComponentTypes);
            }

            var menu = new GenericMenu();
            foreach (var name in names)
            {
                var captured = name;
                menu.AddItem(new GUIContent(captured), _excludedEdit.Contains(captured), () =>
                {
                    if (_excludedEdit.Contains(captured)) _excludedEdit.Remove(captured);
                    else _excludedEdit.Add(captured);
                    yulEmoPreferences.ExcludedNdmfComponentTypes = _excludedEdit.ToArray();
                });
            }
            menu.ShowAsContext();
        }
    }
}
#endif
