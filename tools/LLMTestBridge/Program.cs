using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
const string ChatEndpoint = "/chat/completions";
const string ModelsEndpoint = "/models";

var cli = CliOptions.Parse(args);
if (cli.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

if (!string.IsNullOrWhiteSpace(cli.Error))
{
    Console.Error.WriteLine($"[error] {cli.Error}");
    CliOptions.PrintHelp();
    return 2;
}

var apiKey = string.IsNullOrWhiteSpace(cli.ApiKey)
    ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
    : cli.ApiKey;

if (!string.IsNullOrWhiteSpace(cli.ModConfigPath))
{
    var cfgValues = TryReadBepInExConfig(cli.ModConfigPath!);
    if (cfgValues.TryGetValue("ApiKey", out var cfgApiKey) && !string.IsNullOrWhiteSpace(cfgApiKey) && string.IsNullOrWhiteSpace(apiKey))
        apiKey = cfgApiKey;

    if (cfgValues.TryGetValue("Model", out var cfgModel) && !string.IsNullOrWhiteSpace(cfgModel) && string.IsNullOrWhiteSpace(cli.ModelOverrideRaw))
        cli.Model = cfgModel;

    if (cfgValues.TryGetValue("OpenRouterDataCollectionMode", out var cfgDataCollection) && !string.IsNullOrWhiteSpace(cfgDataCollection) && string.IsNullOrWhiteSpace(cli.DataCollectionOverrideRaw))
        cli.DataCollectionMode = NormalizeDataCollectionMode(cfgDataCollection);

    if (cfgValues.TryGetValue("OpenRouterEnableProviderOverride", out var cfgProviderOverride) && bool.TryParse(cfgProviderOverride, out var enableProviderOverride))
        cli.EnableProviderOverride = enableProviderOverride;
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("[error] Missing API key. Use --api-key or set OPENROUTER_API_KEY.");
    return 2;
}

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};

httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/goingmedieval/llm-npcs");
httpClient.DefaultRequestHeaders.Add("X-Title", "Going Medieval LLM NPCs");

var preflightStopwatch = Stopwatch.StartNew();
var modelValidation = await ValidatePrimaryModelPreflightAsync(httpClient, cli.Model);
preflightStopwatch.Stop();

Console.WriteLine("== Preflight ==");
Console.WriteLine($"model: {cli.Model}");
Console.WriteLine($"primary_model_only: true");
Console.WriteLine($"models_endpoint: {OpenRouterBaseUrl + ModelsEndpoint}");
Console.WriteLine($"preflight_ms: {preflightStopwatch.ElapsedMilliseconds}");
Console.WriteLine($"mod_config_path: {cli.ModConfigPath ?? "<none>"}");

if (!modelValidation.Exists)
{
    Console.WriteLine($"preflight_result: invalid (model-not-found)");
    Console.WriteLine($"nearest_models: {(modelValidation.NearestSuggestions.Count > 0 ? string.Join(", ", modelValidation.NearestSuggestions) : "<none>")}");
    Console.WriteLine("diagnostic_class: model-not-found");
    return 3;
}

Console.WriteLine("preflight_result: valid");

var messages = await BuildMessagesAsync(cli);
var requestBody = BuildRequestBody(messages, cli.Mode == RequestMode.Decision ? 512 : 150, cli.Model, cli.DataCollectionMode, cli.EnableProviderOverride);
var requestJson = requestBody.ToString(Formatting.None);

if (cli.Mode == RequestMode.Decision)
{
    var contextValidation = ValidateDecisionContext(messages);
    Console.WriteLine();
    Console.WriteLine("== Context Validation ==");
    Console.WriteLine($"issues_count: {contextValidation.Count}");
    foreach (var issue in contextValidation)
        Console.WriteLine($"- {issue}");
}

Console.WriteLine();
Console.WriteLine("== Request Metadata ==");
Console.WriteLine($"endpoint: {OpenRouterBaseUrl + ChatEndpoint}");
Console.WriteLine($"mode: {cli.Mode.ToString().ToLowerInvariant()}");
Console.WriteLine($"model: {cli.Model}");
Console.WriteLine($"data_collection: {NormalizeDataCollectionMode(cli.DataCollectionMode)}");
Console.WriteLine($"authorization: Bearer {MaskApiKey(apiKey)}");
Console.WriteLine($"messages_count: {messages.Count}");
Console.WriteLine($"payload_chars: {requestJson.Length}");

