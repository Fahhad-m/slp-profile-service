using Microsoft.EntityFrameworkCore;
using RabotSlp.Configuration;

namespace RabotSlp.Data;

public static class DatabaseConfiguration
{
    /// <summary>Selects the EF Core provider from configuration. The data model is provider neutral.</summary>
    public static void Configure(DbContextOptionsBuilder options, DatabaseOptions database)
    {
        switch (database.Provider.Trim().ToLowerInvariant())
        {
            case "sqlite":
                options.UseSqlite(database.ConnectionString);
                break;
            case "sqlserver":
                options.UseSqlServer(database.ConnectionString);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider '{database.Provider}'. Use 'Sqlite' or 'SqlServer'.");
        }
    }
}
