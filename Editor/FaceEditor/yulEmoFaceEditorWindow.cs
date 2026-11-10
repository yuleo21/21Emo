#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>
    /// 顔のプレビューを見ながらシェイプキーを選び、1 フレームの .anim として書き出すウィンドウ。
    ///
    /// ・左の一覧のシェイプキーにマウスを乗せると、プレビューに 100 で一時的に反映する。
    /// ・クリックすると編集リストに 100 で追加され、スライダーで 0〜100 に調整できる。
    /// ・「書き出す」で、編集リストの値をキー 1 つのクリップにして保存する。
    ///
    /// プレビューは、このウィンドウの右側に表示する。シーンにある本物のアバターを、専用のカメラで撮って見せる。
    /// 専用のカメラは親を持たない普通のカメラなので、Scene ビューと同じように NDMF Preview が効く（NDMF の ProxyManager が対象にする）。
    /// 本物のアバターには触らない。表情は、NDMF Preview が描画する「プレビュー用のコピー」にだけ、フィルター（yulEmoFacePreviewFilter）で設定する。
    ///
    /// プレビュー上の操作:  左ドラッグ = 回転 / 右・中ドラッグ = 平行移動 / ホイール = 拡大・縮小
    /// </summary>
    public partial class yulEmoFaceEditorWindow : EditorWindow
    {
        private const float LeftWidth = 380f;
        private const float RowHeight = 20f;
        private const float ListHeight = 260f;
        private const int GroupLabelWidth = 110;

        /// <summary>この値以下のシェイプキーの値は、設定されていないものとみなす（浮動小数点の誤差を除く）。</summary>
        private const float SeedMinWeight = 0.01f;
        private const string FolderPrefsKey = "yulEmo.FaceEditor.Folder";


        // ── 画面の状態 ─────────────────────────────────────────────
        private class RendererInfo
        {
            public SkinnedMeshRenderer Renderer;
            public string Path;
            public string[] Names;
            public Dictionary<string, int> NameToIndex;
            public yulEmoShapeClassifier.Result Meta;
            public yulEmoShapeTarget[] Targets;
        }

        [Serializable]
        private class Entry
        {
            public string Path;
            public string ShapeName;
            public float Value;
        }

        [SerializeField] private GameObject _avatar;
        [SerializeField] private string _fileName = string.Empty;
        [SerializeField] private bool _showSelectedBoxes;
        [SerializeField] private string _search = string.Empty;
        // 一覧に出さない分類。VRChat と MMD は、標準で隠す
        [SerializeField] private List<string> _hiddenGroups = DefaultHiddenGroups();
        [SerializeField] private bool _onlyEditing;
        [SerializeField] private bool _showEntries = true;
        [SerializeField] private List<Entry> _entries = new List<Entry>();
        // 編集リストに、アバターにすでに設定されている値を取り込み済みのアバター（アバターごとに 1 回だけ取り込む）
        [SerializeField] private GameObject _seededAvatar;

        private string _outputFolder;
        private int _rendererIndex;
        private Vector2 _listScroll;
        private Vector2 _entryScroll;
        private string _status;
        private GUIStyle _rowStyle;
        private GUIStyle _groupLabelStyle;
        private string _queryText;
        private yulEmoShapeQuery _query;

        private int _hoverShape = -1;
        private int _hoverCandidate = -1;

        private readonly List<RendererInfo> _infos = new List<RendererInfo>();


        [MenuItem("21tools/21Emo Face Editor")]
        public static void Open()
        {
            Open(null);
        }

        public static void Open(GameObject avatar)
        {
            var window = GetWindow<yulEmoFaceEditorWindow>("21Emo Face Editor");
            window.minSize = new Vector2(780f, 560f);
            if (avatar == null && Selection.activeGameObject != null)
            {
                var descriptor = Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
                if (descriptor != null) avatar = descriptor.gameObject;
            }
            if (avatar != null && avatar != window._avatar)
            {
                window._avatar = avatar;
                window._entries.Clear();
                window._seededAvatar = null;
                window._hiddenGroups = DefaultHiddenGroups();
            }
            window.QueueRebuild();
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            _outputFolder = EditorPrefs.GetString(FolderPrefsKey, "Assets");
            EditorApplication.update += OnEditorUpdate;

            QueueRebuild();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;

            TeardownCamera();
        }

        private void OnFocus()
        {
            // 設定（枠線の色など）を変えたあとに戻ってきたとき、枠線を描き直す
            _dirty = true;
            Repaint();
        }

        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            // アバターの調査は OnGUI の途中では行わず、OnGUI の後に実行する
            EditorApplication.delayCall += Rebuild;
        }

        // ─────────────────────────────────────────────────────────────
        // 画面
        // ─────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            if (_rowStyle == null)
            {
                // 右側に、分類の名前を重ねて出すため、文字の右に余白を空ける
                _rowStyle = new GUIStyle(GUI.skin.button)
                {
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip,
                    padding = new RectOffset(6, GroupLabelWidth + 8, 2, 2),
                };
                _groupLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                    clipping = TextClipping.Clip,
                };
                _groupLabelStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f, 1f);
            }

            var e = Event.current;
            if (e.type == EventType.MouseMove) Repaint();
            if (e.type == EventType.MouseLeaveWindow)
            {
                if (_hoverShape != -1)
                {
                    _hoverShape = -1;
                    _dirty = true;
                }
                Repaint();
            }
            if (e.type == EventType.Repaint) _hoverCandidate = -1;

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(LeftWidth)))
                {
                    DrawAvatarField();
                    DrawShapeList(e);
                    DrawEntries();
                    DrawExport();
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    DrawPreview(e);
                }
            }

            if (e.type == EventType.Repaint && _hoverCandidate != _hoverShape)
            {
                _hoverShape = _hoverCandidate;
                _dirty = true;
            }
        }

        private void DrawAvatarField()
        {
            EditorGUI.BeginChangeCheck();
            _avatar = (GameObject)EditorGUILayout.ObjectField("アバター", _avatar, typeof(GameObject), true);
            var changed = EditorGUI.EndChangeCheck();
            if (changed)
            {
                _entries.Clear();
                _seededAvatar = null;
                _hiddenGroups = DefaultHiddenGroups();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("アバターをリロード")) changed = true;
                if (GUILayout.Button("視点をリセット")) ResetView();
                if (GUILayout.Button("設定", GUILayout.Width(50f))) SettingsService.OpenUserPreferences(yulEmoPreferencesProvider.SettingsPath);
            }
            if (changed) QueueRebuild();

            EditorGUI.BeginChangeCheck();
            _showSelectedBoxes = EditorGUILayout.ToggleLeft("編集中のシェイプキーの範囲も囲む", _showSelectedBoxes);
            if (EditorGUI.EndChangeCheck()) _dirty = true;

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            }
        }

        private static List<string> DefaultHiddenGroups()
        {
            return new List<string> { yulEmoShapeClassifier.VrchatGroup, yulEmoShapeClassifier.MmdGroup };
        }

        private void ClearHover()
        {
            if (_hoverShape == -1) return;
            _hoverShape = -1;
            _dirty = true;
        }

        private void DrawShapeList(Event e)
        {
            EditorGUILayout.LabelField("シェイプキー", EditorStyles.boldLabel);
            if (_infos.Count == 0)
            {
                EditorGUILayout.LabelField("アバターを読み込むと、ここに一覧が出ます。", EditorStyles.miniLabel);
                GUILayout.Space(ListHeight);
                return;
            }

            // 対象のメッシュ
            var labels = new string[_infos.Count];
            for (var i = 0; i < _infos.Count; i++) labels[i] = _infos[i].Path;
            EditorGUI.BeginChangeCheck();
            _rendererIndex = Mathf.Clamp(EditorGUILayout.Popup("メッシュ", _rendererIndex, labels), 0, _infos.Count - 1);
            _search = EditorGUILayout.TextField(
                new GUIContent("検索",
                    "各語は正規表現として探します（^ や $、.、| など）。\n" +
                    "スペース: すべての語を含む\n" +
                    "-語 / !語: その語を含むものを除く\n" +
                    "@語: 名前ではなく、分類の名前から探す\n" +
                    "空白を含む語は \\s や [ ] で書きます。\n\n" +
                    "全角・半角、大文字・小文字、カタカナ・ひらがなは区別しません。\n" +
                    "「目」と eye のような、日本語と英語の対応語も探します。"),
                _search);
            if (EditorGUI.EndChangeCheck())
            {
                _listScroll = Vector2.zero;
                ClearHover();
            }

            var info = _infos[_rendererIndex];
            DrawFilterOptions(info);

            var visible = BuildVisibleList(info, out var note);
            var shapeCount = info.Names.Length - info.Meta.SeparatorCount;
            EditorGUILayout.LabelField($"{visible.Count} 個を表示（全 {shapeCount} 個）{note}", EditorStyles.miniLabel);

            var showGroup = info.Meta.GroupOrder.Count > 1;
            var listRect = GUILayoutUtility.GetRect(0f, ListHeight, GUILayout.ExpandWidth(true));
            var mouseInView = listRect.Contains(e.mousePosition);
            var contentRect = new Rect(0f, 0f, listRect.width - 16f, visible.Count * RowHeight);

            _listScroll = GUI.BeginScrollView(listRect, _listScroll, contentRect);
            var first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / RowHeight));
            var last = Mathf.Min(visible.Count - 1, Mathf.CeilToInt((_listScroll.y + listRect.height) / RowHeight));
            for (var row = first; row <= last; row++)
            {
                var shapeIndex = visible[row];
                var rowRect = new Rect(0f, row * RowHeight, contentRect.width, RowHeight);
                var selected = FindEntry(info.Path, info.Names[shapeIndex]) != null;
                var text = (selected ? "● " : "    ") + info.Names[shapeIndex];

                if (e.type == EventType.Repaint && mouseInView && rowRect.Contains(e.mousePosition))
                {
                    _hoverCandidate = shapeIndex;
                }

                if (GUI.Button(rowRect, text, _rowStyle))
                {
                    AddEntry(info.Path, info.Names[shapeIndex]);
                }

                if (showGroup && e.type == EventType.Repaint)
                {
                    var groupRect = new Rect(rowRect.xMax - GroupLabelWidth - 4f, rowRect.y, GroupLabelWidth, rowRect.height);
                    GUI.Label(groupRect, info.Meta.Group[shapeIndex], _groupLabelStyle);
                }
            }
            GUI.EndScrollView();
        }

        /// <summary>
        /// 「編集中のみ」の切り替えと、分類の表示・非表示を選ぶドロップダウン。
        /// 分類の選択は MaskField（Camera の Culling Mask と同じ形式）で、開いたまま複数を切り替えられる。
        /// MaskField は 32 個までしか扱えないため、分類がそれより多いときは、33 個目以降を表示したままにする。
        /// </summary>
        private void DrawFilterOptions(RendererInfo info)
        {
            var groups = info.Meta.GroupOrder;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _onlyEditing = GUILayout.Toggle(_onlyEditing, "編集中のみ", EditorStyles.miniButton, GUILayout.Width(80f));
                if (EditorGUI.EndChangeCheck())
                {
                    _listScroll = Vector2.zero;
                    ClearHover();
                }

                if (groups.Count == 0) return;

                // 表示中の分類のビットを立てる
                var mask = 0;
                var maskCount = Mathf.Min(groups.Count, 32);
                for (var i = 0; i < maskCount; i++)
                {
                    if (!_hiddenGroups.Contains(groups[i])) mask |= 1 << i;
                }

                EditorGUI.BeginChangeCheck();
                var newMask = EditorGUILayout.MaskField(mask, groups.ToArray());
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyGroupMask(info, newMask);
                    _listScroll = Vector2.zero;
                    ClearHover();
                }
            }
        }

        /// <summary>MaskField の結果を、隠す分類の一覧に反映する（「すべて表示」は -1 で返る）。</summary>
        private void ApplyGroupMask(RendererInfo info, int mask)
        {
            var groups = info.Meta.GroupOrder;
            var maskCount = Mathf.Min(groups.Count, 32);
            _hiddenGroups.Clear();
            for (var i = 0; i < maskCount; i++)
            {
                if ((mask & (1 << i)) == 0) _hiddenGroups.Add(groups[i]);
            }
        }

        /// <summary>
        /// 一覧に出すシェイプキーの番号を返す。
        /// 検索語があるときは、見つかった順（名前の全体に一致 → 頭に一致 → 単語の頭に一致 → 途中に一致）に並べる。
        /// 隠している分類だけに該当するときは、その分類も含めて探す。
        /// </summary>
        private List<int> BuildVisibleList(RendererInfo info, out string note)
        {
            note = string.Empty;
            if (_query == null || _queryText != _search)
            {
                _queryText = _search;
                _query = yulEmoShapeQuery.Parse(_search);
            }

            var result = CollectShapes(info, false);
            if (result.Count == 0 && !_query.IsEmpty)
            {
                result = CollectShapes(info, true);
                if (result.Count > 0) note = "　（隠している分類も含めて探しました）";
            }

            if (_query.HasInvalidPattern) note += "　（正規表現として読めない語は、文字どおりに探しました）";
            return result;
        }

        private List<int> CollectShapes(RendererInfo info, bool includeHiddenGroups)
        {
            var found = new List<KeyValuePair<int, int>>();
            for (var i = 0; i < info.Names.Length; i++)
            {
                if (info.Meta.IsSeparator[i]) continue;
                if (!includeHiddenGroups && _hiddenGroups.Contains(info.Meta.Group[i])) continue;
                if (_onlyEditing && FindEntry(info.Path, info.Names[i]) == null) continue;

                var score = 0;
                if (!_query.IsEmpty && !_query.Matches(info.Targets[i], out score)) continue;
                found.Add(new KeyValuePair<int, int>(i, score));
            }

            if (!_query.IsEmpty)
            {
                found.Sort((x, y) => x.Value != y.Value ? x.Value.CompareTo(y.Value) : x.Key.CompareTo(y.Key));
            }

            var indices = new List<int>(found.Count);
            foreach (var pair in found) indices.Add(pair.Key);
            return indices;
        }

        private void DrawEntries()
        {
            EditorGUILayout.Space(4);
            _showEntries = EditorGUILayout.Foldout(
                _showEntries, $"編集中のシェイプキー（{_entries.Count}）", true, EditorStyles.foldoutHeader);

            if (!_showEntries) return;

            if (_entries.Count == 0)
            {
                EditorGUILayout.LabelField("上の一覧をクリックすると、ここに追加されます。", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                return;
            }

            var removeAt = -1;
            _entryScroll = EditorGUILayout.BeginScrollView(_entryScroll);
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(entry.ShapeName, entry.Path + " / " + entry.ShapeName), GUILayout.Width(140f));
                    EditorGUI.BeginChangeCheck();
                    var value = EditorGUILayout.Slider(entry.Value, 0f, 100f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        entry.Value = value;
                        _dirty = true;
                    }
                    if (GUILayout.Button("×", GUILayout.Width(22f))) removeAt = i;
                }
            }
            EditorGUILayout.EndScrollView();

            if (removeAt >= 0)
            {
                _entries.RemoveAt(removeAt);
                _dirty = true;
            }
        }

        private void DrawExport()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("書き出し（1 フレーム）", EditorStyles.boldLabel);

            var defaultName = BuildDefaultName();
            _fileName = EditorGUILayout.TextField(
                new GUIContent("ファイル名", "空のときは「" + defaultName + "」になります（拡張子 .anim は自動で付きます）。"),
                _fileName);
            if (string.IsNullOrWhiteSpace(_fileName))
            {
                EditorGUILayout.LabelField("空のとき: " + defaultName + ".anim", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _outputFolder = EditorGUILayout.TextField("保存先フォルダ", _outputFolder);
                if (GUILayout.Button("選択", GUILayout.Width(44f)))
                {
                    var picked = EditorUtility.OpenFolderPanel("保存先フォルダ", Application.dataPath, string.Empty);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        picked = picked.Replace('\\', '/');
                        var dataPath = Application.dataPath.Replace('\\', '/');
                        if (picked == dataPath) _outputFolder = "Assets";
                        else if (picked.StartsWith(dataPath + "/")) _outputFolder = "Assets" + picked.Substring(dataPath.Length);
                        else EditorUtility.DisplayDialog("21Emo", "プロジェクトの Assets 以下のフォルダを選んでください。", "OK");
                        GUI.FocusControl(null);
                    }
                }
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(FolderPrefsKey, _outputFolder);
            }

            using (new EditorGUI.DisabledScope(_entries.Count == 0))
            {
                if (GUILayout.Button("書き出す", GUILayout.Height(28f)))
                {
                    // ダイアログやアセット保存を OnGUI の途中で行わない
                    EditorApplication.delayCall += Export;
                }
            }
        }

        private Entry FindEntry(string path, string shapeName)
        {
            foreach (var entry in _entries)
            {
                if (entry.Path == path && entry.ShapeName == shapeName) return entry;
            }
            return null;
        }

        private void AddEntry(string path, string shapeName)
        {
            var existing = FindEntry(path, shapeName);
            if (existing != null)
            {
                existing.Value = 100f;
            }
            else
            {
                _entries.Add(new Entry { Path = path, ShapeName = shapeName, Value = 100f });
            }
            _dirty = true;
        }

        // ─────────────────────────────────────────────────────────────
        // 書き出し
        // ─────────────────────────────────────────────────────────────
        private string BuildDefaultName()
        {
            var avatarName = _avatar != null ? Sanitize(_avatar.name) : string.Empty;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return string.IsNullOrEmpty(avatarName) ? $"21Face_{stamp}" : $"{avatarName}_21Face_{stamp}";
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            return name.Trim();
        }

        private void Export()
        {
            if (_entries.Count == 0) return;

            // アバターに存在しないシェイプキー（名前の変更やメッシュの入れ替えで古くなったもの）は、書き出さない
            var valid = new List<Entry>();
            var skipped = new List<Entry>();
            foreach (var entry in _entries)
            {
                var info = FindInfo(entry.Path);
                if (info != null && info.NameToIndex.ContainsKey(entry.ShapeName)) valid.Add(entry);
                else skipped.Add(entry);
            }
            if (valid.Count == 0)
            {
                EditorUtility.DisplayDialog("21Emo", "アバターに存在するシェイプキーがありません。アバターをリロードして、追加し直してください。", "OK");
                return;
            }

            var folder = (_outputFolder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(folder) || !(folder == "Assets" || folder.StartsWith("Assets/")))
            {
                EditorUtility.DisplayDialog("21Emo", "保存先フォルダを Assets 以下のパスで指定してください。", "OK");
                return;
            }
            yulEmoEditorUtility.EnsureFolder(folder);

            var name = Sanitize(_fileName);
            if (name.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5);
            if (string.IsNullOrEmpty(name)) name = BuildDefaultName();

            var clip = new AnimationClip { name = name, frameRate = 60f };
            foreach (var entry in valid)
            {
                var binding = EditorCurveBinding.FloatCurve(entry.Path, typeof(SkinnedMeshRenderer), "blendShape." + entry.ShapeName);
                AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0f, entry.Value)));
            }

            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.anim");
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(clip);

            _status = $"書き出しました: {assetPath}（{valid.Count} 個のシェイプキー）";
            if (skipped.Count > 0)
            {
                var names = new List<string>();
                foreach (var entry in skipped) names.Add(entry.Path + " / " + entry.ShapeName);
                _status += $"\nアバターに見つからないシェイプキーが {skipped.Count} 個あったため、書き出しから除外しました。";
                Debug.LogWarning("[21Emo] 書き出しから除外したシェイプキー: " + string.Join(", ", names));
            }
            Debug.Log("[21Emo] " + _status);
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────
        // アバターの調査
        // ─────────────────────────────────────────────────────────────
        private void Rebuild()
        {
            _rebuildQueued = false;
            _state.Weights = new Dictionary<Renderer, KeyValuePair<string, float>[]>();
            _infos.Clear();
            _movedCache.Clear();
            _avatarRoot = null;

            if (_avatar == null)
            {
                _status = "アバターを指定してください。";
                Repaint();
                return;
            }

            var descriptor = yulEmoEditorUtility.FindDescriptor(_avatar);
            if (descriptor == null)
            {
                _status = "VRCAvatarDescriptor が見つかりません。";
                Repaint();
                return;
            }
            if (!descriptor.gameObject.scene.IsValid() || EditorUtility.IsPersistent(descriptor.gameObject))
            {
                _status = "シーン上のアバターを指定してください（プロジェクト内のプレハブアセットは指定できません）。";
                Repaint();
                return;
            }

            _avatarRoot = descriptor.gameObject;
            CollectRenderers(descriptor);
            _infosVersion++;

            // アバターが変わったときだけ、すでに設定されている値を編集リストに取り込む（消したエントリーを復活させない）
            if (_seededAvatar != _avatarRoot)
            {
                SeedEntriesFromAvatar();
                _seededAvatar = _avatarRoot;
            }

            // 視点の初期位置: アバターの目の位置を、アバターの正面から見る
            _initialTarget = descriptor.transform.TransformPoint(descriptor.ViewPosition);
            _initialRotation = Quaternion.LookRotation(-descriptor.transform.forward, Vector3.up) * Quaternion.Euler(-5f, 0f, 0f);
            ResetView();

            _status = _infos.Count == 0 ? "シェイプキーを持つメッシュが見つかりません。" : null;

            _dirty = true;
            Repaint();
        }

        private void CollectRenderers(VRCAvatarDescriptor descriptor)
        {
            var root = descriptor.transform;
            foreach (var renderer in descriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                var info = new RendererInfo
                {
                    Renderer = renderer,
                    Path = AnimationUtility.CalculateTransformPath(renderer.transform, root),
                    Names = new string[mesh.blendShapeCount],
                    NameToIndex = new Dictionary<string, int>(),
                };
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    info.Names[i] = mesh.GetBlendShapeName(i);
                    info.NameToIndex[info.Names[i]] = i;
                }

                // 分類（区切り・VRChat・MMD）と、検索用の情報
                info.Meta = yulEmoShapeClassifier.Classify(info.Names);
                info.Targets = new yulEmoShapeTarget[info.Names.Length];
                for (var i = 0; i < info.Names.Length; i++)
                {
                    info.Targets[i] = yulEmoShapeTarget.Create(info.Names[i], info.Meta.Group[i]);
                }
                _infos.Add(info);
            }

            // 最初に表示するメッシュ: リップシンクで使うメッシュ → 無ければ先頭
            _rendererIndex = 0;
            var visemeMesh = descriptor.VisemeSkinnedMesh;
            if (visemeMesh != null)
            {
                var visemePath = AnimationUtility.CalculateTransformPath(visemeMesh.transform, root);
                for (var i = 0; i < _infos.Count; i++)
                {
                    if (_infos[i].Path == visemePath)
                    {
                        _rendererIndex = i;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// アバターのレンダラーにすでに設定されているシェイプキーの値を、編集リストに取り込む。
        /// 取り込むのは値を読むだけで、アバターは変えない。すでにリストにあるシェイプキーは、リストの値を優先する。
        /// </summary>
        private void SeedEntriesFromAvatar()
        {
            foreach (var info in _infos)
            {
                for (var i = 0; i < info.Names.Length; i++)
                {
                    var weight = info.Renderer.GetBlendShapeWeight(i);
                    if (weight <= SeedMinWeight) continue;
                    if (FindEntry(info.Path, info.Names[i]) != null) continue;
                    _entries.Add(new Entry { Path = info.Path, ShapeName = info.Names[i], Value = weight });
                }
            }
        }

        private RendererInfo FindInfo(string path)
        {
            foreach (var info in _infos)
            {
                if (info.Path == path) return info;
            }
            return null;
        }

    }
}
#endif
