using Npgsql;
using AndyProAcademy.Api.Models;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Acceso a las tablas del catálogo.
///
/// Todas las consultas son parametrizadas ($1, $2...) y Npgsql exige que
/// lo sean, así que no hay superficie de inyección SQL. Los arrays de
/// Postgres (text[]) llegan como string[], que es lo que espera el DTO.
/// </summary>
public sealed class CatalogRepository(NpgsqlConnectionStringBuilder connectionString)
{
    /// <summary>
    /// Abre una conexión nueva por operación.
    ///
    /// Se paga el costo de conectar cada vez a cambio de no arrastrar un
    /// connection pool. Con Pooling=false es obligatorio: el pooler de
    /// transacciones de Supabase (6543) reasigna conexiones entre
    /// transacciones, y una conexión reutilizada puede quedar en otra
    /// sesión a mitad de una operación.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    // =================================================================
    // Servicios
    // =================================================================

    /// <summary>
    /// Servicios publicados, con sus complementos anidados.
    ///
    /// Los complementos llegan por un LEFT JOIN lateral en vez de una
    /// segunda consulta: una sola ida a la base en lugar de N+1.
    /// </summary>
    public async Task<IReadOnlyList<ServiceWithAddOnsDto>> GetServicesAsync(
        bool includeUnpublished = false,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        // En el SQL crudo de Npgsql los marcadores son $1, $2 (posicionales),
        // no @nombre. Por eso se cambia el placeholder: se declara el
        // parámetro y se le pasa por posición.
        var sql = """
            select s.id, s.slug, s.name, s.tagline, s.description, s.badge,
                   s.duration_minutes, s.price, s.rfc_required,
                   s.includes, s.brands, s.preparation, s.image_url, s.sort_order,
                   coalesce(
                     jsonb_agg(
                       jsonb_build_object(
                         'id', a.id,
                         'serviceId', a.service_id,
                         'name', a.name,
                         'description', a.description,
                         'price', a.price,
                         'extraMinutes', a.extra_minutes
                       ) order by a.id
                     ) filter (where a.id is not null),
                     '[]'::jsonb
                   ) as addons
              from public.services s
              left join lateral (
                select * from public.addons a
                 where a.service_id = s.id
                   and a.is_active
              ) a on true
             where ($1 or s.is_published)
             group by s.id
             order by s.sort_order, s.id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(includeUnpublished);

        var results = new List<ServiceWithAddOnsDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var service = new ServiceDto(
                Id: reader.GetInt32(0),
                Slug: reader.GetString(1),
                Name: reader.GetString(2),
                Tagline: reader.GetString(3),
                Description: reader.GetString(4),
                Badge: reader.IsDBNull(5) ? null : reader.GetString(5),
                DurationMinutes: reader.GetInt32(6),
                Price: reader.GetDecimal(7),
                RfcRequired: reader.GetBoolean(8),
                Includes: ReadStringArray(reader, 9),
                Brands: ReadStringArray(reader, 10),
                Preparation: reader.IsDBNull(11) ? null : reader.GetString(11),
                ImageUrl: reader.IsDBNull(12) ? null : reader.GetString(12),
                SortOrder: reader.GetInt32(13));

            results.Add(new ServiceWithAddOnsDto(service, ReadAddOns(reader, 14)));
        }

        return results;
    }

    /// <summary>Un servicio por slug, o null si no existe o no está publicado.</summary>
    public async Task<ServiceWithAddOnsDto?> GetServiceBySlugAsync(
        string slug, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select s.id, s.slug, s.name, s.tagline, s.description, s.badge,
                   s.duration_minutes, s.price, s.rfc_required,
                   s.includes, s.brands, s.preparation, s.image_url, s.sort_order,
                   coalesce(
                     jsonb_agg(
                       jsonb_build_object(
                         'id', a.id,
                         'serviceId', a.service_id,
                         'name', a.name,
                         'description', a.description,
                         'price', a.price,
                         'extraMinutes', a.extra_minutes
                       ) order by a.id
                     ) filter (where a.id is not null),
                     '[]'::jsonb
                   ) as addons
              from public.services s
              left join lateral (
                select * from public.addons a
                 where a.service_id = s.id and a.is_active
              ) a on true
             where s.slug = $1 and s.is_published
             group by s.id
            """, connection);

