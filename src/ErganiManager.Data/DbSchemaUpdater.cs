using Microsoft.EntityFrameworkCore;

namespace ErganiManager.Data;

/// <summary>
/// The main database is created with EnsureCreated(), which never adds new columns to
/// tables that already exist. This adds the columns introduced after the first release,
/// idempotently, for every supported provider. Safe to call on every startup.
/// </summary>
public static class DbSchemaUpdater
{
    private sealed record ColumnPatch(string Table, string Column, string Sqlite, string SqlServer, string MariaDb);

    private static readonly ColumnPatch[] Patches =
    {
        new("ApiSubmissionLogs", "RetryCount",
            "ALTER TABLE ApiSubmissionLogs ADD COLUMN RetryCount INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE ApiSubmissionLogs ADD RetryCount int NOT NULL CONSTRAINT DF_ApiSubmissionLogs_RetryCount DEFAULT 0",
            "ALTER TABLE ApiSubmissionLogs ADD COLUMN RetryCount INT NOT NULL DEFAULT 0"),

        new("ApiSubmissionLogs", "LastRetryAt",
            "ALTER TABLE ApiSubmissionLogs ADD COLUMN LastRetryAt TEXT NULL",
            "ALTER TABLE ApiSubmissionLogs ADD LastRetryAt datetime2 NULL",
            "ALTER TABLE ApiSubmissionLogs ADD COLUMN LastRetryAt DATETIME(6) NULL"),

        new("ApiSubmissionLogs", "WorkCardId",
            "ALTER TABLE ApiSubmissionLogs ADD COLUMN WorkCardId INTEGER NULL",
            "ALTER TABLE ApiSubmissionLogs ADD WorkCardId int NULL",
            "ALTER TABLE ApiSubmissionLogs ADD COLUMN WorkCardId INT NULL"),
    };

    private static string? _lastCheckedKey;

    public static async Task EnsureUpToDateAsync(DbConfig config)
    {
        // The connection state is re-evaluated regularly; only patch once per database.
        var key = $"{config.DatabaseProvider}|{config.SqlitePath}|{config.ConnectionString}";
        if (key == _lastCheckedKey) return;

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        DbProviderFactory.Configure(optionsBuilder, config);
        await using var db = new AppDbContext(optionsBuilder.Options);

        foreach (var patch in Patches)
        {
            if (await ColumnExistsAsync(db, patch)) continue;

            var ddl = config.DatabaseProvider switch
            {
                DatabaseProvider.Sqlite    => patch.Sqlite,
                DatabaseProvider.SqlServer => patch.SqlServer,
                DatabaseProvider.MariaDb   => patch.MariaDb,
                _ => throw new NotSupportedException($"Unknown database provider: {config.DatabaseProvider}")
            };

            await db.Database.ExecuteSqlRawAsync(ddl);
        }

        _lastCheckedKey = key;
    }

    private static async Task<bool> ColumnExistsAsync(AppDbContext db, ColumnPatch patch)
    {
        try
        {
            // Zero rows returned; fails only if the column (or table) is missing.
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT {patch.Column} FROM {patch.Table} WHERE 1 = 0");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
