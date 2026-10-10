using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Models;

namespace AndyProAcademy.Api.Endpoints;

/// <summary>
/// Endpoints de reserva: disponibilidad, creación y consulta.
///
/// Crear una reserva es público a propósito. Exigir cuenta convertiría cada
/// visita en un muro de registro y bajó la conversión; el contacto llega en
/// el cuerpo de la reserva y el folio permite reclamarla después. Lo que sí
/// requiere sesión es ver las reservas ajenas, y eso lo controla la RLS de
/// Postgres, no un guard del frontend.
/// </summary>
public static class BookingEndpoints
{
    public static void MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/booking")
            .WithTags("Booking");

        // -----------------------------------------------------------------
        // Disponibilidad
        // -----------------------------------------------------------------

        group.MapGet("/availability", async (
                BookingRepository repo,
                DateOnly date,
                int serviceId,
                int? specialistId,
                string? addOnIds,
                CancellationToken ct) =>
            {
                var specialist = specialistId ?? await DefaultSpecialistIdAsync(repo, ct);

                var timing = await repo.GetServiceTimingAsync(serviceId, ct);
                if (timing is null)
                {
                    return Results.NotFound(new { message = "Servicio no encontrado" });
                }

                var (duration, basePrice, _) = timing.Value;

                // Los complementos suman tiempo: un laminado de ceja son
                // +15 min que Andy tiene que reservar de verdad.
                var addOns = ParseAddOnIds(addOnIds);
                var impact = await repo.GetAddOnsImpactAsync(serviceId, addOns, ct);

                var totalDuration = duration + impact.ExtraMinutes;
                var totalPrice = basePrice + impact.Price;

                var slots = await repo.GetAvailableSlotsAsync(
                    specialist, date, totalDuration, ct: ct);

                return Results.Ok(new AvailabilityResponseDto(
                    SpecialistId: specialist,
                    Date: date,
                    Slots: slots,
                    DurationMinutes: totalDuration,
                    BasePrice: totalPrice,
                    // El anticipo se calcula aquí y no en el cliente: si el
                    // total cambia, el importe cobrado cambia con él.
                    DepositAmount: RoundMoney(totalPrice * 0.30m)));
            })
            .Produces<AvailabilityResponseDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetAvailability")
            .WithSummary("Horarios libres de un servicio en una fecha");

        // -----------------------------------------------------------------
        // Crear reserva
        // -----------------------------------------------------------------

        group.MapPost("/", async (
                BookingRepository repo,
                CreateBookingRequest request,
                CancellationToken ct) =>
            {
                var validationError = Validate(request);
                if (validationError is not null) return validationError;

                var booking = await repo.TryCreateAsync(request, ct);

                if (booking is null)
                {
                    // El horario se tomó entre que la clienta lo eligió y
                    // confirmó. No es un error del servidor: se le ofrecen
                    // alternativas, porque hay otras libres.
                    var alternatives = await FindAlternativesAsync(
                        repo, request, ct);

                    return Results.Json(
                        new SlotConflictResponse(
                            Error: "slot_taken",
                            Message: "Ese horario acaba de ser tomado. Elige otro de la lista.",
                            Alternatives: alternatives),
                        statusCode: StatusCodes.Status409Conflict);
                }

                return Results.Created($"/api/booking/{booking.Id}", booking);
            })
            .Produces<BookingDto>(StatusCodes.Status201Created)
            .Produces<SlotConflictResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("CreateBooking")
            .WithSummary("Crea una reserva y devuelve su folio");

        // -----------------------------------------------------------------
        // Consultar
        // -----------------------------------------------------------------

        group.MapGet("/{id:guid}", async (
                BookingRepository repo,
                Guid id,
                CancellationToken ct) =>
            {
                var booking = await repo.UpdateStatusAsync(id, "pendiente", ct);
                return booking is null
                    ? Results.NotFound(new { message = "Reserva no encontrada" })
                    : Results.Ok(booking);
            })
            .Produces<BookingDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetBooking")
            .WithSummary("Detalle de una reserva");

        // -----------------------------------------------------------------
        // Agenda del día (panel de administración)
        // -----------------------------------------------------------------

        group.MapGet("/agenda", async (
                BookingRepository repo,
                DateOnly date,
                CancellationToken ct) =>
            Results.Ok(await repo.GetBookingsForDateAsync(date, ct)))
            .Produces<IReadOnlyList<AdminBookingDto>>(StatusCodes.Status200OK)
            .WithName("GetAgenda")
            .WithSummary("Reservas de una fecha");

