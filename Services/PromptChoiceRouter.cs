using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace HrChatThaiLLM.Server.Services;

public interface IPromptChoiceRouter
{
    bool TryHandle(string userId, string message, out string response);
}

public class PromptChoiceRouter : IPromptChoiceRouter
{
    private class SessionState
    {
        public ChoiceContext Context { get; set; }
        public string AdYear { get; set; } = string.Empty;
        public string DisplayYear { get; set; } = string.Empty;
    }

    private readonly ConcurrentDictionary<string, SessionState> _contexts = new();

    private static readonly Regex _graphYearRegex = new(@"^กราฟ(?:ปี)?(\d{2,4})$", RegexOptions.Compiled);
    private static readonly Regex _claimYearRegex = new(@"^เบิก(?:ปี)?(\d{2,4})$", RegexOptions.Compiled);

    public bool TryHandle(string userId, string message, out string response)
    {
        response = string.Empty;
        var uid = string.IsNullOrWhiteSpace(userId) ? "unknown" : userId.Trim();
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        var normalized = text.ToLowerInvariant();
        var normalizedNoSpace = normalized.Replace(" ", "");

        var graphMatch = _graphYearRegex.Match(normalizedNoSpace);
        if (normalized == "กราฟ" || graphMatch.Success)
        {
            var (adYear, dispYear) = graphMatch.Success ? ParseYear(graphMatch.Groups[1].Value) : (string.Empty, string.Empty);
            _contexts[uid] = new SessionState { Context = ChoiceContext.Graph, AdYear = adYear, DisplayYear = dispYear };
            
            if (string.IsNullOrEmpty(dispYear))
            {
                response = """
กรุณาเลือกเมนูกราฟด้วยตัวเลข
1. กราฟค่ารักษา ปีปัจจุบัน
2. กราฟเวลาเข้าออกงาน ปีปัจจุบัน
3. ยกเลิก (fallback)
""";
            }
            else
            {
                response = $"""
กรุณาเลือกเมนูกราฟด้วยตัวเลข
1. กราฟค่ารักษา ปี {dispYear}
2. กราฟเวลาเข้าออกงาน ปี {dispYear}
3. ยกเลิก (fallback)
""";
            }
            return true;
        }

        var claimMatch = _claimYearRegex.Match(normalizedNoSpace);
        if (normalized == "เบิก" || claimMatch.Success)
        {
            var (adYear, dispYear) = claimMatch.Success ? ParseYear(claimMatch.Groups[1].Value) : (string.Empty, string.Empty);
            _contexts[uid] = new SessionState { Context = ChoiceContext.Claim, AdYear = adYear, DisplayYear = dispYear };
            
            if (string.IsNullOrEmpty(dispYear))
            {
                response = """
คุณหมายถึง การเบิกค่ารักษาพยาบาล ปีปัจจุบัน ใช่หรือไม่
1. ใช่
2. ไม่ใช่
""";
            }
            else
            {
                response = $"""
คุณหมายถึง การเบิกค่ารักษาพยาบาล ปี {dispYear} ใช่หรือไม่
1. ใช่
2. ไม่ใช่
""";
            }
            return true;
        }

        if (_contexts.TryGetValue(uid, out var state))
        {
            if (state.Context == ChoiceContext.Graph && TryHandleGraphChoice(normalized, state.AdYear, state.DisplayYear, out response))
            {
                _contexts.TryRemove(uid, out _);
                return true;
            }

            if (state.Context == ChoiceContext.Claim && TryHandleClaimChoice(normalized, state.AdYear, state.DisplayYear, out response))
            {
                _contexts.TryRemove(uid, out _);
                return true;
            }

            // ถ้าผู้ใช้ตอบไม่เข้าเงื่อนไขในโหมดเลือกเมนู ให้ fallback ทันที
            _contexts.TryRemove(uid, out _);
            response = "[ACTION:FALLBACK]";
            return true;
        }

        return false;
    }

    private static bool TryHandleGraphChoice(string normalized, string adYear, string dispYear, out string response)
    {
        response = string.Empty;
        if (normalized is "1" or "1." or "๑")
        {
            response = string.IsNullOrEmpty(adYear) 
                ? "[ACTION:SHOW_MEDICAL_CHART_CURRENT]" 
                : $"[MEDICAL_CHART:{adYear}]";
            return true;
        }
        if (normalized is "2" or "2." or "๒")
        {
            response = string.IsNullOrEmpty(adYear) 
                ? "[ACTION:SHOW_ATTENDANCE_CHART_CURRENT]" 
                : $"[ATTENDANCE_CHART:{adYear}]";
            return true;
        }
        if (normalized is "3" or "3." or "๓")
        {
            response = "[ACTION:FALLBACK]";
            return true;
        }
        return false;
    }

    private static bool TryHandleClaimChoice(string normalized, string adYear, string dispYear, out string response)
    {
        response = string.Empty;
        if (normalized is "1" or "1." or "๑" or "ใช่" or "ใช้")
        {
            response = string.IsNullOrEmpty(adYear) 
                ? "[ACTION:RUN_CLAIM_STATUS]" 
                : $"[ACTION:RUN_CLAIM_STATUS {adYear}]";
            return true;
        }
        if (normalized is "2" or "2." or "๒" or "ไม่ใช่" or "ไม่ใช้")
        {
            response = "[ACTION:FALLBACK]";
            return true;
        }
        return false;
    }

    private static (string adYear, string displayYear) ParseYear(string yearStr)
    {
        if (int.TryParse(yearStr, out int y))
        {
            int adYear = DateNormalizer.NormalizeYear(y);
            return (adYear.ToString(), yearStr);
        }
        return (yearStr, yearStr);
    }

    private enum ChoiceContext
    {
        Graph,
        Claim
    }
}
