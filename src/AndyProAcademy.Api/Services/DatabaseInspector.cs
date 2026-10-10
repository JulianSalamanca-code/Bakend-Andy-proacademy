using AndyProAcademy.Api.Data;
using Npgsql;

namespace AndyProAcademy.Api.Services;

/// <summary>
/// Consulta el estado del esquema en la base: qué tablas existen, cuánto
/// contenido hay sembrado y si la seguridad está activa.
///
/// Es de lectura, sin efectos secundarios. Se elimina al terminar la
/// puesta en marcha, o se deja en modo protegido.
/// </summary>
public sealed class DatabaseInspector(NpgsqlConnectionStringBuilder connectionString)
{
    public async Task<object> InspectAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, object>();

        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);

        // ---------------------------------------------------------------
        // Tablas existentes
        // ---------------------------------------------------------------
        // Solo information_schema: funciona aunque el esquema esté vacío,
        // a diferencia de un SELECT sobre la tabla.
        result["tables"] = await QueryAsync(connection, """
            select table_name,
                   (select count(*)
                      from information_schema.columns c
                     where c.table_schema = 'public'
                       and c.table_name = t.table_name) as columnas,
                   (select rowsecurity
                      from pg_tables pt
                     where pt.schemaname = 'public'
                       and pt.tablename = t.table_name) as rls_activo
              from information_schema.tables t
             where table_schema = 'public'
               and table_type = 'BASE TABLE'
             order by table_name
            """, ct);

        // ---------------------------------------------------------------
        // Contenido, tabla por tabla
        // ---------------------------------------------------------------
        // Se cuenta una a una porque un único SELECT con subconsultas sobre
        // tablas inexistentes aborta el lote completo, y justamente se
        // necesita el informe cuando el esquema está a medio aplicar.
        var countTargets = new[]
        {
            "services", "addons", "courses", "products", "product_variants",
            "reviews", "faqs", "specialists", "working_hours", "bookings", "orders",
        };

        var rowCounts = new Dictionary<string, object?>();
        foreach (var table in countTargets)
        {
            rowCounts[table] = await CountRowsAsync(connection, table, ct);
        }

        result["rowCounts"] = rowCounts;

        // ---------------------------------------------------------------
        // Seguridad
        // ---------------------------------------------------------------
        result["security"] = await QueryAsync(connection, """
            select
              (select count(*) from pg_policies where schemaname = 'public') as politicas_rls,
              (select count(*) from pg_tables
                where schemaname = 'public' and rowsecurity) as tablas_con_rls,
              (select count(*) from pg_tables
                where schemaname = 'public' and not rowsecurity) as tablas_sin_rls,
              (select count(*) from pg_extension where extname = 'btree_gist') as btree_gist
            """, ct);

        // ---------------------------------------------------------------
        // Funciones propias
        // ---------------------------------------------------------------
        result["functions"] = await QueryAsync(connection, """
            select routine_name, routine_type
              from information_schema.routines
             where routine_schema = 'public'
             order by routine_name
            """, ct);

        // ---------------------------------------------------------------
        // La constraint anti-doble-booking
        // ---------------------------------------------------------------
        result["agenda"] = await QueryAsync(connection, """
            select conname, pg_get_constraintdef(oid) as definicion
              from pg_constraint
             where conname = 'bookings_no_overlap'
            """, ct);

        return result;
    }

    /// <summary>
    /// Cuenta filas de una tabla devolviendo null si no existe, en lugar de
    /// propagar el error. Necesario para diagnosticar un esquema a medias.
    /// </summary>
    private async Task<object?> CountRowsAsync(
        NpgsqlConnection connection,
        string table,
        CancellationToken ct)
    {
        try
        {
            // El nombre de tabla viene de una lista constante en el código,
            // nunca de entrada del usuario, así que no necesita parámetro.
            await using var command =
                new NpgsqlCommand($"select count(*) from public.{table}", connection);
            var result = await command.ExecuteScalarAsync(ct);
            return result is long count ? count : null;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // undefined_table: la migración aún no se aplicó.
            return null;
        }
    }

    /// <summary>
    /// Ejecuta una consulta y devuelve las filas como diccionarios.
    /// Tolerar tablas ausentes es intencional: el diagnóstico debe
    /// funcionar aunque el esquema esté a medio aplicar.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> QueryAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }
}