using HrChatThaiLLM.Server.Models;
using HrChatThaiLLM.Server.Services;
using HrChatThaiLLM.Server.Services.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.SemanticKernel;
using System.Diagnostics;
using System.Globalization;
public class AiChatService : IAiChatService
{
    private readonly Kernel _kernel;
    private readonly IChatHistoryService _chatHistory;
    private readonly ISqlExecutorService _sql;
    private readonly IResponseComposer _composer;
    private readonly IGenderDetectorService _genderDetector;
    private readonly IOutOfScopeResponseService _outOfScopeResponse;
    private readonly IThankYouResponses _thankYouResponses;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AiChatService> _logger;
    private readonly IChatSummaryService _summaryService;
    private bool _initialized = false;

    public AiChatService(
        ISqlExecutorService sqlExecutor,
        IChatHistoryService chatHistory,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        ILogger<AiChatService> logger,
        IWebHostEnvironment env,
        IResponseComposer composer,
        IGenderDetectorService genderDetector,
        IOutOfScopeResponseService outOfScopeResponse,
        IThankYouResponses thankYouResponses,
        IChatSummaryService summaryService,
        IHttpContextAccessor httpContextAccessor)
    {
        // IWebHostEnvironment env
        _sql = sqlExecutor;
        _chatHistory = chatHistory;
        _composer = composer;
        _genderDetector = genderDetector;
        _outOfScopeResponse = outOfScopeResponse;
        _thankYouResponses = thankYouResponses;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _kernel = Kernel.CreateBuilder().Build();
        _summaryService = summaryService;

        _kernel.Plugins.AddFromObject(new LeavePlugin(_sql), "HrLeave");
        _kernel.Plugins.AddFromObject(
            new AttendancePlugin(config, loggerFactory.CreateLogger<AttendancePlugin>()), "HrAttendance");
        _kernel.Plugins.AddFromObject(
            new MedicalPlugin(_sql, config, loggerFactory.CreateLogger<MedicalPlugin>()), "HrMedical");
        _kernel.Plugins.AddFromObject(new EmployeePlugin(_sql), "HrEmployee");

        _kernel.Plugins.AddFromObject(new CsvIntentPlugin(env), "CsvIntent");

        _kernel.Plugins.AddFromObject(new MedicalRegulationPlugin(env), "HrMedicalRegulation");
       

       
    }

