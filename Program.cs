using HrChatThaiLLM.Server.Hubs;
using HrChatThaiLLM.Server.Services;
using Microsoft.OpenApi.Models;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// ========================================
// 🔧 1. Register Services
// ========================================

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Session (สำหรับ Login) ─────────────────────────────────────────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    var configuredPathBase = builder.Configuration.GetValue<string>("App:PathBase");
    var normalizedPathBase = string.IsNullOrWhiteSpace(configuredPathBase)
        ? string.Empty
        : (configuredPathBase.StartsWith('/') ? configuredPathBase : "/" + configuredPathBase);

    options.IdleTimeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<int>("Security:SessionTimeoutMinutes", 480));
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
    options.Cookie.Path = string.IsNullOrEmpty(normalizedPathBase) ? "/" : normalizedPathBase;
    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
});

// ── SignalR ────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});

// ── CORS ───────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("Content-Disposition"));
});

// ── HttpClient ─────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();

// ── Application Services ───────────────────────────────────────────────────
//builder.Services.AddSingleton<ISqlExecutorService, SqlExecutorService>();
//builder.Services.AddSingleton<IAuthService, AuthService>();
//builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();
//builder.Services.AddSingleton<IAiChatService, AiChatService>();
//builder.Services.AddScoped<IResponseComposer, ResponseComposer>();
builder.Services.AddScoped<ISqlExecutorService, SqlExecutorService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IChatHistoryService, ChatHistoryService>();
builder.Services.AddScoped<IResponseComposer, ResponseComposer>(); // สิทธิ์ประมวลผลตัวใหม่คัดกรองคำหยาบ
builder.Services.AddScoped<IAiChatService, AiChatService>();
builder.Services.AddScoped<IGenderDetectorService, GenderDetectorService>();
builder.Services.AddScoped<IOutOfScopeResponseService, OutOfScopeResponseService>();
builder.Services.AddScoped<IThankYouResponses, ThankYouResponses>();
builder.Services.AddSingleton<IPromptChoiceRouter, PromptChoiceRouter>();

// ── Chat Summary Service (สำหรับสรุปประวัติการแชท) ────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IChatSummaryService, ChatSummaryService>();


// ========================================
// 🏗️ 2. Build Application
// ========================================
var app = builder.Build();

// ── Initialize AI Service ──────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var aiService = scope.ServiceProvider.GetRequiredService<IAiChatService>();
        _ = aiService.InitializeAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "⚠️ Failed to initialize AI Service");
    }
}

// ========================================
// ⚙️ 3. Middleware Pipeline
// ========================================
var appPathBase = builder.Configuration.GetValue<string>("App:PathBase");
if (!string.IsNullOrWhiteSpace(appPathBase))
{
    if (!appPathBase.StartsWith('/'))
    {
        appPathBase = "/" + appPathBase;
    }
    app.UsePathBase(appPathBase);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opt =>
    {
        //opt.SwaggerEndpoint("/swagger/v1/swagger.json", "HR Chat API v1");
        opt.SwaggerEndpoint("/swagger/v1/swagger.json", "HR Chat API v1");
        opt.RoutePrefix = "swagger";


    });
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("AllowAll");
app.UseSession();          // ← ต้องมาก่อน UseAuthorization
app.UseAuthorization();

// ========================================
// 🗺️ 4. Endpoints
// ========================================
app.MapRazorPages();
app.MapControllers();
app.MapHub<ChatHub>("/chathub");

// Default route → Login
app.MapGet("/", context =>
{
    context.Response.Redirect($"{context.Request.PathBase}/Login");
    return Task.CompletedTask;
});

// ========================================
// 🎯 5. Auto-open Browser (Dev only)
// ========================================
if (app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault(u => u.StartsWith("https")) ?? app.Urls.FirstOrDefault() ?? "https://localhost:7001";
        var loginUrl = $"{url}/Login";
        var chatUrl = $"{url}/Chat";
        var swagger = $"{url}/swagger/index.html";

        Console.WriteLine($"""

╔══════════════════════════════════════════════╗
║  🎉  HR Chat ThaiLLM Server Started!         ║
╠══════════════════════════════════════════════╣
║  🔐 Login:    {loginUrl,-35}║
║  💬 Chat:     {chatUrl,-35}║
║  📋 Swagger:  {swagger,-35}║
║  🔗 SignalR:  {url}/chathub                  ║
╚══════════════════════════════════════════════╝
""");
        OpenBrowser(loginUrl);
    });
}

app.Logger.LogInformation("🚀 HR Chat Server starting...");
app.Run();

// ── Helper ─────────────────────────────────────────────────────────────────
static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine($"\n🌐 โปรดเปิดเบราว์เซอร์เองที่: {url}\n");
    }
}
