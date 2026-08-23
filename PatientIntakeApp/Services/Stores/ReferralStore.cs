using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PatientIntakeApp.Services.Stores;

public record CreateReferralRequest(
    string FacilityLegacyId,
    string SourceFileName,
    string SourceFilePath,
    string? PatientFirstName,
    string? PatientLastName,
    DateTime? PatientDob,
    string? ExternalMrn
);

public class CreateReferralResult
{
    public ReferralEntity Referral { get; init; } = new ReferralEntity();
    public bool IsExactDuplicateByHash { get; init; }
    public List<ReferralEntity> PotentialDuplicates { get; init; } = new();
}

public interface IReferralStore
{
    Task<CreateReferralResult> CreateReferralAsync(CreateReferralRequest request, Guid? actorUserId);
    Task<List<ReferralEntity>> FindExistingReferralsByFileHashAsync(string sourceFilePath);
    Task<List<ReferralEntity>> ListQueueAsync(ReferralStatus? status, Guid? assigneeUserId);
    Task<ReferralEntity?> GetByIdAsync(Guid referralId);
    Task<ReferralEntity?> GetBySourceFilePathAsync(string sourceFilePath);
    Task AssignAsync(Guid referralId, Guid? assigneeUserId, Guid? actorUserId);
    Task UpdateStatusAsync(Guid referralId, ReferralStatus status, Guid? actorUserId);
    Task DeleteAsync(Guid referralId, Guid? actorUserId);
}

