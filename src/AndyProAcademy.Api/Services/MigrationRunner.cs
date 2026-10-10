using Npgsql;
using AndyProAcademy.Api.Data;

namespace AndyProAcademy.Api.Services;

/// <summary>
/// Aplica las migraciones SQL de db/migrations en orden.
///
/// Pensado para el prototipo: la base se crea vacía en Supabase y hay que
/// poblarla. En producción las migraciones se aplican con supabase db push
/// en el pipeline, no desde un endpoint HTTP.
///
/// Por eso este servicio exige un flag de configuración: exponerlo sin
/// verificación permitiría a cualquiera reconstruir el esquema.
/// </summary>
public sealed class MigrationRunner(
    NpgsqlConnectionStringBuilder connectionString,
    IConfiguration config,
    ILogger<MigrationRunner> logger)
{
    public async Task<MigrationResult> RunAsync(CancellationToken ct = default)
    {
        // ---------------------------------------------------------------
        // Guardia: solo con el flag prendido
        // ---------------------------------------------------------------
        if (!config.GetValue<bool>("Database:AllowMigrations"))
        {
            return new MigrationResult(
                Success: false,
                Message: "Migraciones deshabilitadas. Activa Database:AllowMigrations para aplicarlas.");
        }

        // ---------------------------------------------------------------
        // Localizar y ordenar los archivos
        // ---------------------------------------------------------------
        var migrationsDir = ResolveMigrationsDirectory();
        if (migrationsDir is null)
        {
            return new MigrationResult(false,
                "No se encontró db/migrations. Revisa el directorio de trabajo.");
        }

        var files = Directory.GetFiles(migrationsDir, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            return new MigrationResult(false, "No hay archivos .sql en db/migrations.");
        }

        var applied = new List<string>();

        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);

        // ---------------------------------------------------------------
        // Cada migración corre en su propia transacción.
        //
        // Sin esto, un fallo a mitad del 003 deja 001 y 002 aplicados y 003
        // a medias: el peor estado posible, porque parece que funcionó.
        // ---------------------------------------------------------------
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var sql = await File.ReadAllTextAsync(file, ct);

            logger.LogInformation("Aplicando migración {Migration}", name);

            await using var transaction = await connection.BeginTransactionAsync(ct);

            try
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
                applied.Add(name);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);

                // 23505 = unique_violation, 42P07 = duplicate_table,
                // 42710 = duplicate_object. Indican que ya se aplicó.
                if (IsAlreadyApplied(ex))
                {
                    logger.LogInformation("{Migration} ya estaba aplicada, se omite", name);
                    applied.Add($"{name} (ya existía)");
                    continue;
                }

                logger.LogError(ex, "Falló la migración {Migration}", name);
                return new MigrationResult(false,
                    $"Error en {name}: {ex.Message}",
                    applied);
            }
        }

        return new MigrationResult(true,
            $"Se aplicaron {applied.Count} migraciones.",
            applied);
    }

    /// <summary>
    /// Ejecuta db/migrations/000_reset.sql, que borra todo el esquema public.
    ///
    /// Exige un segundo flag además del de migraciones, y muestra en el
    /// mensaje que es destructivo. Es la protección contra ejecutar por
    /// accidente un archivo que hace DROP TABLE.
    /// </summary>
    public async Task<MigrationResult> ResetAsync(CancellationToken ct = default)
    {
        if (!config.GetValue<bool>("Database:AllowMigrations"))
        {
            return new MigrationResult(false, "Migraciones deshabilitadas.");
        }

        if (!config.GetValue<bool>("Database:AllowReset"))
        {
            return new MigrationResult(false,
                "El reset es destructivo. Activa Database:AllowReset para permitirlo.");
        }

        var migrationsDir = ResolveMigrationsDirectory();
        var resetFile = migrationsDir is null
            ? null
            : Path.Combine(migrationsDir, "000_reset.sql");

        if (resetFile is null || !File.Exists(resetFile))
            return new MigrationResult(false, "No se encontró 000_reset.sql.");

        logger.LogWarning("Ejecutando reset: se borrará todo el esquema public");

        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);

        var sql = await File.ReadAllTextAsync(resetFile, ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);

        return new MigrationResult(true,
            "Esquema public borrado. Vuelve a aplicar las migraciones.");
    }

    /// <summary>
    /// Detecta los errores que indican que el objeto ya existía, para poder
    /// reejecutar sin romper. Las migraciones ya usan IF NOT EXISTS donde
    /// importa, pero los CREATE TYPE y las políticas no siempre se pueden
    /// proteger igual.
    /// </summary>
    private static bool IsAlreadyApplied(Exception ex)
    {
        const string AlreadyExists = "already exists";

        if (ex is PostgresException pg)
        {
            // duplicate_table, duplicate_object, duplicate_column,
            // duplicate_schema, duplicate_alias, unique_violation
            if (pg.SqlState is "42P07" or "42710" or "42701" or "42P06" or "23505")
                return true;
        }

        return ex.Message.Contains(AlreadyExists, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Busca db/migrations subiendo desde el directorio de contenido.
    /// Así funciona tanto en `dotnet run` como en `dotnet test`, donde el
    /// directorio actual es distinto.
    /// </summary>
    private string? ResolveMigrationsDirectory()
    {
        var direct = Path.Combine(Directory.GetCurrentDirectory(), "db", "migrations");
        if (Directory.Exists(direct)) return direct;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}

public sealed record MigrationResult(
    bool Success,
    string Message,
    IReadOnlyList<string>? Applied = null);