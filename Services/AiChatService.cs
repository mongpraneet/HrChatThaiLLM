using HrChatThaiLLM.Server.Models;
using HrChatThaiLLM.Server.Services;
using HrChatThaiLLM.Server.Services.Plugins;
using Microsoft.SemanticKernel;
using System.Diagnostics;
using System.Globalization;
using System.Text;


namespace HrChatThaiLLM.Server.Services
{
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
        private readonly IEmpathyResponseService _empathyResponse;
        private readonly IIntentPredictionService _intentAi;
        private readonly IEmployeeSubIntentPredictionService _employeeSubIntentAi;
        private readonly IHrSubIntentPredictionService _hrSubIntentAi;
        private readonly IDynamicTrainingService _dynamicTrainingAi;
        private static readonly HashSet<string> RoutableIntentKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "AssistantIdentity",
            "CsvIntent",
            "HrMedicalRegulation",
            "HrAttendance",
            "HrMedical",
            "HrTraining",
            "HrLeave",
            "HrEmployee",
            "EmpathyIntent",
            "Summary",
            "FollowUpIntent"
        };
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
            IHttpContextAccessor httpContextAccessor,
            IEmpathyResponseService empathyResponse,
            IIntentPredictionService intentAi,
            IEmployeeSubIntentPredictionService employeeSubIntentAi,
            IHrSubIntentPredictionService hrSubIntentAi,
            IDynamicTrainingService dynamicTrainingAi)
        {
            _sql = sqlExecutor;
            _chatHistory = chatHistory;
            _composer = composer;
            _genderDetector = genderDetector;
            _outOfScopeResponse = outOfScopeResponse;
            _thankYouResponses = thankYouResponses;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
            _summaryService = summaryService;
            _empathyResponse = empathyResponse;
            _intentAi = intentAi;
            _employeeSubIntentAi = employeeSubIntentAi;
            _hrSubIntentAi = hrSubIntentAi;
            _dynamicTrainingAi = dynamicTrainingAi;

            _kernel = Kernel.CreateBuilder().Build();
            _kernel.Plugins.AddFromObject(new LeavePlugin(_sql), "HrLeave");
            _kernel.Plugins.AddFromObject(new AttendancePlugin(config, loggerFactory.CreateLogger<AttendancePlugin>()), "HrAttendance");
            _kernel.Plugins.AddFromObject(new MedicalPlugin(_sql, config, loggerFactory.CreateLogger<MedicalPlugin>()), "HrMedical");
            _kernel.Plugins.AddFromObject(new TrainingHistoryPlugins(config, loggerFactory.CreateLogger<TrainingHistoryPlugins>()), "HrTraining");
            _kernel.Plugins.AddFromObject(new EmployeePlugin(_sql, config), "HrEmployee");
            _kernel.Plugins.AddFromObject(new CsvIntentPlugin(env, config), "CsvIntent");
            _kernel.Plugins.AddFromObject(new MedicalRegulationPlugin(env, config), "HrMedicalRegulation");
        }

        // InitializeAsync คงไว้เพื่อ interface compatibility แต่ทำงาน synchronous
        // หากมีการ warm-up จริงในอนาคต ให้เพิ่ม logic ที่นี่
        public Task InitializeAsync()
        {
            if (_initialized) return Task.CompletedTask;
            _logger.LogInformation("AI Service initialized — all HR plugins ready");
            _initialized = true;
            return Task.CompletedTask;
        }

        public async Task<string> ProcessMessageAsync(string userId, string userMessage)
        {
            await InitializeAsync();

            var commandResult = await _dynamicTrainingAi.TryHandleCommandAsync(userId, userMessage);

            if (commandResult != null) return commandResult;

            var stopwatch = Stopwatch.StartNew();
            UpdateGenderPreferenceInSession(userMessage);

            var ctx = await GetUserContextAsync(userId);
            var effectiveGender = ResolveEffectiveGender(ctx.Gender);

            // 1. ตรวจสอบคำหยาบ (Profanity Filter)
            var profanityWarning = _composer.CheckProfanity(userMessage, effectiveGender, ctx.UserName);
            if (profanityWarning != null)
            {
                stopwatch.Stop();
                await _chatHistory.SaveAuditLogAsync(userId, userMessage, "ProfanityBypass", (int)stopwatch.ElapsedMilliseconds);
                return profanityWarning;
            }

            // 2. ตรวจสอบคำขอบคุณ (Thank You Short-circuit)
            if (IsThankYouMessage(userMessage))
            {
                var pref = ResolveCurrentPreference();
                var response = _thankYouResponses.BuildResponse(pref);
                stopwatch.Stop();
                await _chatHistory.SaveAuditLogAsync(userId, userMessage, "ThankYouIntent", (int)stopwatch.ElapsedMilliseconds);
                return response;
            }

            // 4. แปลงวันที่ในประโยค
            userMessage = DateNormalizer.NormalizeThaiDates(userMessage);

            // 5. ดึงประวัติหน่วยความจำแชทล่าสุดมาช่วยเดาบริบทต่อเนื่อง
            var currentSummaryItems = _summaryService.GetSummaryItems(userId);
            var lastActiveItem = currentSummaryItems.OrderByDescending(i => i.Timestamp).FirstOrDefault();
            string lastIntent = lastActiveItem?.Intent ?? "OutOfScope";
            DateTime? lastChatTime = lastActiveItem?.Timestamp;

            // 🤖 เลเยอร์วิเคราะห์เจตจำนงหลักผ่านสมองกล ML.NET ร่วมกับ Rule หน่วยความจำสำรอง
            string matchedPluginKey = DeterminePluginKey(userMessage, lastIntent);

            // กรณีเรียกขอดูกระดานสรุปประวัติเซสชัน
            if (matchedPluginKey == "Summary")
            {
                stopwatch.Stop();
                await _chatHistory.SaveAuditLogAsync(userId, userMessage, "Summary", (int)stopwatch.ElapsedMilliseconds);
                return _composer.ComposeSummaryResponse(currentSummaryItems, effectiveGender, ctx.UserName);
            }

            if (matchedPluginKey == "EmpathyIntent")
            {
                var empathyContent = _empathyResponse.BuildEmpathyResponse(ctx.UserName, effectiveGender);
                stopwatch.Stop();
                await _chatHistory.SaveAuditLogAsync(userId, userMessage, "EmpathyIntent", (int)stopwatch.ElapsedMilliseconds);
                return _composer.ComposeResponse("Fallback", empathyContent, ctx.UserName, effectiveGender);
            }

            // 🚀 ส่งต่อนำทางเข้าสู่ความรู้ปลั๊กอินข้อมูลด้วยผลลัพธ์ของ AI
            var bodyData = await RouteToPluginAsync(userId, userMessage, matchedPluginKey, effectiveGender);

            if (matchedPluginKey != "OutOfScope" && !string.IsNullOrEmpty(bodyData))
            {
                SaveSummaryItem(userId, matchedPluginKey, userMessage, bodyData);
            }

            if (matchedPluginKey == "OutOfScope" && HasGreeting(userMessage))
            {
                var pref = ResolveCurrentPreference();
                //var greet = pref is GenderPreference.Female or GenderPreference.LGBT ? "สวัสดีค่ะ 👋" : "สวัสดีครับ 👋";
                //bodyData = $"{greet}\n\n{bodyData}";
                bodyData = $"\n{bodyData}";
            }

            var finalResponse = _composer.ComposeResponse(matchedPluginKey, bodyData, ctx.UserName, effectiveGender, lastChatTime);
            stopwatch.Stop();
            await _chatHistory.SaveAuditLogAsync(userId, userMessage, matchedPluginKey, (int)stopwatch.ElapsedMilliseconds);

            return finalResponse;
        }

        private string DeterminePluginKey(string msg, string lastIntent)
        {
            _logger.LogInformation("Intent router entered. Message={Message}, LastIntent={LastIntent}", msg, lastIntent);

            var predictedIntent = (_intentAi.PredictIntent(msg) ?? "OutOfScope").Trim();
            _logger.LogInformation("Intent router ML result. PredictedIntent={PredictedIntent}, Message={Message}", predictedIntent, msg);

            // กรณีพนักงานถามสั้นเชื่อมประโยคเดิม (เช่น "แล้วปีที่แล้วล่ะ") -> สืบทอด Intent เก่าทันที
            if (predictedIntent == "FollowUpIntent" && lastIntent != "OutOfScope")
            {
                _logger.LogInformation("Intent router selected follow-up intent. Intent={Intent}, Message={Message}", lastIntent, msg);
                return lastIntent;
            }

            if (RoutableIntentKeys.Contains(predictedIntent))
            {
                _logger.LogInformation("Intent router selected ML intent. Intent={Intent}, Message={Message}", predictedIntent, msg);
                return predictedIntent;
            }

            if (LooksLikeTrainingIntent(msg))
            {
                _logger.LogInformation("Intent router selected training fallback. Message={Message}", msg);
                return "HrTraining";
            }

            _logger.LogWarning("Intent router selected OutOfScope. PredictedIntent={PredictedIntent}, Message={Message}", predictedIntent, msg);
            return "OutOfScope";
        }

        private static bool LooksLikeTrainingIntent(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return false;

            return msg.Contains("อบรม", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("ฝึกอบรม", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("หลักสูตร", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("training", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("course", StringComparison.OrdinalIgnoreCase);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  AI Switch Router (สลับสัญญาณตรงเป๊ะตามโมเดลทำนายไร้เงื่อนไขทับซ้อน)
        // ════════════════════════════════════════════════════════════════════════
        private async Task<string> RouteToPluginAsync(string userId, string msg, string matchedIntent, string gender)
        {
            return matchedIntent switch
            {
                "AssistantIdentity" => _outOfScopeResponse.BuildAssistantIdentityResponse(gender),

                "CsvIntent" => await HandleCsvIntentSubIntentAsync(userId, msg),

                "HrMedicalRegulation" => await HandleMedicalRegulationSubIntentAsync(userId, msg),

                "HrAttendance" => await HandleAttendanceSubIntentAsync(userId, msg, gender),

                "HrMedical" => await GetMedicalDirectResponseAsync(userId, msg),

                "HrTraining" => await HandleTrainingSubIntentAsync(userId, msg),

                "HrLeave" => await HandleLeaveSubIntentAsync(userId, msg),

                "HrEmployee" => await HandleEmployeeSubIntentAsync(userId, msg),

                _ => _outOfScopeResponse.BuildOutOfScopeResponse(gender)
            };
        }

        // ฟังก์ชันย่อยสำหรับสกัดเจาะลึกฟังก์ชันภายในของระบบวันลาตามคีย์เวิร์ดจำเพาะ
        private async Task<string> HandleLeaveSubIntentAsync(string userId, string msg)
        {
            var predictedSubIntent = _hrSubIntentAi.PredictSubIntent("leave", msg);
            if (predictedSubIntent == "LeaveBalance")
            {
                return await Invoke("HrLeave", "get_leave_balance", userId);
            }

            if (predictedSubIntent == "LeaveHistory")
            {
                return await Invoke("HrLeave", "get_leave_requests", userId);
            }

            var mlBalance = await Invoke("HrLeave", "get_leave_balance", userId);
            var mlHistory = await Invoke("HrLeave", "get_leave_requests", userId);
            return mlBalance + "\n\n" + mlHistory;
        }

        private async Task<string> HandleCsvIntentSubIntentAsync(string userId, string msg)
        {
            var predictedSubIntent = _hrSubIntentAi.PredictSubIntent("csvintent", msg);
            _logger.LogInformation(
                "CsvIntent sub-intent route. SubIntent={SubIntent}, Message={Message}",
                predictedSubIntent,
                msg);

            var result = await _kernel.InvokeAsync(
                "CsvIntent",
                "get_intent_training_data",
                new KernelArguments
                {
                    ["employeeId"] = userId,
                    ["question"] = msg,
                    ["predictedSubIntent"] = predictedSubIntent
                });
            return result?.ToString() ?? "ไม่พบข้อมูลทั่วไป";
        }

        private async Task<string> HandleMedicalRegulationSubIntentAsync(string userId, string msg)
        {
            var predictedSubIntent = _hrSubIntentAi.PredictSubIntent("medical_regulation", msg);
            _logger.LogInformation(
                "Medical regulation sub-intent route. SubIntent={SubIntent}, Message={Message}",
                predictedSubIntent,
                msg);

            var result = await _kernel.InvokeAsync(
                "HrMedicalRegulation",
                "get_medical_regulation",
                new KernelArguments
                {
                    ["employeeId"] = userId,
                    ["question"] = msg,
                    ["predictedSubIntent"] = predictedSubIntent
                });
            return result?.ToString() ?? "ไม่พบระเบียบค่ารักษา";
        }

        private async Task<string> HandleAttendanceSubIntentAsync(string userId, string msg, string gender)
        {
            var predictedSubIntent = _hrSubIntentAi.PredictSubIntent("attendance", msg);
            _logger.LogInformation(
                "Attendance sub-intent route. SubIntent={SubIntent}, Message={Message}",
                predictedSubIntent,
                msg);

            // ส่ง predictedSubIntent ให้ plugin ใช้กรองหรือเลือก function ได้ถูกต้อง
            var result = await _kernel.InvokeAsync(
                "HrAttendance",
                "get_recent_attendance",
                new KernelArguments
                {
                    ["employeeId"] = userId,
                    ["question"] = msg,
                    ["predictedSubIntent"] = predictedSubIntent,
                    ["gender"] = gender
                });
            return result?.ToString() ?? "ไม่พบข้อมูลเวลาเข้าออกงาน";
        }

        private async Task<string> HandleTrainingSubIntentAsync(string userId, string msg)
        {
            var predictedSubIntent = _hrSubIntentAi.PredictSubIntent("training", msg);
            _logger.LogInformation(
                "Training sub-intent route. SubIntent={SubIntent}, Message={Message}",
                predictedSubIntent,
                msg);

            var predictedFunction = predictedSubIntent switch
            {
                "AvailableTraining" => "get_available_training_classes",
                "TrainingHours" => "get_training_hours",
                "TrainingCost" => "get_training_cost",
                "TrainingHistory" => "get_training_history",
                "TrainingCount" => "get_training_history", // History API handles count logic inside
                _ => "get_training_history"
            };

            _logger.LogInformation(
                "Training sub-intent selected ML route. SubIntent={SubIntent}, Function={Function}, Message={Message}",
                predictedSubIntent,
                predictedFunction,
                msg);

            var result = await _kernel.InvokeAsync(
                "HrTraining",
                predictedFunction,
                new KernelArguments
                {
                    ["employeeId"] = userId,
                    ["question"] = msg,
                    ["predictedSubIntent"] = predictedSubIntent
                });
            return result?.ToString() ?? "ไม่พบข้อมูล";
        }

        // ฟังก์ชันย่อยสำหรับสกัดเจาะลึกฟังก์ชันภายในของระบบพนักงานตามคีย์เวิร์ดจำเพาะ
        private async Task<string> HandleEmployeeSubIntentAsync(string userId, string msg)
        {
            var predictedSubIntent = _employeeSubIntentAi.PredictSubIntent(msg);
            var predictedFunction = predictedSubIntent switch
            {
                "EmployeeProfile" => "get_my_profile",
                "EmployeeIdCard" => "get_my_id_card",
                "EmployeeColleagues" => "get_dept_colleagues",
                "EmployeeDepartments" => "get_departments",
                "EmployeeDivisions" => "get_divisions",
                "EmployeeManagers" => "get_my_managers",
                "EmployeeLevels" => "get_levels",
                "EmployeePositions" => "get_positions",
                "EmployeeCompanies" => "get_company_info",
                _ => null
            };

            if (!string.IsNullOrEmpty(predictedFunction))
            {
                _logger.LogInformation(
                    "Employee sub-intent selected ML route. SubIntent={SubIntent}, Function={Function}, Message={Message}",
                    predictedSubIntent,
                    predictedFunction,
                    msg);
                return await Invoke("HrEmployee", predictedFunction, userId);
            }

            return await Invoke("HrEmployee", "get_my_profile", userId);
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
            // 🔥 เพิ่มการแปลงค่าตัวย่อจาก DB ("F" / "M") ให้เป็นมาตรฐาน "Female" / "Male" เพื่อให้ตรงกับเงื่อนไขตัวอื่น
            string normalizedGender = defaultGender;
            if (string.Equals(defaultGender, "F", StringComparison.OrdinalIgnoreCase)) normalizedGender = "Female";
            if (string.Equals(defaultGender, "M", StringComparison.OrdinalIgnoreCase)) normalizedGender = "Male";

            var session = _httpContextAccessor.HttpContext?.Session;
            var prefStr = session?.GetString("GenderPreference");
            if (string.IsNullOrWhiteSpace(prefStr)) return normalizedGender; // ส่งค่าที่แปลงแล้วกลับไป

            if (Enum.TryParse<GenderPreference>(prefStr, true, out var pref))
            {
                return pref switch
                {
                    GenderPreference.Female => "Female",
                    GenderPreference.Male => "Male",
                    GenderPreference.LGBT => "Female",
                    _ => normalizedGender // ส่งค่าที่แปลงแล้วกลับไป
                };
            }

            return normalizedGender;
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

        // คำ English สั้นที่ต้องตรวจแบบ whole-word (ป้องกัน false positive เช่น "ok" ใน "ตกลงwork")
        private static readonly HashSet<string> ThankYouWholeWordTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "ok", "ty", "tysm", "tyvm", "thx", "thnx", "thanx",
            "cool", "nice", "great", "awesome", "amazing", "wonderful",
            "fantastic", "superb", "brilliant", "excellent", "perfect",
            "okie", "oki", "okay"
        };

        private static readonly HashSet<string> GreetingWholeWordTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "hi", "hey", "yo", "sup", "hiya"
        };

        /// <summary>ตรวจว่า token ตรงแบบ whole-word (ล้อมด้วย non-letter หรืออยู่ต้น/ท้ายสตริง)</summary>
        private static bool ContainsWholeWord(string source, string token)
        {
            var idx = source.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var before = idx == 0 || !char.IsLetterOrDigit(source[idx - 1]);
                var after = idx + token.Length >= source.Length || !char.IsLetterOrDigit(source[idx + token.Length]);
                if (before && after) return true;
                idx = source.IndexOf(token, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsThankYouMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return false;

            // ตรวจคำยาว/ไทย ด้วย Contains ปกติ (ไม่มีความเสี่ยง false positive)
            if (msg.ContainsAny(
                "ขอบคุณ", "ขอบใจ", "รักนะ", "รักคุณจัก", "appreciate", "เก่งมาก", "เก่งจริง",
                "ขอบคุณนะ", "ขอบคุณค่ะ", "ขอบคุณครับ", "ขอบคุณจ้า", "ขอบคุณมาก ๆ",
                "ขอบคุณมากนะ", "ขอบคุณมากเลย", "ขอบคุณมากครับ", "ขอบคุณมากค่ะ",
                "ขอบคุณจริง ๆ", "ขอบคุณจากใจ", "ขอบใจนะ", "ขอบใจจ้า", "ขอบใจมากนะ",
                "ขอบคุณมากๆ", "ขอบคุณมั่กๆ", "ขอบคุณเว่อร์", "แต้งกิ้ว", "แต๊งค์",
                "thank you", "thank u", "thanks", "thanks a lot",
                "much appreciated", "grateful", "gratitude",
                "ขอบพระคุณ", "ขอบพระคุณมาก", "ขอบพระคุณครับ", "ขอบพระคุณค่ะ",
                "ซาบซึ้ง", "ซึ้งใจ", "ซึ้งมาก",
                "ดีงาม", "ดีเยี่ยม", "ยอดเยี่ยม", "เลิศ", "ปัง", "เริ่ด", "ดีที่สุด",
                "ชอบมาก", "ชอบจัง", "ชอบที่สุด", "ประทับใจ", "โดนใจ", "รักเลย",
                "รักมาก", "รักที่สุด",
                "you rock", "you're the best", "you are the best", "love it",
                "love this", "love u", "love you", "lovely", "เยี่ยมมาก", "เก่งที่สุด",
                "โคตรเก่ง", "เทพ", "เจ๋ง", "เจ๋งมาก", "สุดยอดเลย", "ดีต่อใจ",
                "โอเค", "โอเคเลย", "โอเคค่ะ", "โอเคครับ",
                "โอเค ๆ", "okๆ", "รับทราบ", "เข้าใจแล้ว", "ยอด", "ยอดไปเลย",
                "ดีมาก", "เยี่ยม", "สุดยอด"))
                return true;

            // ตรวจคำ English สั้นแบบ whole-word เพื่อป้องกัน false positive
            foreach (var token in ThankYouWholeWordTokens)
            {
                if (ContainsWholeWord(msg, token)) return true;
            }

            return false;
        }

        private static bool HasGreeting(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return false;

            // ตรวจคำยาว/ไทย ด้วย Contains ปกติ
            if (msg.ContainsAny(
                "สวัสดี", "หวัดดี", "ดีครับ", "ดีค่ะ",
                "ดีกร๊าบ!", "ดีจ้า",
                "สวัสดีจ้า", "สวัสดีค้าบ", "สวัสดีคั๊บ", "สวัสดีฮะ", "สวัสดีคร้าบ",
                "สวัสดีเจ้า", "สวัสดีเด้อ", "สวัสดีค้าบบบ", "หวัดดีครับ", "หวัดดีค่ะ",
                "หวัดดีจ้า", "หวัดดีคับ", "หวัดดีคร้าบ", "หวัดดีฮะ", "หวัดดีเจ้า",
                "ดีคับ", "ดีคร้าบ", "ดีฮะ", "ดีเจ้า", "ดีเด้อ", "ดีค้าบ", "ดีคั๊บ",
                "ดีจร้า", "ดีกราบ", "ดีงับ", "ว่าไง", "ว่าไงครับ", "ว่าไงค่ะ",
                "ว่าไงจ๊ะ", "ว่าไงดี", "ว่าไงเพื่อน", "ว่าไงพวก", "ไงครับ", "ไงค่ะ",
                "ไงจ๊ะ", "เป็นไง", "เป็นไงบ้าง", "เป็นไงมั่ง", "อรุณสวัสดิ์",
                "สายัณห์สวัสดิ์", "ราตรีสวัสดิ์", "ทักทาย", "เฮลโล", "ฮัลโหล", "ฮัลโล",
                "good morning", "morning", "mornin", "good afternoon",
                "good evening", "good night", "good day", "g'day", "howdy",
                "what's up", "wassup", "wazzup",
                "hi there", "hey there", "hello", "hello there", "greetings",
                "hola", "bonjour", "salut", "ciao", "aloha",
                "how are you", "how's it going", "how are things", "what's new",
                "how do you do", "good to see you", "nice to see you", "pleased to meet you",
                "สวัสดีตอนเช้า", "สวัสดีตอนบ่าย", "สวัสดีตอนเย็น", "สวัสดีตอนค่ำ",
                "ไงพวก", "มอร์นิ่ง", "กู๊ดมอร์นิ่ง",
                "สวัสดีค้าบบบบบ", "สวัสดีจ้าาา", "หวัดดีค้าบ", "หวัดดีงับ", "หวัดดีจร้า",
                "ดีค้าบบบ", "ดีงับ", "ดีจร้า", "ดีง้าบ", "ดีค้าบบ",
                "ไงครับเพื่อน", "ไงค่ะเพื่อน", "เป็นไงมั่งคับ", "เป็นไงบ้างคับ", "เป็นไงมั่งคะ",
                "ว่าไงดีคับ", "ว่าไงดีค่ะ", "ไงดีครับ", "ไงดีค่ะ",
                "good morning krub", "good morning ka", "morning krub", "morning ka",
                "hi krub", "hello krub", "hey krub",
                "สวัสดีตอนสาย", "ทักทายจ้า", "แวะมาทัก", "แวะมาทายทัก",
                "hi there!", "hey there!", "heyo", "hiii", "hellooo",
                "yoo", "yooo", "howdy partner", "how's it going?", "how's everything?",
                "what's good?", "what's popping?", "yo what's up", "sup bro", "sup dude",
                "hi ya", "how goes it?", "good to see ya", "nice to meet ya"))
                return true;

            // ตรวจคำ English สั้นแบบ whole-word เพื่อป้องกัน false positive
            foreach (var token in GreetingWholeWordTokens)
            {
                if (ContainsWholeWord(msg, token)) return true;
            }

            return false;
        }

        private async Task<string> Invoke(string plugin, string function, string userId)
        {
            var result = await _kernel.InvokeAsync(
                plugin, function,
                new KernelArguments { ["employeeId"] = userId });
            return result?.ToString() ?? "";
        }

        private async Task<string> GetMedicalDirectResponseAsync(string userId, string userMessage)
        {
            var predictedSubIntent = _hrSubIntentAi.PredictSubIntent("medical", userMessage);
            if (predictedSubIntent == "MedicalHistory")
            {
                var historyResult = await _kernel.InvokeAsync(
                    "HrMedical",
                    "get_claim_history",
                    new KernelArguments
                    {
                        ["employeeId"] = userId,
                        ["question"] = userMessage
                    });
                return historyResult?.ToString() ?? "ไม่พบข้อมูลประวัติค่ารักษาพยาบาล";
            }

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

        public async IAsyncEnumerable<string> ProcessMessageStreamingAsync(string userId, string userMessage)
        {
            await InitializeAsync();

            var fullTextResponse = await ProcessMessageAsync(userId, userMessage);
            var wordsOrChunks = ChunkText(fullTextResponse, chunkSize: 6);
            foreach (var chunk in wordsOrChunks)
            {
                yield return chunk;
                await Task.Delay(8);
            }
        }

        private static IEnumerable<string> ChunkText(string text, int chunkSize)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            var stringInfo = new StringInfo(text);
            int totalElements = stringInfo.LengthInTextElements;

            for (int i = 0; i < totalElements; i += chunkSize)
            {
                yield return stringInfo.SubstringByTextElements(i, Math.Min(chunkSize, totalElements - i));
            }
        }

        public async Task<object?> FetchDataAsync(
            string userId, string dataType, Dictionary<string, string> parameters)
        {
            await InitializeAsync();
            var empId = parameters.GetValueOrDefault("employeeId", userId);

            return dataType.ToLowerInvariant() switch
            {
                "leave" => await _kernel.InvokeAsync("HrLeave", "get_leave_balance", new KernelArguments { ["employeeId"] = empId }),
                "leave_req" => await _kernel.InvokeAsync("HrLeave", "get_leave_requests", new KernelArguments { ["employeeId"] = empId }),
                "attendance" => await _kernel.InvokeAsync("HrAttendance", "get_recent_attendance", new KernelArguments { ["employeeId"] = empId }),
                "medical" => await _kernel.InvokeAsync("HrMedical", "get_claim_status", new KernelArguments { ["employeeId"] = empId }),
                "training" => await _kernel.InvokeAsync("HrTraining", "get_training_history", new KernelArguments { ["employeeId"] = empId }),
                "profile" => await _kernel.InvokeAsync("HrEmployee", "get_my_profile", new KernelArguments { ["employeeId"] = empId }),
                "colleagues" => await _kernel.InvokeAsync("HrEmployee", "get_dept_colleagues", new KernelArguments { ["employeeId"] = empId }),
                "depts" => await _kernel.InvokeAsync("HrEmployee", "get_departments", new KernelArguments { ["employeeId"] = empId }),
                "divisions" => await _kernel.InvokeAsync("HrEmployee", "get_divisions", new KernelArguments { ["employeeId"] = empId }),
                "levels" => await _kernel.InvokeAsync("HrEmployee", "get_levels", new KernelArguments { ["employeeId"] = empId }),
                "positions" => await _kernel.InvokeAsync("HrEmployee", "get_positions", new KernelArguments { ["employeeId"] = empId }),
                "companies" => await _kernel.InvokeAsync("HrEmployee", "get_company_info", new KernelArguments { ["employeeId"] = empId }),
                _ => null
            };
        }

        private async Task<(string UserName, string DeptId, string CompanyId, string Gender)> GetUserContextAsync(string userId)
        {
            try
            {
                const string sql = "SELECT EMFNT, EMLNT, DPID, CMID, EMSX FROM EMPDA WHERE EMID = @UserId";
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

        private void SaveSummaryItem(string sessionId, string pluginKey, string question, string rawData)
        {
            var summaryItem = new ChatSummaryItem
            {
                Intent = pluginKey,
                Topic = ExtractTopicFromPluginKey(pluginKey),
                KeyInfo = ExtractKeyInfo(rawData),
                Timestamp = DateTime.Now
            };

            _summaryService.AddOrUpdateSummaryItem(sessionId, summaryItem);
            _logger.LogDebug("Saved summary for intent: {Intent}, topic: {Topic}", pluginKey, summaryItem.Topic);
        }

        private static string ExtractKeyInfo(string rawData)
        {
            if (string.IsNullOrEmpty(rawData)) return "";

            // ตัดให้สั้นสำหรับ session summary เพื่อประหยัด memory
            // ใช้ StringInfo รองรับ emoji และอักษรไทยที่มีหลาย code point
            const int MaxLength = 500;
            var info = new StringInfo(rawData);
            if (info.LengthInTextElements <= MaxLength) return rawData;

            return info.SubstringByTextElements(0, MaxLength) + "…";
        }

        private string ExtractTopicFromPluginKey(string pluginKey)
        {
            return pluginKey switch
            {
                "HrLeave" => "ยอดวันลาและประวัติการลา",
                "HrMedical" => "สิทธิการเบิกค่ารักษาพยาบาล",
                "HrTraining" => "ประวัติการฝึกอบรม",
                "HrAttendance" => "เวลาเข้า-ออกงาน",
                "HrEmployee" => "ข้อมูลส่วนตัวพนักงาน",
                "HrMedicalRegulation" => "ระเบียบค่ารักษาพยาบาล",
                "CsvIntent" => "ข้อมูลทั่วไปบริษัท",
                "ChartAttendance" => "กราฟเวลาเข้า-ออกงาน",
                "ChartMedical" => "กราฟค่ารักษาพยาบาล",
                "ChartLeave" => "กราฟวันลา",
                "ChartOther" => "กราฟ",

                _ => "ข้อมูลที่สอบถาม"
            };
        }

        public void SaveChartSummary(string userId, string chartType, int? buddhistYear)
        {
            var yearLabel = buddhistYear.HasValue ? $" ปี พ.ศ. {buddhistYear}" : "";

            var (intent, topic) = chartType.ToLowerInvariant() switch
            {
                "attendance" => ("ChartAttendance", $"กราฟเวลาเข้า-ออกงาน{yearLabel}"),
                "medical" => ("ChartMedical", $"กราฟค่ารักษาพยาบาล{yearLabel}"),
                "leave" => ("ChartLeave", $"กราฟวันลา{yearLabel}"),
                _ => ("ChartOther", $"กราฟ{chartType}{yearLabel}")
            };

            var item = new ChatSummaryItem
            {
                Intent = intent,
                Topic = topic,
                KeyInfo = $"ดูกราฟ{topic}",
                Timestamp = DateTime.Now
            };

            _summaryService.AddOrUpdateSummaryItem(userId, item);
            _logger.LogDebug("Chart summary saved: intent={Intent} topic={Topic}", intent, topic);
        }
    }

    public static class StringExtensions
    {
        public static bool ContainsAny(this string source, params string[] keywords)
        {
            if (string.IsNullOrEmpty(source)) return false;
            foreach (var k in keywords)
            {
                if (source.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
