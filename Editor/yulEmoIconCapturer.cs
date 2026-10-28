#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// </summary>
    public static class yulEmoIconCapturer
    {
        public const int IconSize = 256;

        private const string LogPrefix = "[21Emo] アイコン撮影: ";

        // メッシュを直接描画する方法（フォールバック）で使うレイヤー
        internal const int DirectDrawLayer = 31;

        private const string BlendShapePrefix = "blendShape.";
        private const string MaterialPrefix = "m_Materials.Array.data[";

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

            var descriptor = avatarObject.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                descriptor = avatarObject.GetComponentInChildren<VRCAvatarDescriptor>();
            }
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
            var changes = new ChangeLog();

            try
            {
                clone = CreateClone(descriptor.gameObject, applyNDMF, clips, ref message);
                Debug.Log($"{LogPrefix}アバターを複製しました（{clone.name}）");

                // 撮影ではアニメーションを再生しない。Animator は動かさず、
                // 表情はクリップのカーブの値を直接適用する（ApplyFaceCurves）。
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
                    ApplyFaceCurves(clone, clip, clip.length, changes);
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

                    if (string.IsNullOrEmpty(folder))
                    {
                        var memoryTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = $"FaceFix_{i + 1}" };
                        memoryTexture.LoadImage(png);
                        memoryTexture.wrapMode = TextureWrapMode.Clamp;
                        result[i] = memoryTexture;
                        savedCount++;
                        continue;
                    }

                    var assetPath = $"{folder.TrimEnd('/')}/FaceFix_{i + 1}.png";
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

                    result[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
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
        /// Camera.scene（指定したシーンの中だけを描画する設定）を設定する。
        /// Unity のバージョンによる公開範囲の違いに左右されないよう、リフレクションで設定する。
        /// </summary>
        internal static void SetCameraScene(Camera cam, Scene scene)
        {
            var property = typeof(Camera).GetProperty(
                "scene",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite)
            {
                throw new MissingMemberException("Camera.scene が見つかりません。この Unity のバージョンでは撮影できません。");
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
            // ここを中心にすることで、Head ボーンの位置や姿勢に引っ張られて
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
        /// アバターを複製する。applyNDMF が true なら NDMF の全プラグインを複製に適用する。
        /// NDMF の適用に失敗した場合は、適用していない複製に切り替えて message に理由を入れる。
        /// </summary>
        internal static GameObject CreateClone(GameObject avatarRoot, bool applyNDMF, AnimationClip[] clips, ref string message)
        {
            var clone = NewClone(avatarRoot);

            if (applyNDMF)
            {
                try
                {
                    // 自動最適化（Avatar Optimizer の Trace And Optimize）は、使われていないシェイプキーの削除や
                    // メッシュの統合を行い、表情が反映されなくなることがあるので、複製からだけ外す。
                    StripAutoOptimizers(clone);

                    ProcessWithNDMF(clone);
                    Debug.Log(LogPrefix + "NDMF を適用しました");

                    // NDMF の最適化（メッシュの統合・不要なシェイプキーの削除など）で、クリップのシェイプキーが
                    // 見つからなくなると、表情が撮影に反映されない。元のアバターで見つからない数より増えていたら、
                    // NDMF を適用しない複製に切り替える。
                    var baseline = CountUnresolvedBlendShapes(avatarRoot, clips);
                    var missing = CountUnresolvedBlendShapes(clone, clips);
                    Debug.Log($"{LogPrefix}表情のシェイプキーが見つからない数: 元のアバター = {baseline}, NDMF 適用後 = {missing}");
                    if (missing <= baseline) return clone;
                    LogMissingBlendShapes(clone, clips);

                    Debug.LogWarning(LogPrefix + "NDMF の適用後は表情のシェイプキーが見つからなくなるため、NDMF を適用せずに撮影します");
                    message = "NDMF の適用後は表情のシェイプキーが見つからなかったため、NDMF を適用せずに撮影しました。";
                    UnityEngine.Object.DestroyImmediate(clone);
                    clone = NewClone(avatarRoot);
                    return clone;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    message = "NDMF の適用に失敗したため、適用していない状態のアバターで撮影しました。詳細は Console を確認してください。";
                    if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                    clone = NewClone(avatarRoot);
                }
            }

            return clone;
        }

        /// <summary>
        /// NDMF の AvatarProcessor.ProcessAvatar を呼ぶ。
        /// asmdef の参照設定やバージョン差に左右されないよう、リフレクションで探して呼び出す。
        /// </summary>
        private static void ProcessWithNDMF(GameObject avatar)
        {
            Type processorType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                processorType = assembly.GetType("nadena.dev.ndmf.AvatarProcessor");
                if (processorType != null) break;
            }
            if (processorType == null)
            {
                throw new InvalidOperationException("NDMF（nadena.dev.ndmf.AvatarProcessor）が見つかりません。");
            }

            // 引数が GameObject だけのものを優先し、無ければ「2 つ目以降がすべて省略可能」なものを使う
            MethodInfo target = null;
            object[] args = null;
            foreach (var method in processorType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "ProcessAvatar") continue;

                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(GameObject)) continue;

                var allOptional = true;
                for (var i = 1; i < parameters.Length; i++)
                {
                    if (!parameters[i].IsOptional) allOptional = false;
                }
                if (!allOptional) continue;

                if (target == null || parameters.Length < target.GetParameters().Length)
                {
                    target = method;
                    args = new object[parameters.Length];
                    args[0] = avatar;
                    for (var i = 1; i < parameters.Length; i++)
                    {
                        args[i] = Type.Missing;
                    }
                }
            }

            if (target == null)
            {
                throw new MissingMethodException("NDMF の AvatarProcessor.ProcessAvatar(GameObject) が見つかりません。");
            }

            try
            {
                target.Invoke(null, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        private static GameObject NewClone(GameObject avatarRoot)
        {
            var clone = UnityEngine.Object.Instantiate(avatarRoot);
            clone.name = avatarRoot.name;
            clone.SetActive(true);
            clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            clone.transform.localScale = avatarRoot.transform.lossyScale;
            return clone;
        }

        /// <summary>
        /// クリップで変えたオブジェクトの有効 / 無効・レンダラーの有効 / 無効・マテリアルを控えておき、
        /// 次のクリップの前に元へ戻すための記録。
        /// </summary>
        private sealed class ChangeLog
        {
            private readonly Dictionary<GameObject, bool> _active = new Dictionary<GameObject, bool>();
            private readonly Dictionary<Renderer, bool> _enabled = new Dictionary<Renderer, bool>();
            private readonly Dictionary<Renderer, Material[]> _materials = new Dictionary<Renderer, Material[]>();

            public void SetActive(GameObject target, bool value)
            {
                if (!_active.ContainsKey(target)) _active[target] = target.activeSelf;
                target.SetActive(value);
            }

            public void SetEnabled(Renderer target, bool value)
            {
                if (!_enabled.ContainsKey(target)) _enabled[target] = target.enabled;
                target.enabled = value;
            }

            public bool SetMaterial(Renderer target, int index, Material material)
            {
                var current = target.sharedMaterials;
                if (index < 0 || index >= current.Length) return false;

                if (!_materials.ContainsKey(target)) _materials[target] = current;
                var copy = (Material[])current.Clone();
                copy[index] = material;
                target.sharedMaterials = copy;
                return true;
            }

            public void Restore()
            {
                foreach (var pair in _active)
                {
                    if (pair.Key != null) pair.Key.SetActive(pair.Value);
                }
                foreach (var pair in _enabled)
                {
                    if (pair.Key != null) pair.Key.enabled = pair.Value;
                }
                foreach (var pair in _materials)
                {
                    if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
                }
                _active.Clear();
                _enabled.Clear();
                _materials.Clear();
            }
        }

        /// <summary>
        /// クリップの表情に関わるカーブを、指定時刻の値で複製へ直接適用する。
        /// Transform / Animator のカーブは使わないので、体の姿勢は変わらない。
        /// </summary>
        private static void ApplyFaceCurves(GameObject root, AnimationClip clip, float time, ChangeLog changes)
        {
            var blendApplied = 0;
            var blendSkipped = 0;
            var activeApplied = 0;
            var enabledApplied = 0;
            var materialApplied = 0;
            var unresolved = 0;
            var ignored = 0;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                // 体の姿勢を動かすカーブは使わない
                if (binding.type == typeof(Transform) || binding.type == typeof(Animator)) continue;

                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;
                var value = curve.Evaluate(time);

                if (binding.type == typeof(SkinnedMeshRenderer) &&
                    binding.propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal))
                {
                    if (ApplyBlendShape(root, binding, value)) blendApplied++;
                    else blendSkipped++;
                    continue;
                }

                if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
                {
                    var target = ResolveComponentOrObject(root, binding) as GameObject;
                    if (target == null) { unresolved++; continue; }
                    changes.SetActive(target, value > 0.5f);
                    activeApplied++;
                    continue;
                }

                if (typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName == "m_Enabled")
                {
                    var target = ResolveComponentOrObject(root, binding) as Renderer;
                    if (target == null) { unresolved++; continue; }
                    changes.SetEnabled(target, value > 0.5f);
                    enabledApplied++;
                    continue;
                }

                ignored++;
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!typeof(Renderer).IsAssignableFrom(binding.type) ||
                    !binding.propertyName.StartsWith(MaterialPrefix, StringComparison.Ordinal))
                {
                    ignored++;
                    continue;
                }

                var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (frames == null || frames.Length == 0) continue;

                // time 以前で最後のキーの値（時刻が最終フレームなので、通常は最後のキー）
                var material = frames[0].value as Material;
                foreach (var frame in frames)
                {
                    if (frame.time <= time + 0.0001f) material = frame.value as Material;
                }

                if (!TryParseMaterialIndex(binding.propertyName, out var materialIndex)) { ignored++; continue; }

                var target = ResolveComponentOrObject(root, binding) as Renderer;
                if (target == null) { unresolved++; continue; }

                if (changes.SetMaterial(target, materialIndex, material)) materialApplied++;
                else unresolved++;
            }

            Debug.Log($"{LogPrefix}表情カーブ適用: {clip.name}, シェイプキー = {blendApplied}（未適用 {blendSkipped}）, " +
                      $"オブジェクト有効/無効 = {activeApplied}, レンダラー有効/無効 = {enabledApplied}, マテリアル = {materialApplied}, " +
                      $"対象が見つからない = {unresolved}, 使わないカーブ = {ignored}");
        }

        private static bool ApplyBlendShape(GameObject root, EditorCurveBinding binding, float value)
        {
            var shapeName = binding.propertyName.Substring(BlendShapePrefix.Length);

            // クリップのバインディングが指すレンダラーのシェイプキーにだけ適用する（名前での代替探索はしない）
            var renderer = ResolveComponentOrObject(root, binding) as SkinnedMeshRenderer;
            if (renderer == null || renderer.sharedMesh == null) return false;

            var index = renderer.sharedMesh.GetBlendShapeIndex(shapeName);
            if (index < 0) return false;

            renderer.SetBlendShapeWeight(index, value);
            return true;
        }

        /// <summary>
        /// バインディングが指すオブジェクト（コンポーネントまたは GameObject）を返す。
        /// Unity の解決を使い、失敗したときはクリップに記録された相対パスで探す。
        /// </summary>
        private static UnityEngine.Object ResolveComponentOrObject(GameObject root, EditorCurveBinding binding)
        {
            try
            {
                var animated = AnimationUtility.GetAnimatedObject(root, binding);
                if (animated != null) return animated;
            }
            catch (Exception)
            {
                // 下のパス解決へ進む
            }

            var transform = string.IsNullOrEmpty(binding.path) ? root.transform : root.transform.Find(binding.path);
            if (transform == null) return null;

            if (binding.type == typeof(GameObject)) return transform.gameObject;
            return binding.type != null && typeof(Component).IsAssignableFrom(binding.type)
                ? transform.GetComponent(binding.type)
                : null;
        }

        /// <summary>
        /// clips のブレンドシェイプのカーブのうち、root の中で、パスの指すレンダラーに
        /// 対象のシェイプキーが見つからないものの数を返す（同じパスとプロパティは 1 つと数える）。
        /// </summary>
        private static int CountUnresolvedBlendShapes(GameObject root, AnimationClip[] clips)
        {
            var seen = new HashSet<string>();
            var missing = 0;
            if (clips == null) return 0;

            foreach (var clip in clips)
            {
                if (clip == null) continue;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) ||
                        !binding.propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!seen.Add(binding.path + "|" + binding.propertyName)) continue;

                    var renderer = ResolveComponentOrObject(root, binding) as SkinnedMeshRenderer;
                    var shapeName = binding.propertyName.Substring(BlendShapePrefix.Length);
                    if (renderer == null || renderer.sharedMesh == null || renderer.sharedMesh.GetBlendShapeIndex(shapeName) < 0)
                    {
                        missing++;
                    }
                }
            }
            return missing;
        }

        /// <summary>
        /// 複製から Avatar Optimizer の Trace And Optimize を外す（NDMF の適用前に呼ぶ）。
        /// Avatar Optimizer が入っていなくても動くよう、型名の文字列で探す。
        /// 見つかった Avatar Optimizer のコンポーネントの種類と数は Console に出す（原因調べ用）。
        /// </summary>
        private static void StripAutoOptimizers(GameObject root)
        {
            const string Namespace = "Anatawa12.AvatarOptimizer.";
            const string TraceAndOptimize = "Anatawa12.AvatarOptimizer.TraceAndOptimize";

            var present = new Dictionary<string, int>();
            var toRemove = new List<Component>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;

                var typeName = component.GetType().FullName ?? component.GetType().Name;
                if (!typeName.StartsWith(Namespace, StringComparison.Ordinal)) continue;

                present.TryGetValue(typeName, out var count);
                present[typeName] = count + 1;
                if (typeName == TraceAndOptimize) toRemove.Add(component);
            }

            if (present.Count == 0)
            {
                Debug.Log(LogPrefix + "Avatar Optimizer のコンポーネントは見つかりませんでした");
                return;
            }

            var list = new List<string>();
            foreach (var pair in present) list.Add($"{pair.Key.Substring(Namespace.Length)} x{pair.Value}");
            Debug.Log($"{LogPrefix}Avatar Optimizer のコンポーネント: {string.Join(", ", list)}");

            foreach (var component in toRemove)
            {
                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{LogPrefix}Trace And Optimize を外せませんでした: {e.Message}");
                }
            }
            if (toRemove.Count > 0)
            {
                Debug.Log($"{LogPrefix}複製から Trace And Optimize を外しました（{toRemove.Count} 個）");
            }
        }

        /// <summary>
        /// NDMF の適用後に見つからなかったシェイプキーの例と、複製に残っているシェイプキーの数を Console に出す（原因調べ用）。
        /// </summary>
        private static void LogMissingBlendShapes(GameObject root, AnimationClip[] clips)
        {
            var meshCount = 0;
            var withShapes = 0;
            var totalShapes = 0;
            foreach (var candidate in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (candidate == null) continue;
                meshCount++;
                if (candidate.sharedMesh == null || candidate.sharedMesh.blendShapeCount == 0) continue;
                withShapes++;
                totalShapes += candidate.sharedMesh.blendShapeCount;
            }
            Debug.Log($"{LogPrefix}NDMF 適用後の複製: スキンメッシュ = {meshCount} 個, シェイプキーを持つもの = {withShapes} 個, シェイプキー合計 = {totalShapes} 個");

            var shown = 0;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) ||
                        !binding.propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    Debug.Log($"{LogPrefix}見つからないシェイプキーの例: path = '{binding.path}', property = '{binding.propertyName}'");
                    if (++shown >= 3) return;
                }
            }
        }

        private static bool TryParseMaterialIndex(string propertyName, out int index)
        {
            index = -1;
            var start = propertyName.IndexOf('[');
            var end = propertyName.IndexOf(']');
            if (start < 0 || end <= start + 1) return false;
            return int.TryParse(propertyName.Substring(start + 1, end - start - 1), out index);
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
