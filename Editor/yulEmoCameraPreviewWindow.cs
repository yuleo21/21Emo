#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>
    /// アイコン撮影のカメラ位置（左右・上下・前後・画角）をプレビューしながら調整するウィンドウ。
    ///
    /// アバターを複製して専用のプレビューシーンに置き、表情は変えずにカメラだけを動かして表示する。
    /// カメラの置き方は yulEmoIconCapturer.ConfigureCamera を撮影と共用しているので、
    /// ここで見えている構図がそのままアイコンの構図になる。
    /// 値は yulEmoCameraSettings.Current に保存され、21Emo 本体の撮影で使われる。
    ///
    /// プレビュー上の操作:
    ///   左ドラッグ   : 左右・上下に動かす
    ///   マウスホイール: 前後に動かす
    /// </summary>
    public class yulEmoCameraPreviewWindow : EditorWindow
    {
        private const int PreviewSize = 512;
        private static readonly Color BackgroundColor = new Color(0.22f, 0.22f, 0.22f, 1f);

        [SerializeField] private GameObject _avatar;
        [SerializeField] private bool _applyNDMF;

        private bool _rebuildQueued;
        private bool _dirty;
        private string _status;

        private Scene _scene;
        private bool _sceneCreated;
        private GameObject _clone;
        private GameObject _cameraObject;
        private GameObject[] _lights;
        private Camera _cam;
        private RenderTexture _renderTexture;
        private Vector3 _viewPosition;
        private bool _useDirectDraw;

        [MenuItem("21tools/21Emo カメラプレビュー")]
        public static void Open()
        {
            Open(null);
        }

        /// <summary>ウィンドウを開く。avatar が null でなければそのアバターを対象にする。</summary>
        public static void Open(GameObject avatar)
        {
            var window = GetWindow<yulEmoCameraPreviewWindow>("21Emo カメラ");
            window.minSize = new Vector2(380f, 560f);
            if (avatar != null && avatar != window._avatar)
            {
                window._avatar = avatar;
            }
            window.QueueRebuild();
            window.Show();
        }

        private void OnEnable()
        {
            QueueRebuild();
        }

        private void OnDisable()
        {
            Teardown();
        }

        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            // アバターの複製や NDMF の適用は OnGUI の途中では行わず、OnGUI の後に実行する
            EditorApplication.delayCall += Rebuild;
        }

        private void OnGUI()
        {
            var settings = yulEmoCameraSettings.Current;

            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            _avatar = (GameObject)EditorGUILayout.ObjectField("アバター", _avatar, typeof(GameObject), true);
            _applyNDMF = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "NDMF を適用した状態で表示する（時間がかかります）",
                    "撮影用の複製に NDMF の全プラグインを適用します。衣装の切り替えなどがビルド後と同じ見た目になります。\n" +
                    "カメラ位置の確認だけなら、オフのままで構いません。"),
                _applyNDMF);
            var avatarChanged = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("アバターを読み込み直す"))
            {
                avatarChanged = true;
            }
            if (avatarChanged) QueueRebuild();

            EditorGUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            settings.Right = EditorGUILayout.Slider(
                new GUIContent("左右", "カメラから見て右へ動かす量（メートル）。"),
                settings.Right, -yulEmoCameraSettings.MaxPan, yulEmoCameraSettings.MaxPan);
            settings.Up = EditorGUILayout.Slider(
                new GUIContent("上下", "上へ動かす量（メートル）。"),
                settings.Up, -yulEmoCameraSettings.MaxPan, yulEmoCameraSettings.MaxPan);
            settings.Forward = EditorGUILayout.Slider(
                new GUIContent("前後", "プラスで顔へ近づき、マイナスで離れます（メートル）。"),
                settings.Forward, -yulEmoCameraSettings.MaxDolly, yulEmoCameraSettings.MaxDolly);
            settings.Fov = EditorGUILayout.Slider(
                new GUIContent("画角 (FoV)", "小さいほど拡大され、遠近の歪みが少なくなります。標準は " + yulEmoCameraSettings.DefaultFov + "。"),
                settings.Fov, yulEmoCameraSettings.MinFov, yulEmoCameraSettings.MaxFov);
            var changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("標準の位置に戻す"))
            {
                settings.ResetToDefault();
                changed = true;
            }

            EditorGUILayout.LabelField("プレビュー上で、左ドラッグ = 左右・上下 / ホイール = 前後", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            }

            // 正方形のプレビュー領域（アイコンも正方形）
            var rect = GUILayoutUtility.GetAspectRect(1f);
            if (rect.width < 16f) rect = new Rect(rect.x, rect.y, 16f, 16f);

            var e = Event.current;
            if (_cam != null && rect.Contains(e.mousePosition))
            {
                var distance = Mathf.Max(0.02f, EstimateDistance(settings));
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    // 画面上で 1 ピクセル動かしたときに対応するワールド上の長さ
                    var unitsPerPixel = 2f * distance * Mathf.Tan(Mathf.Clamp(settings.Fov, 1f, 120f) * 0.5f * Mathf.Deg2Rad) / rect.height;
                    // 画像を右へ引くには、カメラは左へ動く
                    settings.Right -= e.delta.x * unitsPerPixel;
                    settings.Up += e.delta.y * unitsPerPixel;
                    changed = true;
                    e.Use();
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    // 下へスクロールすると遠ざかる
                    settings.Forward -= e.delta.y * distance * 0.02f;
                    changed = true;
                    e.Use();
                }
            }

            if (changed)
            {
                settings.Clamp();
                yulEmoCameraSettings.Save();
                _dirty = true;
            }

            if (e.type == EventType.Repaint)
            {
                if (_dirty && _cam != null)
                {
                    RenderPreview(settings);
                    _dirty = false;
                }

                EditorGUI.DrawRect(rect, BackgroundColor);
                if (_renderTexture != null && _cam != null)
                {
                    GUI.DrawTexture(rect, _renderTexture, ScaleMode.StretchToFill, false);
                }
            }
            else if (changed)
            {
                Repaint();
            }
        }

        /// <summary>顔までの距離の目安（ドラッグ・ホイールの動く量の計算に使う）。</summary>
        private float _baseDistance;

        private float EstimateDistance(yulEmoCameraSettings settings)
        {
            return _baseDistance - settings.Forward;
        }

        // ─────────────────────────────────────────────────────────────
        // プレビュー用の複製・カメラの準備
        // ─────────────────────────────────────────────────────────────
        private void Rebuild()
        {
            _rebuildQueued = false;
            Teardown();

            if (_avatar == null)
            {
                _status = "アバターを指定してください。";
                Repaint();
                return;
            }

            var descriptor = _avatar.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                descriptor = _avatar.GetComponentInChildren<VRCAvatarDescriptor>();
            }
            if (descriptor == null)
            {
                _status = "VRCAvatarDescriptor が見つかりません。";
                Repaint();
                return;
            }

            try
            {
                string message = null;
                _viewPosition = descriptor.ViewPosition;
                _clone = yulEmoIconCapturer.CreateClone(descriptor.gameObject, _applyNDMF, null, ref message);

                // 表情は変えず、アニメーションも再生しない
                foreach (var animator in _clone.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = false;
                }

                _scene = EditorSceneManager.NewPreviewScene();
                _sceneCreated = true;
                SceneManager.MoveGameObjectToScene(_clone, _scene);

                _cameraObject = new GameObject("21Emo Preview Camera") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(_cameraObject, _scene);
                _cam = _cameraObject.AddComponent<Camera>();
                _cam.enabled = false;
                yulEmoIconCapturer.SetCameraScene(_cam, _scene);
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = BackgroundColor;
                _cam.cullingMask = ~0;
                _cam.allowHDR = false;

                // 標準の位置にカメラを置き、顔までの距離を求める
                yulEmoIconCapturer.ConfigureCamera(_cam, _clone, _viewPosition, new yulEmoCameraSettings(), out _baseDistance, out _);

                _lights = new[]
                {
                    yulEmoIconCapturer.CreateLight(_scene, _cam.transform.rotation * Quaternion.Euler(15f, -20f, 0f), 1.0f),
                    yulEmoIconCapturer.CreateLight(_scene, _cam.transform.rotation * Quaternion.Euler(-15f, 30f, 0f), 0.5f),
                    yulEmoIconCapturer.CreateLight(_scene, _cam.transform.rotation, 0.4f),
                };

                // 画面外判定で描画されないことがないよう、スキンメッシュは常に更新する
                foreach (var renderer in _clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    renderer.updateWhenOffscreen = true;
                }

                _renderTexture = new RenderTexture(PreviewSize, PreviewSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _renderTexture.Create();

                // 通常の描画で何も写らない環境では、撮影と同じくメッシュを直接描画する方法に切り替える
                _useDirectDraw = false;
                RenderPreview(new yulEmoCameraSettings());
                if (!HasAnyDrawnPixel())
                {
                    _useDirectDraw = true;
                    yulEmoIconCapturer.SetCameraScene(_cam, default);
                    _cam.cullingMask = 1 << yulEmoIconCapturer.DirectDrawLayer;
                }

                _status = message;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Teardown();
                _status = "プレビューの準備中にエラーが発生しました。詳細は Console を確認してください。";
            }

            _dirty = true;
            Repaint();
        }

        private void RenderPreview(yulEmoCameraSettings settings)
        {
            if (_cam == null || _clone == null || _renderTexture == null) return;

            yulEmoIconCapturer.ConfigureCamera(_cam, _clone, _viewPosition, settings, out _baseDistance, out _);

            var temporaryMeshes = new List<Mesh>();
            if (_useDirectDraw)
            {
                yulEmoIconCapturer.QueueDirectDraw(_clone, _cam, temporaryMeshes);
            }

            _cam.targetTexture = _renderTexture;
            _cam.Render();
            _cam.targetTexture = null;

            foreach (var mesh in temporaryMeshes)
            {
                if (mesh != null) DestroyImmediate(mesh);
            }
        }

        /// <summary>プレビューに背景以外のピクセルがあるかを調べる（角のピクセルを背景とみなす）。</summary>
        private bool HasAnyDrawnPixel()
        {
            var previous = RenderTexture.active;
            RenderTexture.active = _renderTexture;
            var texture = new Texture2D(_renderTexture.width, _renderTexture.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, _renderTexture.width, _renderTexture.height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;

            var pixels = texture.GetPixels32();
            DestroyImmediate(texture);
            if (pixels.Length == 0) return false;

            var reference = pixels[0];
            foreach (var pixel in pixels)
            {
                if (Mathf.Abs(pixel.r - reference.r) > 2 ||
                    Mathf.Abs(pixel.g - reference.g) > 2 ||
                    Mathf.Abs(pixel.b - reference.b) > 2)
                {
                    return true;
                }
            }
            return false;
        }

        private void Teardown()
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                DestroyImmediate(_renderTexture);
                _renderTexture = null;
            }
            if (_lights != null)
            {
                foreach (var lightObject in _lights)
                {
                    if (lightObject != null) DestroyImmediate(lightObject);
                }
                _lights = null;
            }
            if (_cameraObject != null)
            {
                DestroyImmediate(_cameraObject);
                _cameraObject = null;
            }
            _cam = null;
            if (_clone != null)
            {
                DestroyImmediate(_clone);
                _clone = null;
            }
            if (_sceneCreated)
            {
                EditorSceneManager.ClosePreviewScene(_scene);
                _sceneCreated = false;
            }
        }
    }
}
#endif
