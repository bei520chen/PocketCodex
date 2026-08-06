using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PocketConsole.Api.Services;
using PocketConsole.Api.Models;

namespace PocketConsole.Api;

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/auth/status", (HttpContext context) =>
            Results.Ok(new AuthStatusRo(context.User.Identity?.IsAuthenticated == true)));

        app.MapPost("/api/auth/login", async (LoginVo vo, PasswordVerifier verifier, HttpContext context) =>
        {
            if (!verifier.Verify(vo.Password)) return Results.Unauthorized();
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new AuthStatusRo(true));
        }).RequireRateLimiting("login");

        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        var api = app.MapGroup("/api").RequireAuthorization();


        api.MapPost("/attachments", async (HttpRequest request, AttachmentService service, CancellationToken token) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "请使用 multipart/form-data 上传附件。" });
            var form = await request.ReadFormAsync(token);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "请选择附件。" });
            try { return Results.Created("/api/attachments", await service.SaveAsync(file, token)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).DisableAntiforgery();

        api.MapGet("/host/status", async (CodexAppServerClient client, CancellationToken token) =>
        {
            try { return Results.Ok(await client.GetStatusAsync(token)); }
            catch (Exception exception) { return Results.Problem(exception.Message, statusCode: 503); }
        });

        api.MapGet("/wechat-monitor/status", (WeChatMonitorService service) => Results.Ok(service.GetStatus()));

        api.MapPost("/wechat-monitor/start", async (StartWeChatMonitorVo vo, WeChatMonitorService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.StartAsync(vo.ThreadId, vo.IntervalSeconds, token)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
            catch (Exception exception) { return Results.Problem(exception.Message, statusCode: 502); }
        });

        api.MapPost("/wechat-monitor/stop", async (WeChatMonitorService service) => Results.Ok(await service.StopAsync()));

        api.MapPost("/wechat-monitor/refresh-instances", async (WeChatMonitorService service, CancellationToken token) =>
            Results.Ok(await service.RefreshInstancesAsync(token)));

        api.MapPost("/wechat-monitor/refresh-profile-names", async (WeChatMonitorService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.RefreshProfileNamesAsync(token)); }
            catch (Exception exception) { return Results.Problem(exception.Message, statusCode: 502); }
        });

        api.MapPost("/wechat-monitor/instances/{id}/name", (string id, RenameWeChatInstanceVo vo, WeChatMonitorService service) =>
        {
            try { return Results.Ok(service.RenameInstance(id, vo.Name)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        api.MapGet("/sessions", async (CodexThreadService service, string? search, string? cwd, bool archived = false, int limit = 50, CancellationToken token = default) =>
            Results.Ok(await service.ListSessionsAsync(search, cwd, archived, Math.Clamp(limit, 1, 100), token)));

        api.MapPost("/sessions", async (CreateSessionVo vo, CodexThreadService service, CancellationToken token) =>
        {
            try { return Results.Created("/api/sessions", await service.CreateSessionAsync(vo.ProjectPath, vo.Message, vo.AttachmentIds, token)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (DirectoryNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (UnauthorizedAccessException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        api.MapGet("/sessions/{threadId}/summary", async (string threadId, CodexThreadService service, CancellationToken token) =>
        {
            var session = await service.ReadSessionSummaryAsync(threadId, token);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        api.MapGet("/sessions/{threadId}/content", async (string threadId, CodexThreadService service, CancellationToken token) =>
        {
            var session = await service.ReadSessionContentAsync(threadId, token);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        api.MapGet("/sessions/{threadId}", async (string threadId, CodexThreadService service, CancellationToken token) =>
        {
            var session = await service.ReadSessionAsync(threadId, token);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        api.MapPost("/sessions/{threadId}/messages", async (string threadId, SendSessionMessageVo vo, CodexThreadService service, CancellationToken token) =>
        {
            try
            {
                var session = await service.SendMessageAsync(threadId, vo.Message, vo.AttachmentIds, token);
                return session is null ? Results.NotFound() : Results.Ok(session);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (DirectoryNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (UnauthorizedAccessException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        api.MapGet("/projects", async (ProjectService service, string? search, CancellationToken token) =>
            Results.Ok(await service.ListAsync(search, token)));

        api.MapGet("/projects/roots", (WorkspaceGuard guard) => Results.Ok(guard.GetRoots()));

        api.MapPost("/projects", async (CreateProjectVo vo, ProjectService service, CancellationToken token) =>
        {
            try { return Results.Created("/api/projects", await service.CreateAsync(vo, token)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (DirectoryNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (UnauthorizedAccessException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        api.MapGet("/tasks", async (TaskService service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(token)));

        api.MapGet("/tasks/{id:guid}", async (Guid id, TaskService service, CancellationToken token) =>
        {
            var task = await service.GetAsync(id, token);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        api.MapPost("/tasks", async (CreateTaskVo vo, TaskService service, CancellationToken token) =>
        {
            try
            {
                var task = await service.CreateAsync(vo, token);
                return Results.Created($"/api/tasks/{task.Id}", task);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (DirectoryNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (UnauthorizedAccessException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden); }
        });

        api.MapPatch("/tasks/{id:guid}", async (Guid id, UpdateTaskVo vo, TaskService service, CancellationToken token) =>
        {
            try
            {
                var task = await service.UpdateAsync(id, vo, token);
                return task is null ? Results.NotFound() : Results.Ok(task);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (DirectoryNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (UnauthorizedAccessException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden); }
        });

        api.MapDelete("/tasks/{id:guid}", async (Guid id, TaskService service, CancellationToken token) =>
            await service.DeleteAsync(id, token) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/tasks/{id:guid}/start", async (Guid id, TaskExecutionService service, CancellationToken token) =>
        {
            try
            {
                var task = await service.StartAsync(id, token);
                return task is null ? Results.NotFound() : Results.Ok(task);
            }
            catch (Exception exception) { return Results.Problem(exception.Message, statusCode: 502); }
        });

        api.MapPost("/tasks/{id:guid}/messages", async (Guid id, SendTaskMessageVo vo, TaskExecutionService service, CancellationToken token) =>
        {
            try
            {
                var task = await service.SendMessageAsync(id, vo.Message, token);
                return task is null ? Results.NotFound() : Results.Ok(task);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        api.MapPost("/tasks/{id:guid}/interrupt", async (Guid id, TaskExecutionService service, CancellationToken token) =>
        {
            try
            {
                var task = await service.InterruptAsync(id, token);
                return task is null ? Results.NotFound() : Results.Ok(task);
            }
            catch (Exception exception) { return Results.Problem(exception.Message, statusCode: 502); }
        });
    }
}
