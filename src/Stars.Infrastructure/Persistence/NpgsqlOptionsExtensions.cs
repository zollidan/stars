using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Stars.Infrastructure.Persistence;

internal static class NpgsqlOptionsExtensions
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public const string MigrationsHistorySchema = "public";

    /// <summary>
    /// Provider settings shared by runtime registration and design-time tooling so both
    /// resolve the same migrations assembly and history table.
    /// </summary>
    public static NpgsqlDbContextOptionsBuilder ConfigureStars(this NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.MigrationsAssembly(typeof(StarsDbContext).Assembly.FullName);
        npgsql.MigrationsHistoryTable(MigrationsHistoryTable, MigrationsHistorySchema);

        return npgsql;
    }
}
