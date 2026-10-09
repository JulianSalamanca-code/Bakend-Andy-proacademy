using Npgsql;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Construye la cadena de conexión a Supabase Postgres a partir de la configuración.
/// Valida los campos al construirse: si un nombre está mal, truena al inicio y no a media conexión.
/// </summary>
public static class ConnectionStringFactory
{
    public const string SectionName = "Supabase";

    public static NpgsqlConnectionStringBuilder Build(IConfiguration config)
    {
        var section = config.GetSection(SectionName);

        var host = section["DbHost"];
        var database = section["DbName"];
        var username = section["DbUser"];
        var password = section["DbPassword"];

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Falta 'Supabase:DbHost' en la configuración.");
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Falta 'Supabase:DbUser' en la configuración.");

        var port = int.TryParse(section["DbPort"], out var parsedPort) ? parsedPort : 5432;

        // Sin password permitimos el flujo de Supabase local / CI.
        // En Supabase gestionado la contraseña siempre es obligatoria.
        if (string.IsNullOrWhiteSpace(password) &&
            !host.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Falta 'Supabase:DbPassword' en la configuración.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Require,

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