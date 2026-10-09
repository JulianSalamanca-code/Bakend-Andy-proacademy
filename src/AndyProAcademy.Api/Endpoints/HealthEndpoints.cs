using System.Diagnostics;
using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
}