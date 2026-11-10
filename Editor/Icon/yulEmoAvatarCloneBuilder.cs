#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// アイコン撮影とカメラプレビューで使う、撮影用のアバター複製を作る処理。
    ///
    /// ・アバターを複製し、必要なら NDMF の全プラグインを適用する（自動最適化で表情が消えるときは、適用しない複製に切り替える）。
    /// ・表情クリップのカーブ（ブレンドシェイプ・オブジェクトの有効/無効・レンダラーの有効/無効・マテリアル）を、
    ///   AnimationClip.SampleAnimation を使わずに複製へ直接適用する。適用した変更は ChangeLog で元に戻せる。
    /// 元のアバターには触らない。
    /// </summary>
    internal static class yulEmoAvatarCloneBuilder
    {
        // ログの接頭辞は、撮影側（yulEmoIconCapturer）と合わせる
        private const string LogPrefix = "[21Emo] アイコン撮影: ";

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
                    // 自動最適化（Avatar Optimizer の Trace And Optimize）は、使われていないシェイプキーの削除や
                    // メッシュの統合をして、表情が反映されなくなることがあるので、複製からだけ外す。
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
    }
}
#endif
