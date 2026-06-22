using DocumentFormat.OpenXml.Spreadsheet;
using HrChatThaiLLM.Server.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace HrChatThaiLLM.Server.Services;

public interface IDynamicTrainingService
{
    // แก้ตรงนี้: เพิ่ม string userId เข้าไป
    Task<string?> TryHandleCommandAsync(string userId, string message);
}

public class DynamicTrainingService : IDynamicTrainingService
{
    private static readonly ConcurrentDictionary<string, bool> _authStates = new();
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IIntentPredictionService _intentService;
    private readonly IEmployeeSubIntentPredictionService _empSubIntentService;
    private readonly IHrSubIntentPredictionService _hrSubIntentService;
    private readonly IChatHistoryService _chatHistoryService;
    // เก็บสถานะว่า user คนไหนผ่านการยืนยันรหัสผ่านแล้ว (admin session)
    private static readonly ConcurrentDictionary<string, bool> _adminSessions = new();

    // เก็บคำสั่ง admin ที่ผู้ใช้พิมพ์ไว้ตอนที่ยังไม่ผ่านการยืนยัน เพื่อนำกลับมาทำงานหลังใส่รหัสถูก
    private static readonly ConcurrentDictionary<string, string> _pendingCommands = new();

    public DynamicTrainingService(
        IConfiguration config,
        IWebHostEnvironment env,
        IIntentPredictionService intentService,
        IEmployeeSubIntentPredictionService empSubIntentService,
        IHrSubIntentPredictionService hrSubIntentService,
        IChatHistoryService chatHistoryService)
    {
        _config = config;
        _env = env;
        _intentService = intentService;
        _empSubIntentService = empSubIntentService;
        _hrSubIntentService = hrSubIntentService;
        _chatHistoryService = chatHistoryService;
    }

    public async Task<string?> TryHandleCommandAsync(string userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Trim();
        var lowerText = text.ToLowerInvariant();

        var correctPassword = _config["Security:ManualPassword"] ?? "2523252500";

        // ----- คำสั่งที่ต้องใช้สิทธิ์ admin (รวม /help) -----
        bool isAdminCommand = lowerText.StartsWith("/train") ||
                              lowerText.StartsWith("/delete") ||
                              lowerText.StartsWith("/add-company") ||
                              lowerText.StartsWith("/view-domains") ||
                              lowerText.StartsWith("/view-messages") ||
                              lowerText.StartsWith("/trace") ||
                              lowerText == "/help" ||
                              lowerText == "/manual" ||
                              lowerText.Contains("คู่มือการใช้งาน");

        // 🔒 1. ถ้าผู้ใช้กำลังอยู่ในสถานะรอรหัสผ่าน (จากครั้งก่อน)
        if (_authStates.TryGetValue(userId, out _))
        {
            _authStates.TryRemove(userId, out _); // ลบสถานะรอ

            if (text == correctPassword)
            {
                // รหัสถูก → สร้าง admin session
                _adminSessions.TryAdd(userId, true);

                // ถ้ามีคำสั่ง admin ที่ค้างไว้ → รันทันที
                if (_pendingCommands.TryRemove(userId, out var pendingCmd))
                {
                    return await ExecuteAdminCommandAsync(userId, pendingCmd);
                }

                return "✅ **ยืนยันตัวตนสำเร็จ** คุณสามารถใช้คำสั่ง admin ได้แล้ว";
            }
            else
            {
                // รหัสผิด → ล้างคำสั่งที่ค้างไว้ด้วย (ถ้ามี)
                _pendingCommands.TryRemove(userId, out _);
                return "❌ **ระบบ:** รหัสผ่านไม่ถูกต้อง ยกเลิกการเข้าถึงคำสั่ง admin";
            }
        }

        // 🔒 2. ถ้าเป็นคำสั่ง admin แต่ยังไม่มี admin session → ขอรหัสผ่าน
        if (isAdminCommand && !_adminSessions.ContainsKey(userId))
        {
            _authStates.TryAdd(userId, true);            // ตั้งสถานะรอรหัสผ่าน
            _pendingCommands[userId] = text;             // เก็บคำสั่งที่ผู้ใช้ต้องการเรียก
            return "🔒 **ระบบรักษาความปลอดภัย:** กรุณากรอกรหัสผ่านเพื่อเข้าถึงคำสั่ง admin";
        }

        // ---- หลังจากนี้ผู้ใช้มี admin session แล้ว หรือเป็นข้อความทั่วไป ----
        return await ExecuteAdminCommandAsync(userId, text);
    }

