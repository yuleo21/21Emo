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
    /// 表情は、本物のアバターのシェイプキーの値を一時的に変えて反映する。
    /// 専用のカメラは親を持たない普通のカメラなので、Scene ビューと同じように NDMF Preview が効く（NDMF の ProxyManager が対象にする）。
    /// 変えた値は、覚えておいた元の値に戻す（ウィンドウを閉じるとき、シーンの保存、再生、コードの再読み込みの前）。
    ///
    /// プレビュー上の操作:  左ドラッグ = 回転 / 右・中ドラッグ = 平行移動 / ホイール = 拡大・縮小
    /// </summary>
    public class yulEmoFaceEditorWindow : EditorWindow
    {
        private const float LeftWidth = 380f;
        private const float RowHeight = 20f;
        private const float ListHeight = 260f;
        private const int MaxOverlayPoints = 2000;
        private const double ApplyInterval = 0.05;
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
        [SerializeField] private List<Entry> _entries = new List<Entry>();

        private string _outputFolder;
        private int _rendererIndex;
        private Vector2 _listScroll;
        private Vector2 _entryScroll;
        private string _status;
        private GUIStyle _rowStyle;

        private int _hoverShape = -1;
        private int _hoverCandidate = -1;

        private readonly List<RendererInfo> _infos = new List<RendererInfo>();
        private readonly Dictionary<(RendererInfo, int), int[]> _movedCache = new Dictionary<(RendererInfo, int), int[]>();

        // ── プレビュー ─────────────────────────────────────────────
        private bool _rebuildQueued;
        private bool _dirty;
        private readonly Dictionary<(SkinnedMeshRenderer, int), float> _touched = new Dictionary<(SkinnedMeshRenderer, int), float>();
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
        private PreviewSession _emptySession;
        private bool? _appliedUseNdmfPreview;

        private sealed class OverlayBox
        {
            public Vector3[] Points;
            public Color Color;
        }

        private readonly List<OverlayBox> _boxes = new List<OverlayBox>();
        private bool _dim;

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
            }
            window.QueueRebuild();
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            _outputFolder = EditorPrefs.GetString(FolderPrefsKey, "Assets");

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += RestoreWeights;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;

            QueueRebuild();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= RestoreWeights;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;

            RestoreWeights();
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
                _rowStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
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
            if (changed) _entries.Clear();

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
            _search = EditorGUILayout.TextField("検索", _search);
            if (EditorGUI.EndChangeCheck())
            {
                _listScroll = Vector2.zero;
                if (_hoverShape != -1)
                {
                    _hoverShape = -1;
                    _dirty = true;
                }
            }

            var info = _infos[_rendererIndex];
            var visible = new List<int>();
            for (var i = 0; i < info.Names.Length; i++)
            {
                if (string.IsNullOrEmpty(_search) ||
                    info.Names[i].IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    visible.Add(i);
                }
            }

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
            }
            GUI.EndScrollView();
        }

        private void DrawEntries()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("編集中のシェイプキー", EditorStyles.boldLabel);

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
            foreach (var entry in _entries)
            {
                var binding = EditorCurveBinding.FloatCurve(entry.Path, typeof(SkinnedMeshRenderer), "blendShape." + entry.ShapeName);
                AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0f, entry.Value)));
            }

            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.anim");
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(clip);

            _status = $"書き出しました: {assetPath}（{_entries.Count} 個のシェイプキー）";
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
            RestoreWeights();
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

        private RendererInfo FindInfo(string path)
        {
            foreach (var info in _infos)
            {
                if (info.Path == path) return info;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // プレビュー（本物のアバターのシェイプキーの値を、一時的に変える）
        // AnimationMode でクリップを再生する方式は、アバターの姿勢や位置が変わってしまうため使わない。
        // シェイプキーの値だけを直接変え、元の値を覚えておいて戻す。
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

            if (_avatarRoot == null)
            {
                RestoreWeights();
                return;
            }

            // 今回の値にするシェイプキー: 編集リスト + 乗せているシェイプキー（100）
            var targets = new Dictionary<(SkinnedMeshRenderer, int), float>();
            foreach (var entry in _entries)
            {
                var info = FindInfo(entry.Path);
                if (info == null || !info.NameToIndex.TryGetValue(entry.ShapeName, out var index)) continue;
                targets[(info.Renderer, index)] = entry.Value;
            }
            if (TryGetHover(out var hoverInfo, out var hoverName) && FindEntry(hoverInfo.Path, hoverName) == null)
            {
                targets[(hoverInfo.Renderer, _hoverShape)] = 100f;
            }

            // 対象から外れたシェイプキーは、元の値に戻す
            var released = new List<(SkinnedMeshRenderer, int)>();
            foreach (var key in _touched.Keys)
            {
                if (!targets.ContainsKey(key)) released.Add(key);
            }
            foreach (var key in released)
            {
                if (key.Item1 != null) key.Item1.SetBlendShapeWeight(key.Item2, _touched[key]);
                _touched.Remove(key);
            }

            // 値を設定する（初めて触るときは、元の値を覚える）
            foreach (var pair in targets)
            {
                var renderer = pair.Key.Item1;
                if (renderer == null) continue;
                if (!_touched.ContainsKey(pair.Key)) _touched[pair.Key] = renderer.GetBlendShapeWeight(pair.Key.Item2);
                renderer.SetBlendShapeWeight(pair.Key.Item2, pair.Value);
            }

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

        /// <summary>変えたシェイプキーを、すべて元の値に戻す。</summary>
        private void RestoreWeights()
        {
            if (_touched.Count == 0) return;

            foreach (var pair in _touched)
            {
                if (pair.Key.Item1 != null) pair.Key.Item1.SetBlendShapeWeight(pair.Key.Item2, pair.Value);
            }
            _touched.Clear();
            _boxes.Clear();
            _dim = false;
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) RestoreWeights();
        }

        // シーンの保存に、プレビュー用の値が入らないようにする
        private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            RestoreWeights();
        }

        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            _dirty = true;
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
            var rect = GUILayoutUtility.GetAspectRect(1f);
            if (rect.width < 16f) rect = new Rect(rect.x, rect.y, 16f, 16f);

            HandlePreviewInput(e, rect);

            if (e.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, PreviewBackground);
            if (_avatarRoot == null) return;

            if (!EnsureCamera()) return;
            ApplyNdmfPreviewSetting();
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
                _appliedUseNdmfPreview = null;
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

        /// <summary>NDMF Preview のオンオフを、このカメラにだけ反映する。</summary>
        private void ApplyNdmfPreviewSetting()
        {
            var use = yulEmoPreferences.UseNdmfPreview;
            if (_appliedUseNdmfPreview == use) return;
            _appliedUseNdmfPreview = use;

            if (use)
            {
                // 割り当てを外すと、NDMF 全体のプレビュー（NDMF のメニューでオンのとき）が効く
                PreviewSession.ClearCameraOverride(_cam);
            }
            else
            {
                // フィルターを持たない空のプレビューを割り当てて、差し替えを止める
                if (_emptySession == null) _emptySession = new PreviewSession();
                _emptySession.OverrideCamera(_cam);
            }
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
            if (_cam != null) PreviewSession.ClearCameraOverride(_cam);
            if (_emptySession != null)
            {
                _emptySession.Dispose();
                _emptySession = null;
            }
            _appliedUseNdmfPreview = null;

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
