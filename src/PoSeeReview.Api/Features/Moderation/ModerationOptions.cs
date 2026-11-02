namespace PoSeeReview.Api.Features.Moderation;

/// <summary>
/// Configuration for the moderation slice. Each slice owns its own options type (NET_RULES 2.2).
/// </summary>
public sealed class ModerationOptions
{
    public const string SectionName = "Moderation";

    /// <summary>
    /// Authorization policy name for operator endpoints.
    /// </summary>
    public const string PolicyName = "moderator";

    /// <summary>
    /// Role a principal must hold to reach <c>/api/moderation</c>.
    /// <para>
    /// Not a secret and not in Key Vault: it names a role, it does not grant one. Membership is
    /// what is privileged, and that lives in the identity provider.
    /// </para>
    /// </summary>
    public string RequiredRole { get; set; } = "Moderator";

    /// <summary>
    /// Distinct reporters that auto-hide a comic pending review.
    /// <para>
    /// Three, not one: a single report is a signal, and letting one person unilaterally
    /// unpublish a business's comic is a griefing tool. Three strangers independently flagging
    /// the same comic is evidence worth acting on before a human gets to it.
    /// </para>
    /// </summary>
    public int AutoHideReportThreshold { get; set; } = 3;

    /// <summary>How far back the queue looks.</summary>
    public int QueueWindowDays { get; set; } = 30;

    /// <summary>Maximum rows the queue returns. This is an operator page, not a user path.</summary>
    public int QueueLimit { get; set; } = 100;

    /// <summary>
    /// Rejects a generated narrative that trips the content screen before it reaches the image
    /// model. Off would mean publishing model-written text about a named business unchecked.
    /// </summary>
    public bool ScreenGeneratedContent { get; set; } = true;

    /// <summary>
    /// Azure AI Content Safety endpoint layered under the word lists. The shared AI Services
    /// resource exposes it, and the scorer's key authenticates it. Empty turns it off and leaves
    /// the lexical screen alone.
    /// </summary>
    public string ContentSafetyEndpoint { get; set; } = string.Empty;

    /// <summary>Chat deployment that writes the moderation queue's AI triage. Judgement, so not the nano.</summary>
    public string TriageDeployment { get; set; } = "gpt-5.4-mini";

    /// <summary>
    /// ntfy topic that hears about everything that hid, blocked, refused or softened a comic
    /// (<see cref="OwnerAlerts"/>). A topic name is its own password on ntfy, so it lives in Key
    /// Vault (<c>PoSeeReview--Moderation--AlertNtfyTopic</c>). Empty turns alerts off.
    /// </summary>
    public string AlertNtfyTopic { get; set; } = string.Empty;

    /// <summary>The ntfy server the topic lives on.</summary>
    public string AlertNtfyServer { get; set; } = "https://ntfy.sh";

    /// <summary>The app's public origin, so an alert opens the comic or the queue. Empty sends no link.</summary>
    public string AlertLinkBase { get; set; } = string.Empty;
}