Console.WriteLine("=== GENERATED SYSTEM PROMPT ===");
Console.WriteLine(messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty);
Console.WriteLine("===============================");

using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
var requestStopwatch = Stopwatch.StartNew();
var response = await httpClient.PostAsync(OpenRouterBaseUrl + ChatEndpoint, content);
var responseBody = await response.Content.ReadAsStringAsync();
requestStopwatch.Stop();

var errorText = ExtractApiErrorText(responseBody);
var classification = ClassifyPrimaryFailure(response.StatusCode, responseBody, errorText);

Console.WriteLine();
Console.WriteLine("== Raw HTTP Response ==");
Console.WriteLine($"status: {(int)response.StatusCode} ({response.StatusCode})");
Console.WriteLine("body:");
Console.WriteLine(responseBody);

Console.WriteLine();
Console.WriteLine("== Diagnostics ==");
Console.WriteLine($"normalized_class: {ToNormalizedDiagnosticClass(classification)}");
Console.WriteLine($"latency_ms: {requestStopwatch.ElapsedMilliseconds}");
Console.WriteLine($"success: {response.IsSuccessStatusCode}");

return response.IsSuccessStatusCode ? 0 : 4;

static JObject BuildRequestBody(List<Message> messages, int maxTokens, string model, string dataCollectionMode, bool enableProviderOverride)
{
    var obj = new JObject
    {
        ["model"] = model,
        ["messages"] = JArray.FromObject(messages),
        ["max_tokens"] = maxTokens,
        ["temperature"] = 0.7
    };
    if (enableProviderOverride)
    {
        obj["provider"] = new JObject
        {
            ["data_collection"] = NormalizeDataCollectionMode(dataCollectionMode)
        };
    }
    return obj;
}

static string NormalizeDataCollectionMode(string? mode)
{
    var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
    return normalized == "deny" ? "deny" : "allow";
}

static string MaskApiKey(string apiKey)
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return "<missing>";

    if (apiKey.Length <= 10)
        return "***";

    return $"{apiKey.Substring(0, 6)}...{apiKey.Substring(apiKey.Length - 4)}";
}

static async Task<List<Message>> BuildMessagesAsync(CliOptions options)
{
    if (options.Mode == RequestMode.Chat)
    {
        return new List<Message>
        {
            new() { Role = "system", Content = "You are a character in a medieval colony simulation game. Respond in character." },
            new() { Role = "user", Content = options.Message ?? string.Empty }
        };
    }

    NPCContext? context = null;
    if (!string.IsNullOrWhiteSpace(options.ContextFile))
    {
        var json = await File.ReadAllTextAsync(options.ContextFile!);
        context = JsonConvert.DeserializeObject<NPCContext>(json);
    }

    context ??= BuildMinimalContext(options.Message);

    if (!string.IsNullOrWhiteSpace(options.MemoryFile) && File.Exists(options.MemoryFile))
    {
        context.MemoryContext = await File.ReadAllTextAsync(options.MemoryFile);
    }
    else if (!string.IsNullOrWhiteSpace(options.MemoryText))
    {
        context.MemoryContext = options.MemoryText;
    }

    List<Message>? history = null;
    if (!string.IsNullOrWhiteSpace(options.HistoryFile) && File.Exists(options.HistoryFile))
    {
        var historyJson = await File.ReadAllTextAsync(options.HistoryFile);
        history = JsonConvert.DeserializeObject<List<Message>>(historyJson);
    }

    if (options.Mode == RequestMode.NpcChat)
    {
        return new PromptBuilder().BuildNpcChatPrompt(context, history, options.Message);
    }

    return new PromptBuilder().BuildDecisionPrompt(context, history: history);
}

static Dictionary<string, string> TryReadBepInExConfig(string configPath)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        if (!File.Exists(configPath))
            return values;

        foreach (var line in File.ReadLines(configPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("["))
                continue;

            var idx = trimmed.IndexOf('=');
            if (idx <= 0 || idx >= trimmed.Length - 1)
                continue;

            var key = trimmed.Substring(0, idx).Trim();
            var value = trimmed.Substring(idx + 1).Trim();
            values[key] = value;
        }
    }
    catch
    {
        // ignore parse failures; bridge remains usable with explicit cli/env inputs
    }

    return values;
}

