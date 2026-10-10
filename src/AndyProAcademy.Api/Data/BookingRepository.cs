using Npgsql;
using AndyProAcademy.Api.Models;

namespace AndyProAcademy.Api.Data;

/// <summary>
/// Acceso a las tablas de reserva.
///
/// Nota sobre el manejo de errores: la validación de disponibilidad está
/// en la base de datos (constraint de exclusión y función create_booking),
/// no aquí. Un repositorio que "comprueba y luego inserta" deja una
/// ventana entre ambas operaciones donde otra petición se cuela. Por eso
/// este código intenta insertar y traduce el error de Postgres, en vez de
/// comprobar antes.
/// </summary>
public sealed class BookingRepository(NpgsqlConnectionStringBuilder connectionString)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    // =================================================================
    // Disponibilidad
    // =================================================================

    /// <summary>
    /// Horarios libres de un especialista en una fecha, delegando en la
    /// función get_available_slots de Postgres.
    ///
    /// Cruzar horario, reservas y bloqueos en SQL evita traer todo y
    /// filtrar en el navegador, que es donde se cuelan los errores de
    /// zona horaria y las fechas que cruzan medianoche.
    /// </summary>
    public async Task<IReadOnlyList<AvailabilitySlotDto>> GetAvailableSlotsAsync(
        int specialistId,
        DateOnly date,
        int durationMinutes,
        int slotIntervalMinutes = 30,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select starts_at, ends_at
              from public.get_available_slots($1, $2, $3, $4)
            """, connection);

        command.Parameters.AddWithValue(specialistId);
        // DateOnly se pasa como 'date': Npgsql no lo convierte solo y
        // mandarlo como timestamp hace que Postgres no encuentre la firma.
        command.Parameters.Add(
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = date });
        command.Parameters.AddWithValue(durationMinutes);
        command.Parameters.AddWithValue(slotIntervalMinutes);

        var slots = new List<AvailabilitySlotDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            slots.Add(new AvailabilitySlotDto(
                StartsAt: reader.GetFieldValue<DateTimeOffset>(0),
                EndsAt: reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return slots;
    }

    /// <summary>Duración en minutos de un servicio publicado.</summary>
    public async Task<(int Duration, decimal Price, string Name)?> GetServiceTimingAsync(
        int serviceId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select duration_minutes, price, name
              from public.services
             where id = $1 and is_published
            """, connection);
        command.Parameters.AddWithValue(serviceId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return (reader.GetInt32(0), reader.GetDecimal(1), reader.GetString(2));
    }

    /// <summary>Duración estándar de sesión para un curso.</summary>
    public async Task<(int Duration, decimal Price, string Name)?> GetCourseTimingAsync(
        Guid courseId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        // Los cursos declaran su duración como texto ("8 horas divididas en
        // 2 sesiones"). Para calcular el slot se usa la sesión más larga,
        // que es la que ocupa la agenda; refinarlo por texto es fragile.
        await using var command = new NpgsqlCommand(
            """
            select 180, price, title
              from public.courses
             where id = $1 and is_published
            """, connection);
        command.Parameters.AddWithValue(courseId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return (reader.GetInt32(0), reader.GetDecimal(1), reader.GetString(2));
    }

    /// <summary>Minutos y precio que suman los complementos de un servicio.</summary>
    public async Task<(int ExtraMinutes, decimal Price)> GetAddOnsImpactAsync(
        int serviceId, int[] addOnIds, CancellationToken ct = default)
    {
        if (addOnIds.Length == 0) return (0, 0m);

        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select coalesce(sum(extra_minutes), 0), coalesce(sum(price), 0)
              from public.addons
             where service_id = $1 and id = any($2) and is_active
            """, connection);

        command.Parameters.AddWithValue(serviceId);
        command.Parameters.Add(new NpgsqlParameter { Value = addOnIds });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, 0m);

        return (reader.GetInt32(0), reader.GetDecimal(1));
    }

    // =================================================================
    // Crear reserva
    // =================================================================

    /// <summary>
    /// Crea una reserva llamando a la función create_booking.
    ///
    /// Devuelve null si el horario ya se tomó; la constraint
    /// bookings_no_overlap lo rechaza y se traduce aquí. No se comprueba
    /// antes de insertar a propósito: entre comprobar e insertar hay una
    /// ventana de carrera que la base cierra de forma atómica.
    /// </summary>
    public async Task<BookingDto?> TryCreateAsync(
        CreateBookingRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select * from public.create_booking(
                p_specialist_id => $1,
                p_starts_at     => $2,
                p_contact_name  => $3,
                p_contact_phone => $4,
                p_contact_email => $5,
                p_service_id    => $6,
                p_course_id     => $7,
                p_addon_ids     => $8,
                p_notes         => $9,
                p_modality      => $10,
                p_student_id    => $11
            )
            """, connection);

        command.Parameters.AddWithValue(request.SpecialistId);
        command.Parameters.AddWithValue(request.StartsAt);
        command.Parameters.AddWithValue(request.ContactName.Trim());
        command.Parameters.AddWithValue(request.ContactPhone.Trim());
        command.Parameters.AddWithValue(request.ContactEmail.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue((object?)request.ServiceId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.CourseId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.AddOnIds ?? Array.Empty<int>());
        command.Parameters.AddWithValue((object?)request.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue(request.Modality);
        command.Parameters.AddWithValue((object?)request.StudentId ?? DBNull.Value);

        try
        {
            var booking = await ReadBookingAsync(command, ct);
            if (booking is not null) return booking;

            // create_booking devuelve SETOF bookings; sin filas significa
            // que la validación delInside abortó.
            return null;
        }
        catch (PostgresException ex) when (ex.SqlState == "23P01")
        {
            // exclusion_violation: el horario se tomó mientras la clienta
            // lo elegía. Es el caso esperado, no un error del servidor.
            return null;
        }
    }

    private static async Task<BookingDto?> ReadBookingAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        // El índice de columnas viene del orden de retorno de bookings.
        // Se lee por nombre para que reordenar el SELECT no rompa esto.
        return new BookingDto(
            Id: reader.GetGuid(reader.GetOrdinal("id")),
            Code: reader.GetString(reader.GetOrdinal("code")),
            StartsAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("starts_at")),
            EndsAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("ends_at")),
            Modality: reader.GetString(reader.GetOrdinal("modality")),
            Status: reader.GetString(reader.GetOrdinal("status")),
            PriceTotal: reader.GetDecimal(reader.GetOrdinal("price_total")),
            DepositAmount: reader.GetDecimal(reader.GetOrdinal("deposit_amount")),
            BalanceAmount: 0m, // se calcula abajo
            ContactName: reader.GetString(reader.GetOrdinal("contact_name")),
            ContactPhone: reader.GetString(reader.GetOrdinal("contact_phone")),
            ContactEmail: reader.GetString(reader.GetOrdinal("contact_email")),
            Notes: reader.IsDBNull(reader.GetOrdinal("notes"))
                ? null
                : reader.GetString(reader.GetOrdinal("notes")),
            ServiceId: reader.IsDBNull(reader.GetOrdinal("service_id"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("service_id")),
            CourseId: reader.IsDBNull(reader.GetOrdinal("course_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("course_id")),
            AddOnIds: reader.GetFieldValue<int[]>(reader.GetOrdinal("addon_ids")),
            SubjectName: null, // se resuelve en el endpoint si se necesita
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")))
        with
        {
            // El saldo es lo que falta por pagar tras el anticipo.
            BalanceAmount = reader.GetDecimal(reader.GetOrdinal("price_total")) -
                            reader.GetDecimal(reader.GetOrdinal("deposit_amount")),
        };
    }

    /// <summary>
    /// Ids de los especialistas activos. Hoy solo Andy, pero el asistente
    /// de reserva lo consulta en vez de codificar el 1.
    /// </summary>
    public async Task<IReadOnlyList<int>> GetSpecialistIdsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select id from public.specialists where is_active order by id", connection);

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));

        return ids;
    }

    // =================================================================
    // Consultar reservas
    // =================================================================

    /// <summary>Reservas de una fecha, para la agenda del admin.</summary>
    public async Task<IReadOnlyList<AdminBookingDto>> GetBookingsForDateAsync(
        DateOnly date, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select b.id, b.code, b.contact_name, b.contact_phone, b.contact_email,
                   coalesce(s.name, c.title, 'Sesión') as subject,
                   b.starts_at, b.ends_at, b.modality, b.status,
                   b.price_total, b.deposit_amount,
                   case when b.deposit_paid_at is not null then b.deposit_amount else 0 end,
                   b.notes, b.rfc_receiver, b.created_at
              from public.bookings b
              left join public.services s on s.id = b.service_id
              left join public.courses c on c.id = b.course_id
             where b.starts_at::date = $1
             order by b.starts_at
            """, connection);

        command.Parameters.Add(
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = date });

        var results = new List<AdminBookingDto>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AdminBookingDto(
                Id: reader.GetGuid(0),
                Code: reader.GetString(1),
                ContactName: reader.GetString(2),
                ContactPhone: reader.GetString(3),
                ContactEmail: reader.GetString(4),
                SubjectName: reader.GetString(5),
                StartsAt: reader.GetFieldValue<DateTimeOffset>(6),
                EndsAt: reader.GetFieldValue<DateTimeOffset>(7),
                Modality: reader.GetString(8),
                Status: reader.GetString(9),
                PriceTotal: reader.GetDecimal(10),
                DepositAmount: reader.GetDecimal(11),
                DepositPaid: reader.GetDecimal(12),
                Notes: reader.IsDBNull(13) ? null : reader.GetString(13),
                RfcReceiver: reader.IsDBNull(14) ? null : reader.GetString(14),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(15)));
        }

        return results;
    }

    /// <summary>Cambia el estado de una reserva.</summary>
    public async Task<BookingDetailDto?> UpdateStatusAsync(
        Guid bookingId, string status, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            with updated as (
              update public.bookings
                 set status = $2::booking_status
               where id = $1
              returning *
            )
            select u.id, u.code, u.starts_at, u.ends_at, u.modality, u.status,
                   u.price_total, u.deposit_amount, u.contact_name,
                   u.contact_phone, u.contact_email, u.notes,
                   u.service_id, u.course_id, u.addon_ids, u.created_at,
                   sp.id as specialist_id, sp.name as specialist_name,
                   case when u.deposit_paid_at is not null then u.deposit_amount else 0 end
              from updated u
              join public.specialists sp on sp.id = u.specialist_id
            """, connection);

        command.Parameters.AddWithValue(bookingId);
        command.Parameters.AddWithValue(status);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var booking = new BookingDto(
            Id: reader.GetGuid(0),
            Code: reader.GetString(1),
            StartsAt: reader.GetFieldValue<DateTimeOffset>(2),
            EndsAt: reader.GetFieldValue<DateTimeOffset>(3),
            Modality: reader.GetString(4),
            Status: reader.GetString(5),
            PriceTotal: reader.GetDecimal(6),
            DepositAmount: reader.GetDecimal(7),
            BalanceAmount: reader.GetDecimal(6) - reader.GetDecimal(7),
            ContactName: reader.GetString(8),
            ContactPhone: reader.GetString(9),
            ContactEmail: reader.GetString(10),
            Notes: reader.IsDBNull(11) ? null : reader.GetString(11),
            ServiceId: reader.IsDBNull(12) ? null : reader.GetInt32(12),
            CourseId: reader.IsDBNull(13) ? null : reader.GetGuid(13),
            AddOnIds: reader.GetFieldValue<int[]>(14),
            SubjectName: null,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(15));

        return new BookingDetailDto(
            Booking: booking,
            SpecialistId: reader.GetInt32(16),
            SpecialistName: reader.GetString(17),
            DepositPaid: reader.GetDecimal(18),
            DepositConfirmed: reader.GetDecimal(18) > 0);
    }
}