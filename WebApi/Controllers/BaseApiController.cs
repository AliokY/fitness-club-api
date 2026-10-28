using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = "admin";

    protected long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected bool IsAdmin => User.IsInRole(AdminRole);
}