        command.Parameters.AddWithValue(slug);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var service = new ServiceDto(
            Id: reader.GetInt32(0),
            Slug: reader.GetString(1),
            Name: reader.GetString(2),
            Tagline: reader.GetString(3),
            Description: reader.GetString(4),
            Badge: reader.IsDBNull(5) ? null : reader.GetString(5),
            DurationMinutes: reader.GetInt32(6),
            Price: reader.GetDecimal(7),
            RfcRequired: reader.GetBoolean(8),
            Includes: ReadStringArray(reader, 9),
            Brands: ReadStringArray(reader, 10),
            Preparation: reader.IsDBNull(11) ? null : reader.GetString(11),
            ImageUrl: reader.IsDBNull(12) ? null : reader.GetString(12),
            SortOrder: reader.GetInt32(13));

        return new ServiceWithAddOnsDto(service, ReadAddOns(reader, 14));
    }

    // =================================================================
    // Cursos
    // =================================================================

    /// <summary>
    /// Cursos publicados.
    ///
    /// Trae el temario completo siempre. La versión resumida se veía más
    /// ligera, pero obligaba al frontend a pedir cada curso por separado
    /// para pintar la página, y sin temario la vista salía vacía: es peor
    /// el intercambio. Tres cursos con seis temas cada uno son unos
    /// pocos kilobytes.
    /// </summary>
    public async Task<IReadOnlyList<CourseDto>> GetCoursesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id, slug, title, tagline, description, tag, level,
                   duration_label, session_count, price, deposit_amount,
                   topics, includes, image_url
              from public.courses
             where is_published
             order by price
            """, connection);

        var results = new List<CourseDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CourseDto(
                Id: reader.GetGuid(0),
                Slug: reader.GetString(1),
                Title: reader.GetString(2),
                Tagline: reader.GetString(3),
                Description: reader.GetString(4),
                Tag: reader.GetString(5),
                Level: reader.GetString(6),
                DurationLabel: reader.GetString(7),
                SessionCount: reader.GetInt32(8),
                Price: reader.GetDecimal(9),
                DepositAmount: reader.GetDecimal(10),
                Topics: ReadStringArray(reader, 11),
                Includes: ReadStringArray(reader, 12),
                ImageUrl: reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return results;
    }

    public async Task<CourseDto?> GetCourseBySlugAsync(
        string slug, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id, slug, title, tagline, description, tag, level,
                   duration_label, session_count, price, deposit_amount,
                   topics, includes, image_url
              from public.courses
             where slug = $1 and is_published
            """, connection);

        command.Parameters.AddWithValue(slug);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new CourseDto(
            Id: reader.GetGuid(0),
            Slug: reader.GetString(1),
            Title: reader.GetString(2),
            Tagline: reader.GetString(3),
            Description: reader.GetString(4),
            Tag: reader.GetString(5),
            Level: reader.GetString(6),
            DurationLabel: reader.GetString(7),
            SessionCount: reader.GetInt32(8),
            Price: reader.GetDecimal(9),
            DepositAmount: reader.GetDecimal(10),
            Topics: ReadStringArray(reader, 11),
            Includes: ReadStringArray(reader, 12),
            ImageUrl: reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    /// <summary>Slugs publicados, para el prerender de /cursos/:slug.</summary>
    public async Task<IReadOnlyList<string>> GetCourseSlugsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select slug from public.courses where is_published order by slug", connection);

        var slugs = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) slugs.Add(reader.GetString(0));

        return slugs;
    }

    // =================================================================
    // Productos
    // =================================================================

    public async Task<IReadOnlyList<ProductDto>> GetProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select p.id, p.slug, p.name, p.brand, p.category, p.description,
                   p.icon, p.price, p.tax_rate, p.image_url,
                   coalesce(
                     jsonb_agg(
                       jsonb_build_object(
                         'id', v.id,
                         'sizeLabel', v.size_label,
                         'sku', v.sku,
                         'stockOnHand', v.stock_on_hand,
                         'priceOverride', v.price_override
                       ) order by v.id
                     ) filter (where v.id is not null),
                     '[]'::jsonb
                   ) as variants
              from public.products p
              left join lateral (
                select * from public.product_variants v
                 where v.product_id = p.id and v.is_active
              ) v on true
             where p.is_published and ($1::text is null or p.category = $1)
             group by p.id
             order by p.category, p.name
            """, connection);

        command.Parameters.AddWithValue((object?)category ?? DBNull.Value);

        var results = new List<ProductDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProductDto(
                Id: reader.GetGuid(0),
                Slug: reader.GetString(1),
                Name: reader.GetString(2),
                Brand: reader.GetString(3),
                Category: reader.GetString(4),
                Description: reader.GetString(5),
                Icon: reader.IsDBNull(6) ? null : reader.GetString(6),
                Price: reader.GetDecimal(7),
                TaxRate: reader.GetDecimal(8),
                ImageUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
                Variants: ReadVariants(reader, 10)));
        }

        return results;
    }

    // =================================================================
    // Contenido
    // =================================================================

    public async Task<IReadOnlyList<ReviewDto>> GetReviewsAsync(
        int limit = 6, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id, author_name, role_label, initials, rating, body, sort_order
              from public.reviews
             where is_published
             order by sort_order
             limit $1
            """, connection);

        command.Parameters.AddWithValue(limit);

        var results = new List<ReviewDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ReviewDto(
                Id: reader.GetGuid(0),
                AuthorName: reader.GetString(1),
                RoleLabel: reader.IsDBNull(2) ? null : reader.GetString(2),
                Initials: reader.GetString(3),
                Rating: reader.GetInt32(4),
                Body: reader.GetString(5),
                SortOrder: reader.GetInt32(6)));
        }

        return results;
    }

    public async Task<IReadOnlyList<FaqDto>> GetFaqsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id, question, answer, category, sort_order
              from public.faqs
             where is_published
             order by sort_order
            """, connection);

        var results = new List<FaqDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new FaqDto(
                Id: reader.GetGuid(0),
                Question: reader.GetString(1),
                Answer: reader.GetString(2),
                Category: reader.GetString(3),
                SortOrder: reader.GetInt32(4)));
        }

        return results;
    }

    public async Task<IReadOnlyList<SpecialistDto>> GetSpecialistsAsync(
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select id, name, title, bio, image_url from public.specialists where is_active order by id",
            connection);

        var results = new List<SpecialistDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SpecialistDto(
                Id: reader.GetInt32(0),
                Name: reader.GetString(1),
                Title: reader.GetString(2),
                Bio: reader.IsDBNull(3) ? null : reader.GetString(3),
                ImageUrl: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    // =================================================================
    // Lectura de columnas jsonb
    // =================================================================

    /// <summary>
    /// Lee una columna text[] de Postgres. Viene como string[] porque Npgsql
    /// mapea los arrays nativos; si se leerla desde jsonb llega como JsonDocument.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return Array.Empty<string>();
        return reader.GetFieldValue<string[]>(ordinal);
    }

    private static IReadOnlyList<AddOnDto> ReadAddOns(NpgsqlDataReader reader, int ordinal)
    {
        using var doc = JsonbReader.Parse(reader, ordinal);
        if (doc is null) return Array.Empty<AddOnDto>();

        var results = new List<AddOnDto>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            results.Add(new AddOnDto(
                Id: item.GetProperty("id").GetInt32(),
                ServiceId: item.GetProperty("serviceId").GetInt32(),
                Name: item.GetProperty("name").GetString()!,
                Description: item.GetProperty("description").GetString() ?? string.Empty,
                Price: item.GetProperty("price").GetDecimal(),
                ExtraMinutes: item.GetProperty("extraMinutes").GetInt32()));
        }

        return results;
    }

    private static IReadOnlyList<ProductVariantDto> ReadVariants(NpgsqlDataReader reader, int ordinal)
    {
        using var doc = JsonbReader.Parse(reader, ordinal);
        if (doc is null) return Array.Empty<ProductVariantDto>();

        var results = new List<ProductVariantDto>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var priceOverride = item.GetProperty("priceOverride");

            results.Add(new ProductVariantDto(
                Id: item.GetProperty("id").GetInt32(),
                SizeLabel: item.GetProperty("sizeLabel").GetString()!,
                Sku: item.GetProperty("sku").ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : item.GetProperty("sku").GetString(),
                StockOnHand: item.GetProperty("stockOnHand").GetInt32(),
                PriceOverride: priceOverride.ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : priceOverride.GetDecimal()));
        }

        return results;
    }
}