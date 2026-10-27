using System.Security.Claims;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Identity;

/// <summary>
/// The caller's identity, read only from the authenticated principal. Dev and Test get theirs the
/// same way — <c>FakeAuthHandler</c> turns <c>X-Fake-User</c> into a principal — so there is no
/// second, header-based path to spoof.
/// </summary>
public class HttpContextRequestIdentityAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentRequestIdentityAccessor
{
    public UserId GetCurrentUserId()
    {
        var user = httpContextAccessor.HttpContext?.User;
        return user is null
            ? UserId.Anonymous
            : UserId.From(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name);
    }

    public string? GetCurrentUserEmail()
    {
        var user = httpContextAccessor.HttpContext?.User;
        return user?.FindFirstValue(ClaimTypes.Email) ?? user?.FindFirstValue("email");
    }
}