    private async Task<string?> ExecuteAdminCommandAsync(string userId, string text)
    {
        var lowerText = text.ToLowerInvariant();

        // คำสั่งดูคู่มือ
        if (lowerText == "/help" || lowerText == "/manual" || lowerText.Contains("คู่มือการใช้งาน"))
            return await HandleHelpAsync();

        // คำสั่ง train/delete/view/trace (ต้องมี admin session อยู่แล้ว ณ จุดนี้)
        if (text.StartsWith("/train-main")) return await HandleTrainMainAsync(text);
        if (text.StartsWith("/train-sub")) return await HandleTrainSubAsync(text);
        if (text.StartsWith("/add-company")) return await HandleAddCompanyAsync(text);
        if (text.StartsWith("/train ")) return await HandleTrainCombinedAsync(text);
        if (text.StartsWith("/delete ")) return await HandleDeleteCombinedAsync(text);
        if (text.StartsWith("/delete-main")) return await HandleDeleteMainAsync(text);
        if (text.StartsWith("/delete-sub")) return await HandleDeleteSubAsync(text);
        if (text.StartsWith("/delete-company")) return await HandleDeleteCompanyAsync(text);
        if (text.StartsWith("/view-domains")) return HandleViewDomains();
        if (text.StartsWith("/view-messages")) return await HandleViewMessagesAsync(text);
        if (text.StartsWith("/trace ")) return HandleTrace(text);

        return null; // ไม่ใช่คำสั่ง admin
    }

    // 🌟 สร้างฟังก์ชันนี้เพิ่มเข้าไปในคลาส เพื่อจัดการเรื่องอ่านไฟล์คู่มือ (สไตล์เดียวกับโค้ดเก่า)
    private async Task<string> HandleHelpAsync()
    {
        try
        {
            // ดึง Path จาก appsettings.json (ถ้าหาไม่เจอจะใช้ Default Path แทน)
            var relativePath = _config["FilePaths:Walkthrough"] ?? "file/Helper/walkthrough.md";

            // นำ Path มาประกอบกับ WebRootPath 
            var helpPath = Path.Combine(_env.WebRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(helpPath))
            {
                return await File.ReadAllTextAsync(helpPath);
            }
            else
            {
                return $"⚠️ **ระบบ:** ไม่พบไฟล์คู่มือการใช้งานที่ตั้งค่าไว้ในระบบ (`{relativePath}`)";
            }
        }
        catch (Exception)
        {
            return "⚠️ **ระบบ:** เกิดข้อผิดพลาดภายในระบบ ไม่สามารถเปิดไฟล์คู่มือการใช้งานได้ในขณะนี้";
        }
    }
    private async Task<string> HandleTrainMainAsync(string text)
    {
        // รูปแบบ: /train-main
        // รูปแบบ: /train-main Intent
        // รูปแบบ: /train-main "คำถาม" "Intent"
        var matchAdd = Regex.Match(text, @"^/train-main\s+""([^""]+)""\s+""([^""]+)""$");
        if (matchAdd.Success)
        {
            var question = matchAdd.Groups[1].Value.Trim();
            var intent = matchAdd.Groups[2].Value.Trim();
            
            var csvPath = GetPath("FilePaths:IntentTraining", "file\\Training\\intent_classifier_training_data.csv");
            var modelPath = GetPath("FilePaths:IntentModel", "file\\Modeling\\intent_classifier_model.zip");
            
            await AppendCsvAsync(csvPath, question, intent);
            DeleteModel(modelPath);
            _intentService.InvalidateCache();
            
            return $"✅ เพิ่มข้อมูลสอน Intent หลักสำเร็จ: `{question}` ➔ `{intent}`\nระบบรีโหลดโมเดลเรียบร้อยแล้ว";
        }

        var matchFilter = Regex.Match(text, @"^/train-main\s+([a-zA-Z0-9_]+)$");
        if (matchFilter.Success)
        {
            var filterIntent = matchFilter.Groups[1].Value.Trim();
            var csvPath = GetPath("FilePaths:IntentTraining", "file\\Training\\intent_classifier_training_data.csv");
            
            var mainView = await ReadFilteredCsvAsync(csvPath, filterIntent, "Intent หลัก");
            
            // เช็คว่ามี SubIntent File ไหม
            var domainFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["attendance"] = "attendance_subintent_training_data.csv",
                ["csvintent"] = "csvintent_subintent_training_data.csv",
                ["leave"] = "leave_subintent_training_data.csv",
                ["medical"] = "medical_subintent_training_data.csv",
                ["medical_regulation"] = "medical_regulation_subintent_training_data.csv",
                ["training"] = "training_subintent_training_data.csv",
                ["employee"] = "employee_subintent_training_data.csv"
            };

            var domainKey = filterIntent.ToLowerInvariant().Replace("hr", "");
            if (domainFiles.TryGetValue(domainKey, out var subCsvName))
            {
                var dir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
                var subPath = Path.Combine(_env.WebRootPath ?? "", dir, subCsvName);
                var subView = await ReadCsvAsync(subPath, $"Sub Intent ({domainKey})");
                return $"{mainView}\n\n---\n\n{subView}";
            }

            return mainView;
        }

