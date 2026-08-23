namespace PatientIntakeApp.Data.Entities;

public enum ReferralStatus
{
    New = 0,
    InProgress = 1,
    Paused = 2,
    NeedsSme = 3,
    Completed = 4,
    Rejected = 5
}

public enum ReviewSessionState
{
    InProgress = 0,
    Paused = 1,
    Completed = 2
}

public enum RuleKind
{
    Keyword = 0,
    Context = 1
}

public enum RuleSeverity
{
    Green = 0,
    Yellow = 1,
    Red = 2
}

public enum ReferralEventType
{
    Created = 0,
    Assigned = 1,
    Unassigned = 2,
    Transferred = 3,
    StatusChanged = 4,
    ReviewPaused = 5,
    ReviewResumed = 6,
    ReviewCompleted = 7,
    DuplicateFlagged = 8,
    ExternalCheckRequested = 9,
    ExternalCheckCompleted = 10,
    Deleted = 11
}

public enum ExternalCheckType
{
    Financial = 0,
    Litigation = 1,
    Criminal = 2
}

public enum ExternalCheckResultStatus
{
    Pending = 0,
    Success = 1,
    Failed = 2
}

