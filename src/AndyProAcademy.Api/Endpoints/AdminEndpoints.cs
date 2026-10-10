using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Models;
using Npgsql;

namespace AndyProAcademy.Api.Endpoints;

/// <summary>
/// Endpoints del panel de administración.
///
/// ADVERTENCIA — estos endpoints no tienen control de acceso propio. La
/// protección depende de que la conexión a la base pase por el rol de
/// servicio y de que las políticas RLS bloqueen el acceso sin JWT de
/// admin. Hoy la API se conecta con el usuario postgres del pooler, que
/// salta la RLS, así que este endpoint queda abierto a quien conozca la
/// URL.
///
/// Antes de publicar hay que resolverlo de una de estas dos formas:
///   1. Middleware que valide el JWT de Supabase y su rol admin.
///   2. Conectar con un rol de Postgres que solo tenga los permisos
///      justos y esté sujeto a RLS.
///
/// La opción 1 es la recomendada: mantiene una sola conexión y reutiliza el
/// Auth que ya existe para el login.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin");

        // -----------------------------------------------------------------
        // Métricas y agenda
        // -----------------------------------------------------------------

        group.MapGet("/metrics", async (AdminRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetMetricsAsync(DateOnly.FromDateTime(DateTime.Now), ct)))
            .Produces<AdminMetricsDto>(StatusCodes.Status200OK)
            .WithName("GetAdminMetrics")
            .WithSummary("Métricas del día para las tarjetas del panel");

        group.MapGet("/agenda", async (AdminRepository repo, DateOnly date, CancellationToken ct) =>
            Results.Ok(await repo.GetAgendaAsync(date, ct)))
            .Produces<IReadOnlyList<AgendaEntryDto>>(StatusCodes.Status200OK)
            .WithName("GetAdminAgenda")
            .WithSummary("Reservas de una fecha, incluidas las canceladas");

        group.MapGet("/bookings/{id:guid}", async (
                AdminRepository repo, Guid id, CancellationToken ct) =>
            {
                var booking = await repo.GetBookingDetailAsync(id, ct);
                return booking is null
                    ? Results.NotFound(new { message = "Reserva no encontrada" })
                    : Results.Ok(booking);
            })
            .Produces<AdminBookingDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetAdminBookingDetail")
            .WithSummary("Detalle completo de una reserva");

        group.MapGet("/bookings", async (
                AdminRepository repo, DateOnly date, CancellationToken ct) =>
            Results.Ok(await repo.GetAgendaAsync(date, ct)))
            .Produces<IReadOnlyList<AgendaEntryDto>>(StatusCodes.Status200OK)
            .WithName("GetAdminBookingsByDate")
            .WithSummary("Alias de /agenda, para el flujo del panel");

        // -----------------------------------------------------------------
        // Productos
        // -----------------------------------------------------------------

        group.MapGet("/products", async (AdminRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetProductsAsync(ct)))
            .Produces<IReadOnlyList<AdminProductDto>>(StatusCodes.Status200OK)
            .WithName("GetAdminProducts")
            .WithSummary("Productos con stock, incluidos los no publicados");

        group.MapPost("/products", async (
                AdminRepository repo, UpsertProductRequest request, CancellationToken ct) =>
            {
                if (request.Price < 0)
                    return Results.BadRequest(new { message = "El precio no puede ser negativo." });

                if (string.IsNullOrWhiteSpace(request.Name) ||
                    string.IsNullOrWhiteSpace(request.Brand) ||
                    string.IsNullOrWhiteSpace(request.Category))
                {
                    return Results.BadRequest(new { message = "Nombre, marca y categoría son obligatorios." });
                }

                var product = await repo.UpsertProductAsync(request, ct);
                return request.Id is null
                    ? Results.Created($"/api/admin/products/{product.Id}", product)
                    : Results.Ok(product);
            })
            .Produces<AdminProductDto>(StatusCodes.Status201Created)
            .Produces<AdminProductDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("UpsertProduct")
            .WithSummary("Crea o actualiza un producto");

        group.MapPatch("/products/{id:guid}/published", async (
                AdminRepository repo, Guid id, bool isPublished, CancellationToken ct) =>
            {
                var updated = await repo.ToggleProductPublishedAsync(id, isPublished, ct);
                return Results.Ok(new { id, isPublished = updated });
            })
            .WithName("ToggleProductPublished")
            .WithSummary("Muestra u oculta un producto del catálogo");

        group.MapPost("/variants/{id:int}/stock", async (
                AdminRepository repo, int id, AdjustStockRequest request, CancellationToken ct) =>
            {
                if (request.Delta == 0)
                {
                    return Results.BadRequest(new
                    {
                        message = "El ajuste debe ser distinto de cero.",
                        // Se explica por qué se manda delta y no total.
                        hint = "Envía cuántas unidades entran (+) o salen (-), no el total.",
                    });
                }

                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    return Results.BadRequest(new
                    {
                        message = "Indica el motivo del ajuste.",
                        hint = "Sin motivo, un cambio de stock no se puede auditar después.",
                    });
                }

                try
                {
                    var stock = await repo.AdjustStockAsync(id, request.Delta, request.Reason, ct);
                    return stock is null
                        ? Results.NotFound(new { message = "Variante no encontrada" })
                        : Results.Ok(new { variantId = id, stockOnHand = stock });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
                catch (PostgresException ex) when (ex.SqlState == "23514")
                {
                    // 23514 = check_violation. La base rechaza el stock
                    // negativo con la constraint stock_on_hand >= 0, que es
                    // la defensa correcta: aunque el código tuviera un
                    // error, la base no deja pasar un inventario imposible.
                    // Aquí solo se traduce a un mensaje que el panel pueda
                    // mostrar en vez de un 500.
                    return Results.BadRequest(new
                    {
                        message = "El ajuste dejaría el stock en negativo. Revisa la cantidad.",
                    });
                }
            })
            .WithName("AdjustStock")
            .WithSummary("Ajusta el stock de una variante y registra el movimiento")
            .WithDescription(
                "Manda un delta (cuántas unidades entran o salen), no el total. "
                + "Poner el total desde el panel pisaría las ventas que ocurrieron mientras se escribía.");
    }
}