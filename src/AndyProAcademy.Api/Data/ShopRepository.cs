using Npgsql;
using AndyProAcademy.Api.Models;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Acceso a datos de la tienda.
///
/// Los importes se calculan aquí, nunca se reciben del frontend. El
/// cliente manda identificadores y cantidades; si mandara el total,
/// podría comprar un shampoo de 650 por un peso.
/// </summary>
public sealed class ShopRepository(NpgsqlConnectionStringBuilder connectionString)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    /// Verifica que todas las líneas del carrito tengan stock.
    ///
    /// Se comprueba antes de crear el pedido para dar un mensaje útil. No
    /// sustituye al descuento en la función confirm_order_payment, que es
    /// la que resuelve la carrera: entre esta comprobación y el pago puede
    /// venderse la última unidad.
    /// </summary>
    public async Task<IReadOnlyList<StockShortageDto>> CheckStockAsync(
        IReadOnlyList<CartItemDto> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return Array.Empty<StockShortageDto>();

        await using var connection = await OpenAsync(ct);

        var ids = items.Select(i => i.VariantId).ToArray();

        await using var command = new NpgsqlCommand(
            """
            select v.id, p.name, v.size_label, v.stock_on_hand
              from public.product_variants v
              join public.products p on p.id = v.product_id
             where v.id = any($1) and v.is_active and p.is_published
            """, connection);

        command.Parameters.Add(new NpgsqlParameter { Value = ids });

        var stock = new Dictionary<int, (string Name, string Size, int Stock)>();

        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                stock[reader.GetInt32(0)] = (
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3));
            }
        }

        var shortages = new List<StockShortageDto>();

        foreach (var item in items)
        {
            if (!stock.TryGetValue(item.VariantId, out var info))
            {
                // La variante no existe o el producto está oculto. Se reporta
                // como faltante con stock 0 para que el frontend la saque
                // del carrito en vez de dejarla colgada.
                shortages.Add(new StockShortageDto(
                    item.VariantId, "Producto no disponible", "—", item.Quantity, 0));
                continue;
            }

            if (info.Stock < item.Quantity)
            {
                shortages.Add(new StockShortageDto(
                    item.VariantId, info.Name, info.Size, item.Quantity, info.Stock));
            }
        }

        return shortages;
    }

    /// <summary>
    /// Crea un pedido con los importes calculados en la base.
    ///
    /// El IVA se aplica sobre el precio: cosméticos en México son 16%. Se
    /// redondea a dos decimales con ToEven para que el total cuadre con la
    /// suma de las líneas, sin centavo huérfano.
    /// </summary>
    public async Task<OrderDto> CreateOrderAsync(
        CreateOrderRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Marca si la transacción ya terminó. Sin ella, un fallo posterior
        // al commit intentaría revertir una transacción cerrada.
        var transactionCompleted = false;

        try
        {
            var orderId = Guid.NewGuid();
            var code = $"PED-{orderId.ToString("N")[..6].ToUpperInvariant()}";

            // -----------------------------------------------------------------
            // Calcular líneas desde los precios de la base
            // -----------------------------------------------------------------
            var items = request.Items
                .GroupBy(i => i.VariantId)
                .Select(g => (VariantId: g.Key, Quantity: g.Sum(i => i.Quantity)))
                .ToList();

            var ids = items.Select(i => i.VariantId).ToArray();

            await using var prices = new NpgsqlCommand(
                """
                select v.id, p.name, p.brand, v.size_label,
                       coalesce(v.price_override, p.price) as unit_price,
                       p.tax_rate
                  from public.product_variants v
                  join public.products p on p.id = v.product_id
                 where v.id = any($1) and v.is_active and p.is_published
                """, connection, transaction);

            prices.Parameters.Add(new NpgsqlParameter { Value = ids });

            var lines = new List<(int VariantId, string Name, string Brand, string Size,
                                  int Qty, decimal UnitPrice, decimal TaxRate)>();

            await using (var reader = await prices.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    lines.Add((
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        0, // se rellena abajo
                        reader.GetDecimal(4),
                        reader.GetDecimal(5)));
                }
            }

            if (lines.Count != items.Count)
            {
                await transaction.RollbackAsync(ct);
                throw new ShopException(
                    "Uno de los productos ya no está disponible. Revisa tu carrito.");
            }

            decimal subtotal = 0m;
            decimal tax = 0m;
            var lineRows = new List<(int VariantId, int Qty, decimal Unit, decimal Sub, decimal Tax)>();

            foreach (var item in items)
            {
                var line = lines.First(l => l.VariantId == item.VariantId);

                var lineSubtotal = Math.Round(line.UnitPrice * item.Quantity, 2, MidpointRounding.ToEven);
                var lineTax = Math.Round(lineSubtotal * line.TaxRate, 2, MidpointRounding.ToEven);

                subtotal += lineSubtotal;
                tax += lineTax;
                lineRows.Add((item.VariantId, item.Quantity, line.UnitPrice, lineSubtotal, lineTax));
            }

            subtotal = Math.Round(subtotal, 2, MidpointRounding.ToEven);
            tax = Math.Round(tax, 2, MidpointRounding.ToEven);
            var total = Math.Round(subtotal + tax, 2, MidpointRounding.ToEven);

            // -----------------------------------------------------------------
            // Insertar pedido y líneas
            // -----------------------------------------------------------------
            await using var insertOrder = new NpgsqlCommand(
                """
                insert into public.orders (
                  id, code, status, fulfillment, notes, rfc_receiver,
                  contact_name, contact_phone, contact_email,
                  subtotal, tax_amount, total
                )
                values ($1, $2, 'pendiente', $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """, connection, transaction);

            insertOrder.Parameters.AddWithValue(orderId);
            insertOrder.Parameters.AddWithValue(code);
            insertOrder.Parameters.AddWithValue(
                request.Fulfillment == "envio" ? "envio" : "pickup");
            insertOrder.Parameters.AddWithValue((object?)request.Notes ?? DBNull.Value);
            insertOrder.Parameters.AddWithValue((object?)request.RfcReceiver ?? DBNull.Value);
            insertOrder.Parameters.AddWithValue(request.ContactName.Trim());
            insertOrder.Parameters.AddWithValue(request.ContactPhone.Trim());
            insertOrder.Parameters.AddWithValue(request.ContactEmail.Trim().ToLowerInvariant());
            insertOrder.Parameters.AddWithValue(subtotal);
            insertOrder.Parameters.AddWithValue(tax);
            insertOrder.Parameters.AddWithValue(total);

            await insertOrder.ExecuteNonQueryAsync(ct);

            foreach (var row in lineRows)
            {
                await using var insertLine = new NpgsqlCommand(
                    """
                    insert into public.order_items
                        (order_id, variant_id, quantity, unit_price, subtotal)
                    values ($1, $2, $3, $4, $5)
                    """, connection, transaction);

                insertLine.Parameters.AddWithValue(orderId);
                insertLine.Parameters.AddWithValue(row.VariantId);
                insertLine.Parameters.AddWithValue(row.Qty);
                insertLine.Parameters.AddWithValue(row.Unit);
                insertLine.Parameters.AddWithValue(row.Sub);

                await insertLine.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            transactionCompleted = true;

            // La lectura va fuera de la transacción a propósito: ya se
            // confirmó el pedido y releerlo con la misma transacción
            // abierta exigiría mantenerla viva por una consulta que no
            // necesita consistencia transaccional.
            var order = await GetOrderAsync(connection, orderId, ct)
                ?? throw new ShopException("El pedido se creó pero no se pudo leer.");

            return order;
        }
        catch
        {
            // Rollback sobre una transacción ya confirmada lanza
            // 'This NpgsqlTransaction has completed'. Ese error enmascaraba
            // el problema real, que era la lectura fallida.
            if (!transactionCompleted)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }
    }

    /// <summary>Un pedido con sus líneas.</summary>
    public async Task<OrderDto?> GetOrderAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await GetOrderAsync(connection, id, ct);
    }

    private static async Task<OrderDto?> GetOrderAsync(
        NpgsqlConnection connection, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            select o.id, o.code, o.status, o.fulfillment, o.subtotal,
                   o.tax_amount, o.total, o.contact_name, o.contact_phone,
                   o.contact_email, o.rfc_receiver, o.created_at,
                   coalesce(
                     jsonb_agg(
                       jsonb_build_object(
                         'productName', p.name,
                         'brand', p.brand,
                         'sizeLabel', v.size_label,
                         'quantity', oi.quantity,
                         'unitPrice', oi.unit_price,
                         'subtotal', oi.subtotal,
                         'taxAmount', round(oi.subtotal * p.tax_rate, 2)
                       ) order by p.name
                     ) filter (where oi.id is not null),
                     '[]'::jsonb
                   ) as lines
              from public.orders o
              left join public.order_items oi on oi.order_id = o.id
              left join public.product_variants v on v.id = oi.variant_id
              left join public.products p on p.id = v.product_id
             where o.id = $1
             group by o.id
            """, connection);

        command.Parameters.AddWithValue(id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new OrderDto(
            Id: reader.GetGuid(0),
            Code: reader.GetString(1),
            Status: reader.GetString(2),
            Fulfillment: reader.GetString(3),
            Subtotal: reader.GetDecimal(4),
            TaxAmount: reader.GetDecimal(5),
            Total: reader.GetDecimal(6),
            ContactName: reader.GetString(7),
            ContactPhone: reader.GetString(8),
            ContactEmail: reader.GetString(9),
            RfcReceiver: reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(11),
            Lines: ReadLines(reader, 12));
    }

    /// <summary>
    /// Confirma el pago de un pedido y descuenta el stock.
    ///
    /// Delega en la función confirm_order_payment, que hace el descuento
    /// dentro de una transacción y bloquea cada variante. Aquí solo se
    /// localiza el pedido y se pasa la referencia del pago.
    /// </summary>
    public async Task<OrderDto?> ConfirmPaymentAsync(
        Guid id, string paymentReference, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select * from public.confirm_order_payment($1, $2)
            """, connection);

        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(paymentReference);

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "22023")
        {
            // 22023 = invalid_parameter_value. La función la usa para stock
            // insuficiente o pedido ya procesado.
            throw new ShopException(
                ex.MessageText.Contains("insuficiente", StringComparison.OrdinalIgnoreCase)
                    ? "No hay stock suficiente para completar el pedido."
                    : "El pedido ya fue procesado anteriormente.");
        }

        return await GetOrderAsync(connection, id, ct);
    }

    /// <summary>Busca un pedido por su código de folio.</summary>
    public async Task<Guid?> GetOrderIdByCodeAsync(
        string code, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select id from public.orders where code = $1", connection);
        command.Parameters.AddWithValue(code);

        var result = await command.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }

    /// <summary>Pedidos por estado, para el panel.</summary>
    public async Task<IReadOnlyList<OrderDto>> GetOrdersByStatusAsync(
        string? status, int limit = 50, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select o.id
              from public.orders o
             where ($1::order_status is null or o.status = $1::order_status)
             order by o.created_at desc
             limit $2
            """, connection);

        // Se pasa como texto porque el parámetro se usa en un cast explícito
        // ($1::order_status) y un null sin tipo haría ambiguo el operador.
        command.Parameters.Add(
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
                Value = (object?)status ?? DBNull.Value,
            });
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 200));

        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }

        var orders = new List<OrderDto>();
        foreach (var id in ids)
        {
            var order = await GetOrderAsync(connection, id, ct);
            if (order is not null) orders.Add(order);
        }

        return orders;
    }

    /// <summary>Resumen de la tienda para el panel.</summary>
    public async Task<ShopSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select
              (select count(*) from public.products where is_published) as publicados,
              (select count(*) from public.products where not is_published) as ocultos,
              (select count(*) from public.orders where status = 'pendiente') as pedidos_pend,
              (select count(*) from public.orders where status = 'pagado') as pedidos_pag,
              (select coalesce(sum(total), 0) from public.orders where status = 'pagado') as cobrado,
              (select coalesce(sum(total), 0) from public.orders where status = 'pendiente') as por_cobrar,
              (select coalesce(sum(tax_amount), 0) from public.orders where status = 'pagado') as iva
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new ShopSummaryDto(
            ProductsPublished: reader.GetInt32(0),
            ProductsHidden: reader.GetInt32(1),
            OrdersPending: reader.GetInt32(2),
            OrdersPaid: reader.GetInt32(3),
            RevenueCollected: reader.GetDecimal(4),
            RevenuePending: reader.GetDecimal(5),
            TaxCollected: reader.GetDecimal(6));
    }

    private static IReadOnlyList<OrderLineDto> ReadLines(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return Array.Empty<OrderLineDto>();

        using var doc = JsonbReader.Parse(reader, ordinal);
        if (doc is null) return Array.Empty<OrderLineDto>();

        var lines = new List<OrderLineDto>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            lines.Add(new OrderLineDto(
                ProductName: item.GetProperty("productName").GetString() ?? "",
                Brand: item.GetProperty("brand").GetString() ?? "",
                SizeLabel: item.GetProperty("sizeLabel").GetString() ?? "",
                Quantity: item.GetProperty("quantity").GetInt32(),
                UnitPrice: item.GetProperty("unitPrice").GetDecimal(),
                Subtotal: item.GetProperty("subtotal").GetDecimal(),
                TaxAmount: item.GetProperty("taxAmount").GetDecimal()));
        }

        return lines;
    }
}

/// <summary>Error de la tienda que puede mostrarse al usuario.</summary>
public sealed class ShopException(string message) : Exception(message);