public class ReferralStore : IReferralStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public ReferralStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<CreateReferralResult> CreateReferralAsync(CreateReferralRequest request, Guid? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(request.FacilityLegacyId)) throw new ArgumentException("FacilityLegacyId required");
        if (string.IsNullOrWhiteSpace(request.SourceFileName)) throw new ArgumentException("SourceFileName required");
        if (string.IsNullOrWhiteSpace(request.SourceFilePath)) throw new ArgumentException("SourceFilePath required");

        var fileHash = ComputeSha256Hex(request.SourceFilePath);
        var patientKey = NormalizePatientKey(request.PatientFirstName, request.PatientLastName, request.PatientDob, request.ExternalMrn);

        await using var db = await _dbFactory.CreateDbContextAsync();

        var facility = await db.Facilities.FirstOrDefaultAsync(f => f.LegacyId == request.FacilityLegacyId.Trim());
        if (facility == null)
        {
            // If config facilities changed at runtime, allow creating a matching facility row.
            facility = new FacilityEntity { LegacyId = request.FacilityLegacyId.Trim(), Name = request.FacilityLegacyId.Trim() };
            db.Facilities.Add(facility);
            await db.SaveChangesAsync();
        }

        var patient = await db.Patients.FirstOrDefaultAsync(p => p.NormalizedKey == patientKey);
        if (patient == null)
        {
            patient = new PatientEntity
            {
                ExternalMrn = string.IsNullOrWhiteSpace(request.ExternalMrn) ? null : request.ExternalMrn.Trim(),
                FirstName = string.IsNullOrWhiteSpace(request.PatientFirstName) ? null : request.PatientFirstName.Trim(),
                LastName = string.IsNullOrWhiteSpace(request.PatientLastName) ? null : request.PatientLastName.Trim(),
                Dob = request.PatientDob,
                NormalizedKey = patientKey
            };
            db.Patients.Add(patient);
            await db.SaveChangesAsync();
        }

        // Duplicate candidates
        var exactDup = await db.Referrals
            .AsNoTracking()
            .Where(r => r.SourceFileHash == fileHash)
            .OrderByDescending(r => r.IngestedAt)
            .FirstOrDefaultAsync();

        var potential = await db.Referrals
            .AsNoTracking()
            .Where(r => r.PatientId == patient.Id)
            .OrderByDescending(r => r.IngestedAt)
            .Take(10)
            .ToListAsync();

        // Flag duplicates (hash is strongest, else patient-key match).
        var isExactDuplicateByHash = exactDup != null;
        var isPatientDuplicate = !isExactDuplicateByHash && potential.Count > 0;
        var duplicateOfReferralId = isExactDuplicateByHash
            ? exactDup!.Id
            : (isPatientDuplicate ? potential.First().Id : (Guid?)null);

        var referral = new ReferralEntity
        {
            FacilityId = facility.Id,
            PatientId = patient.Id,
            SourceFileName = request.SourceFileName.Trim(),
            SourceFilePath = request.SourceFilePath.Trim(),
            SourceFileHash = fileHash,
            IngestedAt = DateTime.UtcNow,
            Status = ReferralStatus.New,
            DuplicateOfReferralId = duplicateOfReferralId
        };
        db.Referrals.Add(referral);
        await db.SaveChangesAsync();

        await AddEventAsync(db, referral.Id, ReferralEventType.Created, actorUserId, payloadJson: null);
        if (isExactDuplicateByHash)
        {
            await AddEventAsync(db, referral.Id, ReferralEventType.DuplicateFlagged, actorUserId,
                payloadJson: $"{{\"reason\":\"hash\",\"duplicateOf\":\"{duplicateOfReferralId}\"}}");
        }
        else if (isPatientDuplicate)
        {
            await AddEventAsync(db, referral.Id, ReferralEventType.DuplicateFlagged, actorUserId,
                payloadJson: $"{{\"reason\":\"patientKey\",\"duplicateOf\":\"{duplicateOfReferralId}\"}}");
        }

        await db.SaveChangesAsync();

        return new CreateReferralResult
        {
            Referral = referral,
            IsExactDuplicateByHash = isExactDuplicateByHash,
            PotentialDuplicates = potential
        };
    }

    public async Task<List<ReferralEntity>> FindExistingReferralsByFileHashAsync(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath)) return new List<ReferralEntity>();
        if (!File.Exists(sourceFilePath)) return new List<ReferralEntity>();

        var hash = ComputeSha256Hex(sourceFilePath);
        if (string.IsNullOrWhiteSpace(hash)) return new List<ReferralEntity>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Referrals
            .Include(r => r.Facility)
            .Include(r => r.Patient)
            .AsNoTracking()
            .Where(r => r.SourceFileHash == hash)
            .OrderByDescending(r => r.IngestedAt)
            .Take(25)
            .ToListAsync();
    }

    public async Task<List<ReferralEntity>> ListQueueAsync(ReferralStatus? status, Guid? assigneeUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var q = db.Referrals
            .Include(r => r.Patient)
            .Include(r => r.Facility)
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue) q = q.Where(r => r.Status == status.Value);
        if (assigneeUserId.HasValue) q = q.Where(r => r.CurrentAssigneeUserId == assigneeUserId.Value);

        return await q
            .OrderByDescending(r => r.IngestedAt)
            .Take(500)
            .ToListAsync();
    }

    public async Task<ReferralEntity?> GetByIdAsync(Guid referralId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Referrals
            .Include(r => r.Patient)
            .Include(r => r.Facility)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == referralId);
    }

    public async Task<ReferralEntity?> GetBySourceFilePathAsync(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var path = sourceFilePath.Trim();
        return await db.Referrals
            .Include(r => r.Patient)
            .Include(r => r.Facility)
            .AsNoTracking()
            .OrderByDescending(r => r.IngestedAt)
            .FirstOrDefaultAsync(r => r.SourceFilePath == path);
    }

    public async Task AssignAsync(Guid referralId, Guid? assigneeUserId, Guid? actorUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.Id == referralId);
        if (referral == null) return;

        var previousAssignee = referral.CurrentAssigneeUserId;
        referral.CurrentAssigneeUserId = assigneeUserId;
        referral.UpdatedAt = DateTime.UtcNow;

        var eventType =
            previousAssignee.HasValue && assigneeUserId.HasValue && previousAssignee.Value != assigneeUserId.Value
                ? ReferralEventType.Transferred
                : assigneeUserId.HasValue
                    ? ReferralEventType.Assigned
                    : ReferralEventType.Unassigned;

        var payload =
            eventType == ReferralEventType.Transferred
                ? $"{{\"fromUserId\":\"{previousAssignee!.Value}\",\"toUserId\":\"{assigneeUserId!.Value}\"}}"
                : assigneeUserId.HasValue
                    ? $"{{\"assigneeUserId\":\"{assigneeUserId.Value}\"}}"
                    : null;

        await AddEventAsync(db, referral.Id, eventType, actorUserId, payloadJson: payload);

        await db.SaveChangesAsync();
    }

    public async Task UpdateStatusAsync(Guid referralId, ReferralStatus status, Guid? actorUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.Id == referralId);
        if (referral == null) return;

        referral.Status = status;
        referral.UpdatedAt = DateTime.UtcNow;
        await AddEventAsync(db, referral.Id, ReferralEventType.StatusChanged, actorUserId, payloadJson: $"{{\"status\":{(int)status}}}");

        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid referralId, Guid? actorUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.Id == referralId);
        if (referral == null) return;

        await AddEventAsync(db, referral.Id, ReferralEventType.Deleted, actorUserId, payloadJson: null);
        await db.SaveChangesAsync();

        db.Referrals.Remove(referral);
        await db.SaveChangesAsync();
    }

    private static string NormalizePatientKey(string? first, string? last, DateTime? dob, string? mrn)
    {
        // Prefer MRN if present; else use name+DOB.
        if (!string.IsNullOrWhiteSpace(mrn))
        {
            return "MRN:" + NormalizeToken(mrn);
        }

        // If we have no usable identifiers, do NOT collapse all patients into one bucket.
        // This avoids spamming false duplicates when the user hasn't entered patient info yet.
        if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(last) && !dob.HasValue)
        {
            return "ANON:" + Guid.NewGuid().ToString("N");
        }

        var dobToken = dob.HasValue ? dob.Value.ToString("yyyyMMdd") : "UNKNOWNDOB";
        return $"NAME:{NormalizeToken(last)}:{NormalizeToken(first)}:DOB:{dobToken}";
    }

    private static string NormalizeToken(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "UNKNOWN";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.Length == 0 ? "UNKNOWN" : sb.ToString();
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static Task AddEventAsync(PatientIntakeDbContext db, Guid referralId, ReferralEventType type, Guid? actorUserId, string? payloadJson)
    {
        db.ReferralEvents.Add(new ReferralEventEntity
        {
            ReferralId = referralId,
            Type = type,
            ActorUserId = actorUserId,
            At = DateTime.UtcNow,
            PayloadJson = payloadJson
        });
        return Task.CompletedTask;
    }
}