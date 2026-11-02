using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Unit.Services;

/// <summary>The owner hears about every hide, block and refusal, and nothing leaves without a topic.</summary>
public sealed class OwnerAlertsTests
{
    [Fact]
    public async Task Send_PostsTopicTitleAndComicLink_AndStaysSilentWithoutATopic()
    {
        var handler = new CapturingHandler();
        var factory = new SingleClientFactory(new HttpClient(handler));

        new OwnerAlerts(factory, Options.Create(new ModerationOptions()), NullLogger<OwnerAlerts>.Instance)
            .Send("Comic blocked", "blocked: prohibited", PlaceId.From("ChIJabc"), urgent: true);

        var alerts = new OwnerAlerts(factory, Options.Create(new ModerationOptions
        {
            AlertNtfyTopic = "posee-test",
            AlertNtfyServer = "https://ntfy.example/",
            AlertLinkBase = "https://app.example"
        }), NullLogger<OwnerAlerts>.Instance);
        alerts.Send("Café blocked", "blocked: prohibited", PlaceId.From("ChIJabc"), urgent: true);

        var body = JsonDocument.Parse(await handler.Body.Task.WaitAsync(TimeSpan.FromSeconds(5))).RootElement;
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://ntfy.example/", handler.Uri!.ToString());
        Assert.Equal("posee-test", body.GetProperty("topic").GetString());
        Assert.Equal("Café blocked", body.GetProperty("title").GetString());
        Assert.Equal(4, body.GetProperty("priority").GetInt32());
        Assert.Equal("https://app.example/comic/ChIJabc", body.GetProperty("click").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<string> Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Body.TrySetResult(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
