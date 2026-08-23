using CommunityToolkit.Mvvm.ComponentModel;

namespace PatientIntakeApp.Models;

public class Facility
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Rules { get; set; } = new List<string>();
    // Contextual (non-keyword) rules/instructions that are provided to the AI prompt.
    // These are NOT used for local keyword matching.
    public List<string> ContextRules { get; set; } = new List<string>();
}

public class Finding : ObservableObject
{
    private string _term = string.Empty;
    public string Term
    {
        get => _term;
        set => SetProperty(ref _term, value);
    }

    private string _category = string.Empty;
    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    private int _page;
    public int Page
    {
        get => _page;
        set => SetProperty(ref _page, value);
    }

    private string _context = string.Empty;
    public string Context
    {
        get => _context;
        set => SetProperty(ref _context, value);
    }

    private bool _isReviewed;
    public bool IsReviewed
    {
        get => _isReviewed;
        set => SetProperty(ref _isReviewed, value);
    }

    private ReviewStatus _reviewStatus = ReviewStatus.Pending;
    public ReviewStatus ReviewStatus
    {
        get => _reviewStatus;
        set => SetProperty(ref _reviewStatus, value);
    }

    private SeverityLevel _severity = SeverityLevel.Yellow;
    public SeverityLevel Severity
    {
        get => _severity;
        set => SetProperty(ref _severity, value);
    }

    private bool _isFalseFlag;
    public bool IsFalseFlag
    {
        get => _isFalseFlag;
        set => SetProperty(ref _isFalseFlag, value);
    }

    private string? _falseFlagReason;
    public string? FalseFlagReason
    {
        get => _falseFlagReason;
        set => SetProperty(ref _falseFlagReason, value);
    }

    private FindingSource _source = FindingSource.Unknown;
    public FindingSource Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    // Optional: start index of the matched term in extracted page text.
    // Used to keep multiple occurrences of the same term on a single page as distinct cards.
    public int? MatchIndex { get; set; }
}

public class FindingGroup : ObservableObject
{
    public string Term { get; set; } = string.Empty;
    public int Page { get; set; }
    public int? MatchIndex { get; set; }
    public List<Finding> Findings { get; set; } = new List<Finding>();

    public bool HasLocal => Findings.Any(f => f.Source == FindingSource.Local);
    public bool HasAI => Findings.Any(f => f.Source == FindingSource.AI);

    // Tiered flagging system: show the highest severity present in this group.
    // Enum values are ordered Green(0) < Yellow(1) < Red(2), so Max() gives the "worst" tier.
    public SeverityLevel Severity => Findings.Count == 0 ? SeverityLevel.Yellow : Findings.Max(f => f.Severity);

    public bool IsReviewed => Findings.Count > 0 && Findings.All(f => f.IsReviewed);

    private Finding? ContextRuleFinding =>
        Findings.FirstOrDefault(f => string.Equals(f.Category, "Context Rule", StringComparison.OrdinalIgnoreCase));

    public string DisplayTitle
    {
        get
        {
            // Context rule findings: show evidence first line as title
            var ctx = ContextRuleFinding;
            if (ctx != null)
            {
                var evidence = (ctx.Context ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    var firstLine = evidence.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? "";
                    if (!string.IsNullOrWhiteSpace(firstLine)) return firstLine.Trim();
                }
            }
            return Term;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            // Context rule findings
            var ctx = ContextRuleFinding;
            if (ctx != null)
            {
                var rule = (ctx.Term ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(rule))
                {
                    return $"Context Rule Violation: {rule}";
                }
            }

            // Keyword / AI findings: prefer AI-sourced finding for evidence
            var aiFinding = Findings.FirstOrDefault(f => f.Source == FindingSource.AI);
            if (aiFinding != null && !string.IsNullOrWhiteSpace(aiFinding.Context))
            {
                var firstLine = aiFinding.Context.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? "";
                if (!string.IsNullOrWhiteSpace(firstLine) && !firstLine.StartsWith("Found '", StringComparison.OrdinalIgnoreCase) && !firstLine.StartsWith("AI-only keyword detection", StringComparison.OrdinalIgnoreCase))
                {
                    return $"AI cited: {firstLine.Trim()}";
                }
            }

            return string.Empty;
        }
    }

