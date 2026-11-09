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
    public class yulEmoFaceEditorWindow : EditorWindow
    {
        private const float LeftWidth = 380f;
        private const float RowHeight = 20f;
        private const float ListHeight = 260f;
        private const int GroupLabelWidth = 110;
        private const int MaxOverlayPoints = 2000;
        private const double ApplyInterval = 0.05;

        /// <summary>この値以下のシェイプキーの値は、設定されていないものとみなす（浮動小数点の誤差を除く）。</summary>
        private const float SeedMinWeight = 0.01f;
        private const string FolderPrefsKey = "yulEmo.FaceEditor.Folder";

        /// <summary>この距離（メッシュ上の長さ、メートル）より小さい移動は「動いていない」とみなす。区切り用のシェイプキーが持つ、ごくわずかな頂点の移動を除くため。</summary>
        private const float MinVisibleMove = 1e-4f;

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
        private readonly Dictionary<(RendererInfo, int), int[]> _movedCache = new Dictionary<(RendererInfo, int), int[]>();

        // ── プレビュー ─────────────────────────────────────────────
        private bool _rebuildQueued;
        private bool _dirty;
        private readonly yulEmoFacePreviewFilter.State _state = new yulEmoFacePreviewFilter.State();
        private int _infosVersion;
        private double _lastApply;
        private GameObject _avatarRoot;
        private double _lastRepaint;

        // 視点
        private const int PreviewSize = 640;
        private const float PreviewFov = 30f;
        private static readonly Color PreviewBackground = new Color(0.22f, 0.22f, 0.22f, 1f);
        private Vector3 _initialTarget;
        private Quaternion _initialRotation = Quaternion.identity;
        private Vector3 _target;
        private Quaternion _rotation = Quaternion.identity;
        private float _distance = 0.5f;

        // 描画
        private GameObject _cameraObject;
        private Camera _cam;
        private RenderTexture _renderTexture;
        private PreviewSession _session;
        private PreviewSession _sessionParent;
        private bool _sessionUseNdmf;
        private int _sessionInfosVersion = -1;

        private sealed class OverlayBox
        {
            public Vector3[] Points;
            public Color Color;
        }

        private readonly List<OverlayBox> _boxes = new List<OverlayBox>();
        private bool _dim;
        private bool _ndmfOffNote;

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
                    "スペース: すべての語を含む\n" +
                    "a|b: どちらかを含む\n" +
                    "-語: その語を含むものを除く\n" +
                    "@語: 名前ではなく、分類の名前から探す\n" +
                    "\"a b\": 空白を含む語\n\n" +
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
        /// 隠している分類だけに該当するときは、その分類も含めて探す。それでも無いときは、あいまい検索にする。
        /// </summary>
        private List<int> BuildVisibleList(RendererInfo info, out string note)
        {
            note = string.Empty;
            if (_query == null || _queryText != _search)
            {
                _queryText = _search;
                _query = yulEmoShapeQuery.Parse(_search);
            }

            var result = CollectShapes(info, false, false);
            if (result.Count > 0 || _query.IsEmpty) return result;

            result = CollectShapes(info, false, true);
            if (result.Count > 0)
            {
                note = "　（隠している分類も含めて探しました）";
                return result;
            }

            result = CollectShapes(info, true, true);
            if (result.Count > 0) note = "　（一致するものが無いため、あいまい検索の結果です）";
            return result;
        }

        private List<int> CollectShapes(RendererInfo info, bool fuzzy, bool includeHiddenGroups)
        {
            var found = new List<KeyValuePair<int, int>>();
            for (var i = 0; i < info.Names.Length; i++)
            {
                if (info.Meta.IsSeparator[i]) continue;
                if (!includeHiddenGroups && _hiddenGroups.Contains(info.Meta.Group[i])) continue;
                if (_onlyEditing && FindEntry(info.Path, info.Names[i]) == null) continue;

                var score = 0;
                if (!_query.IsEmpty && !_query.Matches(info.Targets[i], fuzzy, out score)) continue;
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

        /// <summary>シェイプキーを最大まで動かしたときに、位置が動く頂点の番号。結果は保存して使い回す。</summary>
        private int[] GetMovedVertices(RendererInfo info, int shape)
        {
            if (_movedCache.TryGetValue((info, shape), out var cached)) return cached;

            var mesh = info.Renderer.sharedMesh;
            var result = Array.Empty<int>();
            var frameCount = mesh.GetBlendShapeFrameCount(shape);
            if (frameCount > 0)
            {
                var count = mesh.vertexCount;
                var deltaVertices = new Vector3[count];
                var deltaNormals = new Vector3[count];
                var deltaTangents = new Vector3[count];
                mesh.GetBlendShapeFrameVertices(shape, frameCount - 1, deltaVertices, deltaNormals, deltaTangents);

                var list = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    if (deltaVertices[i].sqrMagnitude > MinVisibleMove * MinVisibleMove) list.Add(i);
                }
                result = list.ToArray();
            }

            _movedCache[(info, shape)] = result;
            return result;
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

        // ─────────────────────────────────────────────────────────────
        // プレビューに反映する値
        // 本物のアバターは変えず、値は yulEmoFacePreviewFilter（NDMF Preview のフィルター）を通して、プレビュー用のコピーにだけ設定する。
        // ─────────────────────────────────────────────────────────────
        private void OnEditorUpdate()
        {
            // NDMF Preview の差し替えは、数フレーム遅れて追いつくことがあるため、プレビューを定期的に描き直す
            if (_avatarRoot != null && EditorApplication.timeSinceStartup - _lastRepaint > 0.1)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }

            if (!_dirty) return;
            if (EditorApplication.timeSinceStartup - _lastApply < ApplyInterval) return;
            ApplyPreview();
        }

        private void ApplyPreview()
        {
            _dirty = false;
            _lastApply = EditorApplication.timeSinceStartup;

            // プレビューに反映する値: 編集リスト + 乗せているシェイプキー（100）
            var perRenderer = new Dictionary<Renderer, List<KeyValuePair<string, float>>>();
            void Add(RendererInfo info, string shapeName, float value)
            {
                if (!perRenderer.TryGetValue(info.Renderer, out var list))
                {
                    list = new List<KeyValuePair<string, float>>();
                    perRenderer[info.Renderer] = list;
                }
                list.Add(new KeyValuePair<string, float>(shapeName, value));
            }

            foreach (var entry in _entries)
            {
                var info = FindInfo(entry.Path);
                if (info == null || !info.NameToIndex.ContainsKey(entry.ShapeName)) continue;
                Add(info, entry.ShapeName, entry.Value);
            }
            if (TryGetHover(out var hoverInfo, out var hoverName) && FindEntry(hoverInfo.Path, hoverName) == null)
            {
                Add(hoverInfo, hoverName, 100f);
            }

            var weights = new Dictionary<Renderer, KeyValuePair<string, float>[]>();
            foreach (var pair in perRenderer) weights[pair.Key] = pair.Value.ToArray();
            _state.Weights = weights;

            RefreshOverlay();
            Repaint();
        }

        private bool TryGetHover(out RendererInfo info, out string shapeName)
        {
            info = null;
            shapeName = null;
            if (_hoverShape < 0 || _rendererIndex < 0 || _rendererIndex >= _infos.Count) return false;

            info = _infos[_rendererIndex];
            if (_hoverShape >= info.Names.Length) return false;
            shapeName = info.Names[_hoverShape];
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // プレビューの表示・カメラ
        // ─────────────────────────────────────────────────────────────
        private void ResetView()
        {
            _target = _initialTarget;
            _rotation = _initialRotation;
            _distance = 0.5f;
        }

        private void DrawPreview(Event e)
        {
            // Layout と Repaint で表示が食い違わないように、判定は Layout のときだけ行う
            if (e.type == EventType.Layout)
            {
                _ndmfOffNote = yulEmoPreferences.UseNdmfPreview && PreviewSession.Current == null;
            }
            if (_ndmfOffNote)
            {
                EditorGUILayout.HelpBox("NDMF のメニューで NDMF Preview が無効になっているため、Modular Avatar などの処理は反映されません。", MessageType.Info);
            }

            var rect = GUILayoutUtility.GetAspectRect(1f);
            if (rect.width < 16f) rect = new Rect(rect.x, rect.y, 16f, 16f);

            HandlePreviewInput(e, rect);

            if (e.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, PreviewBackground);
            if (_avatarRoot == null) return;

            // NDMF Preview は、再生中とプレハブの編集画面では動かない。その間は、表情をプレビューに反映できない。
            if (EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                var style = new GUIStyle(EditorStyles.wordWrappedLabel) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(rect, "再生中とプレハブの編集画面では、NDMF Preview が動かないため、\nプレビューに表情を反映できません。", style);
                return;
            }

            if (!EnsureCamera()) return;
            EnsureSession();
            RenderPreview();

            GUI.DrawTexture(rect, _renderTexture, ScaleMode.StretchToFill, false);
            if (_dim) EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.35f));
            DrawBoxes(rect);
        }

        private void HandlePreviewInput(Event e, Rect rect)
        {
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDrag)
            {
                if (e.button == 0)
                {
                    // 水平方向はワールドの上向き軸まわり、垂直方向はカメラの横軸まわりに回す
                    _rotation = Quaternion.AngleAxis(e.delta.x * 0.3f, Vector3.up) * _rotation;
                    _rotation = _rotation * Quaternion.AngleAxis(e.delta.y * 0.3f, Vector3.right);
                    e.Use();
                    Repaint();
                }
                else if (e.button == 1 || e.button == 2)
                {
                    // 画面上の 1 ピクセルに対応するワールド上の長さ
                    var unitsPerPixel = 2f * _distance * Mathf.Tan(PreviewFov * 0.5f * Mathf.Deg2Rad) / rect.height;
                    _target += (_rotation * Vector3.left) * (e.delta.x * unitsPerPixel);
                    _target += (_rotation * Vector3.up) * (e.delta.y * unitsPerPixel);
                    e.Use();
                    Repaint();
                }
            }
            else if (e.type == EventType.ScrollWheel)
            {
                // 下へスクロールすると遠ざかる
                _distance = Mathf.Clamp(_distance * (1f + e.delta.y * 0.05f), 0.05f, 3f);
                e.Use();
                Repaint();
            }
        }

        private bool EnsureCamera()
        {
            if (_cam == null)
            {
                // 親を持たない普通のカメラにする（NDMF Preview は、親の無いカメラを対象にする）
                _cameraObject = new GameObject("21Emo Face Editor Camera") { hideFlags = HideFlags.HideAndDontSave };
                _cam = _cameraObject.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = PreviewBackground;
                _cam.cullingMask = ~0;
                _cam.allowHDR = false;
                _cam.fieldOfView = PreviewFov;
                _cam.aspect = 1f;
            }

            if (_renderTexture == null)
            {
                _renderTexture = new RenderTexture(PreviewSize, PreviewSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _renderTexture.Create();
            }
            return _cam != null && _renderTexture != null;
        }

        private void RenderPreview()
        {
            _cam.transform.SetPositionAndRotation(_target - _rotation * Vector3.forward * _distance, _rotation);

            // 近くの物だけが映るようにする（シーンにある背景やほかのアバターを避ける）
            _cam.nearClipPlane = Mathf.Max(0.01f, _distance - 0.4f);
            _cam.farClipPlane = _distance + 0.4f;

            _cam.targetTexture = _renderTexture;
            _cam.Render();
            _cam.targetTexture = null;
        }

        /// <summary>
        /// このカメラ専用の NDMF Preview のセッションを用意する。
        /// 「NDMF Preview を使う」がオンで、NDMF 全体のプレビューが有効なときは、それを引き継いで、表情のフィルターを最後に足す。
        /// そうでないときは、空のセッションに表情のフィルターだけを足す（NDMF の処理は反映されない）。
        /// </summary>
        private void EnsureSession()
        {
            var use = yulEmoPreferences.UseNdmfPreview;
            var parent = use ? PreviewSession.Current : null;

            if (_session != null && _sessionUseNdmf == use && _sessionParent == parent && _sessionInfosVersion == _infosVersion) return;

            DisposeSession();

            _session = parent != null ? parent.Fork("21Emo Face Editor") : new PreviewSession();
            var renderers = new List<SkinnedMeshRenderer>();
            foreach (var info in _infos) renderers.Add(info.Renderer);
            _session.AddMutator(new SequencePoint(), new yulEmoFacePreviewFilter(renderers, _state));
            _session.OverrideCamera(_cam);

            _sessionUseNdmf = use;
            _sessionParent = parent;
            _sessionInfosVersion = _infosVersion;
        }

        private void DisposeSession()
        {
            if (_cam != null) PreviewSession.ClearCameraOverride(_cam);
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
            _sessionParent = null;
            _sessionInfosVersion = -1;
        }

        private void DrawBoxes(Rect previewRect)
        {
            foreach (var box in _boxes)
            {
                if (box == null || box.Points == null || box.Points.Length == 0) continue;

                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);
                var any = false;
                foreach (var point in box.Points)
                {
                    var viewport = _cam.WorldToViewportPoint(point);
                    if (viewport.z <= 0f) continue;

                    var gui = new Vector2(
                        previewRect.x + viewport.x * previewRect.width,
                        previewRect.y + (1f - viewport.y) * previewRect.height);
                    min = Vector2.Min(min, gui);
                    max = Vector2.Max(max, gui);
                    any = true;
                }
                if (!any) continue;

                const float pad = 4f;
                var xMin = Mathf.Max(min.x - pad, previewRect.xMin);
                var yMin = Mathf.Max(min.y - pad, previewRect.yMin);
                var xMax = Mathf.Min(max.x + pad, previewRect.xMax);
                var yMax = Mathf.Min(max.y + pad, previewRect.yMax);
                if (xMax <= xMin || yMax <= yMin) continue;

                GUI.DrawTexture(
                    Rect.MinMaxRect(xMin, yMin, xMax, yMax),
                    Texture2D.whiteTexture,
                    ScaleMode.StretchToFill,
                    false,
                    0f,
                    box.Color,
                    2f,
                    0f);
            }
        }

        private void TeardownCamera()
        {
            DisposeSession();

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                DestroyImmediate(_renderTexture);
                _renderTexture = null;
            }
            if (_cameraObject != null)
            {
                DestroyImmediate(_cameraObject);
                _cameraObject = null;
            }
            _cam = null;
        }

        // ─────────────────────────────────────────────────────────────
        // 動く範囲の枠線
        // ─────────────────────────────────────────────────────────────
        private void RefreshOverlay()
        {
            var boxes = new List<OverlayBox>();
            var dim = false;

            var baked = new Dictionary<RendererInfo, Mesh>();
            try
            {
                if (_showSelectedBoxes)
                {
                    foreach (var entry in _entries)
                    {
                        var info = FindInfo(entry.Path);
                        if (info == null || !info.NameToIndex.TryGetValue(entry.ShapeName, out var index)) continue;
                        if (TryCollectPoints(info, index, baked, out var points))
                        {
                            boxes.Add(new OverlayBox { Points = points, Color = yulEmoPreferences.SelectedBoxColor });
                        }
                    }
                }

                if (TryGetHover(out var hoverInfo, out var hoverName))
                {
                    var index = _hoverShape;
                    if (TryCollectPoints(hoverInfo, index, baked, out var points))
                    {
                        boxes.Add(new OverlayBox { Points = points, Color = yulEmoPreferences.HoverBoxColor });
                    }

                    // 動く頂点が 1 つも無いシェイプキーは、枠線を出さず、プレビューを暗くして知らせる
                    try
                    {
                        dim = GetMovedVertices(hoverInfo, index).Length == 0;
                    }
                    catch (Exception)
                    {
                        dim = false;
                    }
                }
            }
            finally
            {
                foreach (var mesh in baked.Values)
                {
                    if (mesh != null) DestroyImmediate(mesh);
                }
            }

            _boxes.Clear();
            _boxes.AddRange(boxes);
            _dim = dim;
        }

        /// <summary>動く頂点の現在のワールド座標を集める（多いときは間引く）。</summary>
        private bool TryCollectPoints(RendererInfo info, int shape, Dictionary<RendererInfo, Mesh> baked, out Vector3[] points)
        {
            points = null;
            try
            {
                var moved = GetMovedVertices(info, shape);
                if (moved.Length == 0) return false;

                if (!baked.TryGetValue(info, out var mesh))
                {
                    mesh = new Mesh();
                    info.Renderer.BakeMesh(mesh, true);
                    baked[info] = mesh;
                }
                if (mesh.vertexCount != info.Renderer.sharedMesh.vertexCount) return false;

                var vertices = mesh.vertices;
                var transform = info.Renderer.transform;
                var matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

                var stride = Mathf.Max(1, moved.Length / MaxOverlayPoints);
                var list = new List<Vector3>();
                for (var i = 0; i < moved.Length; i += stride)
                {
                    list.Add(matrix.MultiplyPoint3x4(vertices[moved[i]]));
                }
                points = list.ToArray();
                return points.Length > 0;
            }
            catch (Exception ex)
            {
                // メッシュが読み取れないときなどは、枠線を出さないだけにする
                Debug.LogWarning("[21Emo] 動く範囲を計算できませんでした: " + ex.Message);
                return false;
            }
        }

    }
}
#endif
