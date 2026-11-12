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
    /// yulEmoFaceEditorWindow のプレビュー部分。
    /// 専用カメラと NDMF Preview のセッションの管理、プレビューへの値の反映、動く範囲の枠線を担当する。
    /// 画面の描画と書き出しは yulEmoFaceEditorWindow.cs にある。
    /// </summary>
    public partial class yulEmoFaceEditorWindow
    {
        private const int MaxOverlayPoints = 2000;
        private const double ApplyInterval = 0.05;
        /// <summary>この距離（メッシュ上の長さ、メートル）より小さい移動は「動いていない」とみなす。区切り用のシェイプキーが持つ、ごくわずかな頂点の移動を除くため。</summary>
        private const float MinVisibleMove = 1e-4f;

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
        private readonly Dictionary<(RendererInfo, int), int[]> _movedCache = new Dictionary<(RendererInfo, int), int[]>();

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

            var foundDeadRenderer = false;
            foreach (var entry in _entries)
            {
                var info = FindInfo(entry.Path);
                if (info == null || !info.NameToIndex.ContainsKey(entry.ShapeName)) continue;
                // シーン側で消された Renderer は、再調査するまで触らない（MissingReferenceException になるため）
                if (info.Renderer == null)
                {
                    foundDeadRenderer = true;
                    continue;
                }
                Add(info, entry.ShapeName, entry.Value);
            }
            // 消された Renderer を見つけたら、古い一覧のまま静かに止まらないよう、再調査を 1 回予約する
            // （QueueRebuild 側で多重予約は防がれている）
            if (foundDeadRenderer) QueueRebuild();
            if (TryGetHover(out var hoverInfo, out var hoverName) && hoverInfo.Renderer != null && FindEntry(hoverInfo.Path, hoverName) == null)
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
