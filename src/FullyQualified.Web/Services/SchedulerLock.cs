using Npgsql;

namespace FullyQualified.Web;

/// <summary>
/// The scheduler role as a Postgres session advisory lock on one dedicated connection (ADR 0010). The lock lives as long as the connection,
/// so a dead holder frees it and another process takes it on its next try.
/// </summary>
public sealed class SchedulerLock(string connection) : IAsyncDisposable
{
    const long Key = 0x46515F5343484544; // "FQ_SCHED"
    readonly string connectionString = new NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString; // a pooled connection would keep the lock after Dispose
    NpgsqlConnection? held;

    /// <summary>True when this instance holds the lock, taking it if it is free. A held lock is checked with a ping, because a dropped connection means it is gone.</summary>
    public async Task<bool> TryHoldAsync(CancellationToken ct)
    {
        if (held is not null)
        {
            try
            {
                await using var ping = new NpgsqlCommand("select 1", held);
                await ping.ExecuteScalarAsync(ct);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { await ReleaseAsync(); }
        }
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select pg_try_advisory_lock(@k)", conn) { Parameters = { new("k", Key) } };
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) { held = conn; return true; }
        }
        catch { await conn.DisposeAsync(); throw; }
        await conn.DisposeAsync();
        return false;
    }

    async Task ReleaseAsync()
    {
        var c = held;
        held = null;
        if (c is not null) await c.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(ReleaseAsync());
}
