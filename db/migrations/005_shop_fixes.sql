-- =====================================================================
-- 005 — Correcciones sobre el esquema ya aplicado
--
-- Migraciones incrementales para bases donde 001..004 ya se aplicaron.
-- No las borres aunque 001 contenga el cambio: una base en producción no
-- vuelve a correr la migración inicial.
-- =====================================================================

-- La columna faltaba en orders: el endpoint de creación de pedido la
-- escribe y Postgres rechazaba el INSERT con 'column notes does not exist'.
alter table public.orders
  add column if not exists notes text;

comment on column public.orders.notes is
  'Instrucciones de entrega o notas de la clienta.';

-- Verificación: las columnas que el código de la tienda necesita.
do $$
declare
  required text[] := array['notes', 'tax_amount', 'rfc_receiver', 'fulfillment'];
  missing text;
begin
  foreach missing in array required
  loop
    if not exists (
      select 1 from information_schema.columns
       where table_schema = 'public'
         and table_name = 'orders'
         and column_name = missing
    ) then
      raise exception 'Falta la columna orders.%', missing;
    end if;
  end loop;
end;
$$;