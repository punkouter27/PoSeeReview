namespace PoSeeReview.Client.Services;

/// <summary>
/// Optional dev/test delegating handler attaching <c>X-Fake-User</c> (and <c>X-Fake-Roles</c>)
/// when a non-production dev switcher overrides the active persona.
/// </summary>
public sealed class DevFakeUserHandler(Func<(string? User, string? Roles)> getPersona) : DelegatingHandler
{
    public const string UserHeader = "X-Fake-User";
    public const string RolesHeader = "X-Fake-Roles";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (user, roles) = getPersona();
        if (!string.IsNullOrWhiteSpace(user))
        {
            request.Headers.Remove(UserHeader);
            request.Headers.TryAddWithoutValidation(UserHeader, user);

            if (!string.IsNullOrWhiteSpace(roles))
            {
                request.Headers.Remove(RolesHeader);
                request.Headers.TryAddWithoutValidation(RolesHeader, roles);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
