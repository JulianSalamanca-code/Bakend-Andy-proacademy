using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Models;
using AndyProAcademy.Api.Services;

namespace AndyProAcademy.Api.Endpoints;

/// <summary>
/// Endpoints de la tienda.
///
/// Flujo de un pedido:
///   1. POST /shop/orders          crea el pedido en estado 'pendiente'
///   2. (el frontend redirige)      el cliente paga en Mercado Pago
///   3. POST /shop/webhooks/...     el webhook confirma y descuenta stock
///
/// El stock se descuenta en el paso 3, no en el 1. Si se descontara al
/// crear el pedido, una clienta que abandona el checkout dejaría producto
/// bloqueado sin haber comprado nada.
/// </summary>
public static class ShopEndpoints
{
    public static void MapShopEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shop")
            .WithTags("Shop");

        // -----------------------------------------------------------------
        // Pedidos
        // -----------------------------------------------------------------

        group.MapPost("/orders", async (
                ShopRepository repo,
                MercadoPagoClient mercadoPago,
                CreateOrderRequest request,
                CancellationToken ct) =>
            {
                var error = Validate(request);
                if (error is not null) return error;

                // Se comprueba stock antes de crear para dar un mensaje
                // concreto. La carrera real la resuelve la constraint de
                // stock al confirmar el pago.
                var shortages = await repo.CheckStockAsync(request.Items, ct);
                if (shortages.Count > 0)
                {
                    return Results.Json(
                        new StockConflictResponse(
                            Error: "insufficient_stock",
                            Message: "Uno de los productos ya no tiene stock suficiente.",
                            Shortages: shortages),
                        statusCode: StatusCodes.Status409Conflict);
                }

                OrderDto order;
                try
                {
                    order = await repo.CreateOrderAsync(request, ct);
                }
                catch (ShopException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }

                // Si Mercado Pago no está configurado, el pedido se crea
                // igual y se devuelve sin enlace de pago. Así la demo
                // funciona antes de tener las llaves, sin bloquear el flujo.
                string? checkoutUrl = null;
                string? preferenceId = null;

                try
                {
                    var preference = await mercadoPago.CreatePreferenceAsync(
                        order.Code,
                        order.Total,
                        $"Pedido {order.Code} · Andy Cosmetología",
                        order.ContactEmail,
                        ct);

                    checkoutUrl = preference.CheckoutUrl;
                    preferenceId = preference.PreferenceId;
                }
                catch (Exception ex) when (
                    ex is MercadoPagoException or MercadoPagoConfigurationException)
                {
                    // Se registra pero no se falla el pedido: la clienta ya
                    // tiene su folio y Andy puede cobrarle por otro medio.
                }

                return Results.Created($"/api/shop/orders/{order.Id}", new OrderCheckoutDto(
                    OrderId: order.Id,
                    Code: order.Code,
                    Status: order.Status,
                    Total: order.Total,
                    CheckoutUrl: checkoutUrl,
                    PreferenceId: preferenceId,
                    PaidAt: null,
                    PaymentReference: null));
            })
            .Produces<OrderCheckoutDto>(StatusCodes.Status201Created)
            .Produces<StockConflictResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("CreateOrder")
            .WithSummary("Crea un pedido y devuelve el enlace de pago");

        group.MapGet("/orders/{id:guid}", async (
                ShopRepository repo, Guid id, CancellationToken ct) =>
            {
                var order = await repo.GetOrderAsync(id, ct);
                return order is null
                    ? Results.NotFound(new { message = "Pedido no encontrado" })
                    : Results.Ok(order);
            })
            .Produces<OrderDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetOrder")
            .WithSummary("Detalle de un pedido");

        group.MapGet("/orders", async (
                ShopRepository repo, string? status, CancellationToken ct) =>
            Results.Ok(await repo.GetOrdersByStatusAsync(status, ct: ct)))
            .Produces<IReadOnlyList<OrderDto>>(StatusCodes.Status200OK)
            .WithName("GetOrders")
            .WithSummary("Lista pedidos, opcionalmente por estado");

        // -----------------------------------------------------------------
        // Confirmación de pago
        // -----------------------------------------------------------------

