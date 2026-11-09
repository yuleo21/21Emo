#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;

namespace yulEmo
{
    /// <summary>シェイプキーの名前を、検索しやすい形にそろえる処理。</summary>
    internal static class yulEmoShapeText
    {
        /// <summary>全角英数・半角カナなどを、標準の形にそろえる。失敗したときは元の文字列を返す。</summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            try
            {
                return raw.Normalize(NormalizationForm.FormKC);
            }
            catch (Exception)
            {
                return raw;
            }
        }

        /// <summary>大文字を小文字に、カタカナをひらがなにする（1 文字ずつなので、長さは変わらない）。</summary>
        public static char FoldChar(char c)
        {
            if ((c >= '\u30A1' && c <= '\u30F6') || c == '\u30FD' || c == '\u30FE') return (char)(c - 0x60);
            return char.ToLowerInvariant(c);
        }

        /// <summary>正規化したうえで、大文字・小文字とカタカナ・ひらがなの違いをなくす。</summary>
        public static string Fold(string raw)
        {
            var normalized = Normalize(raw);
            var chars = new char[normalized.Length];
            for (var i = 0; i < chars.Length; i++) chars[i] = FoldChar(normalized[i]);
            return new string(chars);
        }
    }

    /// <summary>1 つのシェイプキーの、検索用の情報。</summary>
    internal sealed class yulEmoShapeTarget
    {
        /// <summary>正規化して、大文字・小文字とカタカナ・ひらがなの違いをなくした名前。</summary>
        public string Folded;

        /// <summary>Folded の各位置が、単語の頭か（区切り記号の後、小文字から大文字への変わり目、英字と数字の変わり目）。</summary>
        public bool[] Boundary;

        /// <summary>所属する分類の名前（Folded と同じ処理をしたもの）。</summary>
        public string GroupFolded;

        public static yulEmoShapeTarget Create(string name, string group)
        {
            var normalized = yulEmoShapeText.Normalize(name);
            var folded = new char[normalized.Length];
            var boundary = new bool[normalized.Length];
            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                folded[i] = yulEmoShapeText.FoldChar(c);

                if (i == 0)
                {
                    boundary[i] = true;
                    continue;
                }

                var previous = normalized[i - 1];
                boundary[i] =
                    !char.IsLetterOrDigit(previous) ||
                    (char.IsLower(previous) && char.IsUpper(c)) ||
                    (char.IsLetterOrDigit(c) && char.IsDigit(previous) != char.IsDigit(c));
            }

            return new yulEmoShapeTarget
            {
                Folded = new string(folded),
                Boundary = boundary,
                GroupFolded = yulEmoShapeText.Fold(group),
            };
        }
    }

    /// <summary>
    /// シェイプキーの検索式。
    ///
    ///   スペース   : すべての語を含むものだけを残す
    ///   a|b        : どちらかを含む
    ///   -語 / !語  : その語を含むものを除く
    ///   @語        : 名前ではなく、分類の名前に含まれるものを探す
    ///   "a b"      : 空白を含む語
    ///
    /// 全角・半角、大文字・小文字、カタカナ・ひらがなの違いは区別しない。
    /// 「目」と eye のような、日本語と英語の対応語も探す（SynonymGroups）。
    /// 結果は、名前の全体に一致 → 頭に一致 → 単語の頭に一致 → 途中に一致、の順に並べる。
    /// あいまい検索（語の文字が順に出てくれば一致）は、通常の検索で 1 つも見つからないときだけ使う。
    /// </summary>
    internal sealed class yulEmoShapeQuery
    {
        private sealed class Term
        {
            public bool Negative;
            public bool OnGroup;
            public readonly List<string> Alternatives = new List<string>();
        }

        /// <summary>同じ意味の語の組。入力した語が組のどれかと一致したら、組のすべてを探す。</summary>
        private static readonly string[][] SynonymGroups =
        {
            new[] { "eye", "eyes", "目", "眼", "瞳", "ひとみ" },
            new[] { "brow", "eyebrow", "眉", "まゆ" },
            new[] { "mouth", "口", "くち", "lip", "lips", "唇", "くちびる" },
            new[] { "smile", "笑", "笑顔", "えがお", "にこ" },
            new[] { "angry", "anger", "怒", "おこ" },
            new[] { "sad", "sorrow", "悲", "泣", "かなし" },
            new[] { "surprise", "surprised", "びっくり", "驚" },
            new[] { "cheek", "頬", "ほほ", "ほお" },
            new[] { "tongue", "舌", "べろ", "ぺろ" },
            new[] { "blink", "まばたき", "瞬き", "瞬" },
            new[] { "wink", "ウィンク" },
            new[] { "left", "左", "ひだり" },
            new[] { "right", "右", "みぎ" },
            new[] { "up", "上", "うえ" },
            new[] { "down", "下" },
            new[] { "open", "開" },
            new[] { "close", "closed", "閉", "とじ" },
            new[] { "happy", "joy", "喜", "うれしい" },
            new[] { "shy", "blush", "照", "赤面", "てれ" },
        };

        private static readonly Dictionary<string, string[]> Synonyms = BuildSynonyms();

        private readonly List<Term> _terms = new List<Term>();

        public bool IsEmpty => _terms.Count == 0;

        public static yulEmoShapeQuery Parse(string raw)
        {
            var query = new yulEmoShapeQuery();
            var text = yulEmoShapeText.Normalize(raw ?? string.Empty);

            foreach (var token in Tokenize(text))
            {
                var body = token;
                var term = new Term();

                // 先頭の - ! @ を読む（順不同）
                while (body.Length > 1 && (body[0] == '-' || body[0] == '!' || body[0] == '@'))
                {
                    if (body[0] == '@') term.OnGroup = true;
                    else term.Negative = true;
                    body = body.Substring(1);
                }

                foreach (var alternative in body.Split('|'))
                {
                    var folded = yulEmoShapeText.Fold(alternative.Trim());
                    if (folded.Length > 0) AddWithSynonyms(term.Alternatives, folded);
                }

                if (term.Alternatives.Count > 0) query._terms.Add(term);
            }

            return query;
        }

        /// <summary>
        /// 検索式に一致するか。一致したら、並べ替えに使う点数（小さいほど良い）を score に入れる。
        /// fuzzy が true のときは、あいまい一致も認める（除外の語には使わない）。
        /// </summary>
        public bool Matches(yulEmoShapeTarget target, bool fuzzy, out int score)
        {
            score = 0;
            foreach (var term in _terms)
            {
                var best = -1;
                foreach (var alternative in term.Alternatives)
                {
                    var s = term.OnGroup
                        ? ScoreGroup(target, alternative)
                        : ScoreName(target, alternative, fuzzy && !term.Negative);
                    if (s >= 0 && (best < 0 || s < best)) best = s;
                }

                if (term.Negative)
                {
                    if (best >= 0) return false;
                }
                else
                {
                    if (best < 0) return false;
                    score += best;
                }
            }
            return true;
        }

        private static int ScoreName(yulEmoShapeTarget target, string text, bool fuzzy)
        {
            var index = target.Folded.IndexOf(text, StringComparison.Ordinal);
            if (index >= 0)
            {
                if (target.Folded.Length == text.Length) return 0;
                if (index == 0) return 1;
                return target.Boundary[index] ? 2 : 3;
            }

            if (fuzzy && text.Length >= 2 && IsSubsequence(text, target.Folded)) return 6;
            return -1;
        }

        private static int ScoreGroup(yulEmoShapeTarget target, string text)
        {
            return target.GroupFolded.IndexOf(text, StringComparison.Ordinal) >= 0 ? 2 : -1;
        }

        private static bool IsSubsequence(string needle, string haystack)
        {
            var position = 0;
            foreach (var c in haystack)
            {
                if (c == needle[position] && ++position == needle.Length) return true;
            }
            return false;
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inQuote = false;

            foreach (var c in text)
            {
                if (c == '"' || c == '\u201C' || c == '\u201D')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuote)
                {
                    if (current.Length > 0) tokens.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }

                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        private static void AddWithSynonyms(List<string> list, string folded)
        {
            if (!list.Contains(folded)) list.Add(folded);

            if (!Synonyms.TryGetValue(folded, out var set)) return;
            foreach (var synonym in set)
            {
                if (!list.Contains(synonym)) list.Add(synonym);
            }
        }

        private static Dictionary<string, string[]> BuildSynonyms()
        {
            var dictionary = new Dictionary<string, string[]>();
            foreach (var group in SynonymGroups)
            {
                var folded = new string[group.Length];
                for (var i = 0; i < group.Length; i++) folded[i] = yulEmoShapeText.Fold(group[i]);
                foreach (var term in folded) dictionary[term] = folded;
            }
            return dictionary;
        }
    }
}
#endif
