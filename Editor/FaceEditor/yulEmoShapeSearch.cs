#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

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
    ///   -語 / !語  : その語を含むものを除く
    ///   @語        : 名前ではなく、分類の名前に含まれるものを探す
    ///
    /// 各語は正規表現として探す（| による選択、^ や $、. なども使える）。
    /// 空白を含む語は、\s や [ ] のように書く。
    /// 全角・半角、大文字・小文字、カタカナ・ひらがなの違いは区別しない（語の側も、名前と同じそろえ方をしてから正規表現にする）。
    /// 「目」と eye のような、日本語と英語の対応語も探す（対応表ファイル yulEmoShapeSynonyms.json）。対応語が付くのは、語がそのまま対応表にあるときだけ。
    /// 正規表現として読めない語は、文字どおりの語として探し、HasInvalidPattern を立てる。
    /// 結果は、名前の全体に一致 → 頭に一致 → 単語の頭に一致 → 途中に一致、の順に並べる。
    /// </summary>
    internal sealed class yulEmoShapeQuery
    {
        private sealed class Term
        {
            public bool Negative;
            public bool OnGroup;
            public readonly List<Regex> Patterns = new List<Regex>();
        }

        /// <summary>マッチングにかけられる上限の時間。これを超える正規表現は、一致しなかったものとして扱う。</summary>
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

        private static Dictionary<string, string[]> _synonyms;
        private static string[][] _synonymSource;

        /// <summary>対応表（外部ファイル）から作った、語から対応語の組への対応。ファイルが読み直されたら、作り直す。</summary>
        private static Dictionary<string, string[]> Synonyms
        {
            get
            {
                var groups = yulEmoShapeSynonyms.Groups;
                if (_synonyms == null || !ReferenceEquals(groups, _synonymSource))
                {
                    _synonymSource = groups;
                    _synonyms = BuildSynonyms(groups);
                }
                return _synonyms;
            }
        }

        private readonly List<Term> _terms = new List<Term>();

        // 照合がタイムアウトした正規表現は、以降のシェイプキーでは照合しない
        // 重い正規表現だと、シェイプキーの数だけタイムアウト待ちが積み上がるため
        private readonly HashSet<Regex> _timedOutPatterns = new HashSet<Regex>();

        public bool IsEmpty => _terms.Count == 0;

        /// <summary>正規表現として読めず、文字どおりに探した語があるか。</summary>
        public bool HasInvalidPattern { get; private set; }

        /// <summary>照合が重くて、途中で打ち切った正規表現があるか。</summary>
        public bool HasTimedOutPattern => _timedOutPatterns.Count > 0;

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

                if (body.Length == 0) continue;

                var folded = FoldPattern(body);
                term.Patterns.Add(query.Compile(folded, body));

                // 対応語は、語がそのまま対応表にあるときだけ足す（対応語の側は、文字どおりに探す）
                if (Synonyms.TryGetValue(NormalizeSmallKana(folded), out var synonyms))
                {
                    foreach (var synonym in synonyms)
                    {
                        if (synonym == folded) continue;
                        term.Patterns.Add(new Regex(Regex.Escape(synonym), RegexOptions.None, MatchTimeout));
                    }
                }

                query._terms.Add(term);
            }

            return query;
        }

        /// <summary>
        /// 検索式に一致するか。一致したら、並べ替えに使う点数（小さいほど良い）を score に入れる。
        /// </summary>
        public bool Matches(yulEmoShapeTarget target, out int score)
        {
            score = 0;
            foreach (var term in _terms)
            {
                var best = -1;
                foreach (var pattern in term.Patterns)
                {
                    var s = term.OnGroup
                        ? ScoreGroup(target, pattern)
                        : ScoreName(target, pattern);
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

        private int ScoreName(yulEmoShapeTarget target, Regex pattern)
        {
            if (_timedOutPatterns.Contains(pattern)) return -1;

            Match match;
            try
            {
                match = pattern.Match(target.Folded);
            }
            catch (RegexMatchTimeoutException)
            {
                _timedOutPatterns.Add(pattern);
                return -1;
            }

            if (!match.Success) return -1;
            if (match.Index == 0 && match.Length == target.Folded.Length) return 0;
            if (match.Index == 0) return 1;
            if (match.Index < target.Boundary.Length && target.Boundary[match.Index]) return 2;
            return 3;
        }

        private int ScoreGroup(yulEmoShapeTarget target, Regex pattern)
        {
            if (_timedOutPatterns.Contains(pattern)) return -1;

            try
            {
                return pattern.IsMatch(target.GroupFolded) ? 2 : -1;
            }
            catch (RegexMatchTimeoutException)
            {
                _timedOutPatterns.Add(pattern);
                return -1;
            }
        }

        /// <summary>正規表現を作る。読めない語は、文字どおりに探す正規表現にして、印を立てる。</summary>
        private Regex Compile(string pattern, string originalBody)
        {
            try
            {
                return new Regex(pattern, RegexOptions.None, MatchTimeout);
            }
            catch (ArgumentException)
            {
                HasInvalidPattern = true;
                return new Regex(
                    Regex.Escape(yulEmoShapeText.Fold(originalBody)), RegexOptions.None, MatchTimeout);
            }
        }

        /// <summary>
        /// 語を、名前と同じそろえ方（大文字・小文字、カタカナ・ひらがな）にしてから、正規表現として使える形にする。
        /// エスケープの中身（\d、\B など）と、(?i) や (?&lt;名前&gt; の先頭の記号は、そろえると意味が変わるので、そのまま写す。
        /// </summary>
        private static string FoldPattern(string body)
        {
            var result = new StringBuilder(body.Length);
            var i = 0;
            while (i < body.Length)
            {
                var c = body[i];

                if (c == '\\' && i + 1 < body.Length)
                {
                    result.Append(c);
                    result.Append(body[i + 1]);
                    i += 2;

                    // \p{Lu} のような、Unicode の分類の名前も、そのまま写す
                    if (body[i - 1] == 'p' && i < body.Length && body[i] == '{')
                    {
                        var end = body.IndexOf('}', i);
                        if (end < 0) end = body.Length - 1;
                        result.Append(body, i, end - i + 1);
                        i = end + 1;
                    }
                    continue;
                }

                if (c == '(' && i + 2 < body.Length && body[i + 1] == '?')
                {
                    result.Append(c);
                    result.Append(body[i + 1]);
                    result.Append(body[i + 2]);
                    i += 3;
                    continue;
                }

                result.Append(yulEmoShapeText.FoldChar(c));
                i++;
            }
            return result.ToString();
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
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

        /// <summary>小さいかな（ぁ、ぃ、っ、ゎ など）を、大きいかなにする。ういんくと、うぃんくのような書き方の違いを吸収するため。</summary>
        private static string NormalizeSmallKana(string text)
        {
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                switch (chars[i])
                {
                    case 'ぁ': chars[i] = 'あ'; break;
                    case 'ぃ': chars[i] = 'い'; break;
                    case 'ぅ': chars[i] = 'う'; break;
                    case 'ぇ': chars[i] = 'え'; break;
                    case 'ぉ': chars[i] = 'お'; break;
                    case 'っ': chars[i] = 'つ'; break;
                    case 'ゃ': chars[i] = 'や'; break;
                    case 'ゅ': chars[i] = 'ゆ'; break;
                    case 'ょ': chars[i] = 'よ'; break;
                    case 'ゎ': chars[i] = 'わ'; break;
                    case 'ゕ': chars[i] = 'か'; break;
                    case 'ゖ': chars[i] = 'け'; break;
                }
            }
            return new string(chars);
        }

        private static Dictionary<string, string[]> BuildSynonyms(string[][] groups)
        {
            var dictionary = new Dictionary<string, string[]>();
            foreach (var group in groups)
            {
                // 対応語の組には、そろえた形と、小さいかなを大きくした形の両方を入れる（名前の書き方がどちらでも当てるため）
                var values = new List<string>();
                foreach (var entry in group)
                {
                    var folded = yulEmoShapeText.Fold(entry);
                    if (!values.Contains(folded)) values.Add(folded);
                    var loose = NormalizeSmallKana(folded);
                    if (!values.Contains(loose)) values.Add(loose);
                }

                var array = values.ToArray();
                foreach (var value in values) dictionary[NormalizeSmallKana(value)] = array;
            }
            return dictionary;
        }
    }
}
#endif
