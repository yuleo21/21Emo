#if UNITY_EDITOR
using System.Collections.Generic;
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
    ///     ├ MA Parameters       : FaceFix (Int) / FaceLock (Bool) / FacePreset (Bool or Int) を同期パラメータとして宣言
    ///     ├ MA Menu Installer   : メニューをアバターのルートメニューへインストール
    ///     ├ MA Menu Item        : SubMenu (21Emo)。MenuSource = Children
    ///     ├ <プリセット名>      : MA Menu Item (Toggle)  FacePreset = 1〜N（Lock より手前。プリセット 1 個なら Bool、2 個以上なら Int）
    ///     ├ Lock                : MA Menu Item (Toggle)  FaceLock = 1
    ///     ├ ジェスチャ表情      : MA Menu Item (SubMenu)  ※ Config.UseGestureFaceMenu がオンのときだけ
    ///     │   ├ （プリセットがあるとき）Default / <プリセット名> : SubMenu
    ///     │   │   ├ （Both モードのとき）Left / Right : SubMenu
    ///     │   │   │   └ <ジェスチャ名> : Toggle  FaceFix = 1 から順（Generator.GetGestureFaceEntries と同じ割り当て）
    ///     │   │   └ （Either モードのとき）<ジェスチャ名> : Toggle
    ///     ├ 1                   : MA Menu Item (Toggle)  FaceFix  = ジェスチャ表情の個数 + 1（名前は Config.FaceFixNames で変更可。既定は番号）
    ///     ├ ...
    ///     └ 8                   : MA Menu Item (Toggle)  FaceFix  = ジェスチャ表情の個数 + 8
    ///
    /// ※ 1〜8 のトグルは、FaceFix を使用する設定で、かつクリップが設定されているスロットのみ作成される
    ///   （Generator が FaceFix ステートを作る条件と同じ）。
    /// ※ ジェスチャ表情を使わないときは、FaceFix の値はこれまで通り 1〜8。
    /// </summary>
    public static class yulEmoModularAvatarSetup
    {
        public const string RootObjectName = "21Emo";
        public const string FaceFixParameter = "FaceFix";
        public const string FaceLockParameter = "FaceLock";
        public const string FacePresetParameter = yulEmoGenerator.FacePresetParameter;
        public const string LockItemName = "Lock";
        public const string GestureFaceMenuName = "ジェスチャ表情";

        /// <summary>「21Emo」オブジェクトに付ける NDMF 設定コンポーネント（yulEmoNDMFComponent）の初期値。</summary>
        public sealed class NDMFOptions
        {
            public string IconFolder;
            public yulEmoCameraSettings Camera;
            public bool CaptureWithNDMF = true;

            /// <summary>作成時のウィンドウの設定。「21Emo」オブジェクトに保存し、ウィンドウへ読み込み直せるようにする。</summary>
            public yulEmoSettingsSnapshot Snapshot;
        }

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
            out string message,
            NDMFOptions setupOptions = null)
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
            // 作り直す前に、NDMF 設定コンポーネントのユーザー設定（ビルド時撮影・クリップ差し替え）を控えて引き継ぐ
            var existing = avatarRoot.transform.Find(RootObjectName);
            var keptRecapture = false;
            var keptFlatten = false;
            var keptCaptureWithNDMF = setupOptions != null ? setupOptions.CaptureWithNDMF : true;
            List<yulEmoNDMFComponent.ClipSwap> keptSwaps = null;
            if (existing != null)
            {
                var oldComponent = existing.GetComponent<yulEmoNDMFComponent>();
                if (oldComponent != null)
                {
                    keptRecapture = oldComponent.recaptureOnBuild;
                    keptFlatten = oldComponent.flattenToZeroFrame;
                    keptCaptureWithNDMF = oldComponent.captureWithNDMF;
                    keptSwaps = new List<yulEmoNDMFComponent.ClipSwap>();
                    foreach (var swap in oldComponent.clipSwaps)
                    {
                        if (swap == null) continue;
                        keptSwaps.Add(new yulEmoNDMFComponent.ClipSwap { source = swap.source, replacement = swap.replacement });
                    }
                }
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

            // ── 21Emo NDMF 設定コンポーネント ─────────────────
            // アイコンの撮影し直し・ビルド時の撮影・クリップの差し替えを担当する。
            var settingsComponent = Undo.AddComponent<yulEmoNDMFComponent>(root);
            settingsComponent.recaptureOnBuild = keptRecapture;
            settingsComponent.flattenToZeroFrame = keptFlatten;
            settingsComponent.captureWithNDMF = keptCaptureWithNDMF;
            if (keptSwaps != null) settingsComponent.clipSwaps = keptSwaps;
            if (setupOptions != null && setupOptions.Snapshot != null)
            {
                settingsComponent.settings = setupOptions.Snapshot;
                settingsComponent.hasSettings = true;
            }
            if (setupOptions != null)
            {
                settingsComponent.iconFolder = setupOptions.IconFolder ?? string.Empty;
                if (setupOptions.Camera != null)
                {
                    settingsComponent.cameraRight = setupOptions.Camera.Right;
                    settingsComponent.cameraUp = setupOptions.Camera.Up;
                    settingsComponent.cameraForward = setupOptions.Camera.Forward;
                    settingsComponent.cameraFov = setupOptions.Camera.Fov;
                }
            }
            // アイコン撮影用のクリップ。インデックス = FaceFix の値 - 1
            // （ジェスチャ表情メニューを使うときは、先頭がジェスチャ表情で、その後ろが FaceFix のスロット 1〜8）
            var gestureFaceEntries = yulEmoGenerator.GetGestureFaceEntries(config);
            settingsComponent.faceFixClips = yulEmoGenerator.GetFixClipSlots(config);

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

            // FacePreset: 追加プリセットが 1 個なら Bool、2 個以上なら Int（Generator のパラメータ型と同じ判定）
            var presets = yulEmoGenerator.GetActivePresets(config);
            if (presets.Count > 0)
            {
                parameters.parameters.Add(new ParameterConfig
                {
                    nameOrPrefix = FacePresetParameter,
                    syncType = presets.Count == 1 ? ParameterSyncType.Bool : ParameterSyncType.Int,
                    defaultValue = 0f,
                    saved = false,
                    localOnly = false,
                });
            }

            var hasFaceFix = HasAnyFaceFix(config);
            if (hasFaceFix || gestureFaceEntries.Count > 0)
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

            // ── プリセットのトグル (FacePreset) ───────────────
            // Lock より手前に並べるため、Lock より先に作る（子オブジェクトの順番 = メニューの順番）。
            // 1 個のとき Bool なので値は 1（= True）、2 個以上のとき Int なので 1〜N。
            for (var i = 0; i < presets.Count; i++)
            {
                var presetName = string.IsNullOrWhiteSpace(presets[i].Name) ? $"Preset {i + 1}" : presets[i].Name.Trim();
                AddToggle(root.transform, presetName, FacePresetParameter, i + 1);
            }

            // ── Lock トグル (FaceLock) ────────────────────────
            AddToggle(root.transform, LockItemName, FaceLockParameter, 1f);

            // ── ジェスチャ表情メニュー (FaceFix = 1 から順) ───
            // Lock の後、FaceFix トグルより前に並べる。
            if (gestureFaceEntries.Count > 0)
            {
                AddGestureFaceMenu(root.transform, gestureFaceEntries, presets.Count > 0);
            }

            // ── FaceFix トグル (1〜8) ─────────────────────────
            // 値はジェスチャ表情の個数だけ後ろにずれる（Generator と同じ）
            if (hasFaceFix)
            {
                for (var i = 0; i < config.FaceFixClips.Length; i++)
                {
                    if (config.FaceFixClips[i] == null) continue;

                    var value = gestureFaceEntries.Count + i + 1;
                    var icon = config.FaceFixIcons != null && i < config.FaceFixIcons.Length ? config.FaceFixIcons[i] : null;
                    var menuName = config.FaceFixNames != null && i < config.FaceFixNames.Length && !string.IsNullOrWhiteSpace(config.FaceFixNames[i])
                        ? config.FaceFixNames[i].Trim()
                        : value.ToString();
                    AddToggle(root.transform, menuName, FaceFixParameter, value, icon);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (avatarRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(avatarRoot.scene);
            }

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            var menuSummary = (presets.Count > 0 ? "プリセット + " : "")
                + "Lock"
                + (gestureFaceEntries.Count > 0 ? " + ジェスチャ表情" : "")
                + (hasFaceFix ? " + FaceFix" : "")
                + " トグル";
            message = $"{avatarRoot.name}/{RootObjectName} に Modular Avatar のセットアップを行いました。\n（Merge Animator / Parameters / Menu Installer / {menuSummary}）";
            return true;
        }

        /// <summary>
        /// セットアップ済みの "21Emo" 配下の FaceFix トグル（ジェスチャ表情を含む）に、メニューアイコンを設定する。
        /// icons は FaceFix の値の順（0 = FaceFix の値 1）。null の要素は何もしない。
        /// </summary>
        public static void ApplyFaceFixIcons(GameObject avatarObject, Texture2D[] icons)
        {
            if (avatarObject == null || icons == null) return;

            var descriptor = avatarObject.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                descriptor = avatarObject.GetComponentInChildren<VRCAvatarDescriptor>();
            }
            if (descriptor == null) return;

            var root = descriptor.transform.Find(RootObjectName);
            if (root == null) return;

            ApplyFaceFixIcons(root, icons, true);

            if (descriptor.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);
            }
        }

        /// <summary>
        /// root（「21Emo」オブジェクト）配下の FaceFix トグルにアイコンを設定する。
        /// recordUndo が false のときは Undo に記録しない（NDMF のビルド中の複製に対して使う）。
        /// </summary>
        public static void ApplyFaceFixIcons(Transform root, Texture2D[] icons, bool recordUndo)
        {
            if (root == null || icons == null) return;

            for (var i = 0; i < icons.Length; i++)
            {
                if (icons[i] == null) continue;

                // メニュー名は変更できるため、名前ではなく「FaceFix パラメータ = i + 1」のトグルを探す
                var item = FindFaceFixItem(root, i + 1);
                if (item == null) continue;

                if (recordUndo) Undo.RecordObject(item, "21Emo: アイコン設定");
                item.Control.icon = icons[i];
                if (recordUndo) EditorUtility.SetDirty(item);
            }
        }

        private static ModularAvatarMenuItem FindFaceFixItem(Transform root, int value)
        {
            foreach (var item in root.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item.Control == null || item.Control.parameter == null) continue;
                if (item.Control.parameter.name != FaceFixParameter) continue;
                if (Mathf.Approximately(item.Control.value, value)) return item;
            }
            return null;
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
        /// 「ジェスチャ表情」サブメニューを作る。
        ///   プリセットがあるとき: Default / <プリセット名> のサブメニューを作り、その下に各表情を置く
        ///   Both モード（Left / Right のエントリ）のとき: さらに Left / Right のサブメニューに分け、その下にジェスチャ名のトグルを置く
        ///   Either モードのとき: ジェスチャ名のトグルを直接置く
        /// </summary>
        private static void AddGestureFaceMenu(Transform parent, List<yulEmoGenerator.GestureFaceEntry> entries, bool hasPresets)
        {
            var gestureMenu = AddSubMenu(parent, GestureFaceMenuName);

            // 並び順（基本 → プリセット 1 → …）のまま、セットごとにまとめる
            var setIndexes = new List<int>();
            foreach (var entry in entries)
            {
                if (!setIndexes.Contains(entry.SetIndex)) setIndexes.Add(entry.SetIndex);
            }

            foreach (var setIndex in setIndexes)
            {
                var setParent = gestureMenu;
                if (hasPresets)
                {
                    var setName = string.Empty;
                    foreach (var entry in entries)
                    {
                        if (entry.SetIndex != setIndex) continue;
                        setName = entry.SetName;
                        break;
                    }
                    setParent = AddSubMenu(gestureMenu, setName);
                }

                Transform leftParent = null;
                Transform rightParent = null;
                foreach (var entry in entries)
                {
                    if (entry.SetIndex != setIndex) continue;

                    var toggleParent = setParent;
                    if (entry.Side == yulEmoGenerator.GestureFaceSide.Left)
                    {
                        if (leftParent == null) leftParent = AddSubMenu(setParent, "Left");
                        toggleParent = leftParent;
                    }
                    else if (entry.Side == yulEmoGenerator.GestureFaceSide.Right)
                    {
                        if (rightParent == null) rightParent = AddSubMenu(setParent, "Right");
                        toggleParent = rightParent;
                    }

                    AddToggle(toggleParent, entry.GestureName, FaceFixParameter, entry.FixValue);
                }
            }
        }

        /// <summary>
        /// 子オブジェクトを MenuSource = Children のサブメニュー（MA Menu Item）として作成し、その Transform を返す。
        /// 子オブジェクトの順番がそのままメニューの順番になる。
        /// </summary>
        private static Transform AddSubMenu(Transform parent, string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create 21Emo SubMenu");
            go.transform.SetParent(parent, false);

            var item = Undo.AddComponent<ModularAvatarMenuItem>(go);
            item.Control = new VRCExpressionsMenu.Control
            {
                name = name,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty },
            };
            item.MenuSource = SubmenuSource.Children;
            item.automaticValue = false;
            return go.transform;
        }

        /// <summary>
        /// Toggle 型の MA Menu Item を持つ子オブジェクトを作成する。
        /// オブジェクト名がそのままメニュー上の表示名になる。
        /// </summary>
        private static void AddToggle(Transform parent, string name, string parameterName, float value, Texture2D icon = null)
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
                icon = icon,
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