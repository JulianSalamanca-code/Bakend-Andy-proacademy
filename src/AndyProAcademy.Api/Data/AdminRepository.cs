using Npgsql;
using AndyProAcademy.Api.Models;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Acceso a datos del panel de administración.
///
/// A diferencia del catálogo, aquí casi todo son escrituras. Cada cambio
/// va acompañado de su registro: un movimiento de stock sin motivo es
/// imposible de auditar cuando alguien pregunta por qué el número no
/// cuadra.
/// </summary>
public sealed class AdminRepository(NpgsqlConnectionStringBuilder connectionString)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    // =================================================================
    // Métricas
    // =================================================================

    /// <summary>
    /// Números de las tarjetas del panel.
    ///
    /// Una sola consulta con subselects en lugar de seis: el panel se abre
    /// varias veces al día y seis viajes a la base se notan en una base
    /// remota (la latencia hacia us-east-1 ronda un segundo).
    /// </summary>
    public async Task<AdminMetricsDto> GetMetricsAsync(
        DateOnly today, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select
              (select count(*) from public.bookings
                where starts_at::date = $1
                  and status <> 'cancelada') as citas_hoy,
              (select count(*) from public.bookings
                where status = 'pendiente') as por_confirmar,
              (select count(*) from public.bookings
                where starts_at::date = $1
                  and status = 'confirmada') as confirmadas_hoy,
              (select coalesce(sum(deposit_amount), 0) from public.bookings
                where starts_at::date = $1
                  and status <> 'cancelada') as anticipos_hoy,
              (select coalesce(sum(price_total), 0) from public.bookings
                where starts_at::date = $1
                  and status <> 'cancelada') as esperado_hoy,
              (select count(*) from public.product_variants
                where is_active and stock_on_hand between 1 and 5) as stock_bajo,
              (select count(*) from public.product_variants
                where is_active and stock_on_hand = 0) as sin_stock
            """, connection);

        command.Parameters.Add(
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = today });

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new AdminMetricsDto(
            BookingsToday: reader.GetInt32(0),
            PendingConfirmation: reader.GetInt32(1),
            ConfirmedToday: reader.GetInt32(2),
            DepositsCollected: reader.GetDecimal(3),
            ExpectedToday: reader.GetDecimal(4),
            LowStockProducts: reader.GetInt32(5),
            OutOfStockProducts: reader.GetInt32(6));
    }

    // =================================================================
    // Agenda
    // =================================================================

    /// <summary>
    /// Reservas de una fecha.
    ///
    /// Sin filtro de estado: el panel muestra la agenda completa del día,
    /// incluidas las canceladas, porque Andy necesita ver qué se canceló
    /// además de lo que sigue en pie.
    /// </summary>
    public async Task<IReadOnlyList<AgendaEntryDto>> GetAgendaAsync(
        DateOnly date, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select b.id, b.code, b.contact_name, b.contact_phone, b.contact_email,
                   coalesce(s.name, c.title, 'Sesión') as subject,
                   case when b.course_id is not null then 'curso' else 'servicio' end,
                   b.starts_at, b.ends_at, b.status, b.modality,
                   b.price_total, b.deposit_amount,
                   b.deposit_paid_at is not null,
                   b.notes, b.rfc_receiver
              from public.bookings b
              left join public.services s on s.id = b.service_id
              left join public.courses c on c.id = b.course_id
             where b.starts_at::date = $1
             order by b.starts_at
            """, connection);

        command.Parameters.Add(
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = date });

        var results = new List<AgendaEntryDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AgendaEntryDto(
                Id: reader.GetGuid(0),
                Code: reader.GetString(1),
                ClientName: reader.GetString(2),
                ClientPhone: reader.GetString(3),
                ClientEmail: reader.GetString(4),
                Subject: reader.GetString(5),
                Kind: reader.GetString(6),
                StartsAt: reader.GetFieldValue<DateTimeOffset>(7),
                EndsAt: reader.GetFieldValue<DateTimeOffset>(8),
                Status: reader.GetString(9),
                Modality: reader.GetString(10),
                Total: reader.GetDecimal(11),
                Deposit: reader.GetDecimal(12),
                DepositPaid: reader.GetBoolean(13),
                Notes: reader.IsDBNull(14) ? null : reader.GetString(14),
                RfcReceiver: reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return results;
    }

    /// <summary>Detalle completo de una reserva, para el panel.</summary>
    public async Task<AdminBookingDetailDto?> GetBookingDetailAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select b.id, b.code, b.contact_name, b.contact_phone, b.contact_email,
                   coalesce(s.name, c.title, 'Sesión'),
                   case when b.course_id is not null then 'curso' else 'servicio' end,
                   b.status, b.modality, b.starts_at, b.ends_at,
                   b.price_total, b.deposit_amount, b.deposit_paid_at,
                   b.payment_reference, b.notes, b.rfc_receiver,
                   sp.name, b.addon_ids, b.created_at
              from public.bookings b
              join public.specialists sp on sp.id = b.specialist_id
              left join public.services s on s.id = b.service_id
              left join public.courses c on c.id = b.course_id
             where b.id = $1
            """, connection);

        command.Parameters.AddWithValue(id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new AdminBookingDetailDto(
            Id: reader.GetGuid(0),
            Code: reader.GetString(1),
            ClientName: reader.GetString(2),
            ClientPhone: reader.GetString(3),
            ClientEmail: reader.GetString(4),
            Subject: reader.GetString(5),
            Kind: reader.GetString(6),
            Status: reader.GetString(7),
            Modality: reader.GetString(8),
            StartsAt: reader.GetFieldValue<DateTimeOffset>(9),
            EndsAt: reader.GetFieldValue<DateTimeOffset>(10),
            Total: reader.GetDecimal(11),
            Deposit: reader.GetDecimal(12),
            DepositPaid: !reader.IsDBNull(13),
            DepositPaidAt: reader.IsDBNull(13)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(13),
            PaymentReference: reader.IsDBNull(14) ? null : reader.GetString(14),
            Notes: reader.IsDBNull(15) ? null : reader.GetString(15),
            RfcReceiver: reader.IsDBNull(16) ? null : reader.GetString(16),
            SpecialistName: reader.GetString(17),
            AddOnIds: reader.GetFieldValue<int[]>(18),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(19));
    }

    // =================================================================
    // Productos
    // =================================================================

    /// <summary>Productos con sus variantes, incluyendo los no publicados.</summary>
    public async Task<IReadOnlyList<AdminProductDto>> GetProductsAsync(
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select p.id, p.slug, p.name, p.brand, p.category, p.description,
                   p.icon, p.image_url, p.price, p.tax_rate, p.is_published,
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
              left join public.product_variants v on v.product_id = p.id
             group by p.id
             order by p.category, p.name
            """, connection);

        var results = new List<AdminProductDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AdminProductDto(
                Id: reader.GetGuid(0),
                Slug: reader.GetString(1),
                Name: reader.GetString(2),
                Brand: reader.GetString(3),
                Category: reader.GetString(4),
                Description: reader.GetString(5),
                Icon: reader.IsDBNull(6) ? null : reader.GetString(6),
                ImageUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
                Price: reader.GetDecimal(8),
                TaxRate: reader.GetDecimal(9),
                IsPublished: reader.GetBoolean(10),
                Variants: ReadVariants(reader, 11)));
        }

        return results;
    }

    /// <summary>
    /// Crea o actualiza un producto.
    ///
    /// El slug se genera si no viene, y se hace único con el sufijo numérico
    /// cuando ya existe otro producto con ese nombre. Si no, el segundo
    /// producto llamado "Shampoo" rompería la URL del primero.
    /// </summary>
    public async Task<AdminProductDto> UpsertProductAsync(
        UpsertProductRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? await BuildUniqueSlugAsync(connection, request.Name, request.Id, ct)
            : request.Slug.Trim().ToLowerInvariant();

        Guid productId;

        if (request.Id is null)
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into public.products
                    (slug, name, brand, category, description, icon, image_url, price)
                values ($1, $2, $3, $4, $5, $6, $7, $8)
                returning id
                """, connection);

            insert.Parameters.AddWithValue(slug);
            insert.Parameters.AddWithValue(request.Name.Trim());
            insert.Parameters.AddWithValue(request.Brand.Trim());
            insert.Parameters.AddWithValue(request.Category.Trim());
            insert.Parameters.AddWithValue(request.Description.Trim());
            insert.Parameters.AddWithValue((object?)request.Icon ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)request.ImageUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue(request.Price);

            productId = (Guid)(await insert.ExecuteScalarAsync(ct) ?? Guid.Empty);

            // Sin variantes el producto no se puede comprar, así que se crea
            // una presentación por defecto en lugar de dejarlo incompleto.
            await using var variant = new NpgsqlCommand(
                """
                insert into public.product_variants (product_id, size_label, stock_on_hand)
                values ($1, 'Única', 0)
                """, connection);
            variant.Parameters.AddWithValue(productId);
            await variant.ExecuteNonQueryAsync(ct);
        }
        else
        {
            productId = request.Id.Value;

            await using var update = new NpgsqlCommand(
                """
                update public.products
                   set name = $2, brand = $3, category = $4, description = $5,
                       icon = $6, image_url = $7, price = $8, is_published = $9
                 where id = $1
                """, connection);

            update.Parameters.AddWithValue(productId);
            update.Parameters.AddWithValue(request.Name.Trim());
            update.Parameters.AddWithValue(request.Brand.Trim());
            update.Parameters.AddWithValue(request.Category.Trim());
            update.Parameters.AddWithValue(request.Description.Trim());
            update.Parameters.AddWithValue((object?)request.Icon ?? DBNull.Value);
            update.Parameters.AddWithValue((object?)request.ImageUrl ?? DBNull.Value);
            update.Parameters.AddWithValue(request.Price);
            update.Parameters.AddWithValue(request.IsPublished);

            await update.ExecuteNonQueryAsync(ct);
        }

        var product = await GetProductAsync(connection, productId, ct);
        return product
            ?? throw new InvalidOperationException("El producto no se pudo guardar.");
    }

    /// <summary>
    /// Ajusta el stock de una variante.
    ///
    /// Manda un delta, no un total. Poner 'stock = 5' desde el panel pisaría
    /// las ventas que ocurrieron mientras se escribía, y la diferencia
    /// sería inexplicable después.
    /// </summary>
    /// <returns>El stock resultante, o null si la variante no existe.</returns>
    public async Task<int?> AdjustStockAsync(
        int variantId, int delta, string reason, CancellationToken ct = default)
    {
        if (delta == 0) return null;

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Marca si la transacción ya terminó, para no revertir una
        // transacción ya confirmada si algo falla después.
        var transactionCompleted = false;

        try
        {
            // La variante se bloquea durante la operación: dos ajustes
            // simultáneos sobre la misma fila se serializan en vez de
            // pisarse.
            await using var update = new NpgsqlCommand(
                """
                update public.product_variants
                   set stock_on_hand = stock_on_hand + $2
                 where id = $1 and is_active
                returning stock_on_hand
                """, connection, transaction);

            update.Parameters.AddWithValue(variantId);
            update.Parameters.AddWithValue(delta);

            var result = await update.ExecuteScalarAsync(ct);
            if (result is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var newStock = (int)result;

            // No se permite dejar la variante en negativo: sería un
            // inventario que dice que hay producto de algo que no existe.
            if (newStock < 0)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    "El ajuste dejaría el stock en negativo. Revisa la cantidad.");
            }

            await using var movement = new NpgsqlCommand(
                """
                insert into public.inventory_movements (variant_id, delta, reason)
                values ($1, $2, $3)
                """, connection, transaction);

            movement.Parameters.AddWithValue(variantId);
            movement.Parameters.AddWithValue(delta);
            movement.Parameters.AddWithValue(reason.Trim());

            await movement.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            transactionCompleted = true;

            return newStock;
        }
        catch
        {
            if (!transactionCompleted)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }
    }

    /// <summary>Alterna la publicación de un producto.</summary>
    public async Task<bool> ToggleProductPublishedAsync(
        Guid id, bool isPublished, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "update public.products set is_published = $2 where id = $1 returning is_published",
            connection);

        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(isPublished);

        var result = await command.ExecuteScalarAsync(ct);
        return result is bool published && published;
    }

    // =================================================================
    // Auxiliares
    // =================================================================

    private static async Task<AdminProductDto?> GetProductAsync(
        NpgsqlConnection connection, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            select p.id, p.slug, p.name, p.brand, p.category, p.description,
                   p.icon, p.image_url, p.price, p.tax_rate, p.is_published,
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
              left join public.product_variants v on v.product_id = p.id
             where p.id = $1
             group by p.id
            """, connection);

        command.Parameters.AddWithValue(id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new AdminProductDto(
            Id: reader.GetGuid(0),
            Slug: reader.GetString(1),
            Name: reader.GetString(2),
            Brand: reader.GetString(3),
            Category: reader.GetString(4),
            Description: reader.GetString(5),
            Icon: reader.IsDBNull(6) ? null : reader.GetString(6),
            ImageUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
            Price: reader.GetDecimal(8),
            TaxRate: reader.GetDecimal(9),
            IsPublished: reader.GetBoolean(10),
            Variants: ReadVariants(reader, 11));
    }

    /// <summary>
    /// Construye un slug único a partir del nombre.
    ///
    /// Agrega un sufijo numérico si ya existe otro producto con ese slug. Sin
    /// esto, guardar dos productos llamados "Shampoo" rompería la URL del
    /// primero, que es la que las clientas ya tienen guardada.
    /// </summary>
    private static async Task<string> BuildUniqueSlugAsync(
        NpgsqlConnection connection,
        string name,
        Guid? excludeId,
        CancellationToken ct)
    {
        var baseSlug = Slugify(name);
        var candidate = baseSlug;
        var suffix = 2;

        while (true)
        {
            await using var command = new NpgsqlCommand(
                "select 1 from public.products where slug = $1 and ($2::uuid is null or id <> $2)",
                connection);

            command.Parameters.AddWithValue(candidate);
            command.Parameters.AddWithValue((object?)excludeId ?? DBNull.Value);

            var exists = await command.ExecuteScalarAsync(ct) is not null;
            if (!exists) return candidate;

            candidate = $"{baseSlug}-{suffix++}";
        }
    }

    /// <summary>
    /// Convierte un nombre en slug: sin acentos, con guiones.
    ///
    /// "Kérastase Bain Satin" tiene que volverse "kerastase-bain-satin", no
    /// "kérastase-bain-satin". Las URLs con acentos se rompen en algunos
    /// navegadores y ensucian los enlaces que se comparten por WhatsApp.
    /// </summary>
    internal static string Slugify(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var withoutDiacritics = new string(
            normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());

        var slug = new string(withoutDiacritics
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        // Colapsa guiones repetidos y quita los de los extremos.
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private static IReadOnlyList<AdminVariantDto> ReadVariants(
        NpgsqlDataReader reader, int ordinal)
    {
        using var doc = JsonbReader.Parse(reader, ordinal);
        if (doc is null) return Array.Empty<AdminVariantDto>();

        var results = new List<AdminVariantDto>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var sku = item.GetProperty("sku");
            var priceOverride = item.GetProperty("priceOverride");

            results.Add(new AdminVariantDto(
                Id: item.GetProperty("id").GetInt32(),
                SizeLabel: item.GetProperty("sizeLabel").GetString()!,
                Sku: sku.ValueKind == System.Text.Json.JsonValueKind.Null ? null : sku.GetString(),
                StockOnHand: item.GetProperty("stockOnHand").GetInt32(),
                PriceOverride: priceOverride.ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : priceOverride.GetDecimal()));
        }

        return results;
    }
}