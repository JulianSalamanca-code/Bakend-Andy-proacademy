using Npgsql;
using System;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Construye la cadena de conexión a Supabase Postgres a partir de la configuración.
/// Valida los campos al construirse: si un nombre está mal, truena al inicio y no a media conexión.
/// También lee de variables de entorno con formato .env (SUPABASE_DB_HOST, etc.).
/// </summary>
public static class ConnectionStringFactory
{
    public const string SectionName = "Supabase";

    public static NpgsqlConnectionStringBuilder Build(IConfiguration config)
    {
        var section = config.GetSection(SectionName);

        // Primero intentar leer de la configuración (appsettings)
        var host = section["DbHost"];
        var database = section["DbName"];
        var username = section["DbUser"];
        var password = section["DbPassword"];

        // Si no están en la configuración, intentar leer de variables de entorno .env
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.GetEnvironmentVariable("SUPABASE_DB_HOST");
        if (string.IsNullOrWhiteSpace(database))
            database = Environment.GetEnvironmentVariable("SUPABASE_DB_NAME");
        if (string.IsNullOrWhiteSpace(username))
            username = Environment.GetEnvironmentVariable("SUPABASE_DB_USER");
        if (string.IsNullOrWhiteSpace(password))
            password = Environment.GetEnvironmentVariable("SUPABASE_DB_PASSWORD");

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Falta 'Supabase:DbHost' en la configuración o variable de entorno SUPABASE_DB_HOST.");
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Falta 'Supabase:DbUser' en la configuración o variable de entorno SUPABASE_DB_USER.");

        var portStr = section["DbPort"];
        if (string.IsNullOrWhiteSpace(portStr))
            portStr = Environment.GetEnvironmentVariable("SUPABASE_DB_PORT");
        var port = int.TryParse(portStr, out var parsedPort) ? parsedPort : 5432;

        // Sin password permitimos el flujo de Supabase local / CI.
        // En Supabase gestionado la contraseña siempre es obligatoria.
        if (string.IsNullOrWhiteSpace(password) &&
            !host.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Falta 'Supabase:DbPassword' en la configuración o variable de entorno SUPABASE_DB_PASSWORD.");
        }

        // Para desarrollo local (localhost/127.0.0.1) deshabilitar SSL
        var isLocalhost = host.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                          host.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        var sslMode = isLocalhost ? SslMode.Disable : SslMode.Require;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = sslMode,

            // CRÍTICO: el pooler de transacciones de Supabase (puerto 6543) corre sobre
            // PgBouncer, que NO soporta prepared statements. Npgsql los usa por defecto,
            // así que sin este false la conexión falla con:
            //   "Npgsql.PostgresException: prepared statements are not supported"
            // No quitar aunque "no estemos usando el pool".
            Pooling = false,
        };

        return builder;
    }

    public static string BuildRaw(IConfiguration config) => Build(config).ConnectionString;
}