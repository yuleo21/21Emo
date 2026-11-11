#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// アイコン撮影とカメラプレビューで使う、撮影用のアバター複製を作る処理。
    ///
    /// ・アバターを複製し、必要なら NDMF の全プラグインを適用する。適用で表情のシェイプキーが全て見つからなくなった
    ///   ときだけ適用しない複製に切り替え、一部が見つからないときは、その分が反映されないことを知らせて適用したまま撮影する。
    /// ・表情クリップのカーブ（ブレンドシェイプ・オブジェクトの有効/無効・レンダラーの有効/無効・マテリアル）を、
    ///   AnimationClip.SampleAnimation を使わずに複製へ直接適用する。適用した変更は ChangeLog で元に戻せる。
    /// 元のアバターには触らない。
    /// </summary>
    internal static class yulEmoAvatarCloneBuilder
    {
        // ログの接頭辞は、撮影側（yulEmoIconCapturer）と合わせる
        private const string LogPrefix = "[21Emo] アイコン撮影: ";

        // NDMF 適用後にシェイプキーが見つからなかったときの診断ファイル（プロジェクトのルートに書く）
        private const string DiagnosisFileName = "21Emo_NDMF_CaptureDiagnosis.txt";

        private const string BlendShapePrefix = "blendShape.";
        private const string MaterialPrefix = "m_Materials.Array.data[";

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
                    // 診断用に、NDMF を適用する前の複製に付いているコンポーネントの一覧を控えておく
                    var componentInventory = CollectComponentInventory(clone);

                    // 自動最適化（Avatar Optimizer の Trace And Optimize）は、使われていないシェイプキーの削除や
                    // メッシュの統合をして、表情が反映されなくなることがあるので、複製からだけ外す。
                    StripAutoOptimizers(clone);

                    ProcessWithNDMF(clone);
                    Debug.Log(LogPrefix + "NDMF を適用しました");

                    // 汎用方針（個別の最適化ツールへの対策はしない）:
                    // NDMF の適用でシェイプキーが一部見つからなくなっても、複製は捨てない。
                    // 見つからなかったものは「その分だけ撮影に反映されない」ものとして名前を報告し、NDMF を適用したまま撮影する。
                    // 元のアバターで使えていたシェイプキーが全て見つからなくなった場合だけ、
                    // 表情が丸ごと反映されない撮影になるので、NDMF を適用しない複製に切り替える。
                    var totalUnique = CountUniqueBlendShapeBindings(clips);
                    var originalMissing = CollectMissingBlendShapes(avatarRoot, clips);
                    var cloneMissing = CollectMissingBlendShapes(clone, clips);
                    var originalMissingKeys = new HashSet<string>();
                    foreach (var entry in originalMissing) originalMissingKeys.Add(entry.Key);
                    var lost = new List<MissingBlendShapeInfo>();
                    foreach (var entry in cloneMissing)
                    {
                        if (!originalMissingKeys.Contains(entry.Key)) lost.Add(entry);
                    }
                    if (lost.Count == 0) return clone;

                    var resolvable = totalUnique - originalMissing.Count;
                    Debug.Log($"{LogPrefix}NDMF の適用で見つからなくなったシェイプキー: {lost.Count} 個（元のアバターで使えるシェイプキーは {resolvable} 個）");

                    // 撮影中は Console に大量のログが流れて診断を探しにくいので、詳細はファイルにも書く
                    var lostLines = new List<string>();
                    foreach (var entry in lost) lostLines.Add(entry.Line);
                    var extraSections = new List<string>
                    {
                        string.Empty,
                        "== 撮影用複製にあったコンポーネント（NDMF 適用前） ==",
                    };
                    extraSections.AddRange(componentInventory);
                    extraSections.AddRange(BuildRendererShapeDump(avatarRoot, clone, clips));
                    var diagnosisPath = WriteDiagnosisFile(lost.Count, lostLines, extraSections);
                    var diagnosisNote = diagnosisPath != null
                        ? $"\n（詳細を、プロジェクトのルートの {DiagnosisFileName} に書きました）"
                        : string.Empty;

                    if (resolvable > 0 && lost.Count >= resolvable)
                    {
                        LogMissingBlendShapes(clone, clips);
                        Debug.LogWarning(LogPrefix + "NDMF の適用後は表情のシェイプキーが全て見つからなくなるため、NDMF を適用せずに撮影します");
                        message = "NDMF の適用後は表情のシェイプキーが全て見つからなかったため、NDMF を適用せずに撮影しました。" + diagnosisNote;
                        UnityEngine.Object.DestroyImmediate(clone);
                        clone = NewClone(avatarRoot);
                        return clone;
                    }

                    Debug.LogWarning($"{LogPrefix}見つからなくなったシェイプキーの分は撮影に反映されません: {JoinShapeNames(lost, 5)}");
                    message = $"NDMF を適用して撮影しましたが、{lost.Count} 個のシェイプキーが見つからず、その分は反映されませんでした（{JoinShapeNames(lost, 5)}）。" + diagnosisNote;
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

            // NDMF 内の Avatar Optimizer（OptimizationMetrics）が、フォーマット未設定の RenderTexture を持つアバターで
            // 「Unsupported texture type」というエラーログを出すことがある。撮影の結果には影響しないので、
            // NDMF を適用している間だけ、このメッセージに限ってログに出さない（他のログはそのまま出す）。
            var logger = Debug.unityLogger;
            var originalHandler = logger.logHandler;
            logger.logHandler = new NoiseFilterLogHandler(originalHandler);
            try
            {
                target.Invoke(null, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
            finally
            {
                logger.logHandler = originalHandler;
            }
        }

        /// <summary>特定のメッセージだけを捨て、それ以外は元のログ出力へそのまま渡す。</summary>
        private sealed class NoiseFilterLogHandler : ILogHandler
        {
            private const string IgnoredPrefix = "Unsupported texture type. assuming";

            private readonly ILogHandler _inner;

            public NoiseFilterLogHandler(ILogHandler inner)
            {
                _inner = inner;
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                var text = args != null && args.Length > 0 && args[0] != null ? args[0].ToString() : format;
                if (text != null && text.StartsWith(IgnoredPrefix, StringComparison.Ordinal)) return;

                _inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                _inner.LogException(exception, context);
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
        internal sealed class ChangeLog
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
        internal static void ApplyFaceCurves(GameObject root, AnimationClip clip, float time, ChangeLog changes)
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
        /// NDMF の適用後に見つからなかったシェイプキーと、複製に残っているシェイプキーの数を Console に出す（原因調べ用）。
        /// 見つからないものだけを、理由（パス先のレンダラーが無い／メッシュが無い／そのレンダラーにシェイプキーが無い）と一緒に出す。
        /// 同じ名前のシェイプキーが複製の中の別のレンダラーに残っている場合は、その場所も出す。
        /// メッシュの統合や移動で置き場所が変わっただけなら、そちらに出る。
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

            var details = CollectMissingBlendShapes(root, clips);
            var shown = 0;
            foreach (var entry in details)
            {
                Debug.Log(LogPrefix + entry.Line);
                if (++shown >= 10) break;
            }
        }

        /// <summary>見つからないシェイプキー 1 件分の情報。Key は「パス|プロパティ名」、Line は表示用の文字列。</summary>
        private sealed class MissingBlendShapeInfo
        {
            public string Key;
            public string ShapeName;
            public string Line;
        }

        /// <summary>
        /// clips のブレンドシェイプのカーブのうち、重複を除いたものの数を返す。
        /// </summary>
        private static int CountUniqueBlendShapeBindings(AnimationClip[] clips)
        {
            var seen = new HashSet<string>();
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
                    seen.Add(binding.path + "|" + binding.propertyName);
                }
            }
            return seen.Count;
        }

        /// <summary>
        /// シェイプキーの名前を、表示用に最大 max 個まで「、」でつないだ文字列にする。多い場合は残りの数を付ける。
        /// </summary>
        private static string JoinShapeNames(List<MissingBlendShapeInfo> entries, int max)
        {
            var names = new List<string>();
            foreach (var entry in entries)
            {
                if (!names.Contains(entry.ShapeName)) names.Add(entry.ShapeName);
            }

            var head = names.GetRange(0, Math.Min(max, names.Count));
            var text = string.Join(", ", head);
            if (names.Count > max) text += $" ほか {names.Count - max} 個";
            return text;
        }

        /// <summary>
        /// root の中で見つからないシェイプキーを、理由付きで返す（Console と診断ファイルで共用する）。
        /// 理由は、パス先のレンダラーが無い／メッシュが無い／そのレンダラーにシェイプキーが無い、のどれか。
        /// 同じ名前のシェイプキーが別のレンダラーに残っている場合は、その場所も入れる。
        /// </summary>
        private static List<MissingBlendShapeInfo> CollectMissingBlendShapes(GameObject root, AnimationClip[] clips)
        {
            var details = new List<MissingBlendShapeInfo>();
            if (clips == null) return details;

            var seen = new HashSet<string>();
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

                    var shapeName = binding.propertyName.Substring(BlendShapePrefix.Length);
                    var renderer = ResolveComponentOrObject(root, binding) as SkinnedMeshRenderer;
                    string reason;
                    if (renderer == null)
                    {
                        reason = "パス先のレンダラーがありません";
                    }
                    else if (renderer.sharedMesh == null)
                    {
                        reason = "パス先のレンダラーにメッシュがありません";
                    }
                    else if (renderer.sharedMesh.GetBlendShapeIndex(shapeName) >= 0)
                    {
                        continue;
                    }
                    else
                    {
                        reason = "パス先のレンダラーにそのシェイプキーがありません";
                    }

                    var elsewhere = FindShapeOwners(root, shapeName, renderer);
                    var hint = elsewhere.Count > 0
                        ? $"（同じ名前のシェイプキーは別のレンダラーにあります: {string.Join(", ", elsewhere)}）"
                        : "（同じ名前のシェイプキーは複製のどこにもありません）";
                    details.Add(new MissingBlendShapeInfo
                    {
                        Key = binding.path + "|" + binding.propertyName,
                        ShapeName = shapeName,
                        Line = $"見つからないシェイプキー: path = '{binding.path}', property = '{binding.propertyName}', 理由 = {reason}{hint}",
                    });
                }
            }
            return details;
        }

        /// <summary>
        /// 診断の内容を、プロジェクトのルート（Assets フォルダの 1 つ上）のファイルに書く。
        /// 撮影中は Console に大量のログが流れて診断ログを探しにくいため。失敗したら null を返す。
        /// </summary>
        private static string WriteDiagnosisFile(int lostCount, List<string> details, List<string> extraSections)
        {
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                var path = Path.Combine(projectRoot, DiagnosisFileName);

                var lines = new List<string>
                {
                    "21Emo アイコン撮影: NDMF の適用で見つからなくなったシェイプキーの診断",
                    $"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"NDMF の適用で見つからなくなったシェイプキーの数: {lostCount}",
                    string.Empty,
                };
                if (details.Count == 0)
                {
                    lines.Add("（詳細はありません）");
                }
                else
                {
                    lines.AddRange(details);
                }
                if (extraSections != null)
                {
                    lines.AddRange(extraSections);
                }

                File.WriteAllLines(path, lines);
                Debug.Log($"{LogPrefix}診断をファイルに書きました: {path}");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix}診断ファイルに書けませんでした: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// root の中にあるコンポーネントの種類と数を、診断ファイル用に文字列の並びで返す。
        /// どの最適化ツールがシェイプキーを消したかを絞るために使う。
        /// </summary>
        private static List<string> CollectComponentInventory(GameObject root)
        {
            var counts = new Dictionary<string, int>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var typeName = component.GetType().FullName ?? component.GetType().Name;
                counts.TryGetValue(typeName, out var count);
                counts[typeName] = count + 1;
            }

            var names = new List<string>(counts.Keys);
            names.Sort(StringComparer.Ordinal);

            var lines = new List<string>();
            foreach (var name in names)
            {
                lines.Add($"{name} x{counts[name]}");
            }
            return lines;
        }

        /// <summary>
        /// クリップが指すレンダラーごとに、元のアバターと NDMF 適用後のシェイプキー名を全て並べた診断用の文字列を返す。
        /// 名前が変わっただけなのか、シェイプキー自体が消えたのかを比べるために使う。
        /// </summary>
        private static List<string> BuildRendererShapeDump(GameObject originalRoot, GameObject cloneRoot, AnimationClip[] clips)
        {
            var lines = new List<string>();
            if (clips == null) return lines;

            var seenPaths = new HashSet<string>();
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
                    if (!seenPaths.Add(binding.path)) continue;

                    var original = ResolveComponentOrObject(originalRoot, binding) as SkinnedMeshRenderer;
                    var processed = ResolveComponentOrObject(cloneRoot, binding) as SkinnedMeshRenderer;

                    lines.Add(string.Empty);
                    lines.Add($"== レンダラー '{binding.path}' のシェイプキー名一覧 ==");
                    lines.Add($"-- 元のアバター（{(original != null && original.sharedMesh != null ? original.sharedMesh.blendShapeCount : 0)} 個） --");
                    AddShapeNames(lines, original);
                    lines.Add($"-- NDMF 適用後（{(processed != null && processed.sharedMesh != null ? processed.sharedMesh.blendShapeCount : 0)} 個） --");
                    AddShapeNames(lines, processed);
                }
            }
            return lines;
        }

        private static void AddShapeNames(List<string> lines, SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null)
            {
                lines.Add("（レンダラーまたはメッシュがありません）");
                return;
            }

            var mesh = renderer.sharedMesh;
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                lines.Add(mesh.GetBlendShapeName(i));
            }
        }

        /// <summary>
        /// root の中で shapeName のシェイプキーを持つレンダラーの場所（root からの相対パス）を返す。except は除く。
        /// </summary>
        private static List<string> FindShapeOwners(GameObject root, string shapeName, SkinnedMeshRenderer except)
        {
            var owners = new List<string>();
            foreach (var candidate in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (candidate == null || candidate == except || candidate.sharedMesh == null) continue;
                if (candidate.sharedMesh.GetBlendShapeIndex(shapeName) < 0) continue;

                owners.Add(GetRelativePath(root.transform, candidate.transform));
                if (owners.Count >= 3) break;
            }
            return owners;
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (target == root) return string.Empty;

            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool TryParseMaterialIndex(string propertyName, out int index)
        {
            index = -1;
            var start = propertyName.IndexOf('[');
            var end = propertyName.IndexOf(']');
            if (start < 0 || end <= start + 1) return false;
            return int.TryParse(propertyName.Substring(start + 1, end - start - 1), out index);
        }
    }
}
#endif
