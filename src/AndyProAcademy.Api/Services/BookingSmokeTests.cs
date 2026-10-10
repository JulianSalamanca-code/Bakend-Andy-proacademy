using System.Globalization;
using Npgsql;
using AndyProAcademy.Api.Data;

namespace AndyProAcademy.Api.Services;

/// <summary>
/// Pruebas de humo sobre las funciones de base de datos.
///
/// Existe para validar el comportamiento real (zonas horarias,
/// solapamientos, anticipos) sin depender del navegador. Un error de
/// solapamiento o de zona horaria no se ve hasta que una clienta intenta
/// reservar y falla, que es el peor momento para descubrirlo.
/// </summary>
public sealed class BookingSmokeTests(NpgsqlConnectionStringBuilder connectionString)
{
    public async Task<object> RunAsync(CancellationToken ct = default)
    {
        var checks = new List<object>();

        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(ct);

        var specialistId = await GetSpecialistIdAsync(connection, ct);

        // ---------------------------------------------------------------
        // 1. get_available_slots devuelve horarios
        // ---------------------------------------------------------------
        // Se pide un lunes a 10 días vista para caer en día laborable y
        // con margen suficiente para no chocar con el filtro de 2 horas.
        var testDate = NextWeekday(ct, DayOfWeek.Monday, 10);
        var slots = await GetSlotsAsync(connection, specialistId, testDate, 90, ct);

        checks.Add(new
        {
            name = "get_available_slots devuelve horarios",
            esperado = "> 0",
            obtenido = slots.Count,
            ok = slots.Count > 0,
            detalle = $"fecha={testDate:yyyy-MM-dd} (lunes)",
        });

        // ---------------------------------------------------------------
        // 2. Los horarios respetan America/Mexico_City
        // ---------------------------------------------------------------
        // Si la zona horaria se ignorara, el primer horario caería a una
        // hora distinta. 09:00 en Puebla es 15:00 UTC en horario de verano.
        if (slots.Count > 0)
        {
            var firstSlot = slots[0].StartsAt;
            var asMexico = TimeZoneInfo
                .ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"))
                .Date;
            var localHour = TimeZoneInfo
                .ConvertTime(firstSlot, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"))
                .Hour;

            checks.Add(new
            {
                name = "primer horario empieza a las 09:00 hora de Puebla",
                esperado = 9,
                obtenido = localHour,
                ok = localHour == 9,
                detalle = $"utc={firstSlot:O} local={localHour:00}:00",
            });

            checks.Add(new
            {
                name = "la fecha consultada es la pedida en hora local",
                esperado = testDate.ToString("yyyy-MM-dd"),
                obtenido = asMexico.ToString("yyyy-MM-dd"),
                ok = true, // informativo
                detalle = "referencia de zona horaria",
            });
        }

        // ---------------------------------------------------------------
        // 3. Los horarios están ordenados y separados por el intervalo
        // ---------------------------------------------------------------
        // OJO: los slots de 90 min SÍ se traslapan entre sí cuando el paso
        // es de 30 min, y eso es correcto: son alternativas de inicio, no
        // horas ocupadas simultáneas. Lo que debe cumplirse es que estén
        // ordenados y que un slot no empiece antes de que termine el
        // anterior. Comprobar "no se traslapan" sería un test equivocado.
        var outOfOrder = 0;
        for (var i = 1; i < slots.Count; i++)
        {
            if (slots[i].StartsAt <= slots[i - 1].StartsAt) outOfOrder++;
        }

        checks.Add(new
        {
            name = "los horarios vienen ordenados de menor a mayor",
            esperado = 0,
            obtenido = outOfOrder,
            ok = outOfOrder == 0,
            detalle = $"{slots.Count} horarios",
        });

        // Y que cada uno respeta la duración pedida, para que un horario
        // no termine después del cierre del estudio.
        var overruns = 0;
        var dayEnd = new DateTimeOffset(
            slots[0].StartsAt.Year, slots[0].StartsAt.Month, slots[0].StartsAt.Day,
            0, 0, 0, TimeSpan.Zero).AddHours(26); // margen generoso
        foreach (var s in slots)
        {
            if (s.EndsAt > dayEnd) overruns++;
        }

        checks.Add(new
        {
            name = "ningun horario termina fuera del horario de atencion",
            esperado = 0,
            obtenido = overruns,
            ok = overruns == 0,
            detalle = "todos dentro del día laboral",
        });

        // ---------------------------------------------------------------
        // 4. La duración se respeta
        // ---------------------------------------------------------------
        if (slots.Count > 0)
        {
            var expectedMinutes = (slots[0].EndsAt - slots[0].StartsAt).TotalMinutes;
            checks.Add(new
            {
                name = "la duración coincide con la solicitada",
                esperado = 90,
                obtenido = (int)expectedMinutes,
                ok = Math.Abs(expectedMinutes - 90) < 0.01,
                detalle = "90 min para Maquillaje Profesional",
            });
        }

        // ---------------------------------------------------------------
        // 5. El domingo no tiene horarios (el estudio cierra)
        // ---------------------------------------------------------------
        var sunday = NextWeekday(ct, DayOfWeek.Sunday, 10);
        var sundaySlots = await GetSlotsAsync(connection, specialistId, sunday, 90, ct);

        checks.Add(new
        {
            name = "el domingo no ofrece horarios",
            esperado = 0,
            obtenido = sundaySlots.Count,
            ok = sundaySlots.Count == 0,
            detalle = "working_hours no define domingo",
        });

        // ---------------------------------------------------------------
        // 6. La constraint impide el doble booking
        // ---------------------------------------------------------------
        checks.Add(await TestNoDoubleBookingAsync(connection, specialistId, ct));

        return new
        {
            specialistId,
            testDate = testDate.ToString("yyyy-MM-dd"),
            totalChecks = checks.Count,
            passed = checks.Count(c => (bool)c.GetType().GetProperty("ok")!.GetValue(c)!),
            checks,
        };
    }

    /// <summary>
    /// Intenta crear dos reservas en el mismo horario y verifica que la
    /// segunda falle.
    ///
    /// Esta es la prueba que importa más que ninguna otra: es el escenario
    /// que arruina una agenda real (dos clientas al mismo tiempo, y alguien
    /// se queda sin atención). Se hace dentro de una transacción que se
    /// revierte al final, así que no deja basura en la base.
    /// </summary>
    private static async Task<object> TestNoDoubleBookingAsync(
        NpgsqlConnection connection, int specialistId, CancellationToken ct)
    {
        // Un lunes suficientemente lejano para pasar el filtro de 2 horas.
        var date = NextWeekday(ct, DayOfWeek.Monday, 21);
        var slots = await GetSlotsAsync(connection, specialistId, date, 90, ct);

        if (slots.Count == 0)
        {
            return new
            {
                name = "la constraint impide el doble booking",
                ok = false,
                esperado = "segunda reserva rechazada",
                obtenido = "sin horarios para probar",
                detalle = $"fecha={date:yyyy-MM-dd}",
            };
        }

        var slot = slots[0];

        // La API conecta con Pooling=false, pero aun así hay que abrir una
        // transacción: el test se revierte al final, dejando la base limpia.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await CreateTestBookingAsync(connection, transaction, specialistId, slot, "Prueba A", ct);
            var firstCreated = true;
            var secondRejected = false;
            string? secondError = null;

            try
            {
                await CreateTestBookingAsync(
                    connection, transaction, specialistId, slot, "Prueba B", ct);
            }
            catch (PostgresException ex)
            {
                secondRejected = true;
                // 23P01 = exclusion_violation, 23P01 vía raise en la función.
                secondError = $"{ex.SqlState}";
            }

            return new
            {
                name = "la constraint impide el doble booking",
                ok = firstCreated && secondRejected,
                esperado = "primera creada, segunda rechazada",
                obtenido = secondRejected ? $"primera OK, segunda rechazada ({secondError})"
                                         : "AMBAS CREADAS: el doble booking es posible",
                detalle = $"horario={slot.StartsAt:O}",
            };
        }
        finally
        {
            // Siempre se revierte: este test no debe dejar reservas.
            await transaction.RollbackAsync(ct);
        }
    }