    // ── Initialize ───────────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _logger.LogInformation("AI Service initialized โ€” all HR plugins ready");
        _initialized = true;
      //  await _composer.RefreshCacheAsync();
        await Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ProcessMessageAsync  —  ResponseComposer entry point หลัก  แนวคิดการเปลี่ยนมาใช้ Deterministic NLG (Natural Language Generation)
    // ════════════════════════════════════════════════════════════════════════
    // ── Entry Point หลักสำหรับ REST Message ──
    public async Task<string> ProcessMessageAsync(string userId, string userMessage)
    {
        await InitializeAsync();
        var stopwatch = Stopwatch.StartNew();
        UpdateGenderPreferenceInSession(userMessage);

        var ctx = await GetUserContextAsync(userId);
        var effectiveGender = ResolveEffectiveGender(ctx.Gender);

        // 1. ตรวจสอบคำหยาบ
        var profanityWarning = _composer.CheckProfanity(userMessage, effectiveGender, ctx.UserName);
        if (profanityWarning != null)
        {
            stopwatch.Stop();
            await _chatHistory.SaveAuditLogAsync(userId, userMessage, "ProfanityBypass", (int)stopwatch.ElapsedMilliseconds);
            return profanityWarning;
        }

        // 2. ตรวจสอบคำขอบคุณ
        if (IsThankYouMessage(userMessage))
        {
            var pref = ResolveCurrentPreference();
            var response = _thankYouResponses.BuildResponse(pref);
            stopwatch.Stop();
            await _chatHistory.SaveAuditLogAsync(userId, userMessage, "ThankYouIntent", (int)stopwatch.ElapsedMilliseconds);
            return response;
        }

        // 3. Normalize และ หา Plugin Key
        userMessage = DateNormalizer.NormalizeThaiDates(userMessage);
        string matchedPluginKey = DeterminePluginKey(userMessage);

        // 🔴 กรณีเป็น Summary ให้ดึงข้อมูลมาแสดงทันที (ไม่ต้องวิ่ง Plugin)
        if (matchedPluginKey == "Summary")
        {
            var summaryItems = _summaryService.GetSummaryItems(userId);
            stopwatch.Stop();
            await _chatHistory.SaveAuditLogAsync(userId, userMessage, "Summary", (int)stopwatch.ElapsedMilliseconds);
            return _composer.ComposeSummaryResponse(summaryItems, effectiveGender, ctx.UserName);
        }

        // 4. วิ่ง Plugin เพื่อเอาข้อมูล
        var bodyData = await RouteToPluginAsync(userId, userMessage);

        // 🔴 จุดสำคัญ: บันทึกข้อมูลลง Summary ทันทีที่ได้ข้อมูล (ถ้าไม่ใช่ OutOfScope)
        if (matchedPluginKey != "OutOfScope" && !string.IsNullOrEmpty(bodyData))
        {
            SaveSummaryItem(userId, matchedPluginKey, userMessage, bodyData);
        }

        // จัดการกรณีทักทาย
        if (matchedPluginKey == "OutOfScope" && HasGreeting(userMessage))
        {
            var pref = ResolveCurrentPreference();
            var greet = pref is GenderPreference.Female or GenderPreference.LGBT ? "สวัสดีค่ะ 👋" : "สวัสดีครับ 👋";
            bodyData = $"{greet}\n\n{bodyData}";
        }

        var finalResponse = _composer.ComposeResponse(matchedPluginKey, bodyData, ctx.UserName, effectiveGender);

        stopwatch.Stop();
        await _chatHistory.SaveAuditLogAsync(userId, userMessage, matchedPluginKey, (int)stopwatch.ElapsedMilliseconds);

        return finalResponse;
    }

    private void UpdateGenderPreferenceInSession(string userMessage)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        if (session is null) return;

        if (_genderDetector.TryHandleOverrideCommand(userMessage, out var overridePref, out var resetRequested))
        {
            if (resetRequested)
            {
                session.Remove("GenderPreference");
                return;
            }

            if (overridePref.HasValue)
            {
                session.SetString("GenderPreference", overridePref.Value.ToString());
                return;
            }
        }

