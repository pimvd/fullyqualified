using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FullyQualified.Tests;

public class ReviewMigrationTests
{
    [DbFact]
    public async Task Hostnames_that_were_added_automatically_before_the_review_count_as_added_afterwards()
    {
        var database = "fq_mig_" + Guid.NewGuid().ToString("N");
        var connection = $"{DbFactAttribute.Server};Database={database}";
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options, new TenantScope { All = true });
            var migrator = db.GetService<IMigrator>();
            var all = db.Database.GetMigrations().ToList();
            var review = all.Single(m => m.EndsWith("_ReviewHosts"));
            await migrator.MigrateAsync(all[all.IndexOf(review) - 1]);       // the database as it was before the review existed

            var tenant = Guid.NewGuid();
            var parent = Guid.NewGuid();
            var found = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(connection))
            {
                await conn.OpenAsync();
                async Task Run(string sql, params (string, object?)[] args)
                {
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                await Run("""INSERT INTO assets ("Id","TenantId","Kind","Name","Discovered","CreatedAt") VALUES (@p, @t, 0, 'corp.example', false, now())""", ("p", parent), ("t", tenant));
                await Run("""
                    INSERT INTO assets ("Id","TenantId","Kind","Name","Discovered","CreatedAt","DiscoveredVia","DiscoveredFromId","DiscoveredIssuer","DiscoveredCertExpires")
                    VALUES (@id, @t, 1, 'vpn.corp.example', true, now() - interval '5 days', 'crt.sh', @p, 'R11', now() + interval '30 days')
                    """, ("id", found), ("t", tenant), ("p", parent));
                await Run("""INSERT INTO assets ("Id","TenantId","Kind","Name","Discovered","CreatedAt") VALUES (gen_random_uuid(), @t, 1, 'typed.corp.example', false, now())""", ("t", tenant));
            }

            await migrator.MigrateAsync();                                   // the review migration, with its backfill

            await using var check = new NpgsqlConnection(connection);
            await check.OpenAsync();
            await using var query = new NpgsqlCommand("""SELECT "Name","State","ParentAssetId","Service","Issuer","Reason","DecidedBy" FROM review_hosts""", check);
            await using var reader = await query.ExecuteReaderAsync();
            var rows = new List<(string Name, int State, Guid? Parent, string? Service, string? Issuer, string? Reason, string? By)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                          reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));

            var row = Assert.Single(rows);                                   // the hostname that was found, not the ones typed in
            Assert.Equal("vpn.corp.example", row.Name);
            Assert.Equal((int)ReviewState.Added, row.State);
            Assert.Equal(parent, row.Parent);
            Assert.Equal("crt.sh", row.Service);
            Assert.Equal("R11", row.Issuer);
            Assert.Null(row.Reason);
            Assert.Null(row.By);                                             // shown as "added automatically, before review existed"
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection($"{DbFactAttribute.Server};Database=postgres");
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
