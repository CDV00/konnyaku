using System.Data.Common;
using Konnyaku.Application.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Konnyaku.Infrastructure.Tenancy;

/// <summary>
/// Sets <c>app.tenant_id</c> for PostgreSQL Row-Level Security every time a connection is opened.
/// Npgsql resets session state (DISCARD ALL) when a pooled connection is reused, so the value never
/// leaks to another request. No tenant → empty string → RLS policies see NULL → no rows (fail-closed).
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant, false)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenant.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);
        return command;
    }
}
