using System.Text.Json;
using Npgsql;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Lectura de columnas jsonb.
/// </summary>
internal static class JsonbReader
{
    /// <summary>
    /// Lee una columna jsonb como texto.
    ///
    /// Npgsql devuelve DataTypeName '-' para jsonb y lanza
    /// "Reading as String is not supported for fields having DataTypeName -"
    /// si se pide GetFieldValue&lt;string&gt;. Hay que leer el valor con
    /// GetValue y convertirlo.
    ///
    /// Centralizado porque el error aparece en cuanto una consulta usa
    /// json_agg, y se repite en cada repositorio.
    /// </summary>
    public static string? Read(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;

        return reader.GetValue(ordinal)?.ToString();
    }

    /// <summary>
    /// Lee una columna jsonb y la deserializa.
    ///
    /// Devuelve null si la columna está vacía o trae algo que no es JSON,
    /// para que un dato malformado no tumbe toda la consulta.
    /// </summary>
    public static JsonDocument? Parse(NpgsqlDataReader reader, int ordinal)
    {
        var json = Read(reader, ordinal);

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}