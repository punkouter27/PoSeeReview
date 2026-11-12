#:project ../src/PoSeeReview.Api/PoSeeReview.Api.csproj
#:property PublishAot=false

// Golden-set eval for the strangeness analysis prompt. Runs every case in prompt-eval.json
// through the REAL chat service classes (same prompt, same parse, same score formula) several
// times and reports what a prompt change should be judged on: does the score land in the
// expected band, how much does it wobble run to run, does every panel come back with its own
// scene and cite a review it was shown, and how often does the image backstop still blunt a word.
//
// --judge adds a second opinion on the writing itself: a stronger model grades each case's first
// script for humour, drawability and faithfulness to the reviews, and counts invented facts. It
// needs the Azure endpoint and key even when the scorer under test is Ollama.
//
//   dotnet run SCRIPTS/prompt-eval.cs                                 # Ollama, configured model
//   dotnet run SCRIPTS/prompt-eval.cs -- --model gemma3:4b --runs 3
//   $env:AZURE_OPENAI_ENDPOINT = "..."; $env:AZURE_OPENAI_API_KEY = "..."
//   dotnet run SCRIPTS/prompt-eval.cs -- --provider azure              # paid, ~$0.0005 per call
//   dotnet run SCRIPTS/prompt-eval.cs -- --provider azure --judge gpt-5.4-mini [--verbose]
//
// Not a test: it calls a model, and its verdict is a spread, not a pass/fail.

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Telemetry;