static List<string> ValidateDecisionContext(List<Message> messages)
{
    var issues = new List<string>();
    if (messages == null || messages.Count < 2)
    {
        issues.Add("messages should contain at least system + user message");
        return issues;
    }

    var user = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
    if (user == null)
    {
        issues.Add("missing user message");
        return issues;
    }

    var content = user.Content ?? string.Empty;
    if (!content.Contains("Identity:", StringComparison.OrdinalIgnoreCase))
        issues.Add("missing identity block in state prompt");
    if (!content.Contains("Needs:", StringComparison.OrdinalIgnoreCase))
        issues.Add("missing needs block in state prompt");
    if (!content.Contains("Environment:", StringComparison.OrdinalIgnoreCase))
        issues.Add("missing environment block in state prompt");

    return issues;
}

static NPCContext BuildMinimalContext(string? message)
{
    var text = string.IsNullOrWhiteSpace(message) ? "No extra instruction provided." : message;

    return new NPCContext
    {
        Id = "cli-npc-001",
        Name = "Aldric",
        Age = 31,
        Gender = "male",
        BackgroundOrRole = "woodcutter",
        Mood = "focused",
        MoodScore = 62,
        Needs = new NeedsContext
        {
            Food = 58,
            Water = 61,
            Rest = 49,
            Recreation = 40,
            Comfort = 50,
            Beauty = 37,
            Privacy = 45
        },
        Environment = new EnvironmentContext
        {
            Room = "stockpile",
            TimeOfDay = "afternoon",
            Weather = "light_rain",
            NearbyThreats = new List<string>()
        },
        CurrentActivity = new ActivityContext
        {
            Type = "haul",
            Description = text,
            Target = "oak_logs",
            Progress = 0.35f
        },
        MemoryContext = "Recent memory: Worked with Branna to move logs before rain.",
        Skills = new Dictionary<string, int>
        {
            ["woodcutting"] = 13,
            ["construction"] = 8
        },
        Traits = new List<string> { "hardworking", "reserved" },
        Inventory = new List<string> { "iron_axe", "smoked_meat" }
    };
}