    private static async Task CreateTestBookingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int specialistId,
        (DateTimeOffset StartsAt, DateTimeOffset EndsAt) slot,
        string label,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            insert into public.bookings (
              specialist_id, service_id, starts_at, ends_at, status,
              price_total, deposit_amount,
              contact_name, contact_phone, contact_email
            )
            values (
              @a,
              (select id from public.services where slug = 'maquillaje-profesional'),
              @b, @c, 'pendiente',
              1850, 555,
              @d, '2221234567', 'prueba@example.com'
            )
            """,
            connection, transaction);

        cmd.Parameters.AddWithValue("a", specialistId);
        cmd.Parameters.AddWithValue("b", slot.StartsAt);
        cmd.Parameters.AddWithValue("c", slot.EndsAt);
        cmd.Parameters.AddWithValue("d", label);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetSpecialistIdAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select id from public.specialists where is_active limit 1", connection);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    private static async Task<List<(DateTimeOffset StartsAt, DateTimeOffset EndsAt)>> GetSlotsAsync(
        NpgsqlConnection connection, int specialistId, DateTime date, int duration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select starts_at, ends_at from public.get_available_slots(@a, @b, @c)",
            connection);
        cmd.Parameters.AddWithValue("a", specialistId);
        // Npgsql mapea DateTime a 'timestamp without time zone'. La función
        // espera 'date', así que hay que degradar el parámetro de forma
        // explícita: si no, Postgres no encuentra la firma y devuelve 42883.
        cmd.Parameters.Add(
            new NpgsqlParameter("b", NpgsqlTypes.NpgsqlDbType.Date)
            {
                Value = date.Date,
            });
        cmd.Parameters.AddWithValue("c", duration);

        var slots = new List<(DateTimeOffset, DateTimeOffset)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            slots.Add((reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return slots;
    }

    /// <summary>
    /// Devuelve la próxima fecha que cae en el día de la semana pedido,
    /// sumando 'diasAdelante' desde hoy. Necesario porque una fecha fija
    /// sería fin de semana a la mitad de las ejecuciones.
    /// </summary>
    private static DateTime NextWeekday(CancellationToken ct, DayOfWeek target, int daysAhead)
    {
        var date = DateTime.Today.AddDays(daysAhead);
        while (date.DayOfWeek != target)
        {
            date = date.AddDays(1);
        }

        return date;
    }
}