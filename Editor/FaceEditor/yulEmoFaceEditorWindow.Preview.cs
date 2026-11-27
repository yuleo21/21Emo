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

        // クリック位置の絞り込み: 選んだ点と範囲、その周りの頂点（中心から離れるほど薄く描く）
        private Vector3 _pickPoint;
        private float _pickRadius;
        private readonly List<PickHighlightPoint> _pickHighlight = new List<PickHighlightPoint>();
        private Vector2 _previewMouseDown;
        private bool _previewMouseDownInRect;

        private struct PickHighlightPoint
        {
            public Vector3 Position;
            public float Alpha;
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

            // プレビューに反映する値: 編集リスト + マウスオーバー中のシェイプキー（100）
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
            EditorGUILayout.LabelField("プレビューをクリックすると、その位置の頂点を動かすシェイプキーに絞り込みます。", EditorStyles.miniLabel);

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
            DrawPickHighlight(rect);
        }

        private void HandlePreviewInput(Event e, Rect rect)
        {
            // クリック（押して、ほとんど動かさずに離す）は、回転ではなく位置の選択として扱う
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                _previewMouseDown = e.mousePosition;
                _previewMouseDownInRect = true;
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                var wasDownInRect = _previewMouseDownInRect;
                _previewMouseDownInRect = false;
                if (wasDownInRect && rect.Contains(e.mousePosition) &&
                    (e.mousePosition - _previewMouseDown).sqrMagnitude <= 16f)
                {
                    PickAt(e.mousePosition, rect);
                }
            }

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

                // クリックで選んだ点の周りの頂点は、今の形に合わせて集め直す
                if (_pickShapes != null && _pickRenderer != null && _pickRenderer.Renderer != null)
                {
                    if (TryCollectWorldVertices(_pickRenderer, baked, out var world)) RebuildPickHighlight(world);
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

        // ─────────────────────────────────────────────────────────────
        // クリック位置による絞り込み
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// プレビューのクリック位置で、メッシュの手前の面に当たった点に一番近い頂点を選び、
        /// その周りの頂点を動かすシェイプキーに絞り込む。
        /// 光線と面の交点で選ぶので、奥の頂点を貫通して選ぶことはない。
        /// どこにも当たらない場所（背景など）のクリックは、絞り込みの解除として扱う。
        /// </summary>
        private void PickAt(Vector2 guiPosition, Rect rect)
        {
            if (_rendererIndex < 0 || _rendererIndex >= _infos.Count) return;
            var info = _infos[_rendererIndex];
            if (info.Renderer == null || info.Renderer.sharedMesh == null) return;

            // 画面の位置から、プレビュー用カメラの光線を作る（RenderPreview のカメラ配置と同じ計算。アスペクトは 1）
            var viewportX = (guiPosition.x - rect.x) / rect.width;
            var viewportY = 1f - (guiPosition.y - rect.y) / rect.height;
            var tan = Mathf.Tan(PreviewFov * 0.5f * Mathf.Deg2Rad);
            var direction = (_rotation * Vector3.forward
                + _rotation * Vector3.right * ((viewportX * 2f - 1f) * tan)
                + _rotation * Vector3.up * ((viewportY * 2f - 1f) * tan)).normalized;
            var origin = _target - _rotation * Vector3.forward * _distance;

            var mesh = new Mesh();
            try
            {
                info.Renderer.BakeMesh(mesh, true);
                if (mesh.vertexCount != info.Renderer.sharedMesh.vertexCount) return;

                var vertices = mesh.vertices;
                var transform = info.Renderer.transform;
                var matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
                var world = new Vector3[vertices.Length];
                for (var i = 0; i < vertices.Length; i++) world[i] = matrix.MultiplyPoint3x4(vertices[i]);

                // 光線が当たる一番手前の面を探し、その交点に一番近い頂点を選ぶ
                if (!TryRaycastMesh(world, mesh.triangles, origin, direction, out var hitPoint))
                {
                    ClearPick();
                    Repaint();
                    return;
                }

                var best = -1;
                var bestSqr = float.MaxValue;
                for (var i = 0; i < world.Length; i++)
                {
                    var sqr = (world[i] - hitPoint).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = i;
                    }
                }
                if (best < 0)
                {
                    ClearPick();
                    Repaint();
                    return;
                }

                // 絞り込みの範囲は、メッシュの大きさから決める
                var radius = Mathf.Max(0.004f, mesh.bounds.extents.magnitude * 0.08f);

                _pickPoint = world[best];
                _pickRadius = radius;
                _pickRenderer = info;

                var shapes = new HashSet<int>();
                for (var shape = 0; shape < info.Names.Length; shape++)
                {
                    if (info.Meta.IsSeparator[shape]) continue;
                    var moved = GetMovedVertices(info, shape);
                    for (var k = 0; k < moved.Length; k++)
                    {
                        if ((world[moved[k]] - _pickPoint).sqrMagnitude <= radius * radius)
                        {
                            shapes.Add(shape);
                            break;
                        }
                    }
                }
                _pickShapes = shapes;
                _pickVersion++;
                RebuildPickHighlight(world);
                _listScroll = Vector2.zero;
                ClearHover();
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[21Emo] クリックした位置の頂点を調べられませんでした: " + ex.Message);
            }
            finally
            {
                DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// 光線とメッシュの面との交点のうち、光線の始点に一番近いものを返す。
        /// 裏向きの面も対象にする（両面表示のマテリアルでも手前として当たるようにするため）。
        /// </summary>
        private static bool TryRaycastMesh(Vector3[] world, int[] triangles, Vector3 origin, Vector3 direction, out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;
            var bestT = float.MaxValue;
            var found = false;

            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var v0 = world[triangles[t]];
                var edge1 = world[triangles[t + 1]] - v0;
                var edge2 = world[triangles[t + 2]] - v0;

                var p = Vector3.Cross(direction, edge2);
                var det = Vector3.Dot(edge1, p);
                if (Mathf.Abs(det) < 1e-8f) continue;
                var invDet = 1f / det;

                var s = origin - v0;
                var u = Vector3.Dot(s, p) * invDet;
                if (u < 0f || u > 1f) continue;

                var q = Vector3.Cross(s, edge1);
                var v = Vector3.Dot(direction, q) * invDet;
                if (v < 0f || u + v > 1f) continue;

                var distance = Vector3.Dot(edge2, q) * invDet;
                if (distance <= 0f || distance >= bestT) continue;

                bestT = distance;
                found = true;
            }

            if (found) hitPoint = origin + direction * bestT;
            return found;
        }

        /// <summary>現在の形での、全頂点のワールド座標を返す（TryCollectPoints と同じ変換）。</summary>
        private bool TryCollectWorldVertices(RendererInfo info, Dictionary<RendererInfo, Mesh> baked, out Vector3[] world)
        {
            world = null;
            try
            {
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
                world = new Vector3[vertices.Length];
                for (var i = 0; i < vertices.Length; i++) world[i] = matrix.MultiplyPoint3x4(vertices[i]);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>選んだ点の周りの頂点を集める。中心に近いほど濃く、端ほど薄く描くための濃さも持たせる。</summary>
        private void RebuildPickHighlight(Vector3[] world)
        {
            _pickHighlight.Clear();
            if (_pickShapes == null || world == null) return;

            var indices = new List<int>();
            for (var i = 0; i < world.Length; i++)
            {
                if ((world[i] - _pickPoint).sqrMagnitude <= _pickRadius * _pickRadius) indices.Add(i);
            }

            var stride = Mathf.Max(1, indices.Count / 800);
            for (var k = 0; k < indices.Count; k += stride)
            {
                var position = world[indices[k]];
                var distance = (position - _pickPoint).magnitude;
                var alpha = (1f - distance / _pickRadius) * 0.85f + 0.15f;
                _pickHighlight.Add(new PickHighlightPoint { Position = position, Alpha = alpha });
            }
        }

        /// <summary>選んだ点の周りの頂点を、中心から離れるほど薄くなる点で描く。</summary>
        private void DrawPickHighlight(Rect previewRect)
        {
            if (_pickShapes == null || _pickHighlight.Count == 0 || _cam == null) return;

            var baseColor = yulEmoPreferences.HoverBoxColor;
            foreach (var point in _pickHighlight)
            {
                var viewport = _cam.WorldToViewportPoint(point.Position);
                if (viewport.z <= 0f) continue;

                var gui = new Vector2(
                    previewRect.x + viewport.x * previewRect.width,
                    previewRect.y + (1f - viewport.y) * previewRect.height);
                if (!previewRect.Contains(gui)) continue;

                EditorGUI.DrawRect(
                    new Rect(gui.x - 1.5f, gui.y - 1.5f, 3f, 3f),
                    new Color(baseColor.r, baseColor.g, baseColor.b, point.Alpha));
            }
        }
    }
}
#endif