static async Task<ModelValidationResult> ValidatePrimaryModelPreflightAsync(HttpClient httpClient, string model)
{
    try
    {
        var response = await httpClient.GetAsync(OpenRouterBaseUrl + ModelsEndpoint);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return ModelValidationResult.FromPreflightUnavailable();

        var parsed = JObject.Parse(json);
        var data = parsed["data"] as JArray;
        if (data == null)
            return ModelValidationResult.FromPreflightUnavailable();

        var models = data
            .Select(m => m?["id"]?.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();

        if (models.Any(x => string.Equals(x, model, StringComparison.OrdinalIgnoreCase)))
            return ModelValidationResult.Valid();

        var nearest = GetNearestModelIds(models, model, 3);
        return ModelValidationResult.Invalid(nearest);
    }
    catch
    {
        return ModelValidationResult.FromPreflightUnavailable();
    }
}

static List<string> GetNearestModelIds(List<string> models, string targetModelId, int maxCount)
{
    var target = (targetModelId ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(target) || models.Count == 0)
        return new List<string>();

    return models
        .Select(id => new { Id = id, Score = ComputeLevenshteinDistance(target.ToLowerInvariant(), id.ToLowerInvariant()) })
        .OrderBy(x => x.Score)
        .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .Take(Math.Max(1, maxCount))
        .Select(x => x.Id)
        .ToList();
}

static int ComputeLevenshteinDistance(string a, string b)
{
    if (string.IsNullOrEmpty(a))
        return string.IsNullOrEmpty(b) ? 0 : b.Length;
    if (string.IsNullOrEmpty(b))
        return a.Length;

    var rows = a.Length + 1;
    var cols = b.Length + 1;
    var d = new int[rows, cols];

    for (var i = 0; i < rows; i++) d[i, 0] = i;
    for (var j = 0; j < cols; j++) d[0, j] = j;

    for (var i = 1; i < rows; i++)
    {
        for (var j = 1; j < cols; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(
                Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                d[i - 1, j - 1] + cost);
        }
    }

    return d[rows - 1, cols - 1];
}

static string ExtractApiErrorText(string responseJson)
{
    if (string.IsNullOrWhiteSpace(responseJson))
        return "Unknown API error";

    try
    {
        var parsed = JObject.Parse(responseJson);
        var errorToken = parsed["error"];
        if (errorToken == null)
            return responseJson;

        if (errorToken.Type == JTokenType.String)
            return errorToken.ToString();

        var message = errorToken["message"]?.ToString();
        if (!string.IsNullOrWhiteSpace(message))
            return message;

        return errorToken.ToString(Formatting.None);
    }
    catch
    {
        return responseJson;
    }
}

static PrimaryFailureKind ClassifyPrimaryFailure(HttpStatusCode statusCode, string responseJson, string errorText)
{
    var haystack = $"{responseJson}\n{errorText}".ToLowerInvariant();

    if (haystack.Contains("data policy") || haystack.Contains("no endpoints found matching your data policy") || haystack.Contains("privacy") || haystack.Contains("free model publication"))
        return PrimaryFailureKind.PolicyMismatch;

    if (haystack.Contains("not a valid model id") || haystack.Contains("model not found") || haystack.Contains("unknown model") || (statusCode == HttpStatusCode.NotFound && haystack.Contains("model")))
        return PrimaryFailureKind.ModelNotFound;

    if (haystack.Contains("no endpoints found for") || haystack.Contains("no endpoint found for") || haystack.Contains("endpoint unavailable") || haystack.Contains("provider unavailable") || haystack.Contains("does not have any endpoints") || statusCode == HttpStatusCode.ServiceUnavailable || statusCode == HttpStatusCode.BadGateway || statusCode == HttpStatusCode.GatewayTimeout)
        return PrimaryFailureKind.EndpointUnavailable;

    return PrimaryFailureKind.Other;
}

static string ToNormalizedDiagnosticClass(PrimaryFailureKind kind) => kind switch
{
    PrimaryFailureKind.PolicyMismatch => "policy-mismatch",
    PrimaryFailureKind.ModelNotFound => "model-not-found",
    PrimaryFailureKind.EndpointUnavailable => "endpoint-unavailable",
    _ => "other"
};

enum PrimaryFailureKind
{
    PolicyMismatch,
    ModelNotFound,
    EndpointUnavailable,
    Other
}

enum RequestMode
{
    Decision,
    Chat,
    NpcChat
}

sealed class ModelValidationResult
{
    public bool Exists { get; private init; }
    public bool IsPreflightUnavailable { get; private init; }
    public List<string> NearestSuggestions { get; private init; } = new();

    public static ModelValidationResult Valid() => new() { Exists = true };

    public static ModelValidationResult Invalid(List<string> nearest) => new()
    {
        Exists = false,
        NearestSuggestions = nearest ?? new List<string>()
    };

    public static ModelValidationResult FromPreflightUnavailable() => new()
    {
        Exists = true,
        IsPreflightUnavailable = true
    };
}

sealed class CliOptions
{
    public string? ApiKey { get; set; }
    public string? ModelOverrideRaw { get; set; }
    public string Model { get; set; } = "google/gemini-2.0-flash-exp:free";
    public RequestMode Mode { get; set; } = RequestMode.Decision;
    public string? Message { get; set; }
    public string? ContextFile { get; set; }
    public string? MemoryFile { get; set; }
    public string? MemoryText { get; set; }
    public string? HistoryFile { get; set; }
    public string? ModConfigPath { get; set; }
    public string? DataCollectionOverrideRaw { get; set; }
    public string DataCollectionMode { get; set; } = "allow";
    public bool EnableProviderOverride { get; set; } = false;
    public bool ShowHelp { get; set; }
    public string? Error { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h")
            {
                options.ShowHelp = true;
                return options;
            }

            string NextValue(string name)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {name}");
                i++;
                return args[i];
            }

            try
            {
                switch (arg)
                {
                    case "--api-key":
                        options.ApiKey = NextValue(arg);
                        break;
                    case "--model":
                        options.Model = NextValue(arg);
                        options.ModelOverrideRaw = options.Model;
                        break;
                    case "--mode":
                        var modeRaw = NextValue(arg).Trim().ToLowerInvariant();
                        switch (modeRaw)
                        {
                            case "decision":
                                options.Mode = RequestMode.Decision;
                                break;
                            case "chat":
                                options.Mode = RequestMode.Chat;
                                break;
                            case "npcchat":
                                options.Mode = RequestMode.NpcChat;
                                break;
                            default:
                                options.Error = "--mode must be decision|chat|npcchat";
                                return options;
                        }
                        break;
                    case "--message":
                        options.Message = NextValue(arg);
                        break;
                    case "--context-file":
                        options.ContextFile = NextValue(arg);
                        break;
                    case "--memory-file":
                        options.MemoryFile = NextValue(arg);
                        break;
                    case "--memory-text":
                        options.MemoryText = NextValue(arg);
                        break;
                    case "--history-file":
                        options.HistoryFile = NextValue(arg);
                        break;
                    case "--mod-config":
                        options.ModConfigPath = NextValue(arg);
                        break;
                    case "--data-collection":
                        var dataCollection = NextValue(arg).Trim().ToLowerInvariant();
                        if (dataCollection != "allow" && dataCollection != "deny")
                        {
                            options.Error = "--data-collection must be allow|deny";
                            return options;
                        }

                        options.DataCollectionMode = dataCollection;
                        options.DataCollectionOverrideRaw = dataCollection;
                        break;
                    default:
                        options.Error = $"Unknown arg: {arg}";
                        return options;
                }
            }
            catch (Exception ex)
            {
                options.Error = ex.Message;
                return options;
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("LLMTestBridge - OpenRouter request path bridge for Going Medieval LLM NPCs");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tools/LLMTestBridge/LLMTestBridge.csproj -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --api-key <key>            OpenRouter API key (fallback env: OPENROUTER_API_KEY)");
        Console.WriteLine("  --model <id>               OpenRouter model id (primary model only)");
        Console.WriteLine("  --mode <decision|chat|npcchat> Request mode");
        Console.WriteLine("  --message <text>           Prompt message/user text");
        Console.WriteLine("  --context-file <path>      JSON file for decision mode NPC context");
        Console.WriteLine("  --memory-file <path>       Plain text memory/context to inject into decision prompt");
        Console.WriteLine("  --memory-text <text>       Inline memory/context text to inject into decision prompt");
        Console.WriteLine("  --history-file <path>      JSON message array for prior turns (role/content)");
        Console.WriteLine("  --mod-config <path>        BepInEx mod cfg path (ApiKey/Model/DataCollection source)");
        Console.WriteLine("  --data-collection <allow|deny>  OpenRouter provider policy setting");
        Console.WriteLine("  --help, -h                 Show help");
    }
}

public class PromptBuilder
{
    public List<Message> BuildDecisionPrompt(NPCContext context, List<Message>? history)
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = "system",
                Content = BuildSystemPrompt()
            }
        };

        if (history != null)
            messages.AddRange(history);

        messages.Add(new Message
        {
            Role = "user",
            Content = BuildStatePrompt(context)
        });

        return messages;
    }

    public List<Message> BuildNpcChatPrompt(NPCContext context, List<Message>? history, string? extraMessage)
    {
        var messages = new List<Message>();
        messages.Add(new Message
        {
            Role = "system",
            Content = @"You are generating dialogue for a settler in a medieval colony game. 
Generate a single line of dialogue (1-2 sentences max) that the speaking settler would say.
Consider their relationship, current situation, and personalities.

Respond with JSON: {""dialogue"": ""what they say""}"
        });

        var sb = new StringBuilder();
        sb.AppendLine($"SPEAKER: {context.Name} ({context.Profession})");
        sb.AppendLine(BuildRichContextSummary(context, includeMemory: true));
        sb.AppendLine();
        
        sb.AppendLine("CONVERSATION HISTORY:");
        if (history != null) {
            foreach (var msg in history) {
                sb.AppendLine($"{msg.Role}: {msg.Content}");
            }
        }
        if (!string.IsNullOrWhiteSpace(extraMessage)) {
            sb.AppendLine($"user: {extraMessage}");
        }
        sb.AppendLine();
        sb.AppendLine($"Generate what {context.Name} says next:");

        messages.Add(new Message
        {
            Role = "user",
            Content = sb.ToString()
        });

        return messages;
    }

    private string BuildSystemPrompt()
    {
        return @"You are controlling a settler in a medieval colony simulation game.
Your job is to make decisions for this character based on their current state, needs, and environment.

You must respond with a JSON object containing:
- action: The action to take (continue_job, switch_job, rest, eat, socialize, flee, defend, seek_shelter, explore, gather)
- parameters: Optional parameters for the action (e.g., job name for switch_job)
- reasoning: Brief explanation of why you chose this action

Consider:
- Prioritize low needs (food, water, rest when below 30%)
- Respond to threats immediately
- Balance work with recreation
- Use skills effectively
- Consider relationships with other settlers

Available actions:
- continue_job: Continue current task
- switch_job [job_name]: Change job (woodcutting, mining, farming, building, cooking, etc.)
- rest: Take a break
- eat: Find and eat food
- socialize [target]: Interact with another settler
- flee: Run from danger
- defend: Prepare for combat
- seek_shelter: Find safe location
- explore: Discover new areas
- gather [resource]: Collect resources";
    }

    private string BuildStatePrompt(NPCContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {context.Name}'s Current State ===");
        sb.AppendLine(BuildRichContextSummary(context, includeMemory: true));
        sb.AppendLine();
        sb.AppendLine("What action should I take? Respond with JSON.");
        return sb.ToString();
    }

    public static string BuildRichContextSummary(NPCContext context, bool includeMemory)
    {
        if (context == null)
            return "Context unavailable.";

        var sb = new StringBuilder();
        sb.AppendLine($"Identity: {context.Name} (Id={context.Id ?? "unknown"})");
        sb.AppendLine($"Demographics: age={context.Age}, gender={context.Gender ?? "unknown"}, background={context.BackgroundOrRole ?? "unknown"}, pseudonym={context.Pseudonym ?? "none"}");
        sb.AppendLine($"Health: {(context.Health != null ? $"{context.Health.Current:F0}/{context.Health.Max:F0}" : "unknown")}, mood={context.Mood ?? "unknown"} ({context.MoodScore:F0}/100)");

        if (context.Needs != null)
            sb.AppendLine($"Needs: food={context.Needs.Food:F0}, water={context.Needs.Water:F0}, rest={context.Needs.Rest:F0}, recreation={context.Needs.Recreation:F0}, comfort={context.Needs.Comfort:F0}, beauty={context.Needs.Beauty:F0}, privacy={context.Needs.Privacy:F0}");

        if (context.Vitals != null && context.Vitals.Count > 0)
            sb.AppendLine($"Vitals: {string.Join(", ", context.Vitals.Select(kv => $"{kv.Key}={kv.Value}"))}");

        if (context.States != null && context.States.Count > 0)
            sb.AppendLine($"States: {string.Join(", ", context.States)}");

        sb.AppendLine($"Profession: {context.Profession ?? "unknown"}");

        if (context.Skills != null && context.Skills.Count > 0)
            sb.AppendLine($"Skills: {string.Join(", ", context.Skills.Select(kv => $"{kv.Key}={kv.Value}"))}");

        if (context.Traits != null && context.Traits.Count > 0)
            sb.AppendLine($"Traits: {string.Join(", ", context.Traits)}");
        if (context.Perks != null && context.Perks.Count > 0)
            sb.AppendLine($"Perks: {string.Join(", ", context.Perks)}");
        if (context.BackgroundTags != null && context.BackgroundTags.Count > 0)
            sb.AppendLine($"Background tags: {string.Join(", ", context.BackgroundTags)}");

        if (context.Inventory != null && context.Inventory.Count > 0)
            sb.AppendLine($"Inventory({context.Inventory.Count}): {string.Join(", ", context.Inventory.Take(12))}");

        if (context.CurrentActivity != null)
            sb.AppendLine($"Activity: type={context.CurrentActivity.Type ?? "idle"}, desc={context.CurrentActivity.Description ?? "none"}, target={context.CurrentActivity.Target ?? "none"}, progress={context.CurrentActivity.Progress:F2}");

        if (context.Environment != null)
            sb.AppendLine($"Environment: room={context.Environment.Room ?? "unknown"}, time={context.Environment.TimeOfDay ?? "unknown"}, weather={context.Environment.Weather ?? "unknown"}, threats={string.Join(", ", context.Environment.NearbyThreats ?? new List<string>())}");

        if (context.Relationships != null && context.Relationships.Count > 0)
            sb.AppendLine($"Relationships: {context.Relationships.Count} known");
        if (context.SocialLogs != null && context.SocialLogs.Count > 0)
            sb.AppendLine($"Recent social interactions: {string.Join(" | ", context.SocialLogs)}");
        if (context.BeliefLogs != null && context.BeliefLogs.Count > 0)
            sb.AppendLine($"Recent religious thoughts: {string.Join(" | ", context.BeliefLogs)}");

        if (includeMemory && !string.IsNullOrWhiteSpace(context.MemoryContext))
        {
            sb.AppendLine("=== YOUR MEMORIES (Hierarchical Context) ===");
            sb.AppendLine(context.MemoryContext);
            sb.AppendLine("=== END MEMORIES ===");
        }

        return sb.ToString();
    }
}

