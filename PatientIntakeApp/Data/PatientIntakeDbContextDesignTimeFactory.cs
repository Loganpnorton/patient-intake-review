using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PatientIntakeApp.Services;
using System.IO;

namespace PatientIntakeApp.Data;

// Enables `dotnet ef` migrations for this WPF app.
public class PatientIntakeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PatientIntakeDbContext>
{
    public PatientIntakeDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("PATIENTINTAKE_DB_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(cs))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "PatientIntakeApp");
            Directory.CreateDirectory(dir);
            cs = $"Data Source={Path.Combine(dir, "patientintake.dev.sqlite")}";
        }

        var looksLikeSqlServer = cs.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                                 cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase);

        var optionsBuilder = new DbContextOptionsBuilder<PatientIntakeDbContext>();
        if (looksLikeSqlServer)
        {
            optionsBuilder.UseSqlServer(cs);
        }
        else
        {
            optionsBuilder.UseSqlite(cs);
        }

        return new PatientIntakeDbContext(optionsBuilder.Options);
    }
}