        /// <summary>
        /// Confirma el pago de un pedido y descuenta el stock.
        ///
        /// La usa el frontend cuando la clienta vuelve del checkout. El
        /// webhook sigue siendo la fuente principal: si cierra la pestaña
        /// antes de regresar, esta llamada nunca llega.
        /// </summary>
        group.MapPost("/orders/{id:guid}/confirm", async (
                ShopRepository repo,
                MercadoPagoClient mercadoPago,
                Guid id,
                string paymentId,
                CancellationToken ct) =>
            {
                var (status, reference) = await mercadoPago.GetPaymentStatusAsync(paymentId, ct);

                if (status != "approved")
                {
                    return Results.BadRequest(new
                    {
                        message = status switch
                        {
                            "rejected" => "Mercado Pago rechazó el pago.",
                            "pending" or "in_process" => "El pago sigue en proceso.",
                            "cancelled" => "El pago fue cancelado.",
                            _ => "No se pudo verificar el pago con Mercado Pago.",
                        },
                        status,
                    });
                }

                try
                {
                    var order = await repo.ConfirmPaymentAsync(id, paymentId, ct);
                    return order is null
                        ? Results.NotFound(new { message = "Pedido no encontrado" })
                        : Results.Ok(order);
                }
                catch (ShopException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .Produces<OrderDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("ConfirmOrderPayment")
            .WithSummary("Confirma el pago tras volver del checkout");

        /// <summary>
        /// Webhook de Mercado Pago.
        ///
        /// Es la vía fiable: el frontend puede cerrarse antes de regresar y
        /// aun así el pago debe confirmar el pedido y descontar el stock.
        ///
        /// Por eso devuelve 200 incluso sin hacer nada: Mercado Pago reintenta
        /// el envío si recibe un error, y no sirve de nada que se pierda a
        /// propósito. La conciliación periódica cubre los casos perdidos.
        /// </summary>
        group.MapPost("/webhooks/mercadopago", async (
                ShopRepository repo,
                MercadoPagoWebhookDto notification,
                CancellationToken ct) =>
            {
                if (notification.Action != "payment.updated" &&
                    notification.Action != "payment.approved")
                {
                    // Otros tipos de notificación no aplican a este flujo.
                    return Results.Ok(new { received = true, handled = false });
                }

                // Se busca por external_reference, que es el código del
                // pedido. Sin él no hay forma de saber qué pedido es.
                if (string.IsNullOrWhiteSpace(notification.ExternalReference))
                {
                    return Results.Ok(new { received = true, handled = false });
                }

                var orderId = await repo.GetOrderIdByCodeAsync(
                    notification.ExternalReference, ct);

                if (orderId is null)
                {
                    return Results.Ok(new { received = true, handled = false });
                }

                try
                {
                    await repo.ConfirmPaymentAsync(
                        orderId.Value,
                        notification.DataId.ToString(),
                        ct);

                    return Results.Ok(new { received = true, handled = true });
                }
                catch (ShopException)
                {
                    // Stock insuficiente o pedido ya procesado. Se responde
                    // 200 para no provocar reintentos infinitos de
                    // Mercado Pago: reenviar el mismo aviso no va a
                    // conseguir más stock.
                    return Results.Ok(new { received = true, handled = false });
                }
            })
            .WithName("MercadoPagoWebhook")
            .WithSummary("Notificación de pago de Mercado Pago")
            .WithDescription(
                "Devuelve 200 incluso cuando no puede aplicar el cambio. "
                + "Mercado Pago reintenta ante errores, y repetir el mismo aviso "
                + "no mejora la situación.");

        // -----------------------------------------------------------------
        // Estado del proveedor y resumen
        // -----------------------------------------------------------------

        group.MapGet("/payment-config", (MercadoPagoClient client) => Results.Ok(new
        {
            // El frontend muestra el aviso de modo sandbox con esto, para que
            // nadie espere un cargo real en una demo.
            sandbox = client.IsSandbox,
            available = client.IsConfigured,
            publicKey = client.IsConfigured ? client.GetPublicKey() : string.Empty,
        }))
        .WithName("GetPaymentConfig")
        .WithSummary("Indica si el pago está en modo sandbox");

        group.MapGet("/summary", async (ShopRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetSummaryAsync(ct)))
            .Produces<ShopSummaryDto>(StatusCodes.Status200OK)
            .WithName("GetShopSummary")
            .WithSummary("Resumen de ventas para el panel");
    }

    private static IResult? Validate(CreateOrderRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
            return Results.BadRequest(new { message = "El carrito está vacío." });

        if (request.Items.Any(i => i.Quantity <= 0))
            return Results.BadRequest(new { message = "Alguna cantidad no es válida." });

        if (request.Items.Count > 50)
            return Results.BadRequest(new { message = "Demasiados productos en el carrito." });

        if (string.IsNullOrWhiteSpace(request.ContactName) ||
            request.ContactName.Trim().Length < 3)
        {
            return Results.BadRequest(new { message = "Escribe tu nombre completo." });
        }

        if (request.ContactPhone.Where(char.IsDigit).Count() < 10)
        {
            return Results.BadRequest(new
            {
                message = "Escribe un número de WhatsApp válido con lada.",
            });
        }

        if (!request.ContactEmail.Contains('@'))
            return Results.BadRequest(new { message = "Escribe un correo válido." });

        return null;
    }
}