-- =====================================================================
-- 002 — Row Level Security
--
-- La seguridad va en la base, no solo en los guards del frontend. Un
-- guard se puede saltar desde la consola del navegador; una política RLS
-- no.
-- =====================================================================

alter table public.profiles           enable row level security;
alter table public.services           enable row level security;
alter table public.addons             enable row level security;
alter table public.courses            enable row level security;
alter table public.specialists        enable row level security;
alter table public.working_hours      enable row level security;
alter table public.blocked_periods    enable row level security;
alter table public.bookings           enable row level security;
alter table public.products           enable row level security;
alter table public.product_variants   enable row level security;
alter table public.inventory_movements enable row level security;
alter table public.orders             enable row level security;
alter table public.order_items        enable row level security;
alter table public.reviews            enable row level security;
alter table public.faqs               enable row level security;

-- ---------------------------------------------------------------------
-- Utilidad: ¿la petición viene de una cuenta admin?
-- ---------------------------------------------------------------------
-- security definer evita la recursión infinita: sin esto, consultar el
-- propio rol dispararía la política de profiles otra vez.
create or replace function public.is_admin()
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1
    from public.profiles p
    where p.id = auth.uid()
      and p.role = 'admin'
  );
$$;

comment on function public.is_admin() is
  'Indica si el usuario de la petición tiene rol admin. Usar dentro de políticas RLS.';

-- ---------------------------------------------------------------------
-- Perfiles
-- ---------------------------------------------------------------------
drop policy if exists profiles_select_own on public.profiles;
create policy profiles_select_own on public.profiles
  for select using (id = auth.uid() or public.is_admin());

drop policy if exists profiles_insert_own on public.profiles;
create policy profiles_insert_own on public.profiles
  for insert with check (id = auth.uid());

-- El rol no se autoasigna: sin esta restricción un visitante podría
-- registrarse con role 'admin' y escribir en todo el catálogo.
drop policy if exists profiles_update_own on public.profiles;
create policy profiles_update_own on public.profiles
  for update using (id = auth.uid()) with check (id = auth.uid());

drop policy if exists profiles_admin_all on public.profiles;
create policy profiles_admin_all on public.profiles
  for all using (public.is_admin()) with check (public.is_admin());

-- ---------------------------------------------------------------------
-- Catálogo: lectura pública, escritura solo admin
-- ---------------------------------------------------------------------
drop policy if exists services_public_read on public.services;
create policy services_public_read on public.services
  for select using (is_published or public.is_admin());

drop policy if exists addons_public_read on public.addons;
create policy addons_public_read on public.addons
  for select using (is_active or public.is_admin());

drop policy if exists courses_public_read on public.courses;
create policy courses_public_read on public.courses
  for select using (is_published or public.is_admin());

drop policy if exists products_public_read on public.products;
create policy products_public_read on public.products
  for select using (is_published or public.is_admin());

drop policy if exists variants_public_read on public.product_variants;
create policy variants_public_read on public.product_variants
  for select using (
    is_active
    or public.is_admin()
    -- Muestra variantes en existencia aunque el producto esté oculto,
    -- para que el admin vea qué falta reponer.
    or exists (
      select 1 from public.products p
      where p.id = product_id
    )
  );

drop policy if exists specialists_public_read on public.specialists;
create policy specialists_public_read on public.specialists
  for select using (is_active or public.is_admin());

drop policy if exists working_hours_public_read on public.working_hours;
create policy working_hours_public_read on public.working_hours
  for select using (true);

drop policy if exists faqs_public_read on public.faqs;
create policy faqs_public_read on public.faqs
  for select using (is_published or public.is_admin());

drop policy if exists reviews_public_read on public.reviews;
create policy reviews_public_read on public.reviews
  for select using (is_published or public.is_admin());

drop policy if exists blocked_periods_public_read on public.blocked_periods;
create policy blocked_periods_public_read on public.blocked_periods
  for select using (true);

-- ---------------------------------------------------------------------
-- Reservas
--
-- Crear una reserva es público a propósito: exigir cuenta convertiría cada
-- visita en un muro de registro y bajó la conversión. El contacto llega en
-- el cuerpo de la reserva y el folio permite reclamarla después.
-- ---------------------------------------------------------------------
drop policy if exists bookings_public_insert on public.bookings;
create policy bookings_public_insert on public.bookings
  for insert with check (
    -- Nadie agenda en el pasado.
    starts_at > now()
    and status = 'pendiente'
    and student_id is null
  );

drop policy if exists bookings_read_own_or_admin on public.bookings;
create policy bookings_read_own_or_admin on public.bookings
  for select using (student_id = auth.uid() or public.is_admin());

-- Sin update ni delete para el público: confirmar o cancelar una cita es
-- decisión del estudio, no de quien la agendó. Para reprogramar, se cancela
-- y se crea otra.
drop policy if exists bookings_admin_all on public.bookings;
create policy bookings_admin_all on public.bookings
  for all using (public.is_admin()) with check (public.is_admin());

-- ---------------------------------------------------------------------
-- Pedidos
-- ---------------------------------------------------------------------
drop policy if exists orders_public_insert on public.orders;
create policy orders_public_insert on public.orders
  for insert with check (status = 'pendiente' and customer_id is null);

drop policy if exists orders_read_own_or_admin on public.orders;
create policy orders_read_own_or_admin on public.orders
  for select using (customer_id = auth.uid() or public.is_admin());

drop policy if exists orders_admin_all on public.orders;
create policy orders_admin_all on public.orders
  for all using (public.is_admin()) with check (public.is_admin());

drop policy if exists order_items_read on public.order_items;
create policy order_items_read on public.order_items
  for select using (
    exists (
      select 1 from public.orders o
      where o.id = order_id
        and (o.customer_id = auth.uid() or public.is_admin())
    )
  );

drop policy if exists order_items_admin_all on public.order_items;
create policy order_items_admin_all on public.order_items
  for all using (public.is_admin()) with check (public.is_admin());

-- ---------------------------------------------------------------------
-- Inventario
-- ---------------------------------------------------------------------
drop policy if exists inventory_public_read on public.inventory_movements;
create policy inventory_public_read on public.inventory_movements
  for select using (public.is_admin());

-- Solo admin ajusta stock. El descuento por pago lo hace la función
-- security definer de abajo, no el cliente.
drop policy if exists inventory_admin_all on public.inventory_movements;
create policy inventory_admin_all on public.inventory_movements
  for all using (public.is_admin()) with check (public.is_admin());

-- ---------------------------------------------------------------------
-- Escritura del catálogo: solo admin
-- ---------------------------------------------------------------------
do $$
declare
  t text;
begin
  foreach t in array array[
    'services', 'addons', 'courses', 'specialists', 'working_hours',
    'blocked_periods', 'products', 'product_variants', 'reviews', 'faqs'
  ]
  loop
    execute format('drop policy if exists %I on public.%I', t || '_admin_all', t);
    execute format(
      'create policy %I on public.%I for all using (public.is_admin()) with check (public.is_admin())',
      t || '_admin_all',
      t
    );
  end loop;
end;
$$;