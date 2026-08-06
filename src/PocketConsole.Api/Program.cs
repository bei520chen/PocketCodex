using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PocketConsole.Api.Data;
using PocketConsole.Api.Hubs;
using PocketConsole.Api.Options;
using PocketConsole.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var dataProtectionDirectory = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".runtime", "data-protection"));
Directory.CreateDirectory(dataProtectionDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory))
    .SetApplicationName("PocketConsole");

builder.Services.Configure<CodexOptions>(builder.Configuration.GetSection(CodexOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddSingleton<CodexExecutableResolver>();
builder.Services.AddSingleton<CodexAppServerClient>();
builder.Services.AddSingleton<CodexThreadService>();
builder.Services.AddSingleton<AttachmentService>();
builder.Services.AddSingleton<WeChatInstanceRegistry>();
builder.Services.AddSingleton<WeChatWindowCoordinator>();
builder.Services.AddSingleton<CodexNotificationDispatcher>();
builder.Services.AddSingleton<WeChatMonitorService>();
builder.Services.AddSingleton<WeChatReplyService>();
builder.Services.AddDbContext<PocketConsoleDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("PocketConsole")));
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<TaskExecutionService>();
builder.Services.AddSingleton<WorkspaceGuard>();
builder.Services.AddSingleton<PasswordVerifier>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "PocketConsole.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var connectionString = app.Configuration.GetConnectionString("PocketConsole") ?? throw new InvalidOperationException("PocketConsole database connection is not configured.");
    var connectionBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(connectionBuilder.DataSource))
    {
        var databasePath = Path.GetFullPath(connectionBuilder.DataSource, app.Environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    }
    var dbContext = scope.ServiceProvider.GetRequiredService<PocketConsoleDbContext>();
    dbContext.Database.EnsureCreated();
    dbContext.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Projects (
            Id TEXT NOT NULL CONSTRAINT PK_Projects PRIMARY KEY,
            Name TEXT NOT NULL,
            WorkingDirectory TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Projects_WorkingDirectory ON Projects (WorkingDirectory);
        CREATE INDEX IF NOT EXISTS IX_Projects_UpdatedAt ON Projects (UpdatedAt);
        """);
}

var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

PocketConsole.Api.ApiEndpoints.Map(app);
app.MapHub<CodexHub>("/hubs/codex").RequireAuthorization();

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;