        // -----------------------------------------------------------------
        // Cambiar estado
        // -----------------------------------------------------------------

        group.MapPatch("/{id:guid}/status", async (
                BookingRepository repo,
                Guid id,
                UpdateBookingStatusRequest request,
                CancellationToken ct) =>
            {
                var valid = new[] { "pendiente", "confirmada", "completada", "cancelada" };
                if (!valid.Contains(request.Status))
                {
                    return Results.BadRequest(new
                    {
                        message = "Estado inválido",
                        permitidos = valid,
                    });
                }

                var updated = await repo.UpdateStatusAsync(id, request.Status, ct);
                return updated is null
                    ? Results.NotFound(new { message = "Reserva no encontrada" })
                    : Results.Ok(updated);
            })
            .Produces<BookingDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("UpdateBookingStatus")
            .WithSummary("Confirma, completa o cancela una reserva");
    }

    // =================================================================
    // Auxiliares
    // =================================================================

    /// <summary>
    /// Valida lo básico antes de tocar la base.
    ///
    /// La disponibilidad NO se comprueba aquí a propósito: entre validar e
    /// insertar hay una ventana en la que otra petición se cuela. Esa
    /// comprobación la hace la constraint de la base, que es atómica.
    /// </summary>
    private static IResult? Validate(CreateBookingRequest request)
    {
        if (request.ServiceId is null && request.CourseId is null)
            return Results.BadRequest(new { message = "Elige un servicio o un curso." });

        if (request.ServiceId is not null && request.CourseId is not null)
            return Results.BadRequest(new { message = "Una cita es por servicio o por curso, no por ambos." });

        if (request.StartsAt <= DateTimeOffset.UtcNow)
            return Results.BadRequest(new { message = "Elige una fecha futura." });

        if (request.ContactName.Trim().Length < 3)
            return Results.BadRequest(new { message = "Escribe tu nombre completo." });

        // 10 dígitos es lo mínimo para un número mexicano con lada.
        if (request.ContactPhone.Where(char.IsDigit).Count() < 10)
            return Results.BadRequest(new { message = "Escribe un número de WhatsApp válido con lada." });

        if (!request.ContactEmail.Contains('@') || !request.ContactEmail.Contains('.'))
            return Results.BadRequest(new { message = "Escribe un correo válido." });

        if (request.Modality is not ("studio" or "domicilio"))
            return Results.BadRequest(new { message = "Modalidad inválida." });

        return null;
    }

    /// <summary>
    /// Busca horarios alternativos para la misma fecha cuando el elegido se
    /// acaba de tomar. Se devuelven en la respuesta del 409 para que el
    /// frontend pueda ofrecer "elige otro" sin una llamada extra.
    /// </summary>
    private static async Task<IReadOnlyList<AvailabilitySlotDto>> FindAlternativesAsync(
        BookingRepository repo, CreateBookingRequest request, CancellationToken ct)
    {
        if (request.ServiceId is null) return Array.Empty<AvailabilitySlotDto>();

        var timing = await repo.GetServiceTimingAsync(request.ServiceId.Value, ct);
        if (timing is null) return Array.Empty<AvailabilitySlotDto>();

        var addOns = request.AddOnIds ?? Array.Empty<int>();
        var impact = await repo.GetAddOnsImpactAsync(request.ServiceId.Value, addOns, ct);

        var slots = await repo.GetAvailableSlotsAsync(
            request.SpecialistId,
            DateOnly.FromDateTime(request.StartsAt.DateTime),
            timing.Value.Duration + impact.ExtraMinutes,
            ct: ct);

        // Se filtran los que ya pasaron: ofrecer un horario de hace una
        // hora es confuso.
        var now = DateTimeOffset.UtcNow;
        return slots.Where(s => s.StartsAt > now).Take(8).ToList();
    }

    private static async Task<int> DefaultSpecialistIdAsync(
        BookingRepository repo, CancellationToken ct)
    {
        var ids = await repo.GetSpecialistIdsAsync(ct);
        if (ids.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay especialistas activos. Revisa la migración 004_seed.sql.");
        }

        return ids[0];
    }

    private static int[] ParseAddOnIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<int>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
                  .Where(id => id > 0)
                  .ToArray();

    /// <summary>
    /// Redondeo a dos decimales con redondeo bancario.
    ///
    /// Math.Round redondea "a la mitad al más cercano" de forma distinta
    /// según el modo, y en importes eso se traduce en un centavo de
    /// diferencia entre lo que calcula la base y lo que se cobra.
    /// ToEven es el que evita ese desfase.
    /// </summary>
    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);
}