    public ReviewStatus ReviewStatus
    {
        get
        {
            if (Findings.Count == 0) return ReviewStatus.Pending;
            if (!IsReviewed) return ReviewStatus.Pending;
            var first = Findings[0].ReviewStatus;
            return Findings.All(f => f.ReviewStatus == first) ? first : ReviewStatus.Pending;
        }
    }

    public string ContextSummary
    {
        get
        {
            var contexts = Findings
                .Select(f => f.Context ?? string.Empty)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .Take(3)
                .ToList();
            return contexts.Count == 0 ? string.Empty : string.Join("\n\n", contexts);
        }
    }
}

public enum FindingSource
{
    Unknown = 0,
    Local = 1,
    AI = 2
}

public enum SeverityLevel
{
    Green = 0,
    Yellow = 1,
    Red = 2
}

public enum ReviewStatus
{
    Pending,
    Passed,
    Rejected
}

public class StagedFile
{
    public string FilePath { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
}

public class RecentPatient
{
    public string FileName { get; set; } = string.Empty;
    public DateTime ProcessedDate { get; set; }
}

public class PageContent
{
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    // Raw bytes for a single-page PDF extracted from the source document.
    // Used for AI analysis so the model can perform its own interpretation (including handwriting).
    public byte[]? PagePdfBytes { get; set; }
}

public class AnalysisResult
{
    public string FilePath { get; set; } = string.Empty;
    public List<Finding> Findings { get; set; } = new List<Finding>();
    public bool IsProcessed { get; set; }
    public bool HasErrors { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public class ContextRuleViolation
{
    // Must match an entry from Facility.ContextRules exactly (case-insensitive matching is handled in code).
    public string Rule { get; set; } = string.Empty;
    public int? RuleIndex { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public int? Page { get; set; }
}

public class AgentOverviewResult
{
    public string Overview { get; set; } = string.Empty;
    public List<ContextRuleViolation> ContextRuleViolations { get; set; } = new List<ContextRuleViolation>();
}

public class DevSettings : ObservableObject
{
    private bool _disableLocalKeywordSearch;
    public bool DisableLocalKeywordSearch
    {
        get => _disableLocalKeywordSearch;
        set => SetProperty(ref _disableLocalKeywordSearch, value);
    }

    private bool _alwaysForwardToAI;
    public bool AlwaysForwardToAI
    {
        get => _alwaysForwardToAI;
        set => SetProperty(ref _alwaysForwardToAI, value);
    }

    private bool _isDevMenuVisible;
    public bool IsDevMenuVisible
    {
        get => _isDevMenuVisible;
        set => SetProperty(ref _isDevMenuVisible, value);
    }

    private bool _disableAutoLogout;
    public bool DisableAutoLogout
    {
        get => _disableAutoLogout;
        set => SetProperty(ref _disableAutoLogout, value);
    }

    private bool _enableAiBatching;
    public bool EnableAiBatching
    {
        get => _enableAiBatching;
        set => SetProperty(ref _enableAiBatching, value);
    }

    private bool _stopAiOnFirstWarning = true;
    public bool StopAiOnFirstWarning
    {
        get => _stopAiOnFirstWarning;
        set => SetProperty(ref _stopAiOnFirstWarning, value);
    }

    private int _aiBatchPageLimit = 1;
    public int AiBatchPageLimit
    {
        get => _aiBatchPageLimit;
        set => SetProperty(ref _aiBatchPageLimit, Math.Max(1, value));
    }

    private bool _showRecentlyReviewed;
    public bool ShowRecentlyReviewed
    {
        get => _showRecentlyReviewed;
        set => SetProperty(ref _showRecentlyReviewed, value);
    }
}

public class BatchAnalysisResult
{
    public List<Finding> Findings { get; set; } = new();
    public AgentOverviewResult? AgentOverview { get; set; }
}

public class AppUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
}

public enum UserRole
{
    User = 0,
    Admin = 1,
    Developer = 2
}


