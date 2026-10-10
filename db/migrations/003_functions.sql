-- =====================================================================
-- 003 — Cálculo de disponibilidad
--
-- get_available_slots cruza horario, reservas y bloqueos, y devuelve los
-- huecos reales. Hacerlo en SQL evita el patrón de "traer todos los
-- horarios y filtrar en el navegador", que es donde aparecen los errores
-- de zona horaria y las fechas que cruzan medianoche.
-- =====================================================================

create or replace function public.get_available_slots(
  p_specialist_id integer,
  p_date date,
  p_duration_minutes integer,
  p_slot_interval_minutes integer default 30
)
returns table (starts_at timestamptz, ends_at timestamptz)
language plpgsql
stable
security definer
set search_path = public
as $$
declare
  v_workday   record;
  v_slot      record;
  v_day_start timestamptz;
  v_day_end   timestamptz;
  v_busy      int;
begin
  -- Duración inválida: devolver vacío es mejor que un error en pantalla.
  if p_duration_minutes is null or p_duration_minutes <= 0 then
    return;
  end if;

  -- -------------------------------------------------------------------
  -- El horario del día de la semana de la fecha solicitada
  -- -------------------------------------------------------------------
  select * into v_workday
  from public.working_hours wh
  where wh.specialist_id = p_specialist_id
    and wh.weekday = extract(dow from p_date)::smallint
    and wh.is_active;

  -- El estudio no atiende ese día (domingo, o el día está cerrado).
  if not found then
    return;
  end if;

  -- -------------------------------------------------------------------
  -- El día solicitado en hora de Puebla (America/Mexico_City)
  --
  -- El timestamptz se construye con at time zone para que el resultado sea
  -- el mismo sin importar dónde corra la API. Si el servidor está en UTC
  -- y se usara timestamptz sin zona, '10:00' significaría una hora
  -- distinta y los horarios se corrían.
  -- -------------------------------------------------------------------
  v_day_start := (p_date::text || ' ' || v_workday.start_time::text)::timestamp
                   at time zone 'America/Mexico_City';
  v_day_end   := (p_date::text || ' ' || v_workday.end_time::text)::timestamp
                   at time zone 'America/Mexico_City';

  -- -------------------------------------------------------------------
  -- Recorrer el día en intervalos y quedarse con los huecos libres
  -- -------------------------------------------------------------------
  for v_slot in
    select gs as slot_start,
           gs + make_interval(mins => p_duration_minutes) as slot_end
    from generate_series(
           v_day_start,
           v_day_end - make_interval(mins => p_duration_minutes),
           make_interval(mins => p_slot_interval_minutes)
         ) as gs
  loop
    -- El slot debe caber completo dentro del horario de atención.
    continue when v_slot.slot_end > v_day_end;

    -- Sin slots en el pasado. El margen de 2 horas evita que una
    -- clienta reserve para dentro de poco y no llegue a tiempo.
    continue when v_slot.slot_start < now() + interval '2 hours';

    -- ¿Choca con una reserva activa?
    select count(*) into v_busy
    from public.bookings b
    where b.specialist_id = p_specialist_id
      and b.status in ('pendiente', 'confirmada', 'completada')
      and tstzrange(b.starts_at, b.ends_at) && tstzrange(v_slot.slot_start, v_slot.slot_end);

    continue when v_busy > 0;

    -- ¿Choca con un periodo bloqueado (vacaciones, feriado)?
    select count(*) into v_busy
    from public.blocked_periods bp
    where (bp.specialist_id is null or bp.specialist_id = p_specialist_id)
      and tstzrange(bp.starts_at, bp.ends_at) && tstzrange(v_slot.slot_start, v_slot.slot_end);

    continue when v_busy > 0;

    starts_at := v_slot.slot_start;
    ends_at   := v_slot.slot_end;
    return next;
  end loop;
end;
$$;

comment on function public.get_available_slots(integer, date, integer, integer) is
  'Horarios libres de un especialista en una fecha, restando reservas y bloqueos.';

-- ---------------------------------------------------------------------
-- Confirmar la reserva de forma atómica
--
-- La constraint EXCLUDE ya impide el solapamiento; esta función lo
-- comprueba antes y devuelve un mensaje útil en lugar de un error 23P01
-- crudo, que el frontend no puede mostrar tal cual.
-- ---------------------------------------------------------------------
-- Los parámetros obligatorios van primero y los opcionales después, todos
-- con default. Postgres exige ese orden: si un parámetro tiene default, todos
-- los siguientes deben tenerlo también (error 42P13).
create or replace function public.create_booking(
  -- Obligatorios
  p_specialist_id integer,
  p_starts_at     timestamptz,
  p_contact_name  text,
  p_contact_phone text,
  p_contact_email text,
  -- Opcionales
  p_service_id     integer   default null,
  p_course_id      uuid      default null,
  p_addon_ids      integer[] default '{}',
  p_notes          text      default null,
  p_modality       text      default 'studio',
  p_student_id     uuid      default null
)
returns public.bookings
language plpgsql
security definer
set search_path = public
as $$
declare
  v_duration integer;
  v_duration_extra integer := 0;
  v_base_price numeric(10, 2);
  v_addons_price numeric(10, 2) := 0;
  v_total numeric(10, 2);
  v_ends_at timestamptz;
  v_booking public.bookings;