var provider = Arg("--provider") ?? "ollama";
var runs = int.Parse(Arg("--runs") ?? "3");
var judgeDeployment = Arg("--judge");
var casesPath = Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "SCRIPTS", "prompt-eval.json");
var cases = JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(casesPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var telemetry = new TelemetryClient(new TelemetryConfiguration());
var costs = new AiCostTracker(Options.Create(new AiPricingOptions()), telemetry);

AzureOpenAIClient? azure = null;
AzureOpenAIClient AzureClient() => azure ??= new AzureOpenAIClient(
    new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT")),
    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY")));

IChatCompletionService chat;
string label;
if (provider == "azure")
{
    var options = new AzureOpenAIOptions
    {
        Endpoint = "set-by-client",
        ApiKey = "set-by-client",
        DeploymentName = Arg("--model") ?? new AzureOpenAIOptions().DeploymentName
    };
    chat = new AzureOpenAIChatService(AzureClient(), Options.Create(options), telemetry, costs);
    label = $"azure/{options.DeploymentName}";
}
else
{
    var options = new OllamaOptions { ChatModel = Arg("--model") ?? new OllamaOptions().ChatModel };
    chat = new OllamaChatService(Options.Create(options), NullLogger<OllamaChatService>.Instance, costs);
    label = $"ollama/{options.ChatModel}";
}

Console.WriteLine($"{label}, {cases.Count} cases x {runs} runs{(judgeDeployment is null ? "" : $", judged by {judgeDeployment}")}\n");
Console.WriteLine($"{"case",-24} {"band",-8} {"scores",-16} {"spread",6} {"in band",8}");

int inBand = 0, failures = 0, calls = 0, ownScenes = 0, panels = 0, grounded = 0, blunted = 0;
var spreads = new List<int>();
var verdicts = new List<JudgeVerdict>();

foreach (var c in cases)
{
    var scores = new List<int>();
    StrangenessAnalysis? first = null;
    foreach (var _ in Enumerable.Range(0, runs))
    {
        calls++;
        try
        {
            var a = await chat.AnalyzeStrangenessAsync([.. c.Reviews]);
            first ??= a;
            var captions = ChatPrompts.NormalizeCaptions(a.Captions, a.Narrative, a.PanelCount);
            var scenes = ChatPrompts.NormalizeScenes(a.Scenes, captions, a.PanelCount);
            scores.Add(a.StrangenessScore);
            panels += a.PanelCount;
            ownScenes += scenes.Where((s, i) => s != captions[i]).Count(); // a scene that is not the caption fallback
            grounded += a.PanelCount - ChatPrompts.CountUngroundedPanels(a);
            blunted += ComicImagePrompt.Build(scenes).BluntedTerms.Count;
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  {c.Name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    if (scores.Count == 0) continue;
    var mean = scores.Average();
    var hit = mean >= c.Expect[0] && mean <= c.Expect[1];
    inBand += hit ? 1 : 0;
    spreads.Add(scores.Max() - scores.Min());
    Console.WriteLine($"{c.Name,-24} {$"{c.Expect[0]}-{c.Expect[1]}",-8} {string.Join(",", scores),-16} {spreads[^1],6} {(hit ? "yes" : "NO"),8}");

    if (judgeDeployment is not null && first is not null)
    {
        try
        {
            var verdict = await JudgeAsync(judgeDeployment, c, first);
            verdicts.Add(verdict);
            Console.WriteLine($"  judge: funny {verdict.Funny}/5, drawable {verdict.Drawable}/5, faithful {verdict.Faithful}/5, invented {verdict.InventedFacts}");
            if (args.Contains("--verbose"))
            {
                Console.WriteLine($"    narrative: {first.Narrative}");
                for (var i = 0; i < first.PanelCount; i++)
                    Console.WriteLine($"    panel {i + 1}: {first.Scenes?.ElementAtOrDefault(i)} | {first.Captions?.ElementAtOrDefault(i)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  judge failed: {ex.Message.Split('\n')[0]}");
        }
    }
}

Console.WriteLine($"""

in band        {inBand}/{cases.Count}
mean spread    {(spreads.Count > 0 ? spreads.Average() : 0):0.0} points (run-to-run wobble; lower is steadier)
parse failures {failures}/{calls}
own scenes     {ownScenes}/{panels} panels (the rest fell back to the caption)
grounded       {grounded}/{panels} panels cite a review they were shown
blunted terms  {blunted} across all runs (the image backstop still rewriting words)
""");

if (verdicts.Count > 0)
{
    Console.WriteLine($"""
judge (n={verdicts.Count})
  funny        {verdicts.Average(v => v.Funny):0.0}/5
  drawable     {verdicts.Average(v => v.Drawable):0.0}/5
  faithful     {verdicts.Average(v => v.Faithful):0.0}/5
  invented     {verdicts.Sum(v => v.InventedFacts)} facts across all cases (want 0)
""");
}

async Task<JudgeVerdict> JudgeAsync(string deployment, EvalCase c, StrangenessAnalysis a)
{
    const string system = """
You grade a script for a short wordless comic generated from restaurant reviews. Be strict and consistent.
- funny 1-5: would the strip land as a visual joke?
- drawable 1-5: could an illustrator draw each scene from its one sentence, with no words in the picture?
- faithful 1-5: does the script stay true to what the reviews report (exaggerating the look is fine, new events are not)?
- inventedFacts: how many events, claims or accusations appear in the script that no review states.
Return JSON only.
""";
    var reviews = string.Join("\n", c.Reviews.Select((r, i) => $"<review id=\"{i + 1}\">{r}</review>"));
    var script = string.Join("\n", Enumerable.Range(0, a.PanelCount).Select(i =>
        $"panel {i + 1}: scene: {a.Scenes?.ElementAtOrDefault(i)} | caption: {a.Captions?.ElementAtOrDefault(i)}"));

    var schema = BinaryData.FromString("""
{"type":"object","additionalProperties":false,"required":["funny","drawable","faithful","inventedFacts"],
 "properties":{"funny":{"type":"integer"},"drawable":{"type":"integer"},"faithful":{"type":"integer"},"inventedFacts":{"type":"integer"}}}
""");

    var completion = (await AzureClient().GetChatClient(deployment).CompleteChatAsync(
        [new SystemChatMessage(system), new UserChatMessage($"<reviews>\n{reviews}\n</reviews>\nnarrative: {a.Narrative}\n{script}")],
        new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("judge", schema, jsonSchemaIsStrict: true) })).Value;

    return JsonSerializer.Deserialize<JudgeVerdict>(completion.Content[0].Text)!;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

record EvalCase(string Name, int[] Expect, string[] Reviews);

record JudgeVerdict(
    [property: JsonPropertyName("funny")] int Funny,
    [property: JsonPropertyName("drawable")] int Drawable,
    [property: JsonPropertyName("faithful")] int Faithful,
    [property: JsonPropertyName("inventedFacts")] int InventedFacts);
