namespace AndyProAcademy.Api.Models;

// =====================================================================
// Administración
//
// El panel es donde Andy trabaja el día a día. Su condición principal es
// que cualquier cambio se vea al instante en el catálogo: si actualiza un
// precio y la clienta sigue viendo el anterior, deja de confiar en el
// sistema.
// =====================================================================

/// <summary>Resumen para las tarjetas de métricas del panel.</summary>
public sealed record AdminMetricsDto(
    int BookingsToday,
    int PendingConfirmation,
    int ConfirmedToday,
    decimal DepositsCollected,
    decimal ExpectedToday,
    int LowStockProducts,
    int OutOfStockProducts);

/// <summary>
/// Fila de la agenda del día, pensada para leerse de un vistazo.
/// </summary>
public sealed record AgendaEntryDto(
    Guid Id,
    string Code,
    string ClientName,
    string ClientPhone,
    string ClientEmail,
    string Subject,
    string Kind,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string Modality,
    decimal Total,
    decimal Deposit,
    bool DepositPaid,
    string? Notes,
    string? RfcReceiver);

/// <summary>
/// Datos para crear o editar un producto desde el panel.
/// </summary>
/// <remarks>
/// Es un record con parámetros opcionales porque el mismo endpoint sirve
/// para crear (sin Id) y para editar (con Id). El repositorio decide
/// hacer INSERT o UPDATE según eso.
/// </remarks>
public sealed record UpsertProductRequest(
    string Name,
    string Brand,
    string Category,
    decimal Price,
    string Description = "",
    string? Slug = null,
    string? Icon = null,
    string? ImageUrl = null,
    Guid? Id = null,
    bool IsPublished = true);

/// <summary>
/// Ajuste de stock.
/// </summary>
/// <param name="Delta">
/// Cambio de unidades. Positivo entra producto, negativo sale.
/// Nunca se envía el total absoluto: poner 'stock = 5' desde el panel
/// pisaría las ventas que ocurrieron mientras se escribía.
/// </param>
/// <param name="Reason">
/// Motivo del movimiento. Se guarda en inventory_movements para poder
/// auditar por qué cambió el stock.
/// </param>
public sealed record AdjustStockRequest(
    int Delta,
    string Reason);

/// <summary>Producto con su stock, tal como lo ve el panel.</summary>
public sealed record AdminProductDto(
    Guid Id,
    string Slug,
    string Name,
    string Brand,
    string Category,
    string Description,
    string? Icon,
    string? ImageUrl,
    decimal Price,
    decimal TaxRate,
    bool IsPublished,
    IReadOnlyList<AdminVariantDto> Variants);

public sealed record AdminVariantDto(
    int Id,
    string SizeLabel,
    string? Sku,
    int StockOnHand,
    decimal? PriceOverride);

/// <summary>Desglose de una reserva para el detalle del panel.</summary>
public sealed record AdminBookingDetailDto(
    Guid Id,
    string Code,
    string ClientName,
    string ClientPhone,
    string ClientEmail,
    string Subject,
    string Kind,
    string Status,
    string Modality,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    decimal Total,
    decimal Deposit,
    bool DepositPaid,
    DateTimeOffset? DepositPaidAt,
    string? PaymentReference,
    string? Notes,
    string? RfcReceiver,
    string SpecialistName,
    int[] AddOnIds,
    DateTimeOffset CreatedAt);