begin
  -- -------------------------------------------------------------------
  -- El servicio y el curso son excluyentes
  -- -------------------------------------------------------------------
  if (p_service_id is null) = (p_course_id is null) then
    raise exception 'Se requiere exactamente un servicio o un curso.'
      using errcode = '22023';
  end if;

  -- -------------------------------------------------------------------
  -- Precio y duración según el sujeto
  -- -------------------------------------------------------------------
  if p_service_id is not null then
    select duration_minutes, price
      into v_duration, v_base_price
    from public.services
    where id = p_service_id and is_published;

    if not found then
      raise exception 'El servicio no existe o no está publicado.'
        using errcode = '22023';
    end if;

    -- Los complementos suman precio y también consumen tiempo de cabina:
    -- agregar un laminado de ceja es +15 min que Andy tiene que reservar.
    select coalesce(sum(a.price), 0),
           coalesce(sum(a.extra_minutes), 0)
      into v_addons_price, v_duration_extra
    from public.addons a
    where a.id = any(p_addon_ids)
      and a.service_id = p_service_id
      and a.is_active;

  else
    -- El curso indica su duración en etiqueta de texto; se usa el
    -- estándar de sesión múltiple (3 h) para el cálculo de disponibilidad.
    v_duration := 180;

    select price into v_base_price
    from public.courses
    where id = p_course_id and is_published;

    if not found then
      raise exception 'El curso no existe o no está publicado.'
        using errcode = '22023';
    end if;
  end if;

  v_duration := v_duration + v_duration_extra;
  v_total := v_base_price + v_addons_price;
  v_ends_at := p_starts_at + make_interval(mins => v_duration);

  -- -------------------------------------------------------------------
  -- Validar que el horario sigue libre en este instante.
  -- Aunque la constraint lo garantiza, fallar aquí permite un mensaje
  -- claro en lugar de una violación de exclusión.
  -- -------------------------------------------------------------------
  if exists (
    select 1
    from public.bookings b
    where b.specialist_id = p_specialist_id
      and b.status in ('pendiente', 'confirmada', 'completada')
      and tstzrange(b.starts_at, b.ends_at) && tstzrange(p_starts_at, v_ends_at)
  ) then
    raise exception 'Ese horario acaba de ser tomado. Elige otro.'
      using errcode = '23P01';
  end if;

  if exists (
    select 1
    from public.blocked_periods bp
    where (bp.specialist_id is null or bp.specialist_id = p_specialist_id)
      and tstzrange(bp.starts_at, bp.ends_at) && tstzrange(p_starts_at, v_ends_at)
  ) then
    raise exception 'La fecha no está disponible por bloqueo de agenda.'
      using errcode = '23P01';
  end if;

  -- -------------------------------------------------------------------
  -- Insertar
  -- -------------------------------------------------------------------
  insert into public.bookings (
    student_id, specialist_id, service_id, course_id, addon_ids,
    starts_at, ends_at, modality, status,
    price_total, deposit_amount,
    contact_name, contact_phone, contact_email, notes
  )
  values (
    p_student_id, p_specialist_id, p_service_id, p_course_id, p_addon_ids,
    p_starts_at, v_ends_at,
    case when p_modality = 'domicilio' then 'domicilio'::booking_modality
         else 'studio'::booking_modality end,
    'pendiente',
    v_total,
    round(v_total * 0.30, 2),
    p_contact_name, p_contact_phone, p_contact_email, p_notes
  )
  returning * into v_booking;

  return v_booking;
end;
$$;

comment on function public.create_booking is
  'Crea una reserva validando disponibilidad y calculando el anticipo del 30%.';

-- ---------------------------------------------------------------------
-- Ajuste de inventario al confirmar el pago
--
-- Se descuenta stock únicamente cuando el pago se confirma, no al crear el
-- pedido: si la clienta abandona el checkout, el producto vuelve a estar
-- disponible sin intervention manual.
-- ---------------------------------------------------------------------
create or replace function public.confirm_order_payment(
  p_order_id uuid,
  p_payment_reference text
)
returns public.orders
language plpgsql
security definer
set search_path = public
as $$
declare
  v_order public.orders;
  v_item  record;
  v_new_stock integer;
begin
  select * into v_order
  from public.orders
  where id = p_order_id
  for update;

  if not found then
    raise exception 'El pedido no existe.' using errcode = 'P0002';
  end if;

  if v_order.status <> 'pendiente' then
    raise exception 'El pedido ya fue procesado como %', v_order.status
      using errcode = '22023';
  end if;

  for v_item in
    select variant_id, quantity from public.order_items where order_id = p_order_id
  loop
    -- Bloquear la variante para que dos pagos simultáneos no sobrevendan.
    update public.product_variants
       set stock_on_hand = stock_on_hand - v_item.quantity
     where id = v_item.variant_id
    returning stock_on_hand into v_new_stock;

    if v_new_stock < 0 then
      raise exception 'Stock insuficiente en la variante %', v_item.variant_id
        using errcode = '22023';
    end if;

    insert into public.inventory_movements (variant_id, delta, reason, reference)
    values (v_item.variant_id, -v_item.quantity, 'Venta confirmada', p_payment_reference);
  end loop;

  update public.orders
     set status = 'pagado',
         payment_reference = p_payment_reference,
         paid_at = now()
   where id = p_order_id
  returning * into v_order;

  return v_order;
end;
$$;

comment on function public.confirm_order_payment is
  'Marca el pedido como pagado y descuenta stock en una sola transacción.';