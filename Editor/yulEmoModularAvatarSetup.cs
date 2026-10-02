#if UNITY_EDITOR
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace yulEmo
{
    /// <summary>
    /// 生成した 21Facial コントローラーを Modular Avatar 経由でアバターに組み込む処理。
    ///
    /// アバター直下に "21Emo" オブジェクトを作成し、以下の構成にする:
    ///
    ///   21Emo
    ///     ├ MA Merge Animator   : 21Facial コントローラーを FX レイヤーへ結合 (Absolute パス)
    ///     ├ MA Parameters       : FaceFix (Int) / FaceLock (Bool) を同期パラメータとして宣言
    ///     ├ MA Menu Installer   : メニューをアバターのルートメニューへインストール
    ///     ├ MA Menu Item        : SubMenu (21Emo)。MenuSource = Children
    ///     ├ Lock                : MA Menu Item (Toggle)  FaceLock = 1
    ///     ├ 1                   : MA Menu Item (Toggle)  FaceFix  = 1
    ///     ├ ...
    ///     └ 8                   : MA Menu Item (Toggle)  FaceFix  = 8
    ///
    /// ※ 1〜8 のトグルは、FaceFix を使用する設定で、かつクリップが設定されているスロットのみ作成される
    ///   （Generator が FaceFix ステートを作る条件と同じ）。
    /// </summary>
    public static class yulEmoModularAvatarSetup
    {
        public const string RootObjectName = "21Emo";
        public const string FaceFixParameter = "FaceFix";
        public const string FaceLockParameter = "FaceLock";
        public const string LockItemName = "Lock";

        /// <summary>
        /// アバターに Modular Avatar のコンポーネント一式をセットアップする。
        /// 既に "21Emo" オブジェクトがアバター直下にある場合は作り直す（Undo 可能）。
        /// </summary>
        /// <param name="avatarObject">VRCAvatarDescriptor を持つ（または親/子に持つ）シーン上のアバター。</param>
        /// <param name="controller">Merge Animator に設定するコントローラー。</param>
        /// <param name="config">Generator に渡した設定（FaceFix の有無・クリップ判定に使用）。</param>
        /// <param name="message">結果メッセージ（失敗時は理由）。</param>
        public static bool TrySetup(
            GameObject avatarObject,
            RuntimeAnimatorController controller,
            yulEmoGenerator.Config config,
            out string message)
        {
            if (avatarObject == null)
            {
                message = "アバターが指定されていません。";
                return false;
            }

            if (controller == null)
            {
                message = "Merge Animator に設定するコントローラーが見つかりません。";
                return false;
            }

            if (EditorUtility.IsPersistent(avatarObject))
            {
                message = "シーン上のアバターを指定してください（プロジェクト内のプレハブアセットには設定できません）。";
                return false;
            }

            var descriptor = avatarObject.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                descriptor = avatarObject.GetComponentInChildren<VRCAvatarDescriptor>();
            }

            if (descriptor == null)
            {
                message = "VRCAvatarDescriptor が見つかりませんでした。";
                return false;
            }

            var avatarRoot = descriptor.gameObject;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("21Emo: Modular Avatar セットアップ");
            var undoGroup = Undo.GetCurrentGroup();

            // 既存の 21Emo オブジェクトがあれば作り直す
            var existing = avatarRoot.transform.Find(RootObjectName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
            }

            var root = new GameObject(RootObjectName);
            Undo.RegisterCreatedObjectUndo(root, "Create 21Emo");
            root.transform.SetParent(avatarRoot.transform, false);

            // ── MA Merge Animator ─────────────────────────────
            // クリップのバインディングはアバタールート基準なので Absolute。
            // WD は UI で選択した値をそのまま使うため、Match Avatar Write Defaults は OFF。
            var merge = Undo.AddComponent<ModularAvatarMergeAnimator>(root);
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.deleteAttachedAnimator = false;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = false;
            merge.layerPriority = 0;

            // ── MA Parameters ─────────────────────────────────
            // FaceFix / FaceLock を明示的に宣言（型を確実に Int / Bool にするため）。
            // 他プレイヤーからも見えるよう同期する。保存はしない（アバター変更で 0 に戻す）。
            var parameters = Undo.AddComponent<ModularAvatarParameters>(root);
            parameters.parameters.Add(new ParameterConfig
            {
                nameOrPrefix = FaceLockParameter,
                syncType = ParameterSyncType.Bool,
                defaultValue = 0f,
                saved = false,
                localOnly = false,
            });

            var hasFaceFix = HasAnyFaceFix(config);
            if (hasFaceFix)
            {
                parameters.parameters.Add(new ParameterConfig
                {
                    nameOrPrefix = FaceFixParameter,
                    syncType = ParameterSyncType.Int,
                    defaultValue = 0f,
                    saved = false,
                    localOnly = false,
                });
            }

            // ── MA Menu Installer + サブメニュー (MA Menu Item) ──
            // 同じオブジェクトに Installer と SubMenu 型の Menu Item を置くと、
            // その Menu Item 自体がルートメニューにインストールされる。
            Undo.AddComponent<ModularAvatarMenuInstaller>(root);

            var subMenu = Undo.AddComponent<ModularAvatarMenuItem>(root);
            subMenu.Control = new VRCExpressionsMenu.Control
            {
                name = RootObjectName,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty },
            };
            subMenu.MenuSource = SubmenuSource.Children;
            subMenu.automaticValue = false;

            // ── Lock トグル (FaceLock) ────────────────────────
            AddToggle(root.transform, LockItemName, FaceLockParameter, 1f);

            // ── FaceFix トグル (1〜8) ─────────────────────────
            if (hasFaceFix)
            {
                for (var i = 0; i < config.FaceFixClips.Length; i++)
                {
                    if (config.FaceFixClips[i] == null) continue;

                    var value = i + 1;
                    AddToggle(root.transform, value.ToString(), FaceFixParameter, value);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (avatarRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(avatarRoot.scene);
            }

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            message = hasFaceFix
                ? $"{avatarRoot.name}/{RootObjectName} に Modular Avatar のセットアップを行いました。\n（Merge Animator / Parameters / Menu Installer / Lock + FaceFix トグル）"
                : $"{avatarRoot.name}/{RootObjectName} に Modular Avatar のセットアップを行いました。\n（Merge Animator / Parameters / Menu Installer / Lock トグル）";
            return true;
        }

        private static bool HasAnyFaceFix(yulEmoGenerator.Config config)
        {
            if (config == null || !config.UseFaceFix || config.FaceFixClips == null) return false;

            foreach (var clip in config.FaceFixClips)
            {
                if (clip != null) return true;
            }

            return false;
        }

        /// <summary>
        /// Toggle 型の MA Menu Item を持つ子オブジェクトを作成する。
        /// オブジェクト名がそのままメニュー上の表示名になる。
        /// </summary>
        private static void AddToggle(Transform parent, string name, string parameterName, float value)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create 21Emo Toggle");
            go.transform.SetParent(parent, false);

            var item = Undo.AddComponent<ModularAvatarMenuItem>(go);
            item.Control = new VRCExpressionsMenu.Control
            {
                name = name,
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameterName },
                value = value,
            };
            item.MenuSource = SubmenuSource.Children;
            item.automaticValue = false;
            item.isSynced = true;
            item.isSaved = false;
            item.isDefault = false;
        }
    }
}
#endif