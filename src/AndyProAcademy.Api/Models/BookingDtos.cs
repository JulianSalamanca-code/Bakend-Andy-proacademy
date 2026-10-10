namespace AndyProAcademy.Api.Models;

// =====================================================================
// Reservas
//
// El flujo tiene una regla que conviene tener presente al leer esto:
// una reserva es un BORRADOR hasta que se confirma el anticipo. Por eso
// el estado inicial es 'pendiente' y el folio se entrega al instante:
// la clienta puede quedarse con un código aunque todavía no haya pagado.
// =====================================================================

/// <summary>Un horario libre, ya descontado de reservas y bloqueos.</summary>
public sealed record AvailabilitySlotDto(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

/// <summary>
/// Respuesta de la consulta de disponibilidad.
///
/// Incluye la duración y el precio para que el frontend no tenga que
/// pedirlos aparte: el precio total depende de los complementos, y el
/// resumen lateral necesita ambos antes de que la clienta elija.
/// </summary>
public sealed record AvailabilityResponseDto(
    int SpecialistId,
    DateOnly Date,
    IReadOnlyList<AvailabilitySlotDto> Slots,
    int DurationMinutes,
    decimal BasePrice,
    decimal DepositAmount);

/// <summary>
/// Datos que envía el frontend para crear una reserva.
/// </summary>
/// <remarks>
/// La reserva queda en estado 'pendiente' hasta que se confirme el
/// anticipo. El folio se entrega de inmediato para que la clienta pueda
/// reclamar la cita aunque todavía no haya pagado.
/// </remarks>
public sealed record CreateBookingRequest(
    int SpecialistId,
    DateTimeOffset StartsAt,
    string ContactName,
    string ContactPhone,
    string ContactEmail,
    int? ServiceId = null,
    Guid? CourseId = null,
    int[]? AddOnIds = null,
    string Modality = "studio",
    string? Notes = null,
    string? RfcReceiver = null,
    Guid? StudentId = null);

/// <summary>
/// Reserva creada. Es lo que la clienta ve al confirmar.
/// </summary>
public sealed record BookingDto(
    Guid Id,
    string Code,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Modality,
    string Status,
    decimal PriceTotal,
    decimal DepositAmount,
    decimal BalanceAmount,
    string ContactName,
    string ContactPhone,
    string ContactEmail,
    string? Notes,
    int? ServiceId,
    Guid? CourseId,
    int[] AddOnIds,
    string? SubjectName,
    DateTimeOffset CreatedAt);

/// <summary>
/// Detalle de una reserva concreta.
/// </summary>
public sealed record BookingDetailDto(
    BookingDto Booking,
    int SpecialistId,
    string SpecialistName,
    decimal DepositPaid,
    bool DepositConfirmed);

/// <summary>
/// Fila de la agenda del panel de administración.
/// </summary>
public sealed record AdminBookingDto(
    Guid Id,
    string Code,
    string ContactName,
    string ContactPhone,
    string ContactEmail,
    string SubjectName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Modality,
    string Status,
    decimal PriceTotal,
    decimal DepositAmount,
    decimal DepositPaid,
    string? Notes,
    string? RfcReceiver,
    DateTimeOffset CreatedAt);

/// <summary>
/// Cambio manual del estado de una reserva.
/// </summary>
public sealed record UpdateBookingStatusRequest(
    string Status,
    string? Reason = null);

/// <summary>
/// Resultado de intentar reservar un horario que ya se tomó.
/// </summary>
/// <remarks>
/// Se distingue del error genérico porque el frontend debe ofrecer
/// "elige otro horario", no un mensaje de fallo. Ocurre de verdad: dos
/// personas ven el mismo horario y la segunda llega un segundo después.
/// </remarks>
public sealed record SlotConflictResponse(
    string Error,
    string Message,
    IReadOnlyList<AvailabilitySlotDto> Alternatives);