        var detected = _genderDetector.DetectFromMessage(userMessage);
        if (detected.HasValue)
        {
            session.SetString("GenderPreference", detected.Value.ToString());
        }
    }

    private string ResolveEffectiveGender(string defaultGender)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        var prefStr = session?.GetString("GenderPreference");
        if (string.IsNullOrWhiteSpace(prefStr)) return defaultGender;

        if (Enum.TryParse<GenderPreference>(prefStr, true, out var pref))
        {
            return pref switch
            {
                GenderPreference.Female => "Female",
                GenderPreference.Male => "Male",
                GenderPreference.LGBT => "Female",
                _ => defaultGender
            };
        }

        return defaultGender;
    }
    // ปรับปรุงฟังก์ชันแมปชื่อปลั๊กอินให้ตรงกับฟิลด์ PluginKey ในฐานข้อมูล
    private static string DeterminePluginKey(string msg)
    {
        if (IsSystemIdentityQuestion(msg)) return "AssistantIdentity";
        if (msg.ContainsAny(Kw.Summary)) return "Summary";
        if (msg.ContainsAny(Kw.IntentCsv)) return "CsvIntent";
        if (msg.ContainsAny(Kw.MedicalRegulation)) return "HrMedicalRegulation";
        if (msg.ContainsAny(Kw.Attendance)) return "HrAttendance";
        if (msg.ContainsAny(Kw.LeaveTypes) || msg.ContainsAny(Kw.LeaveHistory) || msg.ContainsAny(Kw.LeaveGeneral)) return "HrLeave";
        if (msg.ContainsAny(Kw.Medical)) return "HrMedical";
        if (msg.ContainsAny(Kw.Profile) || msg.ContainsAny(Kw.IdCard) || msg.ContainsAny(Kw.Colleagues)) return "HrEmployee";
        return "OutOfScope";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Keyword Router
    // ════════════════════════════════════════════════════════════════════════
    private async Task<string> RouteToPluginAsync(string userId, string msg)
    {
        if (IsSystemIdentityQuestion(msg))
            return _outOfScopeResponse.BuildAssistantIdentityResponse();

        if (msg.ContainsAny(Kw.Profile))
            return await Invoke("HrEmployee", "get_my_profile", userId);

        if (msg.ContainsAny(Kw.IntentCsv))
        {
            var result = await _kernel.InvokeAsync(
                "CsvIntent",
                "get_intent_training_data",
                new KernelArguments
                {
                    ["employeeId"] = userId,
                    ["question"] = msg
                });
            return result?.ToString() ?? "ไม่พบข้อมูลตามหัวข้อที่ถาม";
        }

        if (msg.ContainsAny(Kw.MedicalRegulation))
        {
            var result = await _kernel.InvokeAsync(
                "HrMedicalRegulation",
                "get_medical_regulation",
                new KernelArguments
                {
                    ["employeeId"] = userId,
                    ["question"] = msg
                });
            return result?.ToString() ?? "ไม่พบข้อมูลระเบียบค่ารักษาพยาบาล";
        }

        if (msg.ContainsAny(Kw.IdCard))
            return await Invoke("HrEmployee", "get_my_id_card", userId);

        if (msg.ContainsAny(Kw.Colleagues))
            return await Invoke("HrEmployee", "get_dept_colleagues", userId);

        if (msg.ContainsAny(Kw.LeaveTypes) && msg.ContainsAny(Kw.LeaveBalanceQty))
            return await Invoke("HrLeave", "get_leave_balance", userId);

        if (msg.ContainsAny(Kw.LeaveHistory))
            return await Invoke("HrLeave", "get_leave_requests", userId);

        if (msg.ContainsAny(Kw.LeaveGeneral))
        {
            var balance = await Invoke("HrLeave", "get_leave_balance", userId);
            var history = await Invoke("HrLeave", "get_leave_requests", userId);
            return balance + "\n\n" + history;
        }

        if (msg.ContainsAny(Kw.Attendance))
            return await GetAttendanceDirectResponseAsync(userId, msg);

        if (msg.ContainsAny(Kw.Medical))
            return await GetMedicalDirectResponseAsync(userId, msg);

        if (msg.ContainsAny(Kw.Departments))
            return await Invoke("HrEmployee", "get_departments", userId);

        if (msg.ContainsAny(Kw.Divisions))
            return await Invoke("HrEmployee", "get_divisions", userId);

        if (msg.ContainsAny(Kw.Levels))
            return await Invoke("HrEmployee", "get_levels", userId);

        if (msg.ContainsAny(Kw.Positions))
            return await Invoke("HrEmployee", "get_positions", userId);

        if (msg.ContainsAny(Kw.Companies))
            return await Invoke("HrEmployee", "get_company_info", userId);

        _logger.LogDebug("No keyword matched: '{Msg}' ” fallback to profile", msg);
        return _outOfScopeResponse.BuildOutOfScopeResponse();
    }

    private static bool IsSystemIdentityQuestion(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        return msg.ContainsAny(
             "คุณคือใคร", "เธอคือใคร", "บอทคือใคร", "แชทบอทคือใคร",
            "ทำอะไรได้บ้าง", "ช่วยอะไรได้บ้าง", "ความสามารถของคุณ", "แนะนำตัว",
            "who are you", "what can you do"
        );
    }

  
    private static bool IsThankYouMessage(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        return msg.ContainsAny(
              "ขอบคุณ", "ขอบใจ", "ขอบคุณมาก", "ขอบใจมาก", "ดีมาก", "เยี่ยม", "สุดยอด",
            "thank you", "thanks", "thx", "appreciate", "เก่งมาก", "เก่งจริง"
        );
    }

    private GenderPreference ResolveCurrentPreference()
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        var prefStr = session?.GetString("GenderPreference");
        if (Enum.TryParse<GenderPreference>(prefStr, true, out var pref))
        {
            return pref;
        }
        return GenderPreference.Neutral;
    }

    private static bool HasGreeting(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        return msg.ContainsAny("สวัสดี", "สวัสดีครับ", "สวัสดีค่ะ", "หวัดดี","ดีครับ","ดีค่ะ",
            "ดีกร๊าบ!",
            "ดีจ้า", "hello", "hi", "hey");
    }

    // ── Helper: ลด boilerplate InvokeAsync ──────────────────────────────────
    private async Task<string> Invoke(string plugin, string function, string userId)
    {
        var result = await _kernel.InvokeAsync(
            plugin, function,
            new KernelArguments { ["employeeId"] = userId });
        return result?.ToString() ?? "";
    }

    private async Task<string> GetAttendanceDirectResponseAsync(string userId, string userMessage)
    {
        var result = await _kernel.InvokeAsync(
            "HrAttendance",
            "get_recent_attendance",
            new KernelArguments
            {
                ["employeeId"] = userId,
                // ส่ง userMessage ที่ normalize แล้วให้ Plugin
                ["question"] = userMessage
            });
        return result?.ToString() ?? "ไม่พบข้อมูลเวลาเข้าออกงาน";
    }

    private async Task<string> GetMedicalDirectResponseAsync(string userId, string userMessage)
    {
        var result = await _kernel.InvokeAsync(
            "HrMedical",
            "get_claim_status",
            new KernelArguments
            {
                ["employeeId"] = userId,
                ["question"] = userMessage
            });
        return result?.ToString() ?? "ไม่พบข้อมูลค่ารักษาพยาบาล";
    }

    public async Task<string> ExecuteClaimStatusAsync(string userId, string question = "สถานะเคลมค่ารักษา")
    {
        var result = await _kernel.InvokeAsync(
            "HrMedical",
            "get_claim_status",
            new KernelArguments
            {
                ["employeeId"] = userId,
                ["question"] = question
            });
        return result?.ToString() ?? "ไม่พบข้อมูลค่ารักษาพยาบาล";
    }

    public string BuildAssistantIdentityFallback()
    {
        return _outOfScopeResponse.BuildAssistantIdentityResponse();
    }


    // ── ระบบจำลองโครงสร้างสตรีมมิ่งเพื่อส่งผ่านสัญญาณของ SignalR ไปหน้าจอ ──
    public async IAsyncEnumerable<string> ProcessMessageStreamingAsync(string userId, string userMessage)
    {
        await InitializeAsync();

        // ดึงข้อความสมบูรณ์ที่สร้างเสร็จจากระบบอย่างรวดเร็ว
        var fullTextResponse = await ProcessMessageAsync(userId, userMessage);

        // เพื่อให้เอฟเฟกต์หน้าจอของหน้าเว็บบอร์ดไม่กระตุกและคงความลื่นไหลสไตล์พิมพ์ดีด (Typewriter Effect)
        // เราจะหั่นข้อความเป็นก้อนคำย่อยๆ (Chunk) แล้วทะยอยยิงออกไปพร้อม Delay เล็กน้อย
        var wordsOrChunks = ChunkText(fullTextResponse, chunkSize: 6);
        foreach (var chunk in wordsOrChunks)
        {
            yield return chunk;
            await Task.Delay(8); // หน่วงเวลา 8 มิลลิวินาทีเพิ่มความสมจริงสไตล์เรียลไทม์
        }
    }

    // ฟังก์ชันย่อยช่วยในการตัดแบ่งคำส่งผลออกจอ
    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        // เปลี่ยนมาใช้ StringInfo เพื่ออ่านข้อความตามอักขระจริงที่มนุษย์มองเห็น (Text Elements)
        var stringInfo = new StringInfo(text);
        int totalElements = stringInfo.LengthInTextElements;

        for (int i = 0; i < totalElements; i += chunkSize)
        {
            // ตัดข้อความตามหน่วยตัวอักษรจริง ป้องกันการตัดโดนกึ่งกลางรหัสอีโมจิอย่างเด็ดขาด
            yield return stringInfo.SubstringByTextElements(i, Math.Min(chunkSize, totalElements - i));
        }
    }

    // ── FetchDataAsync  —  REST API endpoint ────────────────────────────────
    public async Task<object?> FetchDataAsync(
        string userId, string dataType, Dictionary<string, string> parameters)
    {
        await InitializeAsync();
        var empId = parameters.GetValueOrDefault("employeeId", userId);

        return dataType.ToLowerInvariant() switch
        {
            "leave" => await _kernel.InvokeAsync("HrLeave", "get_leave_balance",
                                new KernelArguments { ["employeeId"] = empId }),
            "leave_req" => await _kernel.InvokeAsync("HrLeave", "get_leave_requests",
                                new KernelArguments { ["employeeId"] = empId }),
            "attendance" => await _kernel.InvokeAsync("HrAttendance", "get_recent_attendance",
                                new KernelArguments { ["employeeId"] = empId }),
            "medical" => await _kernel.InvokeAsync("HrMedical", "get_claim_status",
                                new KernelArguments { ["employeeId"] = empId }),
            "profile" => await _kernel.InvokeAsync("HrEmployee", "get_my_profile",
                                new KernelArguments { ["employeeId"] = empId }),
            "colleagues" => await _kernel.InvokeAsync("HrEmployee", "get_dept_colleagues",
                                new KernelArguments { ["employeeId"] = empId }),
            "depts" => await _kernel.InvokeAsync("HrEmployee", "get_departments",
                                new KernelArguments { ["employeeId"] = empId }),
            "divisions" => await _kernel.InvokeAsync("HrEmployee", "get_divisions",
                                new KernelArguments { ["employeeId"] = empId }),
            "levels" => await _kernel.InvokeAsync("HrEmployee", "get_levels",
                                new KernelArguments { ["employeeId"] = empId }),
            "positions" => await _kernel.InvokeAsync("HrEmployee", "get_positions",
                                new KernelArguments { ["employeeId"] = empId }),
            "companies" => await _kernel.InvokeAsync("HrEmployee", "get_company_info",
                                new KernelArguments { ["employeeId"] = empId }),
            _ => null
        };
    }

    // ── GetUserContext ────────────────────────────────────────────────────────
    private async Task<(string UserName, string DeptId, string CompanyId, string Gender)> GetUserContextAsync(
        string userId)
    {
        try
        {
            const string sql = "SELECT EMFNT, EMLNT, DPID, CMID,EMSX FROM EMPDA WHERE EMID = @UserId";
            var r = await _sql.QueryFirstOrDefaultAsync<dynamic>(sql, new { UserId = userId });
            if (r == null) return ("พนักงาน", "", "", "M");
            return (
                $"{r.EMFNT} {r.EMLNT}".Trim(),
                r.DPID?.ToString() ?? "",
                r.CMID?.ToString() ?? "",
                r.EMSX?.ToString() ?? "M"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetUserContext error for {UserId}", userId);
            return ("พนักงาน", "", "", "M");
        }
    }

    /// <summary>
    /// ฟังก์ชันช่วยวิเคราะห์ว่าคำถามนี้จะถูกวิ่งไปที่ปลั๊กอินตัวไหน สำหรับบันทึกใน Log
    /// </summary>
    private static string DeterminePluginName(string msg)
    {
        if (msg.ContainsAny(Kw.IntentCsv)) return "CsvIntent";
        if (msg.ContainsAny(Kw.MedicalRegulation)) return "HrMedicalRegulation";
        if (msg.ContainsAny(Kw.Profile)) return "HrEmployee (Profile)";
        if (msg.ContainsAny(Kw.IdCard)) return "HrEmployee (IdCard)";
        if (msg.ContainsAny(Kw.Colleagues)) return "HrEmployee (Colleagues)";
        if (msg.ContainsAny(Kw.LeaveTypes) && msg.ContainsAny(Kw.LeaveBalanceQty)) return "HrLeave (Balance)";
        if (msg.ContainsAny(Kw.LeaveHistory)) return "HrLeave (History)";
        if (msg.ContainsAny(Kw.LeaveGeneral)) return "HrLeave (General)";
        if (msg.ContainsAny(Kw.Attendance)) return "HrAttendance";
        if (msg.ContainsAny(Kw.Medical)) return "HrMedical (Personal)";
        if (msg.ContainsAny(Kw.Departments)) return "HrEmployee (Master-Depts)";
        if (msg.ContainsAny(Kw.Divisions)) return "HrEmployee (Master-Divisions)";
        if (msg.ContainsAny(Kw.Levels)) return "HrEmployee (Master-Levels)";
        if (msg.ContainsAny(Kw.Positions)) return "HrEmployee (Master-Positions)";
        if (msg.ContainsAny(Kw.Companies)) return "HrEmployee (Master-Companies)";
        return "OutOfScope";
    }

    // ในเมธอดที่ประมวลผลคำตอบ (หลังจากได้ข้อมูลจาก Plugin แล้ว)
    private void SaveSummaryItem(string sessionId, string pluginKey, string question, string rawData)
    {
        var summaryItem = new ChatSummaryItem
        {
            Intent = pluginKey, // ใช้ PluginKey เป็น Intent หลัก
            Topic = ExtractTopicFromPluginKey(pluginKey),
            KeyInfo = ExtractKeyInfo(rawData),
            Timestamp = DateTime.Now
        };

        _summaryService.AddOrUpdateSummaryItem(sessionId, summaryItem);
        _logger.LogDebug("Saved summary for intent: {Intent}, topic: {Topic}", pluginKey, summaryItem.Topic);
    }
    // ฟังก์ชันช่วยย่อย (ใส่ไว้ในคลาสเดิมของคุณได้เลย)
    private string ExtractKeyInfo(string rawData)
    {
        if (string.IsNullOrEmpty(rawData)) return "";
        return rawData; // หรือใส่ Logic ตัดข้อความให้สั้นลงที่นี่
    }
    private string ExtractTopicFromPluginKey(string pluginKey)
    {
        return pluginKey switch
        {
            "HrLeave" => "ยอดวันลาและประวัติการลา",
            "HrMedical" => "สิทธิการเบิกค่ารักษาพยาบาล",
            "HrAttendance" => "เวลาเข้า-ออกงาน",
            "HrEmployee" => "ข้อมูลส่วนตัวพนักงาน",
            "HrMedicalRegulation" => "ระเบียบค่ารักษาพยาบาล",
            "CsvIntent" => "ข้อมูลทั่วไปบริษัท",
            _ => "ข้อมูลที่สอบถาม"
        };
    }

    private string ExtractTopicFromIntent(string intent, string question)
    {
        return intent switch
        {
            "LeaveBalance" => "ยอดวันลาคงเหลือ",
            "MedicalClaim" => "สิทธิการเบิกค่ารักษาพยาบาล",
            "Attendance" => "เวลาเข้า-ออกงาน",
            "MyProfile" => "ข้อมูลส่วนตัว",
            _ => "ข้อมูลที่สอบถาม"
        };
    }
}