public class Message
{
    [JsonProperty("role")]
    public string Role { get; set; } = string.Empty;

    [JsonProperty("content")]
    public string Content { get; set; } = string.Empty;
}

public class NPCContext
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "Unknown";
    [JsonProperty("age")] public int Age { get; set; }
    [JsonProperty("gender")] public string? Gender { get; set; }
    [JsonProperty("background_or_role")] public string? BackgroundOrRole { get; set; }
    [JsonProperty("pseudonym")] public string? Pseudonym { get; set; }
    [JsonProperty("health")] public HealthContext? Health { get; set; }
    [JsonProperty("mood")] public string? Mood { get; set; }
    [JsonProperty("mood_score")] public float MoodScore { get; set; }
    [JsonProperty("vitals")] public Dictionary<string, string>? Vitals { get; set; }
    [JsonProperty("states")] public List<string>? States { get; set; }
    [JsonProperty("needs")] public NeedsContext? Needs { get; set; }
    [JsonProperty("profession")] public string? Profession { get; set; }
    [JsonProperty("skills")] public Dictionary<string, int>? Skills { get; set; }
    [JsonProperty("traits")] public List<string>? Traits { get; set; }
    [JsonProperty("perks")] public List<string>? Perks { get; set; }
    [JsonProperty("background_tags")] public List<string>? BackgroundTags { get; set; }
    [JsonProperty("inventory")] public List<string>? Inventory { get; set; }
    [JsonProperty("current_activity")] public ActivityContext? CurrentActivity { get; set; }
    [JsonProperty("environment")] public EnvironmentContext? Environment { get; set; }
    [JsonProperty("relationships")] public Dictionary<string, RelationshipContext>? Relationships { get; set; }
    [JsonProperty("social_logs")] public List<string>? SocialLogs { get; set; }
    [JsonProperty("belief_logs")] public List<string>? BeliefLogs { get; set; }
    [JsonIgnore] public string? MemoryContext { get; set; }
}

