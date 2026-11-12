namespace PoSeeReview.Client.Layout;

/// <summary>Account display helpers for the navigation bar and account popover.</summary>
public static class AccountMenu
{
    public static string DisplayName(string? name) => string.IsNullOrWhiteSpace(name) ? "User" : name;
}
