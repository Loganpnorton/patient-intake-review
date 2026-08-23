using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services.Stores;

public interface IFacilityStore
{
    Task<FacilityEntity?> FindByLegacyIdAsync(string legacyId);
    Task<List<FacilityEntity>> ListAsync();
}

public class FacilityStore : IFacilityStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public FacilityStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<FacilityEntity?> FindByLegacyIdAsync(string legacyId)
    {
        if (string.IsNullOrWhiteSpace(legacyId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Facilities.AsNoTracking().FirstOrDefaultAsync(f => f.LegacyId == legacyId.Trim());
    }

    public async Task<List<FacilityEntity>> ListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Facilities.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
    }
}

