using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services.Stores;

public interface IUserStore
{
    Task<UserEntity?> FindActiveByUsernameAsync(string username);
    Task<List<UserEntity>> ListActiveUsersAsync();
}

public class UserStore : IUserStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public UserStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<UserEntity?> FindActiveByUsernameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.IsActive && u.Username == username.Trim());
    }

    public async Task<List<UserEntity>> ListActiveUsersAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Username)
            .ToListAsync();
    }
}

