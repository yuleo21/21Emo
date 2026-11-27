#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace yulEmo
{
    /// <summary>
    /// FaceFix の表情クリップをアバターの顔に適用した状態で撮影し、
    /// メニューアイコン用の背景透過 PNG を保存する。
    ///
    /// 流れ:
    ///   1. シーン上のアバターを複製する（元のアバターは一切変更しない）
    ///   2. 設定により、複製したアバターに NDMF の全プラグインを適用する（ビルド時と同じ見た目にする）。
    ///      そのあと複製を専用のプレビューシーンへ移す
    ///   3. 同じシーンにカメラとライトを作る
    ///   4. ヒューマノイドリグの Head（と両目）の実際の位置から顔の中心と大きさを求め、アバターの正面にカメラを置く
    ///   5. クリップごとに、前のクリップの変更を元に戻してから、表情に関わるカーブ（ブレンドシェイプ・オブジェクトの有効/無効・
    ///      レンダラーの有効/無効・マテリアルの差し替え）だけを最終フレームで適用する
    ///      AnimationClip.SampleAnimation は使わず、Transform / Animator の姿勢は変更しない
    ///   6. 背景を黒と白の 2 通りで撮影し、その差から背景を透過した画像を作る
    ///   7. PNG として保存し、メニューアイコン向けのインポート設定にする
    /// 専用のシーンで描画するため、開いているシーンのライトやカメラの影響を受けない。
    /// 処理の各段階を Console に「[21Emo] アイコン撮影」で始まるログとして出す。
    /// 複製の作成と、複製への表情カーブの適用は yulEmoAvatarCloneBuilder が担当する。
    /// </summary>
    public static class yulEmoIconCapturer
    {
        public const int IconSize = 256;

        private const string LogPrefix = "[21Emo] アイコン撮影: ";

        // メッシュを直接描画する方法（フォールバック）で使うレイヤー
        internal const int DirectDrawLayer = 31;


        // ViewPosition で代用するときの、視点の高さに対する撮影範囲の半分の大きさ（zoom = 1 のとき）
        private const float HalfExtentRatio = 0.14f;
        // ViewPosition で代用するときの、視点から顔の中心までの下方向へのずらし量（視点の高さに対する割合）
        private const float CenterDownRatio = 0.03f;
        // 両目の間隔を基準にするときの倍率（撮影範囲の半分の大きさ / 目の間隔）
        private const float HalfExtentPerEyeDistance = 2.4f;
        // 両目の間隔を基準にするとき、目の中点から顔の中心までの下方向へのずらし量（目の間隔に対する割合）
        private const float CenterDownPerEyeDistance = 0.6f;
        // 目が無いとき、Head と Neck の距離を基準にする倍率
        private const float HalfExtentPerNeckDistance = 1.7f;

        /// <summary>
        /// clips の各クリップを撮影して folder（Assets 以下の存在するフォルダ）に FaceFix_1.png〜 として保存する。
        /// folder が null / 空のときはファイルを保存せず、メモリ上の Texture2D を返す（NDMF のビルド時の撮影で使う）。
        /// 戻り値は clips と同じ長さの配列で、クリップが null のもの・撮影に失敗したものは null。
        /// settings はカメラ位置（左右・上下・前後・画角）。null なら標準。applyNDMF が true なら、撮影用複製に NDMF の全プラグインを適用してから撮影する。
        /// message には結果の概要（失敗したときは理由）が入る。
        /// 高速モード（NDMF Preview）で撮るときは、CaptureFaceFixIconsAsync を使う（ビルド時の撮影はこちらの同期版でよい）。
        /// </summary>
        public static Texture2D[] CaptureFaceFixIcons(
            GameObject avatarObject,
            AnimationClip[] clips,
            string folder,
            yulEmoCameraSettings settings,
            bool applyNDMF,
            out string message)
        {
            message = null;
            var result = new Texture2D[clips != null ? clips.Length : 0];

            if (settings == null) settings = new yulEmoCameraSettings();
            Debug.Log($"{LogPrefix}開始（NDMF 適用 = {applyNDMF}, カメラ = {settings}）");

            if (avatarObject == null)
            {
                message = "アイコンを撮影できませんでした（アバターが指定されていません）。";
                Debug.LogWarning(LogPrefix + message);
                return result;
            }

            var targetCount = 0;
            if (clips != null)
            {
                foreach (var clip in clips)
                {
                    if (clip != null) targetCount++;
                }
            }
            if (targetCount == 0)
            {
                message = "アイコンを撮影できませんでした（FaceFix のクリップが設定されていません）。";
                Debug.LogWarning(LogPrefix + message);
                return result;
            }

            var descriptor = yulEmoEditorUtility.FindDescriptor(avatarObject);
            if (descriptor == null)
            {
                message = "アイコンを撮影できませんでした（VRCAvatarDescriptor が見つかりません）。";
                Debug.LogWarning(LogPrefix + message);
                return result;
            }

            var viewPosition = descriptor.ViewPosition;

            Scene previewScene = default;
            var sceneCreated = false;
            GameObject clone = null;
            GameObject cameraObject = null;
            GameObject[] lightObjects = null;
            RenderTexture renderTexture = null;
            var savedCount = 0;
            var blankCount = 0;
            var changes = new yulEmoAvatarCloneBuilder.ChangeLog();

            try
            {
                clone = yulEmoAvatarCloneBuilder.CreateClone(descriptor.gameObject, applyNDMF, clips, ref message);
                Debug.Log($"{LogPrefix}アバターを複製しました（{clone.name}）");

                // 撮影ではアニメーションを再生しない。Animator は動かさず、
                // 表情はクリップのカーブの値を直接適用する（yulEmoAvatarCloneBuilder.ApplyFaceCurves）。
                foreach (var animator in clone.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = false;
                }

                previewScene = EditorSceneManager.NewPreviewScene();
                sceneCreated = true;
                SceneManager.MoveGameObjectToScene(clone, previewScene);

                // カメラ（このシーンの中だけを写す）
                cameraObject = new GameObject("21Emo Icon Camera") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
                var cam = cameraObject.AddComponent<Camera>();
                cam.enabled = false;
                SetCameraScene(cam, previewScene);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.cullingMask = ~0;
                cam.allowHDR = false;

                // 顔の位置を基準に、設定（左右・上下・前後・画角）を反映してカメラを置く。
                // プレビューウィンドウと同じ ConfigureCamera を使うので、プレビューと同じ構図で撮影される。
                ConfigureCamera(cam, clone, viewPosition, settings, out var baseDistance, out var halfExtent);
                Debug.Log($"{LogPrefix}カメラ位置 = {cam.transform.position}, 基準の距離 = {baseDistance}, 撮影範囲の半分 = {halfExtent}, 画角 = {cam.fieldOfView}");

                // 顔を正面から照らす（カメラの向きに対して少しずらした 3 灯）
                lightObjects = new[]
                {
                    CreateLight(previewScene, cam.transform.rotation * Quaternion.Euler(15f, -20f, 0f), 1.0f),
                    CreateLight(previewScene, cam.transform.rotation * Quaternion.Euler(-15f, 30f, 0f), 0.5f),
                    CreateLight(previewScene, cam.transform.rotation, 0.4f),
                };

                // 画面外判定で描画されないことがないよう、スキンメッシュは常に更新する。
                // クリップが動かさないシェイプキーが前のクリップの影響を受けないよう、初期値も控えておく。
                var renderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var initialWeights = new float[renderers.Length][];
                for (var r = 0; r < renderers.Length; r++)
                {
                    renderers[r].updateWhenOffscreen = true;

                    var mesh = renderers[r].sharedMesh;
                    var count = mesh != null ? mesh.blendShapeCount : 0;
                    initialWeights[r] = new float[count];
                    for (var b = 0; b < count; b++)
                    {
                        initialWeights[r][b] = renderers[r].GetBlendShapeWeight(b);
                    }
                }
                LogRendererStats(clone, cam, "撮影前", renderers.Length);

                renderTexture = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                renderTexture.Create();

                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                var useDirectDraw = false;

                for (var i = 0; i < clips.Length; i++)
                {
                    var clip = clips[i];
                    if (clip == null) continue;

                    // 前のクリップの変更（オブジェクトの有効 / 無効・マテリアル）を元に戻す
                    changes.Restore();
                    for (var r = 0; r < renderers.Length; r++)
                    {
                        for (var b = 0; b < initialWeights[r].Length; b++)
                        {
                            renderers[r].SetBlendShapeWeight(b, initialWeights[r][b]);
                        }
                    }

                    // AnimationClip.SampleAnimation は使用しない。
                    // 表情に関わるカーブ（ブレンドシェイプ・オブジェクトの有効/無効・レンダラーの有効/無効・マテリアル）だけを
                    // 最終フレームで評価して適用し、Transform / Humanoid の姿勢カーブによってアバター全体が動くのを防ぐ。
                    yulEmoAvatarCloneBuilder.ApplyFaceCurves(clone, clip, clip.length, changes);
                    LogRendererStats(clone, cam, $"FaceFix {i + 1} 適用後", renderers.Length);

                    byte[] png;
                    int visiblePixels;
                    if (useDirectDraw)
                    {
                        png = RenderTransparentPng(cam, renderTexture, clone, out visiblePixels);
                    }
                    else
                    {
                        png = RenderTransparentPng(cam, renderTexture, null, out visiblePixels);
                        if (png == null)
                        {
                            // 通常の描画で何も写らなかった場合は、メッシュを直接カメラへ描画する方法に切り替えて撮り直す
                            Debug.LogWarning(LogPrefix + "通常の描画では何も写らなかったため、メッシュを直接描画する方法に切り替えます");
                            useDirectDraw = true;
                            SetCameraScene(cam, default);
                            cam.cullingMask = 1 << DirectDrawLayer;
                            png = RenderTransparentPng(cam, renderTexture, clone, out visiblePixels);
                        }
                    }
                    Debug.Log($"{LogPrefix}FaceFix {i + 1}（{clip.name}）: 写ったピクセル数 = {visiblePixels}");
                    if (png == null)
                    {
                        blankCount++;
                        continue;
                    }

                    result[i] = StoreIconPng(png, i, folder, projectRoot);
                    if (result[i] != null) savedCount++;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                var errorMessage = "アイコンの撮影中にエラーが発生しました。詳細は Console を確認してください。";
                message = message == null ? errorMessage : message + "\n" + errorMessage;
            }
            finally
            {
                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
                if (lightObjects != null)
                {
                    foreach (var lightObject in lightObjects)
                    {
                        if (lightObject != null) UnityEngine.Object.DestroyImmediate(lightObject);
                    }
                }
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                if (sceneCreated) EditorSceneManager.ClosePreviewScene(previewScene);
            }

            var summary = $"アイコンを {savedCount} / {targetCount} 件保存しました。";
            if (blankCount > 0)
            {
                summary += $"（{blankCount} 件は何も写っていませんでした。Console の「[21Emo] アイコン撮影」のログを確認してください）";
            }
            message = message == null ? summary : message + "\n" + summary;
            Debug.Log(LogPrefix + summary);

            return result;
        }

        /// <summary>
        /// アイコンを撮影し、終わったら onComplete(撮影結果, 概要) を呼ぶ。
        /// 高速モード（NDMF Preview）が使える条件のときは、プレビューの用意ができるまで待ってから撮影する。
        /// NDMF Preview の処理は非同期に進むため、その場で撮ると処理前の姿が写ることがあるためである。
        /// 条件を満たさないときと、プレビューで撮れなかったときは、通常の撮影（CaptureFaceFixIcons）で撮る。
        /// 条件を満たさない場合は、その場で撮影して完了を呼ぶ。
        /// 高速モードの撮影がすでに進んでいるときも、通常の撮影で処理する（使い回しの状態を取り合わないため）。
        /// </summary>
        public static void CaptureFaceFixIconsAsync(
            GameObject avatarObject,
            AnimationClip[] clips,
            string folder,
            yulEmoCameraSettings settings,
            bool applyNDMF,
            Action<Texture2D[], string> onComplete)
        {
            if (onComplete == null) throw new ArgumentNullException(nameof(onComplete));
            if (settings == null) settings = new yulEmoCameraSettings();

            if (!CanTryPreviewCapture(avatarObject, clips, applyNDMF) || _previewJobActive)
            {
                var result = CaptureFaceFixIcons(avatarObject, clips, folder, settings, applyNDMF, out var message);
                onComplete(result, message);
                return;
            }

            var descriptor = yulEmoEditorUtility.FindDescriptor(avatarObject);
            var job = new PreviewCaptureJob(descriptor.gameObject, clips, folder, settings, descriptor.ViewPosition, onComplete);
            job.Start();
        }

        /// <summary>高速モード（NDMF Preview）で撮影を試みられる条件がそろっているか。</summary>
        private static bool CanTryPreviewCapture(GameObject avatarObject, AnimationClip[] clips, bool applyNDMF)
        {
            if (!applyNDMF) return false;
            if (!yulEmoPreferences.UsePreviewCapture) return false;
            if (!yulEmoPreferences.UseNdmfPreview) return false;
            if (PreviewSession.Current == null) return false;
            if (avatarObject == null || clips == null) return false;
            if (!Array.Exists(clips, c => c != null)) return false;
            return yulEmoEditorUtility.FindDescriptor(avatarObject) != null;
        }

        /// <summary>
        /// 撮影した PNG を保存し、テクスチャを返す。
        /// folder が null / 空のときはファイルを保存せず、メモリ上のテクスチャを返す。
        /// </summary>
        private static Texture2D StoreIconPng(byte[] png, int index, string folder, string projectRoot)
        {
            if (string.IsNullOrEmpty(folder))
            {
                var memoryTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = $"FaceFix_{index + 1}" };
                memoryTexture.LoadImage(png);
                memoryTexture.wrapMode = TextureWrapMode.Clamp;
                return memoryTexture;
            }

            var assetPath = $"{folder.TrimEnd('/')}/FaceFix_{index + 1}.png";
            File.WriteAllBytes(Path.Combine(projectRoot, assetPath), png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = IconSize;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // 高速モードのセッションの使い回し。Fork は生成済みのフィルターを引き継がず、全フィルターの用意を
        // 最初からやり直す（NDMF 1.15.1 の実装で確認）ため、撮影のたびに作り直すと毎回その待ちが発生する。
        // 同じアバター・同じ親セッションの間は 1 つのセッションとフィルターの状態を使い回し、
        // 2 回目以降の撮影では、用意が終わったパイプラインをそのまま使えるようにする
        private static PreviewSession _sharedCaptureSession;
        private static PreviewSession _sharedCaptureParent;
        private static GameObject _sharedCaptureAvatar;
        private static int _sharedCaptureRendererCount;
        private static yulEmoFacePreviewFilter.State _sharedCaptureState;
        private static bool _sharedCaptureHookInstalled;

        // 高速モードの撮影が進んでいる間は true。同時に 2 つ走らせると使い回しの状態を取り合うため、
        // その間の呼び出しは通常の撮影で処理する
        private static bool _previewJobActive;

        private static PreviewSession GetSharedCaptureSession(GameObject avatarRoot, out yulEmoFacePreviewFilter.State state)
        {
            var parent = PreviewSession.Current;
            var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            if (_sharedCaptureSession != null &&
                (parent != _sharedCaptureParent || avatarRoot != _sharedCaptureAvatar ||
                 renderers.Length != _sharedCaptureRendererCount))
            {
                DisposeSharedCaptureSession();
            }

            if (_sharedCaptureSession == null)
            {
                _sharedCaptureState = new yulEmoFacePreviewFilter.State();
                _sharedCaptureSession = parent.Fork("21Emo Icon Capture");
                _sharedCaptureSession.AddMutator(new SequencePoint(),
                    new yulEmoFacePreviewFilter(renderers, _sharedCaptureState));
                _sharedCaptureParent = parent;
                _sharedCaptureAvatar = avatarRoot;
                _sharedCaptureRendererCount = renderers.Length;

                if (!_sharedCaptureHookInstalled)
                {
                    _sharedCaptureHookInstalled = true;
                    AssemblyReloadEvents.beforeAssemblyReload += DisposeSharedCaptureSession;
                    EditorApplication.quitting += DisposeSharedCaptureSession;
                }
            }

            state = _sharedCaptureState;
            return _sharedCaptureSession;
        }

        private static void DisposeSharedCaptureSession()
        {
            if (_sharedCaptureSession != null)
            {
                _sharedCaptureSession.Dispose();
                _sharedCaptureSession = null;
            }
            _sharedCaptureParent = null;
            _sharedCaptureAvatar = null;
            _sharedCaptureState = null;
            _sharedCaptureRendererCount = 0;
        }

        /// <summary>
        /// 高速モード（試作）: 複製を作らず、元のアバターを NDMF Preview で撮影する仕事。
        /// NDMF Preview の処理は非同期に進む（フィルターの用意は Task で進み、続きはエディターの更新のたびに進む）ため、
        /// 始めてすぐ撮ると、MA のオブジェクト切り替えや TexTransTool などの処理が届く前の姿が写る。
        /// 「用意が終わったか」は、NDMF の内部の動き（1.15.1 の実装で確認）を使って判定する。
        /// 用意が終わったパイプラインで描くときにだけ、NDMF は描画の前に各フィルターの OnFrame を呼ぶ。
        /// そのため、撮影用カメラで描いている間に自分のフィルターの OnFrame が呼ばれた描画だけを、処理済みとして使う。
        /// 用意が終わっていない間は本物がそのまま描かれ、OnFrame は呼ばれない。
        /// さらに、再構築が進んでいないかも NDMF の内部状態から読み、進んでいる間は待つ（読めない環境では OnFrame の判定だけで進める）。
        /// セッションは撮影のたびに作り直さず、同じアバターの撮影で使い回す。Fork は生成済みのフィルターを引き継がず、
        /// 全フィルターの用意を最初からやり直すため、作り直すと毎回その待ちが発生する。
        /// 表情は yulEmoFacePreviewFilter でプレビュー用のコピーにだけ設定するので、元のアバターは変わらない。
        /// 試作としての制限: クリップ内のオブジェクトの有効/無効・レンダラーの有効/無効・マテリアルの差し替えは反映されない。
        /// 撮影は開いているシーンで行うため、シーン内のほかの物が写り込むことがある。直接描画のフォールバックも使わない。
        /// プレビューで撮れなかったときは、通常の撮影（複製への NDMF 全適用）で撮り直してから完了を呼ぶ。
        /// </summary>
        private sealed class PreviewCaptureJob
        {
            // パイプラインの用意ができるのを待つ上限（秒）。重いアバターでは、セッションを分けてから
            // 全フィルターの用意がそろうまで数秒以上かかることがある（実測で 3 秒では足りなかった）。
            // 時間切れのときは、用意できていない姿を撮らず、通常の撮影に切り替える
            private const double ReadinessTimeoutSeconds = 15.0;
            // 用意ができた後、撮影全体にかけてよい上限（秒）。再構築が始まって OnFrame が呼ばれない描画が続く場合の備え
            private const double CaptureTimeoutSeconds = 30.0;
            // 描き直しの間隔（秒）
            private const double TickIntervalSeconds = 0.05;

            private readonly GameObject _avatarRoot;
            private readonly AnimationClip[] _clips;
            private readonly string _folder;
            private readonly yulEmoCameraSettings _settings;
            private readonly Vector3 _viewPosition;
            private readonly Action<Texture2D[], string> _onComplete;
            private readonly Texture2D[] _result;
            private readonly int _targetCount;
            private readonly List<Texture2D> _createdMemoryTextures = new List<Texture2D>();

            // 使い回しのセッションと一緒にもらう（Setup で設定）
            private yulEmoFacePreviewFilter.State _state;

            private PreviewSession _parent;
            private PreviewSession _session;
            private GameObject _cameraObject;
            private Camera _cam;
            private GameObject[] _lightObjects;
            private RenderTexture _renderTexture;
            private string _projectRoot;

            private bool _completed;
            private double _nextTickTime;
            private double _readinessStartTime;
            private double _captureStartTime;
            private bool _ready;
            private int _clipIndex;
            private int _savedCount;
            private int _blankCount;

            public PreviewCaptureJob(
                GameObject avatarRoot,
                AnimationClip[] clips,
                string folder,
                yulEmoCameraSettings settings,
                Vector3 viewPosition,
                Action<Texture2D[], string> onComplete)
            {
                _avatarRoot = avatarRoot;
                _clips = clips;
                _folder = folder;
                _settings = settings;
                _viewPosition = viewPosition;
                _onComplete = onComplete;
                _result = new Texture2D[clips.Length];
                foreach (var clip in clips)
                {
                    if (clip != null) _targetCount++;
                }
            }

            public void Start()
            {
                _previewJobActive = true;
                try
                {
                    Setup();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    FallbackToSyncCapture();
                    return;
                }

                _readinessStartTime = EditorApplication.timeSinceStartup;
                _nextTickTime = _readinessStartTime;
                EditorApplication.update += Tick;
                EditorUtility.DisplayProgressBar("21Emo", "高速モード: NDMF Preview の用意ができるまで待っています", 0f);
            }

            private void Setup()
            {
                _parent = PreviewSession.Current;
                if (_parent == null) throw new InvalidOperationException("NDMF Preview が使えません");

                _session = GetSharedCaptureSession(_avatarRoot, out _state);

                _cameraObject = new GameObject("21Emo Icon Camera (Preview)") { hideFlags = HideFlags.HideAndDontSave };
                _cam = _cameraObject.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.cullingMask = ~0;
                _cam.allowHDR = false;
                _session.OverrideCamera(_cam);

                ConfigureCamera(_cam, _avatarRoot, _viewPosition, _settings, out var baseDistance, out var halfExtent);
                Debug.Log($"{LogPrefix}高速モード: カメラ位置 = {_cam.transform.position}, 基準の距離 = {baseDistance}, 撮影範囲の半分 = {halfExtent}, 画角 = {_cam.fieldOfView}");

                _lightObjects = new[]
                {
                    CreateLight(_avatarRoot.scene, _cam.transform.rotation * Quaternion.Euler(15f, -20f, 0f), 1.0f),
                    CreateLight(_avatarRoot.scene, _cam.transform.rotation * Quaternion.Euler(-15f, 30f, 0f), 0.5f),
                    CreateLight(_avatarRoot.scene, _cam.transform.rotation, 0.4f),
                };

                _renderTexture = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _renderTexture.Create();

                _projectRoot = Directory.GetParent(Application.dataPath).FullName;

                // 最初のクリップの表情にしておく（用意待ちの間も、撮影時と同じ姿勢にしておく）
                foreach (var clip in _clips)
                {
                    if (clip == null) continue;
                    _state.Weights = BuildPreviewWeights(_avatarRoot, clip);
                    break;
                }
            }

            private void Tick()
            {
                if (_completed) return;

                var now = EditorApplication.timeSinceStartup;
                if (now < _nextTickTime) return;
                _nextTickTime = now + TickIntervalSeconds;

                try
                {
                    if (_avatarRoot == null || PreviewSession.Current != _parent)
                    {
                        FallbackToSyncCapture();
                        return;
                    }

                    if (!_ready)
                    {
                        TickReadiness(now);
                        return;
                    }
                    TickCapture(now);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    FallbackToSyncCapture();
                }
            }

            private void TickReadiness(double now)
            {
                // 撮影用カメラで描くこと自体が、パイプラインの用意を始める合図になる（最初の描画で構築が始まる）
                var usedReadyPipeline = RenderReadinessFrame();
                var settled = TryGetPipelineSettled(_session);
                if (usedReadyPipeline && settled != false)
                {
                    _ready = true;
                    _captureStartTime = now;
                    Debug.Log(LogPrefix + "高速モード: NDMF Preview の用意ができたため、撮影を始めます");
                    return;
                }

                var elapsed = now - _readinessStartTime;
                EditorUtility.DisplayProgressBar("21Emo",
                    $"高速モード: NDMF Preview の用意ができるまで待っています（{elapsed:F0} 秒）",
                    (float)(elapsed / ReadinessTimeoutSeconds));
                if (elapsed < ReadinessTimeoutSeconds) return;

                // 用意できていないまま撮ると、処理前の姿が写る（実測で確認済み）。通常の撮影に切り替える
                Debug.Log(LogPrefix + "高速モード: NDMF Preview の用意ができる前に時間切れになりました");
                FallbackToSyncCapture();
            }

            /// <summary>
            /// 用意待ち用の描画。黒背景で 1 回だけ描き、その描画で用意済みのパイプラインが使われたかを返す。
            /// 判定は、描いている間に自分のフィルターの OnFrame が呼ばれたかどうか（State の旗と回数）で行う。
            /// </summary>
            private bool RenderReadinessFrame()
            {
                _cam.backgroundColor = Color.black;
                _cam.targetTexture = _renderTexture;
                _state.InCaptureRender = true;
                _state.CaptureFrameCount = 0;
                try
                {
                    _cam.Render();
                }
                finally
                {
                    _state.InCaptureRender = false;
                    _cam.targetTexture = null;
                }
                return _state.CaptureFrameCount > 0;
            }

            private void TickCapture(double now)
            {
                if (now - _captureStartTime >= CaptureTimeoutSeconds)
                {
                    Debug.Log(LogPrefix + "高速モード: 撮影の途中で時間切れになりました");
                    FallbackToSyncCapture();
                    return;
                }

                // 用意済みのパイプラインで描けたクリップは続けて撮る。描けなかったクリップは撮らずに、
                // 次の更新で描き直す（再構築の最中などに、処理前の姿を撮らないため）
                while (true)
                {
                    while (_clipIndex < _clips.Length && _clips[_clipIndex] == null) _clipIndex++;
                    if (_clipIndex >= _clips.Length)
                    {
                        Finish();
                        return;
                    }

                    var i = _clipIndex;
                    var clip = _clips[i];

                    EditorUtility.DisplayProgressBar("21Emo", $"高速モード: アイコンを撮影しています（{i + 1} / {_clips.Length}）",
                        _targetCount > 0 ? (float)_savedCount / _targetCount : 0f);

                    _state.Weights = BuildPreviewWeights(_avatarRoot, clip);

                    _state.InCaptureRender = true;
                    _state.CaptureFrameCount = 0;
                    byte[] png;
                    int visiblePixels;
                    try
                    {
                        png = RenderTransparentPng(_cam, _renderTexture, null, out visiblePixels);
                    }
                    finally
                    {
                        _state.InCaptureRender = false;
                    }

                    if (_state.CaptureFrameCount == 0) return;

                    Debug.Log($"{LogPrefix}高速モード: FaceFix {i + 1}（{clip.name}）: 写ったピクセル数 = {visiblePixels}");
                    _clipIndex++;
                    if (png == null)
                    {
                        _blankCount++;
                    }
                    else
                    {
                        _result[i] = StoreIconPng(png, i, _folder, _projectRoot);
                        if (_result[i] != null)
                        {
                            if (string.IsNullOrEmpty(_folder)) _createdMemoryTextures.Add(_result[i]);
                            _savedCount++;
                        }
                    }
                }
            }

            private void Finish()
            {
                if (_savedCount == 0)
                {
                    FallbackToSyncCapture();
                    return;
                }

                var summary = $"アイコンを {_savedCount} / {_targetCount} 件保存しました。（高速モード: NDMF Preview で撮影）";
                if (_blankCount > 0)
                {
                    summary += $"（{_blankCount} 件は何も写っていませんでした。Console の「[21Emo] アイコン撮影」のログを確認してください）";
                }
                Debug.Log(LogPrefix + summary);
                Complete(_result, summary);
            }

            private void FallbackToSyncCapture()
            {
                if (_completed) return;

                // プレビューで作った分は破棄して、通常の撮影で撮り直す
                foreach (var texture in _createdMemoryTextures)
                {
                    if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                }
                _createdMemoryTextures.Clear();
                Teardown();

                Debug.LogWarning(LogPrefix + "高速モード（NDMF Preview）では撮影できなかったため、通常の方法で撮影します");
                Texture2D[] result;
                string message;
                try
                {
                    result = CaptureFaceFixIcons(_avatarRoot, _clips, _folder, _settings, true, out message);
                }
                catch (Exception e)
                {
                    // 通常の撮影で失敗しても、完了の呼び出しだけは必ず行う（呼び出し側が待ち続けないため）
                    Debug.LogException(e);
                    result = new Texture2D[_clips.Length];
                    message = "通常の方法でも撮影できませんでした。Console のエラーを確認してください。";
                }
                const string note = "高速モード（NDMF Preview）では撮影できなかったため、通常の方法で撮影しました。";
                Complete(result, string.IsNullOrEmpty(message) ? note : note + "\n" + message);
            }

            private void Complete(Texture2D[] result, string message)
            {
                if (_completed) return;
                _completed = true;
                _previewJobActive = false;
                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();
                Teardown();
                _onComplete(result, message);
            }

            private void Teardown()
            {
                if (_renderTexture != null)
                {
                    _renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(_renderTexture);
                    _renderTexture = null;
                }
                if (_lightObjects != null)
                {
                    foreach (var lightObject in _lightObjects)
                    {
                        if (lightObject != null) UnityEngine.Object.DestroyImmediate(lightObject);
                    }
                    _lightObjects = null;
                }
                // セッションは使い回しの共有物なので、ここでは破棄しない（カメラの紐付けだけ外す）
                if (_session != null && _cam != null) PreviewSession.ClearCameraOverride(_cam);
                _session = null;
                if (_cameraObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(_cameraObject);
                    _cameraObject = null;
                }
                _cam = null;
            }

            // NDMF の内部状態を読むための控え（解決したものを保持する）。
            // 対象は NDMF 1.15.1 で確認した、PreviewSession._proxySession → ProxySession._active / _next →
            // ProxyPipeline.IsReady / IsInvalidated。internal な型とメンバーなのでリフレクションで読む。
            // 版が変わって読めなくなった場合は、その時点で諦めて OnFrame の判定だけで進める
            private static FieldInfo _proxySessionField;
            private static FieldInfo _activePipelineField;
            private static FieldInfo _nextPipelineField;
            private static PropertyInfo _pipelineIsReadyProperty;
            private static PropertyInfo _pipelineIsInvalidatedProperty;
            private static bool _pipelineReflectionFailed;

            /// <summary>
            /// パイプラインが「用意済みで、再構築も進んでいない」状態かを返す。内部状態が読めないときは null（不明）。
            /// false の間は、用意済みでも古い姿が描かれる可能性があるため、撮影に入らない。
            /// </summary>
            private static bool? TryGetPipelineSettled(PreviewSession session)
            {
                if (_pipelineReflectionFailed || session == null) return null;
                try
                {
                    if (_proxySessionField == null)
                    {
                        _proxySessionField = typeof(PreviewSession).GetField("_proxySession",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_proxySessionField == null) { _pipelineReflectionFailed = true; return null; }
                    }

                    var proxySession = _proxySessionField.GetValue(session);
                    if (proxySession == null) return null;

                    var proxyType = proxySession.GetType();
                    if (_activePipelineField == null)
                    {
                        _activePipelineField = proxyType.GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance);
                        _nextPipelineField = proxyType.GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_activePipelineField == null || _nextPipelineField == null)
                        {
                            _pipelineReflectionFailed = true;
                            return null;
                        }
                    }

                    var active = _activePipelineField.GetValue(proxySession);
                    if (active == null) return false;
                    if (_nextPipelineField.GetValue(proxySession) != null) return false;

                    var pipelineType = active.GetType();
                    if (_pipelineIsReadyProperty == null)
                    {
                        _pipelineIsReadyProperty = pipelineType.GetProperty("IsReady",
                            BindingFlags.Public | BindingFlags.Instance);
                        _pipelineIsInvalidatedProperty = pipelineType.GetProperty("IsInvalidated",
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (_pipelineIsReadyProperty == null || _pipelineIsInvalidatedProperty == null)
                        {
                            _pipelineReflectionFailed = true;
                            return null;
                        }
                    }

                    var ready = (bool)_pipelineIsReadyProperty.GetValue(active);
                    var invalidated = (bool)_pipelineIsInvalidatedProperty.GetValue(active);
                    return ready && !invalidated;
                }
                catch
                {
                    _pipelineReflectionFailed = true;
                    return null;
                }
            }
        }

        /// <summary>
        /// クリップのシェイプキーのカーブを、プレビュー用のフィルターに渡す形（レンダラーごとの名前と値）にする。
        /// シェイプキー以外のカーブ（オブジェクトの有効/無効など）は対象外で、件数だけ Console に出す。
        /// </summary>
        private static Dictionary<Renderer, KeyValuePair<string, float>[]> BuildPreviewWeights(GameObject root, AnimationClip clip)
        {
            var perRenderer = new Dictionary<Renderer, List<KeyValuePair<string, float>>>();
            var ignored = 0;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == typeof(SkinnedMeshRenderer) &&
                    binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null || curve.length == 0) continue;

                    var renderer = ResolvePreviewRenderer(root, binding);
                    if (renderer == null) continue;

                    if (!perRenderer.TryGetValue(renderer, out var list))
                    {
                        list = new List<KeyValuePair<string, float>>();
                        perRenderer[renderer] = list;
                    }
                    list.Add(new KeyValuePair<string, float>(
                        binding.propertyName.Substring("blendShape.".Length),
                        curve.Evaluate(clip.length)));
                    continue;
                }

                // 姿勢のカーブは、通常の撮影でも使わない
                if (binding.type == typeof(Transform) || binding.type == typeof(Animator)) continue;
                ignored++;
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) ignored++;

            if (ignored > 0)
            {
                Debug.Log($"{LogPrefix}高速モード: {clip.name} のシェイプキー以外のカーブ {ignored} 件は反映されません（試作の制限）");
            }

            var weights = new Dictionary<Renderer, KeyValuePair<string, float>[]>();
            foreach (var pair in perRenderer) weights[pair.Key] = pair.Value.ToArray();
            return weights;
        }

        private static SkinnedMeshRenderer ResolvePreviewRenderer(GameObject root, EditorCurveBinding binding)
        {
            try
            {
                var animated = AnimationUtility.GetAnimatedObject(root, binding);
                if (animated is SkinnedMeshRenderer renderer) return renderer;
            }
            catch (Exception)
            {
                // 下のパス解決へ進む
            }

            var transform = string.IsNullOrEmpty(binding.path) ? root.transform : root.transform.Find(binding.path);
            return transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
        }

        /// <summary>
        /// Camera.scene（指定したシーンの中だけを描画する設定）を設定する。
        /// Unity のバージョンによる公開範囲の違いに左右されないよう、リフレクションで設定する。
        /// 見つからない場合は、例外で撮影全体を止めず、警告を出して設定なしで続ける
        /// （カメラ自体はプレビュー用シーンの中に作られるため、設定なしでも撮影できることが多い）。
        /// </summary>
        internal static void SetCameraScene(Camera cam, Scene scene)
        {
            var property = typeof(Camera).GetProperty(
                "scene",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite)
            {
                Debug.LogWarning(LogPrefix + "Camera.scene が見つからないため、シーンを指定せずに撮影します。この Unity のバージョンでは、意図しないものが写る可能性があります。");
                return;
            }
            property.SetValue(cam, scene);
        }

        /// <summary>
        /// 撮影対象のレンダラーの状態を Console に出す（何も写らないときの原因調べ用）。
        /// 有効なレンダラーの数と、そのうちカメラの視界に入っているものの数を示す。
        /// </summary>
        private static void LogRendererStats(GameObject avatar, Camera cam, string label, int skinnedCount)
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            var total = 0;
            var active = 0;
            var inFrustum = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                total++;
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;

                active++;
                if (GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) inFrustum++;
            }
            Debug.Log($"{LogPrefix}[{label}] スキンメッシュ = {skinnedCount} 個, 全レンダラー = {total} 個, 有効なもの = {active} 個, 視界に入るもの = {inFrustum} 個");
        }

        /// <summary>
        /// 有効なレンダラーのメッシュを、指定したカメラだけに描画するよう登録する（Graphics.DrawMesh）。
        /// スキンメッシュはその時点の姿（ブレンドシェイプ込み）を焼き込んだメッシュを使う。
        /// 登録したメッシュは temporaryMeshes に入れ、描画後に呼び出し側が破棄する。
        /// </summary>
        internal static void QueueDirectDraw(GameObject avatar, Camera cam, List<Mesh> temporaryMeshes)
        {
            foreach (var skinned in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!skinned.enabled || !skinned.gameObject.activeInHierarchy || skinned.sharedMesh == null) continue;

                var baked = new Mesh();
                skinned.BakeMesh(baked, true);
                temporaryMeshes.Add(baked);

                var materials = skinned.sharedMaterials;
                var matrix = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                for (var s = 0; s < baked.subMeshCount && s < materials.Length; s++)
                {
                    if (materials[s] == null) continue;
                    Graphics.DrawMesh(baked, matrix, materials[s], DirectDrawLayer, cam, s);
                }
            }

            foreach (var meshRenderer in avatar.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!meshRenderer.enabled || !meshRenderer.gameObject.activeInHierarchy) continue;

                var filter = meshRenderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                var materials = meshRenderer.sharedMaterials;
                var matrix = meshRenderer.transform.localToWorldMatrix;
                for (var s = 0; s < filter.sharedMesh.subMeshCount && s < materials.Length; s++)
                {
                    if (materials[s] == null) continue;
                    Graphics.DrawMesh(filter.sharedMesh, matrix, materials[s], DirectDrawLayer, cam, s);
                }
            }
        }

        internal static GameObject CreateLight(Scene scene, Quaternion rotation, float intensity)
        {
            var lightObject = new GameObject("21Emo Icon Light") { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(lightObject, scene);
            lightObject.transform.rotation = rotation;

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.color = Color.white;
            light.shadows = LightShadows.None;
            return lightObject;
        }

        /// <summary>
        /// カメラを、顔の正面を基準にした位置へ置き、settings（左右・上下・前後・画角）を反映する。
        /// 向きは常にアバターの正面を向いたまま変えず、位置だけを平行移動する。
        /// 撮影とプレビューウィンドウの両方がこれを使う。
        /// baseDistance は標準の画角で顔が収まる距離、halfExtent は標準の撮影範囲の半分の大きさ。
        /// </summary>
        internal static void ConfigureCamera(
            Camera cam,
            GameObject avatar,
            Vector3 viewPosition,
            yulEmoCameraSettings settings,
            out float baseDistance,
            out float halfExtent)
        {
            var center = ComputeFaceCenter(avatar, viewPosition, out halfExtent, out _);
            baseDistance = halfExtent / Mathf.Tan(yulEmoCameraSettings.DefaultFov * 0.5f * Mathf.Deg2Rad);

            var rotation = Quaternion.LookRotation(-avatar.transform.forward, avatar.transform.up);
            // カメラから見て 右 / 上 / 前（顔へ近づく方向）
            var offset = rotation * new Vector3(settings.Right, settings.Up, settings.Forward);
            var distance = Mathf.Max(0.001f, baseDistance - settings.Forward);

            cam.fieldOfView = Mathf.Clamp(settings.Fov, 1f, 120f);
            cam.nearClipPlane = Mathf.Max(0.001f, Mathf.Min(baseDistance * 0.05f, distance * 0.5f));
            cam.farClipPlane = baseDistance + halfExtent * 12f + Mathf.Abs(settings.Forward);
            cam.transform.SetPositionAndRotation(center + avatar.transform.forward * baseDistance + offset, rotation);
        }

        /// <summary>
        /// 顔の中心位置（ワールド座標）と、撮影範囲の半分の大きさ（標準の画角）を求める。
        /// 中心は VRCAvatarDescriptor の ViewPosition を優先し、撮影範囲の大きさだけを
        /// 両目の間隔または Head / Neck の距離から推定する。
        /// </summary>
        private static Vector3 ComputeFaceCenter(GameObject avatar, Vector3 viewPosition, out float halfExtent, out string source)
        {
            // ViewPosition はアバターのローカル座標なので、ワールド座標へ変換する。
            // ここを中心にすると、Head ボーンの位置や姿勢の影響で
            // アイコンの中心が首元へずれるのを防ぐ。
            var view = avatar.transform.TransformPoint(viewPosition);
            var animator = avatar.GetComponent<Animator>();
            if (animator == null) animator = avatar.GetComponentInChildren<Animator>(true);

            if (animator != null && animator.isHuman)
            {
                var leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
                var rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
                if (leftEye != null && rightEye != null)
                {
                    var eyeDistance = Vector3.Distance(leftEye.position, rightEye.position);
                    if (eyeDistance > 0.0001f)
                    {
                        halfExtent = eyeDistance * HalfExtentPerEyeDistance;
                        source = "ViewPosition（撮影範囲: 両目間隔）";
                        return view;
                    }
                }

                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                if (head != null && neck != null)
                {
                    var neckDistance = Vector3.Distance(head.position, neck.position);
                    if (neckDistance > 0.0001f)
                    {
                        halfExtent = neckDistance * HalfExtentPerNeckDistance;
                        source = "ViewPosition（撮影範囲: Head / Neck）";
                        return view;
                    }
                }
            }

            // ボーン情報が取れない場合は、ViewPosition の高さから撮影範囲を推定する。
            var height = Mathf.Max(0.01f, Vector3.Distance(view, avatar.transform.position));
            halfExtent = height * HalfExtentRatio;
            source = "ViewPosition";
            return view;
        }

        /// <summary>
        /// 背景を黒と白で 2 回撮影し、その差から背景を透過した PNG を作る。
        /// シェーダーが出力する透明度に頼らないため、不透明なシェーダーでも穴が空かない。
        /// 背景が黒のときの色 = 色 × 不透明度、白のときの色 = 色 × 不透明度 + (1 - 不透明度) なので、
        /// 両者の差から不透明度が求まる。
        /// 何も写っていない（全ピクセルが透明）場合は null を返す。visiblePixels は写ったピクセル数。
        /// </summary>
        private static byte[] RenderTransparentPng(Camera cam, RenderTexture renderTexture, GameObject directDrawSource, out int visiblePixels)
        {
            visiblePixels = 0;

            var black = RenderWithBackground(cam, renderTexture, Color.black, directDrawSource);
            var white = RenderWithBackground(cam, renderTexture, Color.white, directDrawSource);

            var width = black.width;
            var height = black.height;
            var pixelsBlack = black.GetPixels32();
            var pixelsWhite = white.GetPixels32();
            UnityEngine.Object.DestroyImmediate(black);
            UnityEngine.Object.DestroyImmediate(white);

            // 診断用: 黒背景の最大値と白背景の最小値。どちらも背景色のままなら、何も描画されていない。
            var maxOnBlack = 0;
            var minOnWhite = 255;
            for (var i = 0; i < pixelsBlack.Length; i++)
            {
                maxOnBlack = Mathf.Max(maxOnBlack, pixelsBlack[i].r, pixelsBlack[i].g, pixelsBlack[i].b);
                minOnWhite = Mathf.Min(minOnWhite, pixelsWhite[i].r, pixelsWhite[i].g, pixelsWhite[i].b);
            }
            Debug.Log($"{LogPrefix}読み取り結果: 黒背景の最大値 = {maxOnBlack}, 白背景の最小値 = {minOnWhite}（0 / 255 のままなら何も描画されていません）");

            var output = new Color32[pixelsBlack.Length];
            for (var i = 0; i < output.Length; i++)
            {
                var diff = ((pixelsWhite[i].r - pixelsBlack[i].r)
                          + (pixelsWhite[i].g - pixelsBlack[i].g)
                          + (pixelsWhite[i].b - pixelsBlack[i].b)) / (3f * 255f);
                var alpha = Mathf.Clamp01(1f - diff);

                if (alpha < 0.004f)
                {
                    output[i] = new Color32(0, 0, 0, 0);
                    continue;
                }
                visiblePixels++;

                var r = Mathf.Clamp01(pixelsBlack[i].r / 255f / alpha);
                var g = Mathf.Clamp01(pixelsBlack[i].g / 255f / alpha);
                var b = Mathf.Clamp01(pixelsBlack[i].b / 255f / alpha);
                output[i] = new Color32(
                    (byte)Mathf.RoundToInt(r * 255f),
                    (byte)Mathf.RoundToInt(g * 255f),
                    (byte)Mathf.RoundToInt(b * 255f),
                    (byte)Mathf.RoundToInt(alpha * 255f));
            }

            if (visiblePixels == 0) return null;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(output);
            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }

        private static Texture2D RenderWithBackground(Camera cam, RenderTexture renderTexture, Color background, GameObject directDrawSource)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.targetTexture = renderTexture;

            // directDrawSource が指定されているときは、メッシュを直接このカメラへ描画する（描画ごとに登録し直す）
            var temporaryMeshes = new List<Mesh>();
            if (directDrawSource != null)
            {
                QueueDirectDraw(directDrawSource, cam, temporaryMeshes);
            }

            cam.Render();

            foreach (var mesh in temporaryMeshes)
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }

            var previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();

            RenderTexture.active = previous;
            cam.targetTexture = null;
            return texture;
        }
    }
}
#endif