public class HealthContext
{
    [JsonProperty("current")] public float Current { get; set; }
    [JsonProperty("max")] public float Max { get; set; }
    [JsonProperty("status_effects")] public List<string>? StatusEffects { get; set; }
}

public class NeedsContext
{
    [JsonProperty("food")] public float Food { get; set; }
    [JsonProperty("water")] public float Water { get; set; }
    [JsonProperty("rest")] public float Rest { get; set; }
    [JsonProperty("recreation")] public float Recreation { get; set; }
    [JsonProperty("comfort")] public float Comfort { get; set; }
    [JsonProperty("beauty")] public float Beauty { get; set; }
    [JsonProperty("privacy")] public float Privacy { get; set; }
}

public class ActivityContext
{
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("target")] public string? Target { get; set; }
    [JsonProperty("progress")] public float Progress { get; set; }
}

public class EnvironmentContext
{
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("time_of_day")] public string? TimeOfDay { get; set; }
    [JsonProperty("weather")] public string? Weather { get; set; }
    [JsonProperty("nearby_threats")] public List<string>? NearbyThreats { get; set; }
}

public class RelationshipContext
{
    [JsonProperty("npc_id")] public string? NPCId { get; set; }
    [JsonProperty("opinion")] public float Opinion { get; set; }
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("trust")] public float Trust { get; set; }
}
