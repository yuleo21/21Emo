#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace yulEmo
{
    /// <summary>
    /// メッシュのシェイプキーを、分類に振り分ける。
    ///
    /// 分類の決め方（上にあるものを優先）:
    ///   1. 名前が "vrc." で始まるもの → 「VRChat」
    ///   2. MMD の区切り（名前に MMD を含む区切り）の直後から続く、MMD らしい名前のもの → 「MMD」
    ///      （MMD らしい名前とは、MMD の標準の表情名、または半角の英数字を含まない名前）。
    ///      半角の英数字を含む名前が出たら MMD の並びは終わりとし、以降は「（MMD の後ろ）」にする。衣装や体のシェイプキーが、MMD として隠れないようにするため。
    ///      MMD の区切りが無くても、MMD の標準の表情名が 5 個以上あるメッシュでは、その名前のものを「MMD」にする。
    ///   3. それ以外は、直前の区切りの名前の分類。最初の区切りより前は「（区切りなし）」
    ///
    /// 区切りとは、"-------MMD-------" や "▼▼▼目" のように、名前の先頭から記号が 3 つ以上続くもの（または、3 文字以上の記号だけの名前）のこと。
    /// 末尾にだけ記号が続く名前（"mouth_^^^^" など）や、1〜2 文字の記号の名前（"▲" など）は、表情のシェイプキーなので区切りにしない。
    /// 区切りのシェイプキー自体は、表情ではないので、一覧には出さない。
    /// </summary>
    internal static class yulEmoShapeClassifier
    {
        public const string VrchatGroup = "VRChat";
        public const string MmdGroup = "MMD";
        public const string NoGroup = "（区切りなし）";
        public const string AfterMmdGroup = "（MMD の後ろ）";
        public const string UnnamedGroup = "（名前なしの区切り）";

        /// <summary>名前の先頭から記号がこの数以上続くものは、区切りとみなす。</summary>
        private const int MinDecorationRun = 3;

        /// <summary>MMD の標準の表情名が、この数以上あるメッシュでだけ、名前による判定を使う（日本語名の表情を誤って隠さないため）。</summary>
        private const int MmdNameThreshold = 5;

        /// <summary>MMD（PMX）の標準的な表情名。</summary>
        private static readonly string[] MmdStandardNames =
        {
            // 眉
            "真面目", "困る", "にこり", "怒り", "上", "下",
            // 目
            "まばたき", "笑い", "ウィンク", "ウィンク右", "ウィンク２", "ウィンク２右", "なごみ", "はぅ", "びっくり",
            "じと目", "ｷﾘｯ", "はちゅ目", "はちゅ目縦潰れ", "はちゅ目横潰れ", "星目", "はぁと", "瞳小", "瞳縦潰れ",
            "光下", "恐ろしい子", "ハイライト消し",
            // 口
            "あ", "い", "う", "え", "お", "あ２", "ワ", "ω", "ω□", "にやり", "にやり２", "にっこり", "ぺろっ",
            "てへぺろ", "てへぺろ２", "口角上げ", "口角下げ", "口横広げ", "歯無し上", "歯無し下", "ハンサム",
            // その他
            "▲", "∧", "□", "ん",
        };

        private static readonly HashSet<string> MmdNames = BuildMmdNames();

        internal sealed class Result
        {
            /// <summary>区切りのシェイプキーか。</summary>
            public bool[] IsSeparator;

            /// <summary>各シェイプキーの分類。区切り自身は、区切りの名前。</summary>
            public string[] Group;

            /// <summary>分類の名前（メッシュの中で初めて出てくる順）。区切りは含まない。</summary>
            public readonly List<string> GroupOrder = new List<string>();

            /// <summary>分類ごとのシェイプキーの数。</summary>
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

            public int SeparatorCount;
        }

        public static Result Classify(string[] names)
        {
            var count = names.Length;
            var result = new Result { IsSeparator = new bool[count], Group = new string[count] };
            var separatorLabels = new string[count];

            for (var i = 0; i < count; i++)
            {
                result.IsSeparator[i] = TryReadSeparator(names[i], out separatorLabels[i]);
                if (result.IsSeparator[i]) result.SeparatorCount++;
            }

            // MMD の標準の表情名が、まとまって存在するときだけ、名前による判定を使う
            var isMmdName = new bool[count];
            var mmdNameCount = 0;
            for (var i = 0; i < count; i++)
            {
                if (result.IsSeparator[i]) continue;
                if (!MmdNames.Contains(yulEmoShapeText.Fold(names[i]))) continue;
                isMmdName[i] = true;
                mmdNameCount++;
            }
            var useMmdNames = mmdNameCount >= MmdNameThreshold;

            var current = NoGroup;
            var inMmdSection = false;
            for (var i = 0; i < count; i++)
            {
                if (result.IsSeparator[i])
                {
                    current = separatorLabels[i];
                    inMmdSection = current.IndexOf("MMD", StringComparison.OrdinalIgnoreCase) >= 0;
                    result.Group[i] = current;
                    continue;
                }

                string group;
                if (names[i].StartsWith("vrc.", StringComparison.OrdinalIgnoreCase))
                {
                    group = VrchatGroup;
                }
                else if (inMmdSection)
                {
                    if (isMmdName[i] || !ContainsAsciiLetterOrDigit(names[i]))
                    {
                        group = MmdGroup;
                    }
                    else
                    {
                        // MMD の並びが終わった。次の区切りまでは、MMD として扱わない
                        inMmdSection = false;
                        current = AfterMmdGroup;
                        group = current;
                    }
                }
                else if (useMmdNames && isMmdName[i])
                {
                    group = MmdGroup;
                }
                else
                {
                    group = current;
                }

                result.Group[i] = group;
                if (!result.Counts.TryGetValue(group, out var existing))
                {
                    result.GroupOrder.Add(group);
                    existing = 0;
                }
                result.Counts[group] = existing + 1;
            }

            return result;
        }

        /// <summary>
        /// 区切りか調べる。区切りなら、前後の記号を取り除いた名前を label に入れる（空なら UnnamedGroup）。
        /// </summary>
        private static bool TryReadSeparator(string name, out string label)
        {
            label = null;
            var text = (name ?? string.Empty).Trim();
            if (text.Length == 0) return false;

            var leading = 0;
            while (leading < text.Length && IsDecoration(text[leading])) leading++;

            // 記号だけの名前は、3 文字以上のときだけ区切り（"▲" や "□" は、MMD の表情の名前）
            if (leading == text.Length)
            {
                if (text.Length < MinDecorationRun) return false;
                label = UnnamedGroup;
                return true;
            }

            // 先頭に記号が続かない名前は、区切りではない（"eye_><" や "mouth_^^^^" は、表情の名前）
            if (leading < MinDecorationRun) return false;

            var trailing = 0;
            while (trailing < text.Length && IsDecoration(text[text.Length - 1 - trailing])) trailing++;

            label = text.Substring(leading, text.Length - leading - trailing).Trim();
            if (label.Length == 0) label = UnnamedGroup;
            return true;
        }

        private static bool ContainsAsciiLetterOrDigit(string name)
        {
            foreach (var c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return true;
            }
            return false;
        }

        /// <summary>区切りの飾りに使われる文字か（空白、記号、伸ばし棒）。</summary>
        private static bool IsDecoration(char c)
        {
            return char.IsWhiteSpace(c) || !char.IsLetterOrDigit(c) || c == '\u30FC' || c == '\uFF70';
        }

        private static HashSet<string> BuildMmdNames()
        {
            var set = new HashSet<string>();
            foreach (var name in MmdStandardNames) set.Add(yulEmoShapeText.Fold(name));
            return set;
        }
    }
}
#endif
