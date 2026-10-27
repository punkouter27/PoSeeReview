using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PoSeeReview.Api.Identity;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Unit.Utilities;

public class HttpContextRequestIdentityAccessorTests
{
    [Fact]
    public void GetCurrentUserId_ReturnsAnonymous_WhenNoContextOrIdentity()
    {
        var accessor = new HttpContextRequestIdentityAccessor(new HttpContextAccessor());

        Assert.Equal(UserId.From("anonymous"), accessor.GetCurrentUserId());
    }

    /// <summary>
    /// A client-supplied header must never set the identity. The dev-only X-Dev-User-Id override
    /// was removed; this pins that it stays gone.
    /// </summary>
    [Fact]
    public void GetCurrentUserId_UsesTheAuthenticatedPrincipal_AndIgnoresIdentityHeaders()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "real-user")], "cookie"))
        };
        context.Request.Headers["X-Dev-User-Id"] = "spoofed-user";
        context.Request.Headers["X-Dev-UserId"] = "spoofed-user";
        var accessor = new HttpContextRequestIdentityAccessor(new HttpContextAccessor { HttpContext = context });

        Assert.Equal(UserId.From("real-user"), accessor.GetCurrentUserId());
    }
}
