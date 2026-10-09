namespace AndyProAcademy.Api.Models;

/// <summary>
/// Resultado del chequeo de conexión a la base de datos.
/// Se serializa a camelCase para el frontend.
/// </summary>
public sealed record HealthResponse(
    string Status,
    string Database,
    long DatabaseLatencyMs,
    DateTimeOffset Timestamp);