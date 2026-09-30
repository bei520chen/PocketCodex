namespace PocketConsole.Api.Models;

public sealed record LoginVo(string Password);

public sealed record AuthStatusRo(bool Authenticated);
