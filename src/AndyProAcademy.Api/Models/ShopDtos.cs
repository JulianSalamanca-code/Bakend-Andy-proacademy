namespace AndyProAcademy.Api.Models;

// =====================================================================
// Tienda y pedidos
//
// Regla central del módulo: el stock NO se descuenta al agregar al
// carrito ni al crear el pedido, sino cuando el pago se confirma. Si se
// descontara antes, una clienta que abandona el checkout dejaría producto
// bloqueado sin haber comprado nada, y habría que liberarlo a mano.
// =====================================================================

/// <summary>
/// Una línea del carrito.
///
/// <paramref name="VariantId"/> identifica la presentación concreta
/// (250 ml, 500 ml) porque el stock vive en la variante, no en el
/// producto.
/// </summary>
public sealed record CartItemDto(
    int VariantId,
    int Quantity);

/// <summary>
/// Crear un pedido.
///
/// El frontend manda identificadores y cantidades, nunca precios. Los
/// importes se calculan en la base: si el cliente enviara el total, podría
/// comprar un shampoo de 650 por un peso.
/// </summary>
public sealed record CreateOrderRequest(
    string ContactName,
    string ContactPhone,
    string ContactEmail,
    IReadOnlyList<CartItemDto> Items,
    string Fulfillment = "pickup",
    string? Notes = null,
    string? RfcReceiver = null);

/// <summary>Pedido creado, con los importes ya calculados.</summary>
public sealed record OrderDto(
    Guid Id,
    string Code,
    string Status,
    string Fulfillment,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    string ContactName,
    string ContactPhone,
    string ContactEmail,
    string? RfcReceiver,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLineDto> Lines);

/// <summary>Línea del pedido con el precio congelado en la compra.</summary>
public sealed record OrderLineDto(
    string ProductName,
    string Brand,
    string SizeLabel,
    int Quantity,
    decimal UnitPrice,
    decimal Subtotal,
    decimal TaxAmount);

/// <summary>
/// Detalle del pedido tras el pago.
///
/// <paramref name="CheckoutUrl"/> es a donde manda Mercado Pago. En
/// sandbox apunta a un dominio de prueba y no cobra nada.
/// </summary>
public sealed record OrderCheckoutDto(
    Guid OrderId,
    string Code,
    string Status,
    decimal Total,
    string? CheckoutUrl,
    string? PreferenceId,
    DateTimeOffset? PaidAt,
    string? PaymentReference);

/// <summary>
/// Error de stock al crear el pedido.
///
/// Ocurre de verdad: el carrito se arma con precios de hace unos minutos
/// y para cuando se confirma otro pudovenderse la última unidad.
/// </summary>
public sealed record StockConflictResponse(
    string Error,
    string Message,
    IReadOnlyList<StockShortageDto> Shortages);

public sealed record StockShortageDto(
    int VariantId,
    string ProductName,
    string SizeLabel,
    int Requested,
    int Available);

/// <summary>
/// Preferencia de Mercado Pago.
///
/// Solo los campos que el frontend necesita para redirigir. Se devuelve
/// como tipo propio en vez de reenviar la respuesta cruda del proveedor,
/// para que un cambio en la API de Mercado Pago no rompa el frontend.
/// </summary>
public sealed record MercadoPagoPreferenceDto(
    string PreferenceId,
    string CheckoutUrl,
    bool Sandbox);

/// <summary>Notificación de Mercado Pago recibida por webhook.</summary>
public sealed record MercadoPagoWebhookDto(
    string Action,
    long DataId,
    string? ExternalReference);

/// <summary>
/// Resumen de la tienda para el panel.
/// </summary>
public sealed record ShopSummaryDto(
    int ProductsPublished,
    int ProductsHidden,
    int OrdersPending,
    int OrdersPaid,
    decimal RevenueCollected,
    decimal RevenuePending,
    decimal TaxCollected);