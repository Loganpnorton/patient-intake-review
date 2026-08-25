using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task FacilityAndRulesPersistAcrossContexts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PatientIntakeDbContext>().UseSqlite(connection).Options;
        var facilityId = Guid.NewGuid();
        await using (var write = new PatientIntakeDbContext(options))
        {
            await write.Database.EnsureCreatedAsync();
            write.Facilities.Add(new FacilityEntity
            {
                Id = facilityId,
                LegacyId = "FACILITY_ALPHA",
                Name = "Synthetic Facility",
                Rules = [new RuleEntity { Text = "Synthetic rule", Kind = RuleKind.Context }]
            });
            await write.SaveChangesAsync();
        }
        await using var read = new PatientIntakeDbContext(options);
        var facility = await read.Facilities.Include(item => item.Rules).SingleAsync(item => item.Id == facilityId);
        Assert.Equal("FACILITY_ALPHA", facility.LegacyId);
        Assert.Equal("Synthetic rule", facility.Rules.Single().Text);
    }
}

