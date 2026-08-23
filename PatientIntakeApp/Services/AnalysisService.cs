using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PatientIntakeApp.Data.Entities;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services.Stores;

namespace PatientIntakeApp.Services;

public interface IAnalysisService
{
    Task<List<Finding>> AnalyzeDocumentAsync(List<PageContent> pages, Facility facility, DevSettings? devSettings = null);
    Task<AgentOverviewResult?> GenerateAgentOverviewAsync(List<PageContent> pages, Facility facility, DevSettings? devSettings = null);
    /// <summary>
    /// Combines keyword/context findings AND agent overview into a single Gemini API call.
    /// All pages are sent as attachments in one request to avoid exceeding free-tier RPM limits.
    /// </summary>
    Task<Models.BatchAnalysisResult> AnalyzeDocumentBatchAsync(List<PageContent> pages, Facility facility, DevSettings? devSettings = null);
}

public class AnalysisService : IAnalysisService
{
    private readonly IConfigurationService _configService;
    private readonly IRuleStore _ruleStore;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _geminiGate = new(1, 1);
    private const string GeminiApiBase = "https://generativelanguage.googleapis.com/v1";

    public AnalysisService(IConfigurationService configService, IRuleStore ruleStore)
    {
        _configService = configService;
        _ruleStore = ruleStore;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    private static string NormalizeGeminiModel(string? model)
    {
        var m = (model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(m)) return string.Empty;

        // Accept either "gemini-3.5-flash" or "models/gemini-3.5-flash"
        if (m.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            m = m.Substring("models/".Length);
        }

        return m.Trim();
    }

    private static string BuildGenerateContentUrl(string apiKey, string model)
    {
        var normalizedModel = NormalizeGeminiModel(model);
        var encodedModel = Uri.EscapeDataString(normalizedModel);
        var encodedKey = Uri.EscapeDataString(apiKey ?? string.Empty);
        return $"{GeminiApiBase}/models/{encodedModel}:generateContent?key={encodedKey}";
    }

    private static double? TryParseRetryAfterSeconds(string responseBody)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;
            // Gemini often returns: "Please retry in 23.87833968s."
            var m = Regex.Match(responseBody, @"retry in\s+([0-9]+(?:\.[0-9]+)?)s", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return null;
            return seconds;
        }
        catch
        {
            return null;
        }
    }

    private static int ComputeBackoffMs(int attempt, double? retryAfterSeconds)
    {
        // attempt: 0,1,2...
        if (retryAfterSeconds.HasValue)
        {
            return (int)Math.Min(20_000, Math.Max(500, retryAfterSeconds.Value * 1000));
        }

        // exponential with jitter
        var baseMs = (int)Math.Min(20_000, 750 * Math.Pow(2, attempt));
        var jitter = Random.Shared.Next(0, 250);
        return baseMs + jitter;
    }

    private async Task<(List<RuleEntity> KeywordRules, List<RuleEntity> ContextRules)> ResolveEnabledRulesAsync(Facility facility)
    {
        // Primary: DB-backed rules (supports enable/disable + severity).
        try
        {
            var legacyId = facility?.Id ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(legacyId))
            {
                var keyword = await _ruleStore.ListEnabledRulesAsync(legacyId, RuleKind.Keyword);
                var context = await _ruleStore.ListEnabledRulesAsync(legacyId, RuleKind.Context);

                // IMPORTANT: if the DB has no rules yet (common in early dev), fall back to config.json rules
                // so local keyword flagging still works.
                if (keyword.Count > 0 || context.Count > 0)
                {
                    return (keyword, context);
                }

                Log($"[AnalysisService] No enabled DB rules for facility '{legacyId}'. Falling back to config rules.");
            }
        }
        catch
        {
            // Fall back to config.json rules
        }

        // Fallback: config.json string rules (no toggles/severity available)
        var fallbackKeyword = (facility?.Rules ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => new RuleEntity { Text = r.Trim(), IsEnabled = true, Kind = RuleKind.Keyword, Severity = RuleSeverity.Yellow })
            .ToList();

        var fallbackContext = (facility?.ContextRules ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => new RuleEntity { Text = r.Trim(), IsEnabled = true, Kind = RuleKind.Context, Severity = RuleSeverity.Yellow })
            .ToList();

