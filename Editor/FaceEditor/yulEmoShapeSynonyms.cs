#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// シェイプキー検索の対応表（同じ意味の語の組）を、外部ファイルから読む。
    ///
    /// ファイル名は yulEmoShapeSynonyms.json で、プロジェクト内に複数あってもよい（パッケージ付属のものと、利用者が Assets に置いたもの）。
    /// 見つかったファイルをすべて読み、語が重なる組は1つにまとめる。
    /// 書式は JSON で、groups の各要素が1組、その words に語を並べる。
    /// ファイルが変わったら、次に検索したときに読み直す。
    /// </summary>
    internal static class yulEmoShapeSynonyms
    {
        private const string FileName = "yulEmoShapeSynonyms";

        private static string[][] _groups = new string[0][];
        private static string _stamp;
        private static List<string> _paths;
        // 対応表ファイルの一覧を探し直す必要があるか。ファイルの増減は AssetPostprocessor で検知する
        private static bool _filesDirty = true;

        /// <summary>対応表ファイルが追加・変更・削除されたときに、次回の一覧の探し直しを予約する。</summary>
        internal static void MarkFilesDirty()
        {
            _filesDirty = true;
        }

        /// <summary>JSON の形。groups の各要素が、同じ意味の語の組。</summary>
        [Serializable]
        private class SynonymFile
        {
            public SynonymGroup[] groups;
        }

        [Serializable]
        private class SynonymGroup
        {
            public string[] words;
        }

        /// <summary>対応表の組（生の語の並び）。ファイルの状態が変わっていたら、読み直してから返す。</summary>
        public static string[][] Groups
        {
            get
            {
                ReloadIfChanged();
                return _groups;
            }
        }

        private static void ReloadIfChanged()
        {
            // ファイルの一覧（FindAssets）は、対応表ファイルの増減があったときだけ探し直す
            // 検索のたびにプロジェクト全体を探すと、キー入力のたびに重くなるため
            // 中身の変更は、更新日時の確認（ファイル数が少ないので軽い）でこれまでどおり検知する
            if (_filesDirty || _paths == null)
            {
                _paths = FindFiles();
                _filesDirty = false;
                _stamp = null;
            }
            var paths = _paths;

            var stamp = BuildStamp(paths);
            if (stamp == _stamp) return;
            _stamp = stamp;

            var groups = new List<List<string>>();
            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset != null) ParseJson(asset.text, path, groups);
            }

            if (paths.Count == 0)
            {
                Debug.LogWarning("[21Emo] 対応表ファイル " + FileName + ".json が見つかりません。対応語なしで検索します。");
            }

            _groups = new string[groups.Count][];
            for (var i = 0; i < groups.Count; i++) _groups[i] = groups[i].ToArray();
        }

        private static List<string> FindFiles()
        {
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(FileName))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
            }
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        /// <summary>ファイルの一覧と更新日時の並び。読み直しが必要かの判定に使う。</summary>
        private static string BuildStamp(List<string> paths)
        {
            var stamp = string.Empty;
            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                var ticks = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks.ToString() : "?";
                stamp += path + "@" + ticks + ";";
            }
            return stamp;
        }

        private static void ParseJson(string text, string path, List<List<string>> groups)
        {
            SynonymFile file;
            try
            {
                file = JsonUtility.FromJson<SynonymFile>(text);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[21Emo] 対応表ファイル " + path + " を読めませんでした：" + exception.Message);
                return;
            }

            if (file == null || file.groups == null) return;

            foreach (var group in file.groups)
            {
                if (group == null || group.words == null) continue;

                var words = new List<string>();
                foreach (var word in group.words)
                {
                    if (!string.IsNullOrEmpty(word)) words.Add(word);
                }
                if (words.Count > 0) Merge(groups, words);
            }
        }

        /// <summary>語が重なる組とは、1つにまとめる（重なりの判定は、名前と同じそろえ方をした語で見る）。</summary>
        private static void Merge(List<List<string>> groups, List<string> words)
        {
            var folded = new HashSet<string>();
            foreach (var word in words) folded.Add(yulEmoShapeText.Fold(word));

            List<string> target = null;
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                var overlaps = false;
                foreach (var existing in groups[i])
                {
                    if (!folded.Contains(yulEmoShapeText.Fold(existing))) continue;
                    overlaps = true;
                    break;
                }
                if (!overlaps) continue;

                if (target == null)
                {
                    target = groups[i];
                }
                else
                {
                    foreach (var existing in groups[i])
                    {
                        if (!target.Contains(existing)) target.Add(existing);
                    }
                    groups.RemoveAt(i);
                }
            }

            if (target == null)
            {
                groups.Add(words);
                return;
            }

            foreach (var word in words)
            {
                if (!target.Contains(word)) target.Add(word);
            }
        }
    }

    /// <summary>対応表ファイル（yulEmoShapeSynonyms.json）の追加・変更・削除・移動を検知して、読み直しの対象にする。</summary>
    internal class yulEmoShapeSynonymsWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets) MarkIfSynonym(path);
            foreach (var path in deletedAssets) MarkIfSynonym(path);
            foreach (var path in movedAssets) MarkIfSynonym(path);
            foreach (var path in movedFromAssetPaths) MarkIfSynonym(path);
        }

        private static void MarkIfSynonym(string path)
        {
            if (path != null && path.EndsWith("yulEmoShapeSynonyms.json", StringComparison.OrdinalIgnoreCase))
            {
                yulEmoShapeSynonyms.MarkFilesDirty();
            }
        }
    }
}
#endif
