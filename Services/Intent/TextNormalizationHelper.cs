using ICU4N.Globalization;
using ICU4N.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HrChatThaiLLM.Server.Services;

/// <summary>
/// Shared normalization logic for all ML intent classifiers.
/// Includes ICU word tokenization for Thai and removal of punctuation / polite particles.
/// </summary>
public static partial class TextNormalizationHelper
{
    // ───── Regex patterns (compiled, shared) ─────
    [GeneratedRegex("[๐-๙]", RegexOptions.Compiled)]
    private static partial Regex ThaiDigitRegex();

    [GeneratedRegex("[\\u200B-\\u200D\\uFEFF]", RegexOptions.Compiled)]
    private static partial Regex ZeroWidthRegex();

    [GeneratedRegex(@"[\""'`\u201C\u201D\u2018\u2019.,!?;:()\[\]{}<>/\\|@#$%^&*_+=~]", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    // Polite particles – single tokens (ICU already split words)
    private static readonly HashSet<string> PoliteParticles = new(StringComparer.Ordinal)
    {
        "ครับ", "ค่ะ", "คะ", "จ้า", "จ๊ะ", "ฮะ", "นะ", "หน่อย", "ที", "ให้หน่อย"   // “ให้หน่อย” is added as a single token for safety, but it will be split by ICU. We handle multi-token later.
    };

    // เพิ่มใน TextNormalizationHelper.cs ก่อนเข้ากระบวนการ ICU Tokenize
    private static readonly Dictionary<string, string> SynonymMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ล.ป.", "ลาป่วย" },
        { "ล.ก.", "ลากิจ" },
        { "ล.พ.", "ลาพักร้อน" },
        { "ตอกบัตร", "สแกนนิ้ว" },
        { "wfh", "ทำงานที่บ้าน" },
        { "opd", "ผู้ป่วยนอก" },
        { "ipd", "ผู้ป่วยใน" },
        { "ปชช", "ประชาชน" },
        { "ผจก", "ผู้จัดการ" }
    };

    // Multi‑token polite particles that ICU will split
    private static readonly string[] PoliteMultiToken = { "ให้", "หน่อย" }; // sequence “ให้” + “หน่อย”

    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Full normalization pipeline:
    /// Unicode normalise → lower‑case → Thai digits → remove zero‑width → ICU tokenise → remove punct & polite → collapse spaces.
    /// </summary>
    public static string NormalizeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Unicode normalization + lower-case
        var text = input.Normalize().Trim().ToLowerInvariant();

        // 2. Convert Thai digits → Arabic
        text = ThaiDigitRegex().Replace(text, match => ((char)('0' + match.Value[0] - '๐')).ToString());

        // 3. Remove zero-width characters
        text = ZeroWidthRegex().Replace(text, "");

        // 4. ICU word tokenization
        var tokens = TokenizeToList(text);

        // 5. *** แทนที่คำย่อที่นี่ ***
        tokens = ReplaceSynonyms(tokens);

        // 6. Remove punctuation & polite particles
        tokens = CleanTokens(tokens);

        // 7. Return normalized string
        return string.Join(" ", tokens).Trim();
    }


    private static List<string> ReplaceSynonyms(List<string> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (SynonymMap.TryGetValue(tokens[i], out var replacement))
            {
                tokens[i] = replacement;
            }
        }
        return tokens;
    }

    /// <summary>
    /// Uses ICU BreakIterator (th_TH) to split text into word tokens.
    /// </summary>
    public static string TokenizeWithIcu(string input)
    {
        var tokens = TokenizeToList(input);
        return string.Join(" ", tokens);
    }

    private static List<string> TokenizeToList(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new List<string>();

        var bi = BreakIterator.GetWordInstance(new UCultureInfo("th_TH"));
        bi.SetText(input);

        var tokens = new List<string>();
        int start = bi.First();
        for (int end = bi.Next(); end != BreakIterator.Done; start = end, end = bi.Next())
        {
            var token = input[start..end];
            if (!string.IsNullOrWhiteSpace(token))
                tokens.Add(token);
        }
        return tokens;
    }

    private static List<string> CleanTokens(List<string> tokens)
    {
        var cleaned = new List<string>(tokens.Count);
        int i = 0;
        while (i < tokens.Count)
        {
            // Check multi‑token polite particle “ให้หน่อย”
            if (i + 1 < tokens.Count &&
                tokens[i] == "ให้" && tokens[i + 1] == "หน่อย")
            {
                i += 2; // skip both
                continue;
            }

            var tok = tokens[i];
            // Skip if it’s a single polite particle or punctuation
            if (!PoliteParticles.Contains(tok) && !PunctuationRegex().IsMatch(tok))
            {
                cleaned.Add(tok);
            }
            i++;
        }
        return cleaned;
    }
}