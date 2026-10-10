using System.Diagnostics;
using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Models;
using AndyProAcademy.Api.Services;
using Npgsql;

namespace AndyProAcademy.Api.Endpoints;

/// <summary>
/// Endpoint de diagnóstico. Sirve para confirmar que la API arranca y que la
/// conexión a Supabase Postgres responde.
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Chequeo de la API sola, sin tocar la base de datos.
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            service = "AndyProAcademy.Api",
            timestamp = DateTimeOffset.UtcNow,
        }))
        .WithName("HealthApi")
        .WithTags("Health")
        .WithSummary("Verifica que la API esté viva");

        // Chequeo completo: SELECT 1 contra Supabase.
        // Devuelve 503 si la base no responde, para que el fallo sea visible de inmediato.
        app.MapGet("/api/health/database", async (
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("HealthDatabase");

            try
            {
                var connectionString = ConnectionStringFactory.BuildRaw(config);
                var stopwatch = Stopwatch.StartNew();

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);

                await using var command = new NpgsqlCommand("SELECT 1", connection);
                var result = await command.ExecuteScalarAsync(ct);
                stopwatch.Stop();

                return Results.Ok(new HealthResponse(
                    Status: (result is 1 ? "ok" : "unexpected"),
                    Database: "connected",
                    DatabaseLatencyMs: stopwatch.ElapsedMilliseconds,
                    Timestamp: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                // PostgresException trae el código SQLSTATE, que dice exactamente qué falló
                // (28P01 = password incorrecta, 28000 = rol inexistente, etc.).
                var detail = ex is PostgresException pg
                    ? $"{pg.SqlState}: {pg.MessageText}"
                    : ex.Message;

                logger.LogError(ex, "Fallo el chequeo de conexión a la base de datos.");

                return Results.Json(
                    new
                    {
                        status = "error",
                        database = ex.GetType().Name,
                        detail,
                        timestamp = DateTimeOffset.UtcNow,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("HealthDatabase")
        .WithTags("Health")
        .WithSummary("Verifica la conexión a Supabase Postgres");
    }

    /// <summary>
    /// Diagnóstico del esquema: tablas existentes, contenido sembrado y
    /// estado de la seguridad. Solo lectura.
    /// </summary>
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics/database", async (
            NpgsqlConnectionStringBuilder connectionString,
            DatabaseInspector inspector,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            try
            {
                var report = await inspector.InspectAsync(ct);
                return Results.Ok(report);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("DiagnosticsDatabase");
                logger.LogError(ex, "Falló el diagnóstico de la base de datos.");

                return Results.Json(
                    new
                    {
                        status = "error",
                        error = ex.Message,
                        type = ex.GetType().FullName,
                        // Incluye la causa interna: los errores de Postgres
                        // suelen venir envueltos en un NpgsqlException.
                        inner = ex.InnerException?.Message,
                    },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("DiagnosticsDatabase")
        .WithTags("Diagnostics")
        .WithSummary("Informe del estado del esquema y la seguridad");
    }

    /// <summary>
    /// Aplica las migraciones de db/migrations. Solo funciona con
    /// Database:AllowMigrations en true; en producción se usa supabase db push.
    /// </summary>
    public static void MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/diagnostics/migrate", async (
            MigrationRunner runner,
            CancellationToken ct) =>
        {
            var result = await runner.RunAsync(ct);

            return Results.Json(result, statusCode: result.Success
                ? StatusCodes.Status200OK
                : StatusCodes.Status400BadRequest);
        })
        .WithName("RunMigrations")
        .WithTags("Diagnostics")
        .WithSummary("Aplica las migraciones pendientes");

        // Destructivo: borra todo el esquema public. Requiere AllowReset.
        app.MapPost("/api/diagnostics/reset", async (
            MigrationRunner runner,
            CancellationToken ct) =>
        {
            var result = await runner.ResetAsync(ct);

            return Results.Json(result, statusCode: result.Success
                ? StatusCodes.Status200OK
                : StatusCodes.Status400BadRequest);
        })
        .WithName("ResetDatabase")
        .WithTags("Diagnostics")
        .WithSummary("Borra el esquema public (destructivo)");

        // Pruebas de humo sobre las funciones de agenda.
        app.MapGet("/api/diagnostics/booking-tests", async (
            BookingSmokeTests tests,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await tests.RunAsync(ct));
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { status = "error", error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("BookingSmokeTests")
        .WithTags("Diagnostics")
        .WithSummary("Prueba disponibilidad, zona horaria y solapamientos");
    }
}