        if (text.Trim() == "/train-main")
        {
            var csvPath = GetPath("FilePaths:IntentTraining", "file\\Training\\intent_classifier_training_data.csv");
            return await ReadCsvAsync(csvPath, "ข้อมูลสอน Intent หลักทั้งหมด");
        }

        return "❌ รูปแบบคำสั่งไม่ถูกต้อง ลองใช้: `/train-main \"คำถาม\" \"Intent\"` หรือ `/train-main [Intent]`";
    }

    private void CreateBackup(string targetFilePath)
    {
        if (!File.Exists(targetFilePath)) return;

        try
        {
            // 1. หาที่อยู่โฟลเดอร์เก็บ Backup
            var backupRel = _config["FilePaths:TrainingBackupDir"] ?? "file\\Training\\Backups";
            var backupDir = Path.Combine(_env.WebRootPath ?? "", backupRel.Replace('/', '\\'));

            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            // 2. ตั้งชื่อไฟล์ใหม่โดยต่อท้ายด้วย วันเวลา (Versioning)
            var fileName = Path.GetFileNameWithoutExtension(targetFilePath);
            var ext = Path.GetExtension(targetFilePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupDir, $"{fileName}_v{timestamp}{ext}");

            // 3. ก๊อปปี้ไฟล์
            File.Copy(targetFilePath, backupPath, overwrite: true);

            // 4. (ทางเลือก) ลบไฟล์ Backup ที่เก่าเกินไป ป้องกันดิสก์เต็ม (เก็บไว้แค่ 15 ไฟล์ล่าสุดต่อโดเมน)
            var oldFiles = Directory.GetFiles(backupDir, $"{fileName}_v*{ext}")
                                    .Select(f => new FileInfo(f))
                                    .OrderByDescending(f => f.CreationTime)
                                    .Skip(15) // เปลี่ยนตัวเลขนี้ได้ตามต้องการ
                                    .ToList();

            foreach (var file in oldFiles)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            // ควรใช้ _logger.LogError(ex, "Backup failed") ถ้ามีการ Inject ILogger มาครับ
            Console.WriteLine($"⚠️ Backup failed: {ex.Message}");
        }
    }
    private async Task<string> HandleTrainSubAsync(string text)
    {
        // รูปแบบ: /train-sub domain
        // รูปแบบ: /train-sub domain "คำถาม" "SubIntent"
        var matchAdd = Regex.Match(text, @"^/train-sub\s+([a-zA-Z0-9_]+)\s+""([^""]+)""\s+""([^""]+)""$");
        if (matchAdd.Success)
        {
            var domain = matchAdd.Groups[1].Value.Trim().ToLowerInvariant();
            var question = matchAdd.Groups[2].Value.Trim();
            var intent = matchAdd.Groups[3].Value.Trim();

            if (domain == "employee")
            {
                var csvPath = GetPath("FilePaths:EmployeeSubIntentTraining", "file\\Training\\employee_subintent_training_data.csv");
                var modelPath = GetPath("FilePaths:EmployeeSubIntentModel", "file\\Modeling\\employee_subintent_model.zip");
                await AppendCsvAsync(csvPath, question, intent);
                DeleteModel(modelPath);
                _empSubIntentService.InvalidateCache();
            }
            else
            {
                var dir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
                var csvName = $"{domain}_subintent_training_data.csv";
                var csvPath = Path.Combine(_env.WebRootPath ?? "", dir, csvName);
                
                var modelDir = _config["FilePaths:HrSubIntentModelDir"] ?? "file\\Modeling";
                var modelName = $"{domain}_subintent_training_data.model.zip";
                var modelPath = Path.Combine(_env.WebRootPath ?? "", modelDir, modelName);

                await AppendCsvAsync(csvPath, question, intent);
                DeleteModel(modelPath);
                _hrSubIntentService.InvalidateCache(domain);
            }

            return $"✅ เพิ่มข้อมูลสอน Sub Intent ({domain}) สำเร็จ: `{question}` ➔ `{intent}`\nระบบรีโหลดโมเดลเรียบร้อยแล้ว";
        }

        var matchView = Regex.Match(text, @"^/train-sub\s+([a-zA-Z0-9_]+)$");
        if (matchView.Success)
        {
            var domain = matchView.Groups[1].Value.Trim().ToLowerInvariant();
            if (domain == "employee")
            {
                var csvPath = GetPath("FilePaths:EmployeeSubIntentTraining", "file\\Training\\employee_subintent_training_data.csv");
                return await ReadCsvAsync(csvPath, "ข้อมูลสอน Employee Sub Intent");
            }
            else
            {
                var dir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
                var csvName = $"{domain}_subintent_training_data.csv";
                var csvPath = Path.Combine(_env.WebRootPath ?? "", dir, csvName);
                return await ReadCsvAsync(csvPath, $"ข้อมูลสอน Sub Intent ({domain})");
            }
        }

        return "❌ รูปแบบคำสั่งไม่ถูกต้อง ลองใช้: `/train-sub domain \"คำถาม\" \"SubIntent\"`";
    }

    private async Task<string> HandleAddCompanyAsync(string text)
    {
        // รูปแบบ: /add-company "คำถาม" "คำตอบยาวๆ"
        var matchAdd = Regex.Match(text, @"^/add-company\s+""([^""]+)""\s+""([^""]+)""$");
        if (matchAdd.Success)
        {
            var question = matchAdd.Groups[1].Value.Trim();
            var answer = matchAdd.Groups[2].Value.Trim().Replace("\n", "\\n");

            var rel = _config["FilePaths:CsvIntentCompany"] ?? "file/Intent/intent_about_company.csv";
            var csvPath = Path.Combine(_env.WebRootPath ?? "", rel.Replace('/', '\\'));

            await AppendCsvAsync(csvPath, question, answer);
            CsvIntentPlugin.InvalidateCache();

            return $"✅ เพิ่มข้อมูลบริษัทสำเร็จ: `{question}` ➔ `{answer.Replace("\\n", " ")}`\nระบบรีโหลด Cache เรียบร้อยแล้ว";
        }

        if (text.Trim() == "/add-company")
        {
            var rel = _config["FilePaths:CsvIntentCompany"] ?? "file/Intent/intent_about_company.csv";
            var csvPath = Path.Combine(_env.WebRootPath ?? "", rel.Replace('/', '\\'));
            return await ReadCsvAsync(csvPath, "ข้อมูลประวัติบริษัท (Company KB)");
        }

        return "❌ รูปแบบคำสั่งไม่ถูกต้อง ลองใช้: `/add-company \"หัวข้อ/คำถาม\" \"คำตอบ\"`";
    }

    private async Task<string> HandleTrainCombinedAsync(string text)
    {
        // รูปแบบ: /train "คำถาม" "MainIntent" "SubIntent"
        var match = Regex.Match(text, @"^/train\s+""([^""]+)""\s+""([^""]+)""\s+""([^""]+)""$");
        if (match.Success)
        {
            var question = match.Groups[1].Value.Trim();
            var mainIntent = match.Groups[2].Value.Trim();
            var subIntent = match.Groups[3].Value.Trim();

            // 1. บันทึก Main Intent
            var mainCsvPath = GetPath("FilePaths:IntentTraining", "file\\Training\\intent_classifier_training_data.csv");
            var mainModelPath = GetPath("FilePaths:IntentModel", "file\\Modeling\\intent_classifier_model.zip");
            await AppendCsvAsync(mainCsvPath, question, mainIntent);
            DeleteModel(mainModelPath);
            _intentService.InvalidateCache();

            // 2. วิเคราะห์หา Domain และบันทึก Sub Intent
            var domain = mainIntent.ToLowerInvariant().Replace("hr", "");
            
            if (domain == "employee")
            {
                var subCsvPath = GetPath("FilePaths:EmployeeSubIntentTraining", "file\\Training\\employee_subintent_training_data.csv");
                var subModelPath = GetPath("FilePaths:EmployeeSubIntentModel", "file\\Modeling\\employee_subintent_model.zip");
                await AppendCsvAsync(subCsvPath, question, subIntent);
                DeleteModel(subModelPath);
                _empSubIntentService.InvalidateCache();
            }
            else
            {
                var dir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
                var csvName = $"{domain}_subintent_training_data.csv";
                var subCsvPath = Path.Combine(_env.WebRootPath ?? "", dir, csvName);
                
                var modelDir = _config["FilePaths:HrSubIntentModelDir"] ?? "file\\Modeling";
                var modelName = $"{domain}_subintent_training_data.model.zip";
                var subModelPath = Path.Combine(_env.WebRootPath ?? "", modelDir, modelName);

                await AppendCsvAsync(subCsvPath, question, subIntent);
                DeleteModel(subModelPath);
                _hrSubIntentService.InvalidateCache(domain);
            }

            return $"✅ เพิ่มข้อมูลสอน **คู่ (Main & Sub)** สำเร็จ:\nคำถาม: `{question}`\nMain Intent: `{mainIntent}`\nSub Intent ({domain}): `{subIntent}`\nระบบรีโหลดโมเดลเรียบร้อยแล้ว";
        }
        return "❌ รูปแบบคำสั่งไม่ถูกต้อง ลองใช้: `/train \"คำถาม\" \"MainIntent\" \"SubIntent\"`";
    }

    private async Task<string> HandleDeleteMainAsync(string text)
    {
        var match = Regex.Match(text, @"^/delete-main\s+""([^""]+)""$");
        if (match.Success)
        {
            var question = match.Groups[1].Value.Trim();
            var csvPath = GetPath("FilePaths:IntentTraining", "file\\Training\\intent_classifier_training_data.csv");
            var modelPath = GetPath("FilePaths:IntentModel", "file\\Modeling\\intent_classifier_model.zip");
            
            var deleted = await DeleteCsvRowAsync(csvPath, question);
            if (deleted)
            {
                DeleteModel(modelPath);
                _intentService.InvalidateCache();
                return $"🗑️ ลบข้อมูล `{question}` จาก Main Intent สำเร็จ พร้อมรีโหลดโมเดลแล้ว";
            }
            return $"⚠️ ไม่พบคำถาม `{question}` ใน Main Intent";
        }
        return "❌ รูปแบบไม่ถูกต้อง ลองใช้: `/delete-main \"ข้อความคำถามที่ต้องการลบ\"`";
    }

    private async Task<string> HandleDeleteSubAsync(string text)
    {
        var match = Regex.Match(text, @"^/delete-sub\s+([a-zA-Z0-9_]+)\s+""([^""]+)""$");
        if (match.Success)
        {
            var domain = match.Groups[1].Value.Trim().ToLowerInvariant();
            var question = match.Groups[2].Value.Trim();

            string csvPath, modelPath;
            if (domain == "employee")
            {
                csvPath = GetPath("FilePaths:EmployeeSubIntentTraining", "file\\Training\\employee_subintent_training_data.csv");
                modelPath = GetPath("FilePaths:EmployeeSubIntentModel", "file\\Modeling\\employee_subintent_model.zip");
            }
            else
            {
                var dir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
                csvPath = Path.Combine(_env.WebRootPath ?? "", dir, $"{domain}_subintent_training_data.csv");
                var modelDir = _config["FilePaths:HrSubIntentModelDir"] ?? "file\\Modeling";
                modelPath = Path.Combine(_env.WebRootPath ?? "", modelDir, $"{domain}_subintent_training_data.model.zip");
            }

            var deleted = await DeleteCsvRowAsync(csvPath, question);
            if (deleted)
            {
                DeleteModel(modelPath);
                if (domain == "employee") _empSubIntentService.InvalidateCache();
                else _hrSubIntentService.InvalidateCache(domain);
                return $"🗑️ ลบข้อมูล `{question}` จาก Sub Intent ({domain}) สำเร็จ พร้อมรีโหลดโมเดลแล้ว";
            }
            return $"⚠️ ไม่พบคำถาม `{question}` ใน Sub Intent ({domain})";
        }
        return "❌ รูปแบบไม่ถูกต้อง ลองใช้: `/delete-sub domain \"ข้อความคำถามที่ต้องการลบ\"`";
    }

    private async Task<string> HandleDeleteCompanyAsync(string text)
    {
        var match = Regex.Match(text, @"^/delete-company\s+""([^""]+)""$");
        if (match.Success)
        {
            var question = match.Groups[1].Value.Trim();
            var rel = _config["FilePaths:CsvIntentCompany"] ?? "file/Intent/intent_about_company.csv";
            var csvPath = Path.Combine(_env.WebRootPath ?? "", rel.Replace('/', '\\'));
            
            var deleted = await DeleteCsvRowAsync(csvPath, question);
            if (deleted)
            {
                CsvIntentPlugin.InvalidateCache();
                return $"🗑️ ลบข้อมูล `{question}` จากข้อมูลบริษัทสำเร็จ พร้อมรีโหลด Cache แล้ว";
            }
            return $"⚠️ ไม่พบหัวข้อ `{question}` ในข้อมูลบริษัท";
        }
        return "❌ รูปแบบไม่ถูกต้อง ลองใช้: `/delete-company \"หัวข้อที่ต้องการลบ\"`";
    }

    private async Task<string> HandleDeleteCombinedAsync(string text)
    {
        // รูปแบบ: /delete "คำถามที่ต้องการลบ"
        var match = Regex.Match(text, @"^/delete\s+""([^""]+)""$");
        if (match.Success)
        {
            var question = match.Groups[1].Value.Trim();
            var mainCsvPath = GetPath("FilePaths:IntentTraining", "file\\Training\\intent_classifier_training_data.csv");
            
            // ค้นหา Main Intent ก่อนเพื่อหา Domain
            string? foundMainIntent = null;
            if (File.Exists(mainCsvPath))
            {
                var lines = await File.ReadAllLinesAsync(mainCsvPath);
                foreach (var line in lines.Skip(1))
                {
                    var parts = ParseCsvLine(line);
                    if (parts.Length >= 2 && string.Equals(parts[0], question, StringComparison.OrdinalIgnoreCase))
                    {
                        foundMainIntent = parts[1].Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(foundMainIntent))
            {
                return $"⚠️ ไม่พบคำถาม `{question}` ในระบบ Main Intent ครับ";
            }

            var domain = foundMainIntent.ToLowerInvariant().Replace("hr", "");
            
            // ลบจาก Main
            var mainModelPath = GetPath("FilePaths:IntentModel", "file\\Modeling\\intent_classifier_model.zip");
            await DeleteCsvRowAsync(mainCsvPath, question);
            DeleteModel(mainModelPath);
            _intentService.InvalidateCache();

            // ลบจาก Sub
            string subCsvPath, subModelPath;
            if (domain == "employee")
            {
                subCsvPath = GetPath("FilePaths:EmployeeSubIntentTraining", "file\\Training\\employee_subintent_training_data.csv");
                subModelPath = GetPath("FilePaths:EmployeeSubIntentModel", "file\\Modeling\\employee_subintent_model.zip");
            }
            else
            {
                var dir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
                subCsvPath = Path.Combine(_env.WebRootPath ?? "", dir, $"{domain}_subintent_training_data.csv");
                var modelDir = _config["FilePaths:HrSubIntentModelDir"] ?? "file\\Modeling";
                subModelPath = Path.Combine(_env.WebRootPath ?? "", modelDir, $"{domain}_subintent_training_data.model.zip");
            }

            await DeleteCsvRowAsync(subCsvPath, question);
            DeleteModel(subModelPath);
            if (domain == "employee") _empSubIntentService.InvalidateCache();
            else _hrSubIntentService.InvalidateCache(domain);

            return $"🗑️ ลบข้อมูล `{question}` จาก **คู่ (Main & Sub)** สำเร็จ พร้อมรีโหลดโมเดลแล้วครับ";
        }
        return "❌ รูปแบบไม่ถูกต้อง ลองใช้: `/delete \"ข้อความคำถามที่ต้องการลบ\"`";
    }

    private string HandleViewDomains()
    {
        return @"📋 **รายการ Domain ย่อย (Sub Intents) ที่ระบบรองรับ:**
| Main Intent | Domain Name สำหรับคำสั่ง `/train-sub` |
|---|---|
| HrAttendance | `attendance` |
| HrLeave | `leave` |
| HrMedical | `medical` |
| HrMedicalRegulation | `medical_regulation` |
| HrTraining | `training` |
| HrEmployee | `employee` |
| CsvIntent | `csvintent` |

*ปล. หากใช้คำสั่ง `/train` แบบรวบยอด ระบบจะ Mapping โดเมนเหล่านี้ให้อัตโนมัติครับ*";
    }

    private async Task<string> HandleViewMessagesAsync(string text)
    {
        const int pageSize = 200;
        var skip = ExtractSkip(text);
        var employeeId = ExtractViewMessagesEmployeeId(text);
        var date = DateNormalizer.TryExtractDate(text, out var parsedDate)
            ? parsedDate.Date
            : DateTime.Today;

        var messages = await _chatHistoryService.GetAllMessagesByDateAsync(date, employeeId, skip, pageSize);
        var dateText = date.ToString("dd/MM/yyyy");
        var filterText = string.IsNullOrWhiteSpace(employeeId) ? "All employees" : $"EmployeeId: {employeeId}";

        if (messages.Count == 0)
        {
            if (skip > 0)
            {
                return $"**Messages for {dateText}**\n{filterText}\nNo more messages.";
            }

            return $"**Messages for {dateText}**\n{filterText}\nNo messages found.";
        }

        var result = $"**Messages for {dateText}**\n{filterText}\nShowing {skip + 1}-{skip + messages.Count} (page size {pageSize})\n\n";
        foreach (var message in messages)
        {
            var content = NormalizeMessageForAdminView(message.Content);
            var tokensText = message.TokensUsed.HasValue ? $" / tokens:{message.TokensUsed.Value}" : "";
            result += $"[{message.CreatedAt:HH:mm:ss}] {message.EmployeeId} / {message.Role} / #{message.MessageId}{tokensText}\n{content}\n\n";
        }

        if (messages.Count == pageSize)
        {
            var nextSkip = skip + pageSize;
            var employeePart = string.IsNullOrWhiteSpace(employeeId) ? "" : $" {employeeId}";
            var nextCommand = $"/view-messages {dateText}{employeePart} --skip {nextSkip}";
            result += $"[ADMIN_RELOAD:{nextCommand}]";
        }

        return result.TrimEnd();
    }

    private string HandleTrace(string text)
    {
        // รูปแบบ: /trace "คำถาม" หรือ /trace คำถาม
        var match = Regex.Match(text, @"^/trace\s+""?([^""]+)""?$");
        if (match.Success)
        {
            var question = match.Groups[1].Value.Trim();
            
            // 1. Predict Main Intent
            var mainIntent = _intentService.PredictIntent(question);
            
            // 2. Predict Sub Intent
            string subIntent = "-";
            var domain = mainIntent.ToLowerInvariant().Replace("hr", "");
            
            if (domain == "employee")
            {
                subIntent = _empSubIntentService.PredictSubIntent(question);
            }
            else if (domain == "attendance" || domain == "leave" || domain == "medical" || domain == "medical_regulation")
            {
                subIntent = _hrSubIntentService.PredictSubIntent(domain, question);
            }
            else if (domain == "csvintent")
            {
                subIntent = "Matched CSV Intent Flow";
            }
            else if (mainIntent == "OutOfScope" || mainIntent == "EmpathyIntent" || mainIntent == "AssistantIdentity" || mainIntent == "Summary")
            {
                subIntent = "(ไม่มี Sub Intent สำหรับหมวดนี้)";
            }

            return $@"🔍 **Trace (ตรวจสอบการทำงานของโมเดล):**
- **คำถาม:** `{question}`
- **Main Intent:** `{mainIntent}`
- **Sub Intent ({domain}):** `{subIntent}`";
        }
        return "❌ รูปแบบไม่ถูกต้อง ลองใช้: `/trace \"ข้อความที่ต้องการทดสอบ\"`";
    }

    private static int ExtractSkip(string text)
    {
        var match = Regex.Match(text, @"(?:^|\s)--skip\s+(\d+)(?:\s|$)", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;

        return int.TryParse(match.Groups[1].Value, out var skip)
            ? Math.Max(0, skip)
            : 0;
    }

    private static string? ExtractViewMessagesEmployeeId(string text)
    {
        var explicitMatch = Regex.Match(text, @"(?:^|\s)--(?:employee|emp|id)\s+([A-Za-z0-9_-]+)(?:\s|$)", RegexOptions.IgnoreCase);
        if (explicitMatch.Success)
        {
            return explicitMatch.Groups[1].Value.Trim();
        }

        var cleaned = Regex.Replace(text, @"(?:^|\s)--skip\s+\d+(?:\s|$)", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"(?:^|\s)--(?:employee|emp|id)\s+[A-Za-z0-9_-]+(?:\s|$)", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"(?<=[^\d]|^)\d{1,2}[./-]\d{1,2}[./-]\d{2,4}(?=[^\d]|$)", " ");
        cleaned = Regex.Replace(cleaned, @"^/view-messages\b", " ", RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        var token = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => Regex.IsMatch(value, @"^[A-Za-z0-9_-]+$"));

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string NormalizeMessageForAdminView(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty)";

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return normalized.Length <= 1200
            ? normalized
            : normalized[..1200] + "...";
    }

    private string GetPath(string configKey, string defaultPath)
    {
        var rel = _config[configKey] ?? defaultPath;
        return Path.Combine(_env.WebRootPath ?? "", rel.Replace('/', '\\'));
    }

    private void DeleteModel(string modelPath)
    {
        if (File.Exists(modelPath))
        {
            try { File.Delete(modelPath); } catch { }
        }
    }

    private async Task AppendCsvAsync(string path, string col1, string col2)
    {

        CreateBackup(path);
        if (!File.Exists(path))
        {
            // ถ้ายังไม่มีไฟล์ ให้สร้าง Header (Text,Intent)
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, "Text,Intent\r\n");
        }
        else
        {
            // เช็คว่าไฟล์มีขึ้นบรรทัดใหม่ท้ายสุดไหม ถ้าไม่มีให้เติมก่อน
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                int lastByte = fs.ReadByte();
                if (lastByte != '\n' && lastByte != '\r')
                {
                    fs.Close(); // ปิดก่อนเพื่อไม่ให้ติด lock
                    await File.AppendAllTextAsync(path, "\r\n");
                }
            }
        }
        
        // Escape เครื่องหมาย "
        var safeCol1 = col1.Replace("\"", "\"\"");
        var safeCol2 = col2.Replace("\"", "\"\"");
        var line = $"\"{safeCol1}\",\"{safeCol2}\"\r\n";
        
        await File.AppendAllTextAsync(path, line);
    }

    private async Task<bool> DeleteCsvRowAsync(string path, string targetQuestion)
    {
        if (!File.Exists(path)) return false;
        CreateBackup(path);
        var lines = await File.ReadAllLinesAsync(path);
        if (lines.Length <= 1) return false;

        var newLines = new List<string> { lines[0] }; // Header
        bool deleted = false;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var parts = ParseCsvLine(lines[i]);
            if (parts.Length > 0 && string.Equals(parts[0], targetQuestion, StringComparison.OrdinalIgnoreCase))
            {
                deleted = true;
                continue; // ข้ามบรรทัดนี้เพื่อลบ
            }
            newLines.Add(lines[i]);
        }

        if (deleted)
        {
            await File.WriteAllLinesAsync(path, newLines);
        }
        return deleted;
    }

    private async Task<string> ReadCsvAsync(string path, string title)
    {
        if (!File.Exists(path)) return $"⚠️ ไม่พบไฟล์ข้อมูล `{title}`";

        var lines = await File.ReadAllLinesAsync(path);
        if (lines.Length <= 1) return $"📋 **{title}**\nยังไม่มีข้อมูลในระบบ";

        var result = $"📋 **{title}**\n| Text | Intent |\n|---|---|\n";
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = ParseCsvLine(line);
            if (parts.Length >= 2)
            {
                result += $"| {parts[0]} | {parts[1]} |\n";
            }
        }
        return result;
    }

    private async Task<string> ReadFilteredCsvAsync(string path, string filterIntent, string title)
    {
        if (!File.Exists(path)) return $"⚠️ ไม่พบไฟล์ข้อมูล `{title}`";

        var lines = await File.ReadAllLinesAsync(path);
        if (lines.Length <= 1) return $"📋 **{title} ({filterIntent})**\nยังไม่มีข้อมูลในระบบ";

        var result = $"📋 **{title} (กรองเฉพาะ {filterIntent})**\n| Text | Intent |\n|---|---|\n";
        int count = 0;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = ParseCsvLine(line);
            if (parts.Length >= 2 && string.Equals(parts[1].Trim(), filterIntent, StringComparison.OrdinalIgnoreCase))
            {
                result += $"| {parts[0]} | {parts[1]} |\n";
                count++;
            }
        }

        if (count == 0) return $"📋 **{title} (กรองเฉพาะ {filterIntent})**\nไม่พบข้อมูลคำถามที่ตรงกับ Intent นี้";
        return result;
    }

    private string[] ParseCsvLine(string line)
    {
        var idx = line.IndexOf(',');
        if (idx < 0) return new[] { line };
        var col1 = line[..idx].Trim().Trim('"', '“', '”');
        var col2 = line[(idx + 1)..].Trim().Trim('"', '“', '”').Replace("\\n", "<br>");
        return new[] { col1, col2 };
    }
}