        return (fallbackKeyword, fallbackContext);
    }

    private static SeverityLevel MapSeverity(RuleSeverity severity)
    {
        return severity switch
        {
            RuleSeverity.Green => SeverityLevel.Green,
            RuleSeverity.Red => SeverityLevel.Red,
            _ => SeverityLevel.Yellow
        };
    }

    public async Task<List<Finding>> AnalyzeDocumentAsync(List<PageContent> pages, Facility facility, DevSettings? devSettings = null)
    {
        var (keywordRules, contextRules) = await ResolveEnabledRulesAsync(facility);
        var allFindings = new List<Finding>();

        foreach (var page in pages)
        {
            var pageFindings = await AnalyzePageAsync(page, keywordRules, contextRules, devSettings);
            allFindings.AddRange(pageFindings);
        }

        return allFindings;
    }

    public async Task<AgentOverviewResult?> GenerateAgentOverviewAsync(List<PageContent> pages, Facility facility, DevSettings? devSettings = null)
    {
        var (keywordRules, contextRules) = await ResolveEnabledRulesAsync(facility);

        // Keep this small to conserve cost: attach only a few representative pages.
        // If the doc is short, send all pages; otherwise send the first 3.
        var samplePages = (pages ?? new List<PageContent>())
            .Where(p => p.PagePdfBytes != null && p.PagePdfBytes.Length > 0)
            .OrderBy(p => p.PageNumber)
            .Take(3)
            .ToList();

        if (!samplePages.Any())
        {
            Log("[AnalysisService] Agent overview skipped: no PDF page bytes available.");
            return null;
        }

        var apiKey = _configService.ApiKey;
        var requestedModel = _configService.AiModel;

        var rulesString = string.Join(", ", keywordRules.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var contextRuleTexts = (contextRules ?? new List<RuleEntity>()).Select(r => r.Text).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var contextRulesWithIds = contextRuleTexts.Select((r, i) => $"{i + 1}) {r.Trim()}").ToList();
        var contextBlock = contextRulesWithIds.Any()
            ? "\n\nFacility context rules (non-keyword):\n- " + string.Join("\n- ", contextRulesWithIds)
            : string.Empty;

        var prompt = $@"You are an AI Intake Analyst. Write a neutral, professional narrative overview of the patient intake packet.

CRITICAL INSTRUCTION - DUAL EVALUATION PROTOCOL:
You MUST wrap your step-by-step evaluation inside <thinking> </thinking> tags before generating your final output.
Inside the <thinking> block:
1. Textual Rules: Evaluate Demographics and Diagnoses. Check for age restrictions AND diagnosis restrictions (e.g., Type 1 vs Type 2 Diabetes).
2. Checkbox Rules: Visually inspect the square [ ] next to each behavior. If empty, write 'Empty - Skip'. If marked, write 'Marked - Keep'.
3. Boilerplate Filter: If a target keyword appears as part of a pre-printed form instruction, intervention, or general form text (e.g., 'Reduce harmful objects'), you MUST IGNORE IT. Only flag conditions actually diagnosed or exhibited by the specific patient.
After closing the </thinking> tag, output ONLY the final, confirmed disqualification flags. Do not include any of the skipped items in your final output.

IMPORTANT: Do NOT recommend acceptance/rejection, do NOT advise disqualification, and do NOT make treatment recommendations. This is a descriptive summary only.

Goal: Summarize what the packet says (key context, timeline, diagnoses, medications, relevant history, and any notable contradictions) in a calm, factual tone. Avoid judgmental language.

Disqualifying keyword rules (for reference): [{rulesString}]
{contextBlock}

Return ONLY a JSON object with this structure (no markdown):
{{
  ""overview"": ""..."",
  ""contextRuleViolations"": [
    {{ ""ruleIndex"": 1, ""evidence"": ""short evidence quote"", ""page"": 1 }}
  ]
}}

Rules:
- If there are no context rule violations, return an empty array for contextRuleViolations.
- The ""ruleIndex"" MUST be a valid 1-based index from the facility context rules list above.
- Check EACH context rule independently; include an entry for every violated rule.";

        var parts = new List<object> { new { text = prompt } };
        foreach (var p in samplePages)
        {
            parts.Add(new { inline_data = new { mime_type = "application/pdf", data = Convert.ToBase64String(p.PagePdfBytes!) } });
        }

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = parts.ToArray()
                }
            },
            generationConfig = new { temperature = 0.0, topP = 0.1 }
        };

        try
        {
            // Model is pinned via ConfigurationService. Keep the list as a single entry to ensure
            // we never fall back to deprecated model names (which can cause 404s).
            var modelsToTry = new List<string> { requestedModel }
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? lastBody = null;
            HttpStatusCode? lastStatus = null;

            foreach (var model in modelsToTry)
            {
                var url = BuildGenerateContentUrl(apiKey, model);

                // A couple retries for transient 429s/5xx.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await _geminiGate.WaitAsync();
                    HttpResponseMessage response;
                    string responseString;
                    var overviewCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                        response = await _httpClient.PostAsync(url, jsonContent, overviewCts.Token);
                        responseString = await response.Content.ReadAsStringAsync(overviewCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Log($"[AnalysisService] Agent overview API request timed out or was cancelled.");
                        throw;
                    }
                    finally
                    {
                        overviewCts.Dispose();
                        _geminiGate.Release();
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        lastStatus = response.StatusCode;
                        lastBody = responseString;
                        Log($"[AnalysisService] Agent overview API error {response.StatusCode} (model={model}): {TruncateForLog(responseString, 2000)}");

                        // If preview model returns quota=0 or we hit transient throttling, try a different model.
                        if (response.StatusCode == (HttpStatusCode)429)
                        {
                            var retryAfter = TryParseRetryAfterSeconds(responseString);
                            // First retry on SAME model; only move to next model after final attempt
                            var delayMs = ComputeBackoffMs(attempt, retryAfter);
                            await Task.Delay(delayMs);
                            if (attempt < 2) continue;
                            break; // try next model
                        }

                        if ((int)response.StatusCode >= 500 && attempt < 2)
                        {
                            await Task.Delay(ComputeBackoffMs(attempt, retryAfterSeconds: null));
                            continue;
                        }

                        // Non-retryable
                        break;
                    }

                    var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseString);
                    var textPart = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                    if (string.IsNullOrWhiteSpace(textPart)) return null;

                    textPart = textPart.Replace("```json", "").Replace("```", "").Trim();
                    textPart = Regex.Replace(textPart, @"<thinking>.*?</thinking>", "", RegexOptions.Singleline).Trim();

                    try
                    {
                        var result = JsonSerializer.Deserialize<AgentOverviewResult>(textPart, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (result == null) return null;

                        result.ContextRuleViolations ??= new List<ContextRuleViolation>();
                        result.Overview ??= string.Empty;

                        if (string.IsNullOrWhiteSpace(result.Overview))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(textPart);
                                var root = doc.RootElement;
                                if (root.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var key in new[] { "overview", "narrative", "summary", "notes", "aiOverview", "intakeOverview" })
                                    {
                                        if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                                        {
                                            var s = (el.GetString() ?? string.Empty).Trim();
                                            if (!string.IsNullOrWhiteSpace(s))
                                            {
                                                result.Overview = s;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        return result;
                    }
                    catch
                    {
                        return new AgentOverviewResult { Overview = textPart };
                    }
                }
            }

            if (lastStatus != null)
            {
                throw new Exception($"API Error {lastStatus}: {lastBody}");
            }

            return null;
        }
        catch (Exception ex)
        {
            Log($"[AnalysisService] Agent overview failed: {ex.Message}");
            return null;
        }
    }

    public async Task<BatchAnalysisResult> AnalyzeDocumentBatchAsync(List<PageContent> pages, Facility facility, DevSettings? devSettings = null)
    {
        var (keywordRules, contextRules) = await ResolveEnabledRulesAsync(facility);

        // Step 1: Run local keyword search across ALL pages (keeps existing behavior).
        var localFindings = new List<Finding>();
        if (!(devSettings?.DisableLocalKeywordSearch ?? false))
        {
            foreach (var page in pages)
            {
                var normalizedText = page.Text.ToLowerInvariant();
                foreach (var rule in keywordRules.Where(r => r.IsEnabled))
                {
                    var ruleText = (rule.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(ruleText)) continue;
                    var ruleLower = ruleText.ToLowerInvariant();

                    int searchStart = 0;
                    while (true)
                    {
                        var idx = normalizedText.IndexOf(ruleLower, searchStart, StringComparison.Ordinal);
                        if (idx < 0) break;

                        var snippetStart = Math.Max(0, idx - 30);
                        var snippetEnd = Math.Min(page.Text.Length, idx + ruleText.Length + 30);
                        var snippet = page.Text.Substring(snippetStart, snippetEnd - snippetStart).Replace("\r", " ").Replace("\n", " ");

                        localFindings.Add(new Finding
                        {
                            Term = page.Text.Substring(idx, ruleText.Length),
                            Category = "Local Flag",
                            Page = page.PageNumber,
                            Context = $"Found '{ruleText}' via local keyword search. Context: \"{snippet}\"",
                            IsReviewed = false,
                            IsFalseFlag = false,
                            Source = FindingSource.Local,
                            Severity = MapSeverity(rule.Severity),
                            MatchIndex = idx
                        });

                        searchStart = idx + ruleLower.Length;
                        if (searchStart >= normalizedText.Length) break;
                    }
                }
            }
        }

        // Step 2: Decision gate - local findings skip AI unless AlwaysForwardToAI is enabled.
        if (localFindings.Any() && !(devSettings?.AlwaysForwardToAI ?? false))
        {
            Log($"[AnalysisService] Returning {localFindings.Count} local findings. AI skipped.");
            return new BatchAnalysisResult { Findings = localFindings };
        }

        Log("[AnalysisService] No local matches or 'Always Forward to AI' enabled. Sending all pages as a single batched AI request...");

        // Step 3: Single Gemini API call with ALL pages + combined prompt for findings AND overview.
        var apiKey = _configService.ApiKey;
        var requestedModel = _configService.AiModel;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new BatchAnalysisResult
            {
                Findings = new List<Finding>
                {
                    new Finding
                    {
                        Term = "AI Config Error",
                        Category = "AI Not Configured",
                        Page = 1,
                        Context = "No Gemini API key is configured.\n\nFix:\n- Set env var GEMINI_API_KEY to your paid project key, or\n- Add ApiKey to user_settings.json\n",
                        IsReviewed = false,
                        Source = FindingSource.AI
                    }
                }
            };
        }

        var keywordRuleTexts = (keywordRules ?? new List<RuleEntity>())
            .Where(r => r.IsEnabled)
            .Select(r => (r.Text ?? string.Empty).Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contextRuleTexts = (contextRules ?? new List<RuleEntity>())
            .Where(r => r.IsEnabled)
            .Select(r => (r.Text ?? string.Empty).Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keywordBlock = keywordRuleTexts.Any()
            ? "\n\nKeyword rules (EXACT MATCH ONLY; do not infer/synonym-match):\n- " + string.Join("\n- ", keywordRuleTexts)
            : "\n\nKeyword rules: (none)";

        var contextRulesWithIds = contextRuleTexts
            .Select((r, i) => $"{i + 1}) {r.Trim()}")
            .ToList();
        var contextBlock = contextRulesWithIds.Any()
            ? "\n\nContext rules (broad/semantic; check EACH rule independently):\n- " + string.Join("\n- ", contextRulesWithIds)
            : "\n\nContext rules: (none)";

        var safePages = pages ?? new List<PageContent>();
        var pageCount = safePages.Count;
        var prompt = $@"Act as a medical intake analyst. You are given ALL {pageCount} PAGES of a patient intake packet.

CRITICAL INSTRUCTION - DUAL EVALUATION PROTOCOL:
You MUST wrap your step-by-step evaluation inside <thinking> </thinking> tags before generating your final output.
Inside the <thinking> block:
1. Textual Rules: Evaluate Demographics and Diagnoses. Check for age restrictions AND diagnosis restrictions (e.g., Type 1 vs Type 2 Diabetes).
2. Checkbox Rules: Visually inspect the square [ ] next to each behavior. If empty, write 'Empty - Skip'. If marked, write 'Marked - Keep'.
3. Boilerplate Filter: If a target keyword appears as part of a pre-printed form instruction, intervention, or general form text (e.g., 'Reduce harmful objects'), you MUST IGNORE IT. Only flag conditions actually diagnosed or exhibited by the specific patient.
After closing the </thinking> tag, output ONLY the final, confirmed disqualification flags. Do not include any of the skipped items in your final output.

You must perform TWO tasks:

TASK A - Find ALL keyword and context rule violations across ALL pages:
1) For KEYWORD rules: only return a finding if you can visually locate the EXACT keyword/phrase on a page (case-insensitive is OK, but spelling/word form must be exact).
   - Do NOT fuzzy match or infer from similar words.
   - For EVERY keyword-rule finding, include ""matchedText"" (exact text from page), ""page"" (the page number), ""kind"": ""keyword"".
   - For EVERY keyword-rule finding, include ""evidence"": a brief explanation of WHY this keyword was flagged (not just the surrounding text - explain the clinical significance or concern).
2) For CONTEXT rules: flag based on meaning/implication even if exact words do not appear.
   - For EVERY context-rule finding include: ""ruleIndex"" (1-based), ""evidence"" (short quote), ""page"", ""kind"": ""context"".

{keywordBlock}
{contextBlock}

TASK B - Write a neutral, professional narrative overview of the patient intake packet:
- Summarize key context, timeline, diagnoses, medications, relevant history, and notable contradictions in a calm, factual tone.
- Do NOT recommend acceptance/rejection, do NOT advise disqualification, do NOT make treatment recommendations.
- IMPORTANT: Review ALL pages before writing the overview. Do not limit yourself to the first few pages.

Return ONLY a JSON object with this structure (no markdown, no code fences):
{{
  ""findings"": [
    {{
      ""kind"": ""keyword"",
      ""keyword"": ""(must exactly match a keyword rule)"",
      ""matchedText"": ""exact copied keyword text"",
      ""page"": 1,
      ""evidence"": ""short quote showing why the keyword was flagged"",
      ""context"": ""brief context"",
      ""isFalseFlag"": true/false,
      ""falseFlagReason"": ""reason if false flag""
    }},
    {{
      ""kind"": ""context"",
      ""ruleIndex"": 1,
      ""evidence"": ""short evidence phrase"",
      ""page"": 1,
      ""context"": ""brief context""
    }}
  ],
  ""overview"": ""...narrative overview text...""
}}

If no findings, use {{ ""findings"": [] }}. For false positives (e.g., ""not violent""), set isFalseFlag to true with a reason.";

        // Build parts: prompt text + all pages as inline PDF attachments
        var parts = new List<object> { new { text = prompt } };
        var pagesWithPdf = safePages
            .Where(p => p.PagePdfBytes != null && p.PagePdfBytes.Length > 0)
            .OrderBy(p => p.PageNumber)
            .ToList();

        if (!pagesWithPdf.Any())
        {
            // Fallback: attach extracted text if no PDF bytes
            var allText = string.Join("\n\n--- Page Break ---\n\n",
                safePages.OrderBy(p => p.PageNumber).Select(p => $"[PAGE {p.PageNumber}]\n{p.Text}"));
            parts.Add(new { text = "NOTE: PDF bytes were unavailable; use the following extracted text:\n" + allText });
        }
        else
        {
            foreach (var p in pagesWithPdf)
            {
                parts.Add(new { inline_data = new { mime_type = "application/pdf", data = Convert.ToBase64String(p.PagePdfBytes!) } });
            }
        }

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = parts.ToArray()
                }
            },
            generationConfig = new { temperature = 0.0, topP = 0.1 }
        };

        try
        {
            var model = requestedModel;
            var url = BuildGenerateContentUrl(apiKey, model);

            // Retry up to 5 times with strict exponential backoff: 2s, 4s, 8s, 16s, 32s.
            // Only retry on 429 (TooManyRequests) or 503 (ServiceUnavailable).
            string? lastRetryErrorBody = null;
            HttpStatusCode? lastRetryStatus = null;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                await _geminiGate.WaitAsync();
                HttpResponseMessage response;
                string responseString;
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)); // longer timeout for batched call
                try
                {
                    var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync(url, jsonContent, cts.Token);
                    responseString = await response.Content.ReadAsStringAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    cts.Dispose();
                    _geminiGate.Release();
                    Log("[AnalysisService] Batched API request timed out at the HTTP level.");
                    // Timeout is NOT retryable - propagate as exception so error card shows the reason.
                    throw;
                }
                catch (HttpRequestException httpEx)
                {
                    cts.Dispose();
                    _geminiGate.Release();
                    // Network-level failure - retryable if it's a transient connectivity issue.
                    Log($"[AnalysisService] Batched API network error (attempt {attempt + 1}/5): {httpEx.Message}");
                    if (attempt < 4)
                    {
                        var delayMs = 2000 * (int)Math.Pow(2, attempt); // 2s, 4s, 8s, 16s
                        Log($"[AnalysisService] Retrying in {delayMs / 1000}s (attempt {attempt + 2}/5)...");
                        await Task.Delay(delayMs);
                        continue;
                    }
                    // Exhausted retries for network errors - throw so the catch block surfaces the error card.
                    throw;
                }
                finally
                {
                    if (cts != null && !cts.IsCancellationRequested)
                    {
                        // cts is disposed above in catch branches; only dispose here on success path
                        cts.Dispose();
                        _geminiGate.Release();
                    }
                }

                // cts and semaphore already released on the success path above via finally
                // (but we used a pattern where we dispose in catch branches;
                //  on the success path, we need to dispose here)

                if (!response.IsSuccessStatusCode)
                {
                    lastRetryStatus = response.StatusCode;
                    lastRetryErrorBody = responseString;
                    Log($"[AnalysisService] Batched API error {response.StatusCode} (model={model}, attempt {attempt + 1}/5): {TruncateForLog(responseString, 2000)}");

                    // Only retry on 429 (rate limit) or 503 (service unavailable).
                    var isRetryable = response.StatusCode == (HttpStatusCode)429
                                   || response.StatusCode == HttpStatusCode.ServiceUnavailable;

                    if (isRetryable && attempt < 4)
                    {
                        var delayMs = 2000 * (int)Math.Pow(2, attempt); // 2s, 4s, 8s, 16s
                        Log($"[AnalysisService] Retrying in {delayMs / 1000}s (attempt {attempt + 2}/5)...");
                        await Task.Delay(delayMs);
                        continue;
                    }

                    // Non-retryable status, or exhausted retries - fall through to throw.
                    break;
                }

                // Success - parse the combined response
                var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseString);
                var textPart = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                if (string.IsNullOrWhiteSpace(textPart))
                {
                    return new BatchAnalysisResult { Findings = new List<Finding>() };
                }

                textPart = textPart.Replace("```json", "").Replace("```", "").Trim();
                textPart = Regex.Replace(textPart, @"<thinking>.*?</thinking>", "", RegexOptions.Singleline).Trim();

                // Parse findings from the combined response (findings + overview in one JSON)
                var allFindings = new List<Finding>();
                AgentOverviewResult? overview = null;

                try
                {
                    using var doc = JsonDocument.Parse(textPart);
                    var root = doc.RootElement;

                    // Extract overview
                    if (root.TryGetProperty("overview", out var overviewEl) && overviewEl.ValueKind == JsonValueKind.String)
                    {
                        overview = new AgentOverviewResult { Overview = overviewEl.GetString() ?? string.Empty };
                    }
                    else
                    {
                        // Try alternate keys
                        foreach (var key in new[] { "narrative", "summary", "notes", "aiOverview", "intakeOverview" })
                        {
                            if (root.TryGetProperty(key, out var alt) && alt.ValueKind == JsonValueKind.String)
                            {
                                var s = (alt.GetString() ?? string.Empty).Trim();
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                    overview = new AgentOverviewResult { Overview = s };
                                    break;
                                }
                            }
                        }
                    }

                    // Extract findings array
                    if (root.TryGetProperty("findings", out var findingsArr) && findingsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in findingsArr.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            var kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? (k.GetString() ?? "") : "";
                            var pg = el.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 1;
                            pg = Math.Max(1, Math.Min(pg, safePages.Count));

                            if (string.Equals(kind, "context", StringComparison.OrdinalIgnoreCase))
                            {
                                var ruleIndex = el.TryGetProperty("ruleIndex", out var ri) && ri.ValueKind == JsonValueKind.Number ? ri.GetInt32() : 0;
                                if (ruleIndex < 1 || ruleIndex > contextRuleTexts.Count) continue;
                                var rule = contextRuleTexts[ruleIndex - 1].Trim();
                                if (string.IsNullOrWhiteSpace(rule)) continue;

                                var evidence = el.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String ? (ev.GetString() ?? "") : "";
                                evidence = evidence.Trim();

                                var ctx = el.TryGetProperty("context", out var cc) && cc.ValueKind == JsonValueKind.String ? (cc.GetString() ?? "") : "";
                                ctx = ctx.Trim();
                                var combined = string.IsNullOrWhiteSpace(evidence) ? ctx : (string.IsNullOrWhiteSpace(ctx) ? evidence : (evidence + "\n\n" + ctx));

                                var contextSev = (contextRules ?? new List<RuleEntity>())
                                    .Where(r => r.IsEnabled)
                                    .Select(r => MapSeverity(r.Severity))
                                    .ToList();

                                allFindings.Add(new Finding
                                {
                                    Term = rule,
                                    Category = "Context Rule",
                                    Page = pg,
                                    Context = combined,
                                    IsReviewed = false,
                                    ReviewStatus = ReviewStatus.Pending,
                                    IsFalseFlag = false,
                                    Severity = (contextSev != null && contextSev.Count >= ruleIndex)
                                        ? contextSev[ruleIndex - 1]
                                        : SeverityLevel.Yellow,
                                    Source = FindingSource.AI
                                });
                                continue;
                            }

                            // Keyword finding
                            var keyword = el.TryGetProperty("keyword", out var kw) && kw.ValueKind == JsonValueKind.String
                                ? (kw.GetString() ?? "")
                                : (el.TryGetProperty("term", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "") : "");
                            keyword = keyword.Trim();
                            if (string.IsNullOrWhiteSpace(keyword) || !keywordRuleTexts.Any(kt => string.Equals(kt.Trim(), keyword, StringComparison.OrdinalIgnoreCase))) continue;

                            var matchedText = el.TryGetProperty("matchedText", out var mt) && mt.ValueKind == JsonValueKind.String ? (mt.GetString() ?? "") : "";
                            matchedText = matchedText.Trim();

                            var evidence2 = el.TryGetProperty("evidence", out var ev2) && ev2.ValueKind == JsonValueKind.String ? (ev2.GetString() ?? "") : "";
                            evidence2 = evidence2.Trim();

                            var ctx2 = el.TryGetProperty("context", out var c2) && c2.ValueKind == JsonValueKind.String ? (c2.GetString() ?? "") : "";
                            ctx2 = ctx2.Trim();
                            Log($"[AnalysisService] Keyword finding '{keyword}': evidence='{TruncateForLog(evidence2, 200)}' context='{TruncateForLog(ctx2, 200)}'");
                            ctx2 = string.IsNullOrWhiteSpace(evidence2) ? ctx2 : (string.IsNullOrWhiteSpace(ctx2) ? evidence2 : (evidence2 + "\n\n" + ctx2));
                            var isFalseFlag = el.TryGetProperty("isFalseFlag", out var iff) && (iff.ValueKind == JsonValueKind.True || iff.ValueKind == JsonValueKind.False) && iff.GetBoolean();
                            var falseReason = el.TryGetProperty("falseFlagReason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

                            var canonicalKeyword = keywordRuleTexts.First(kt => string.Equals(kt.Trim(), keyword, StringComparison.OrdinalIgnoreCase)).Trim();

                            var pageContent = safePages.FirstOrDefault(pc => pc.PageNumber == pg);
                            var ocrText = pageContent?.Text ?? string.Empty;
                            var ocrHas = !string.IsNullOrWhiteSpace(ocrText) && IsExactKeywordPresent(ocrText, canonicalKeyword);
                            var aiHas = !string.IsNullOrWhiteSpace(matchedText) && string.Equals(matchedText, canonicalKeyword, StringComparison.OrdinalIgnoreCase);

                            if (!ocrHas && !aiHas) continue;

                            var keywordSevMap = (keywordRules ?? new List<RuleEntity>())
                                .Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Text))
                                .GroupBy(r => r.Text.Trim(), StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => MapSeverity(g.First().Severity), StringComparer.OrdinalIgnoreCase);
                            var sev = keywordSevMap.TryGetValue(canonicalKeyword, out var sval) ? sval : SeverityLevel.Yellow;

                            var indices = ocrHas ? FindKeywordMatchIndices(ocrText, canonicalKeyword) : new List<int>();
                            if (indices.Count == 0)
                            {
                                var f = new Finding
                                {
                                    Term = canonicalKeyword,
                                    Category = "AI Keyword",
                                    Page = pg,
                                    Context = ctx2,
                                    IsReviewed = false,
                                    ReviewStatus = ReviewStatus.Pending,
                                    IsFalseFlag = isFalseFlag,
                                    FalseFlagReason = falseReason,
                                    Source = FindingSource.AI,
                                    Severity = sev,
                                    MatchIndex = null
                                };

                                if (!ocrHas && aiHas)
                                {
                                    f.IsFalseFlag = true;
                                    f.FalseFlagReason = "AI-only keyword detection (OCR did not confirm exact keyword). Verify visually; could be handwriting or a false positive.";
                                    var prefix = $"AI-only keyword detection (OCR missed). MatchedText: \"{matchedText}\".";
                                    f.Context = string.IsNullOrWhiteSpace(f.Context) ? prefix : (prefix + "\n\n" + f.Context);
                                }

                                allFindings.Add(f);
                            }
                            else
                            {
                                foreach (var mi in indices)
                                {
                                    allFindings.Add(new Finding
                                    {
                                        Term = canonicalKeyword,
                                        Category = "AI Keyword",
                                        Page = pg,
                                        Context = ctx2,
                                        IsReviewed = false,
                                        ReviewStatus = ReviewStatus.Pending,
                                        IsFalseFlag = isFalseFlag,
                                        FalseFlagReason = falseReason,
                                        Source = FindingSource.AI,
                                        Severity = sev,
                                        MatchIndex = mi
                                    });
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    Log("[AnalysisService] Failed to parse batched JSON response. Falling back to raw text.");
                }

                // Merge local findings (marked as false flags if AI also reviewed)
                if (localFindings.Any() && (devSettings?.AlwaysForwardToAI ?? false))
                {
                    foreach (var lf in localFindings)
                    {
                        lf.Context = $"Found '{lf.Term}' via local keyword search. AI also reviewed this document.";
                        lf.IsFalseFlag = true;
                        lf.FalseFlagReason = "Local keyword match - AI should review for false positive";
                    }
                    allFindings.AddRange(localFindings);
                }

                return new BatchAnalysisResult
                {
                    Findings = allFindings,
                    AgentOverview = overview
                };
            }

            // All 5 retries exhausted - throw an exception so the catch block generates a proper error card.
            var finalStatus = lastRetryStatus?.ToString() ?? "unknown";
            var finalBody = TruncateForLog(lastRetryErrorBody ?? "No response body", 500);
            throw new HttpRequestException(
                $"Gemini API request failed after 5 retries (last status: {finalStatus}). " +
                $"The API returned: {finalBody}");
        }
        catch (Exception ex)
        {
            Log($"[AnalysisService] Batched Gemini API exception: {ex}");

            var friendlyReason = ex switch
            {
                TaskCanceledException or OperationCanceledException =>
                    "The AI request timed out after 120 seconds. This can happen when:\n" +
                    "- The document is very large (10+ pages with images)\n" +
                    "- The Gemini API is experiencing high latency on the free tier\n" +
                    "- Network connectivity is intermittent\n\n" +
                    "Suggestions: Try again (the API may respond faster on a retry), reduce the document size, " +
                    "or check your internet connection.\n\n" +
                    $"Technical: {ex.GetType().Name} - {ex.Message}",
                HttpRequestException httpEx =>
                    "Network error connecting to the Gemini API. This typically means:\n" +
                    "- No internet connection, or\n" +
                    "- A firewall/proxy is blocking the request, or\n" +
                    "- The Gemini API endpoint is unreachable\n\n" +
                    $"Technical: {httpEx.GetType().Name} - {httpEx.Message}",
                _ =>
                    "An unexpected error occurred during AI analysis. This may be caused by:\n" +
                    "- API rate limits (429 Too Many Requests)\n" +
                    "- Invalid API key or expired credentials\n" +
                    "- Temporary Gemini API outage\n\n" +
                    "Check the Settings page to verify your API key and model name are correct.\n\n" +
                    $"Technical: {ex.GetType().Name} - {ex.Message}"
            };

            return new BatchAnalysisResult
            {
                Findings = new List<Finding>
                {
                    new Finding
                    {
                        Term = "AI Error",
                        Category = "AI Analysis Failed",
                        Page = 1,
                        Context = friendlyReason,
                        IsReviewed = false,
                        Source = FindingSource.AI
                    }
                }
            };
        }
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText("debug_log.txt", $"{DateTime.Now}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    private async Task<List<Finding>> AnalyzePageAsync(PageContent page, List<RuleEntity> keywordRules, List<RuleEntity> contextRules, DevSettings? devSettings, CancellationToken cancellationToken = default)
    {
        // Step 1: Local Heuristic (unless disabled in dev settings)
        // "1. local ocr runs on PDF BEFORE ai review" - (Already done, we have page.Text)

        var normalizedText = page.Text.ToLowerInvariant();
        var localFindings = new List<Finding>();

        // Debug logging
        Log($"[AnalysisService] Analyzing page {page.PageNumber}...");
        Log($"[AnalysisService] Keyword Rules (enabled): {string.Join(", ", keywordRules.Select(r => r.Text))}");
        Log($"[AnalysisService] Context Rules (enabled): {string.Join(", ", contextRules.Select(r => r.Text))}");
        Log($"[AnalysisService] Dev Settings: DisableLocalKeywordSearch={devSettings?.DisableLocalKeywordSearch ?? false}, AlwaysForwardToAI={devSettings?.AlwaysForwardToAI ?? false}, EnableAiBatching={devSettings?.EnableAiBatching ?? false}, StopAiOnFirstWarning={devSettings?.StopAiOnFirstWarning ?? true}");
        Log($"[AnalysisService] Page Text Sample: {normalizedText.Substring(0, Math.Min(100, normalizedText.Length))}...");

        // Only perform local keyword search if not disabled in dev settings
        if (!(devSettings?.DisableLocalKeywordSearch ?? false))
        {
            foreach (var rule in keywordRules.Where(r => r.IsEnabled))
            {
                var ruleText = (rule.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(ruleText)) continue;
                var ruleLower = ruleText.ToLowerInvariant();

                // Create a finding for EACH occurrence of the rule on the page (not just one per rule).
                int searchStart = 0;
                while (true)
                {
                    var idx = normalizedText.IndexOf(ruleLower, searchStart, StringComparison.Ordinal);
                    if (idx < 0) break;

                    Log($"[AnalysisService] Found local match: {ruleText} (index {idx})");

                    // Pull a small snippet from the original text around the match for better UX.
                    // (Index positions match because normalizedText is just ToLowerInvariant() of page.Text.)
                    var snippetStart = Math.Max(0, idx - 30);
                    var snippetEnd = Math.Min(page.Text.Length, idx + ruleText.Length + 30);
                    var snippet = page.Text.Substring(snippetStart, snippetEnd - snippetStart).Replace("\r", " ").Replace("\n", " ");

                    localFindings.Add(new Finding
                    {
                        // Preserve the casing from the document (OCR/PDF text), rather than the configured rule casing.
                        Term = page.Text.Substring(idx, ruleText.Length),
                        Category = "Local Flag",
                        Page = page.PageNumber,
                        Context = $"Found '{ruleText}' via local keyword search. Context: \"{snippet}\"",
                        IsReviewed = false,
                        IsFalseFlag = false,
                        Source = FindingSource.Local,
                        Severity = MapSeverity(rule.Severity),
                        MatchIndex = idx
                    });

                    // Move past this match (non-overlapping).
                    searchStart = idx + ruleLower.Length;
                    if (searchStart >= normalizedText.Length) break;
                }
            }
        }

        // Step 2: Decision Gate
        // "2. if it flags a term it is NEVER sent to AI for processing"
        // Unless "Always forward to AI" is enabled in dev settings
        bool shouldSkipAI = localFindings.Any() && !(devSettings?.AlwaysForwardToAI ?? false);

        if (shouldSkipAI)
        {
            Log($"[AnalysisService] Returning {localFindings.Count} local findings. AI skipped.");
            return localFindings;
        }

        Log("[AnalysisService] No local matches found or 'Always forward to AI' enabled. Proceeding to AI...");

        // Step 3: Gemini API Call
        // "3. if it does not find a term it sends it to AI."
        // Also send to AI if "Always forward to AI" is enabled
        var aiFindings = await CallGeminiApiAsync(page, keywordRules, contextRules, devSettings, cancellationToken);
        foreach (var f in aiFindings)
        {
            f.Source = FindingSource.AI;
        }

        // If we have local findings and AI was called (due to AlwaysForwardToAI),
        // mark the local findings as potential false flags for AI review
        if (localFindings.Any() && (devSettings?.AlwaysForwardToAI ?? false))
        {
            foreach (var localFinding in localFindings)
            {
                localFinding.Context = $"Found '{localFinding.Term}' via local keyword search. AI also reviewed this page.";
                localFinding.IsFalseFlag = true;
                localFinding.FalseFlagReason = "Local keyword match - AI should review for false positive";
            }
            aiFindings.AddRange(localFindings);
        }

        return aiFindings;
    }

    private async Task<List<Finding>> CallGeminiApiAsync(PageContent page, List<RuleEntity> keywordRules, List<RuleEntity> contextRules, DevSettings? devSettings, CancellationToken cancellationToken = default)
    {
        var apiKey = _configService.ApiKey;
        var requestedModel = _configService.AiModel;
        Log($"[AnalysisService] Gemini key={_configService.GetApiKeyDebugHint()} model={requestedModel}");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new List<Finding>
            {
                new Finding
                {
                    Term = "AI Config Error",
                    Category = "AI Not Configured",
                    Page = page.PageNumber,
                    Context =
                        "No Gemini API key is configured.\n\n" +
                        "Fix:\n" +
                        "- Set env var GEMINI_API_KEY to your paid project key, or\n" +
                        "- Add ApiKey to user_settings.json\n",
                    IsReviewed = false,
                    Source = FindingSource.AI
                }
            };
        }

        var keywordRuleTexts = (keywordRules ?? new List<RuleEntity>())
            .Where(r => r.IsEnabled)
            .Select(r => (r.Text ?? string.Empty).Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keywordSeverityMap = (keywordRules ?? new List<RuleEntity>())
            .Where(r => r.IsEnabled)
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .GroupBy(r => r.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => MapSeverity(g.First().Severity), StringComparer.OrdinalIgnoreCase);

        var contextRuleTexts = (contextRules ?? new List<RuleEntity>())
            .Where(r => r.IsEnabled)
            .Select(r => (r.Text ?? string.Empty).Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contextSeverityByIndex = (contextRules ?? new List<RuleEntity>())
            .Where(r => r.IsEnabled)
            .Select(r => MapSeverity(r.Severity))
            .ToList();

        var keywordBlock = keywordRuleTexts.Any()
            ? "\n\nKeyword rules (EXACT MATCH ONLY; do not infer/synonym-match):\n- " + string.Join("\n- ", keywordRuleTexts)
            : "\n\nKeyword rules: (none)";

        var contextRulesWithIds = contextRuleTexts
            .Select((r, i) => $"{i + 1}) {r.Trim()}")
            .ToList();
        var contextBlock = contextRulesWithIds.Any()
            ? "\n\nContext rules (broad/semantic; check EACH rule independently):\n- " + string.Join("\n- ", contextRulesWithIds)
            : "\n\nContext rules: (none)";

        var prompt = $@"Act as a medical intake analyst. You will be given a SINGLE PDF PAGE from a patient intake packet (page {page.PageNumber}).

You must follow these rules strictly:
1) For KEYWORD rules: only return a finding if you can visually locate the EXACT keyword/phrase on the page (case-insensitive is OK, but spelling/word form must be exact).
   - Do NOT fuzzy match or infer from similar words. Example: ""aggression"" is NOT the same as ""Aggressive"".
   - If the exact keyword is not present, DO NOT return it.
   - For EVERY keyword-rule finding, you MUST include a field ""matchedText"" that is the exact text copied from the page for the keyword (must equal the keyword, case-insensitive).
   - For EVERY keyword-rule finding, you MUST include a field ""evidence"": a brief explanation of WHY this keyword was flagged in this patient's context (not just the surrounding text - explain the clinical concern).
2) For CONTEXT rules: you may flag based on meaning/implication even if exact words do not appear.
   - For EVERY context-rule finding, you MUST include:
     - ""ruleIndex"": the numeric index of the violated context rule (1-based)
     - ""evidence"": the most relevant short quote/phrase from the page that triggered the rule

{keywordBlock}
{contextBlock}

IMPORTANT: Some terms might be false positives. For example, ""Patient is not violent"" contains the word ""violent"" but is actually stating the opposite.

Return ONLY a JSON object with this structure:
{{
  ""findings"": [
    {{
      ""kind"": ""keyword"",
      ""keyword"": ""(must exactly match one of the keyword rules)"",
      ""matchedText"": ""exact copied keyword text"",
      ""page"": {page.PageNumber},
      ""evidence"": ""short quote showing why the keyword was flagged"",
      ""context"": ""brief context"",
      ""isFalseFlag"": true/false,
      ""falseFlagReason"": ""reason if false flag""
    }},
    {{
      ""kind"": ""context"",
      ""ruleIndex"": 1,
      ""evidence"": ""short evidence phrase from the page"",
      ""page"": {page.PageNumber},
      ""context"": ""brief context""
    }}
  ]
}}
If no findings, return {{ ""findings"": [] }}.

For each finding, analyze if it might be a false positive:
- If the context suggests the term is used in a negative or opposite sense (e.g., ""not violent"", ""no aggression""), set isFalseFlag to true
- Provide a brief reason in falseFlagReason (e.g., ""Term used in negative context: 'not violent'"")";

        // Attach the page as a true 1-page PDF (base64). This allows Gemini to run its own interpretation,
        // including handwritten notes where present.
        object requestBody;
        if (page.PagePdfBytes != null && page.PagePdfBytes.Length > 0)
        {
            var pdfBase64 = Convert.ToBase64String(page.PagePdfBytes);
            requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { inline_data = new { mime_type = "application/pdf", data = pdfBase64 } }
                        }
                    }
                },
                generationConfig = new { temperature = 0.0, topP = 0.1 }
            };
        }
        else
        {
            // Fallback: if we couldn't extract a 1-page PDF, use extracted text (less reliable for handwriting).
            Log($"[AnalysisService] WARNING: Page {page.PageNumber} has no PagePdfBytes; falling back to extracted text.");
            requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt + "\n\nNOTE: PDF bytes were unavailable; use the following extracted text (handwriting may be missing):\n" + page.Text }
                        }
                    }
                },
                generationConfig = new { temperature = 0.0, topP = 0.1 }
            };
        }

        try
        {
            // Model is pinned via ConfigurationService. Keep the list as a single entry to ensure
            // we never fall back to deprecated model names (which can cause 404s).
            var modelsToTry = new List<string> { requestedModel }
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? lastErrorBody = null;
            HttpStatusCode? lastStatus = null;

            foreach (var model in modelsToTry)
            {
                var url = BuildGenerateContentUrl(apiKey, model);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await _geminiGate.WaitAsync();
                    HttpResponseMessage response;
                    string responseString;
                    var overviewCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                        response = await _httpClient.PostAsync(url, jsonContent, overviewCts.Token);
                        responseString = await response.Content.ReadAsStringAsync(overviewCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Log($"[AnalysisService] Agent overview API request timed out or was cancelled.");
                        throw;
                    }
                    finally
                    {
                        overviewCts.Dispose();
                        _geminiGate.Release();
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        lastStatus = response.StatusCode;
                        lastErrorBody = responseString;
                        Log($"[AnalysisService] Gemini API error {response.StatusCode} (model={model}): {TruncateForLog(responseString, 2000)}");

                        // If it's a model-not-found, try next model.
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            break;
                        }

                        // On 429, retry same model a couple times before moving on.
                        if (response.StatusCode == (HttpStatusCode)429)
                        {
                            var retryAfter = TryParseRetryAfterSeconds(responseString);
                            await Task.Delay(ComputeBackoffMs(attempt, retryAfter));
                            if (attempt < 2) continue;
                            break; // try next model
                        }

                        // Non-retryable: stop immediately with an error finding.
                        return new List<Finding> { CreateGeminiErrorFinding(page.PageNumber, response.StatusCode, responseString) };
                    }

                    // Success: parse response
                    var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseString);
            
                    var textPart = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                    if (string.IsNullOrEmpty(textPart)) return new List<Finding>();

                    // Clean up Markdown code blocks if Gemini wraps the JSON
                    textPart = textPart.Replace("```json", "").Replace("```", "").Trim();
                    textPart = Regex.Replace(textPart, @"<thinking>.*?</thinking>", "", RegexOptions.Singleline).Trim();

                    var parsed = ParseGeminiFindings(
                        textPart,
                        page.PageNumber,
                        keywordRuleTexts,
                        contextRuleTexts,
                        page.Text,
                        keywordSeverityMap,
                        contextSeverityByIndex);
                    return parsed;
                }
            }

            // All models failed (likely model deprecated)
            if (lastStatus != null && lastErrorBody != null)
            {
                return new List<Finding> { CreateGeminiErrorFinding(page.PageNumber, lastStatus.Value, lastErrorBody) };
            }

            return new List<Finding>
            {
                new Finding
                {
                    Term = "AI Error",
                    Category = "AI Analysis Failed",
                    Page = page.PageNumber,
                    Context = "AI request failed for an unknown reason.",
                    IsReviewed = false,
                    Source = FindingSource.AI
                }
            };
        }
        catch (Exception ex)
        {
            Log($"[AnalysisService] Gemini API exception: {ex}");
            
            // Return a system error finding so the user knows why it wasn't analyzed
            return new List<Finding>
            {
                new Finding
                {
                    Term = "AI Error",
                    Category = "AI Analysis Failed",
                    Page = page.PageNumber,
                    Context = $"AI service could not process this page. Details: {ex.Message}",
                    IsReviewed = false,
                    Source = FindingSource.AI
                }
            };
        }
    }

    private static string TruncateForLog(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= maxChars) return value;
        return value.Substring(0, maxChars) + "...(truncated)";
    }

    private static Finding CreateGeminiErrorFinding(int pageNumber, HttpStatusCode statusCode, string responseBody)
    {
        var title = "AI Error";
        var category = "AI Analysis Failed";
        var details = ExtractGeminiErrorMessage(responseBody) ?? responseBody;

        if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
        {
            title = "AI Auth Error";
            category = "AI Authentication Failed";
        }
        else if (statusCode == HttpStatusCode.NotFound)
        {
            title = "AI Model Error";
            category = "AI Model Not Found";
        }
        else if (statusCode == (HttpStatusCode)429)
        {
            title = "AI Quota Error";
            category = "AI Quota / Rate Limit";
        }

        return new Finding
        {
            Term = title,
            Category = category,
            Page = pageNumber,
            Context =
                $"Gemini API request failed ({(int)statusCode} {statusCode}).\n\n" +
                $"{details}\n\n" +
                "Common causes:\n" +
                "- Invalid/restricted API key\n" +
                "- Billing/quota not enabled for the project (free tier limits can be 0)\n" +
                "- Model name deprecated (try changing GEMINI_MODEL)\n\n" +
                "If you see 'free_tier_* limit: 0' but your dashboard shows Paid tier limits, you're almost certainly using a different API key/project than the one you're viewing.\n",
            IsReviewed = false,
            Source = FindingSource.AI
        };
    }

    private static string? ExtractGeminiErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static bool IsExactKeywordPresent(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(keyword)) return false;

        var escaped = Regex.Escape(keyword.Trim());

        // Only add \b boundaries when the keyword starts/ends with a word char. This avoids odd behavior
        // for keywords that begin/end with punctuation.
        var startsWithWord = char.IsLetterOrDigit(keyword[0]) || keyword[0] == '_';
        var endsWithWord = char.IsLetterOrDigit(keyword[keyword.Length - 1]) || keyword[keyword.Length - 1] == '_';

        var pattern = (startsWithWord ? @"\b" : "") + escaped + (endsWithWord ? @"\b" : "");
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static List<int> FindKeywordMatchIndices(string text, string keyword)
    {
        var results = new List<int>();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(keyword)) return results;

        var escaped = Regex.Escape(keyword.Trim());
        var startsWithWord = char.IsLetterOrDigit(keyword[0]) || keyword[0] == '_';
        var endsWithWord = char.IsLetterOrDigit(keyword[keyword.Length - 1]) || keyword[keyword.Length - 1] == '_';
        var pattern = (startsWithWord ? @"\b" : "") + escaped + (endsWithWord ? @"\b" : "");

        foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            results.Add(m.Index);
        }

        return results;
    }

    private static List<string?>? TryExtractMatchedTexts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("findings", out var findingsEl) || findingsEl.ValueKind != JsonValueKind.Array) return null;

            var list = new List<string?>();
            foreach (var el in findingsEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty("matchedText", out var mt) &&
                    mt.ValueKind == JsonValueKind.String)
                {
                    list.Add(mt.GetString());
                }
                else
                {
                    list.Add(null);
                }
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    private static List<Finding> ParseGeminiFindings(
        string json,
        int pageNumber,
        List<string> keywordRules,
        List<string> contextRules,
        string ocrText,
        Dictionary<string, SeverityLevel> keywordSeverityMap,
        List<SeverityLevel> contextSeverityByIndex)
    {
        var results = new List<Finding>();
        var keywordSet = new HashSet<string>(keywordRules.Select(r => r.Trim()), StringComparer.OrdinalIgnoreCase);
        var contextSet = new HashSet<string>(contextRules.Select(r => r.Trim()), StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("findings", out var arr) || arr.ValueKind != JsonValueKind.Array) return results;

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? (k.GetString() ?? "") : "";

                if (string.Equals(kind, "context", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer ruleIndex for deterministic matching across multiple context rules.
                    var ruleIndex = el.TryGetProperty("ruleIndex", out var ri) && ri.ValueKind == JsonValueKind.Number ? ri.GetInt32() : 0;
                    if (ruleIndex < 1 || ruleIndex > contextRules.Count) continue;
                    var rule = contextRules[ruleIndex - 1].Trim();
                    if (string.IsNullOrWhiteSpace(rule)) continue;

                    var evidence = el.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String ? (ev.GetString() ?? "") : "";
                    evidence = evidence.Trim();

                    var ctx = el.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";
                    ctx = ctx.Trim();
                    var combined = string.IsNullOrWhiteSpace(evidence) ? ctx : (string.IsNullOrWhiteSpace(ctx) ? evidence : (evidence + "\n\n" + ctx));

                    // IMPORTANT: This method is called for a SINGLE page batch; always trust the batch page number.
                    // Some model outputs will incorrectly return page=1 for all findings.
                    var pg = pageNumber;

                    results.Add(new Finding
                    {
                        Term = rule,
                        Category = "Context Rule",
                        Page = pg,
                        Context = combined,
                        IsReviewed = false,
                        ReviewStatus = ReviewStatus.Pending,
                        IsFalseFlag = false,
                        Severity = (contextSeverityByIndex != null && contextSeverityByIndex.Count >= ruleIndex)
                            ? contextSeverityByIndex[ruleIndex - 1]
                            : SeverityLevel.Yellow,
                        Source = FindingSource.AI
                    });
                    continue;
                }

                // Default: keyword
                var keyword = el.TryGetProperty("keyword", out var kw) && kw.ValueKind == JsonValueKind.String
                    ? (kw.GetString() ?? "")
                    : (el.TryGetProperty("term", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "") : "");
                keyword = keyword.Trim();
                if (string.IsNullOrWhiteSpace(keyword) || !keywordSet.Contains(keyword)) continue;

                var matchedText = el.TryGetProperty("matchedText", out var mt) && mt.ValueKind == JsonValueKind.String ? (mt.GetString() ?? "") : "";
                matchedText = matchedText.Trim();

                var evidence2 = el.TryGetProperty("evidence", out var ev2) && ev2.ValueKind == JsonValueKind.String ? (ev2.GetString() ?? "") : "";
                evidence2 = evidence2.Trim();

                var ctx2 = el.TryGetProperty("context", out var c2) && c2.ValueKind == JsonValueKind.String ? (c2.GetString() ?? "") : "";
                ctx2 = ctx2.Trim();
                ctx2 = string.IsNullOrWhiteSpace(evidence2) ? ctx2 : (string.IsNullOrWhiteSpace(ctx2) ? evidence2 : (evidence2 + "\n\n" + ctx2));
                var isFalseFlag = el.TryGetProperty("isFalseFlag", out var iff) && (iff.ValueKind == JsonValueKind.True || iff.ValueKind == JsonValueKind.False) && iff.GetBoolean();
                var falseReason = el.TryGetProperty("falseFlagReason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

                // IMPORTANT: Single page batch → force page number to the batch page
                var pg2 = pageNumber;

                var canonicalKeyword = keywordRules.First(x => string.Equals(x.Trim(), keyword, StringComparison.OrdinalIgnoreCase)).Trim();
                var ocrHas = !string.IsNullOrWhiteSpace(ocrText) && IsExactKeywordPresent(ocrText, canonicalKeyword);
                var aiHas = !string.IsNullOrWhiteSpace(matchedText) && string.Equals(matchedText, canonicalKeyword, StringComparison.OrdinalIgnoreCase);

                if (!ocrHas && !aiHas)
                {
                    // Drop fuzzy/unsupported keyword matches
                    continue;
                }

                // Expand into one per OCR occurrence when possible (keeps cards distinct)
                var indices = ocrHas ? FindKeywordMatchIndices(ocrText, canonicalKeyword) : new List<int>();
                if (indices.Count == 0)
                {
                    keywordSeverityMap ??= new Dictionary<string, SeverityLevel>(StringComparer.OrdinalIgnoreCase);
                    var sev = keywordSeverityMap.TryGetValue(canonicalKeyword, out var s) ? s : SeverityLevel.Yellow;

                    var f = new Finding
                    {
                        Term = canonicalKeyword,
                        Category = "AI Keyword",
                        Page = pg2,
                        Context = ctx2,
                        IsReviewed = false,
                        ReviewStatus = ReviewStatus.Pending,
                        IsFalseFlag = isFalseFlag,
                        FalseFlagReason = falseReason,
                        Source = FindingSource.AI,
                        Severity = sev,
                        MatchIndex = null
                    };

                    if (!ocrHas && aiHas)
                    {
                        f.IsFalseFlag = true;
                        f.FalseFlagReason = "AI-only keyword detection (OCR did not confirm exact keyword). Verify visually; could be handwriting or a false positive.";
                        var prefix = $"AI-only keyword detection (OCR missed). MatchedText: \"{matchedText}\".";
                        f.Context = string.IsNullOrWhiteSpace(f.Context) ? prefix : (prefix + "\n\n" + f.Context);
                    }

                    results.Add(f);
                }
                else
                {
                    keywordSeverityMap ??= new Dictionary<string, SeverityLevel>(StringComparer.OrdinalIgnoreCase);
                    var sev = keywordSeverityMap.TryGetValue(canonicalKeyword, out var s) ? s : SeverityLevel.Yellow;

                    foreach (var mi in indices)
                    {
                        results.Add(new Finding
                        {
                            Term = canonicalKeyword,
                            Category = "AI Keyword",
                            Page = pg2,
                            Context = ctx2,
                            IsReviewed = false,
                            ReviewStatus = ReviewStatus.Pending,
                            IsFalseFlag = isFalseFlag,
                            FalseFlagReason = falseReason,
                            Source = FindingSource.AI,
                            Severity = sev,
                            MatchIndex = mi
                        });
                    }
                }
            }
        }
        catch
        {
            // ignore parse failures
        }

        return results;
    }

    // Helper classes for Gemini JSON parsing
    private class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; set; }
    }

    private class Candidate
    {
        [JsonPropertyName("content")]
        public Content? Content { get; set; }
    }

    private class Content
    {
        [JsonPropertyName("parts")]
        public List<Part>? Parts { get; set; }
    }

    private class Part
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class GeminiResult
    {
        public List<Finding>? Findings { get; set; }
    }
}

