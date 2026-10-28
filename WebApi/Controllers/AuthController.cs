using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApi.Auth;

namespace WebApi.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController(
    IUnitOfWork unitOfWork,
    AccessTokenService accessTokenService,
    IPasswordHasher<User> passwordHasher) : ControllerBase
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly AccessTokenService _accessTokenService = accessTokenService;
    private readonly IPasswordHasher<User> _passwordHasher = passwordHasher;

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await _unitOfWork
            .GetRepository<User>()
            .Query()
            .FirstOrDefaultAsync(x => x.Email == request.Email, ct);

        if (user == null)
        {
            return Unauthorized();
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            return Unauthorized();
        }

        return Ok(_accessTokenService.Create(user));
    }
}
