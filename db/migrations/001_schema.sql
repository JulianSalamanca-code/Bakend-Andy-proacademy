-- =====================================================================
-- 001 — Esquema base: catálogo, agenda y tienda
-- Andy Pro Academy · Supabase Postgres
-- =====================================================================

-- btree_gist habilita el operator class de '=' para text, necesario para
-- combinar el EXCLUDE de rangos con la columna specialist más abajo.
create extension if not exists btree_gist;

-- ---------------------------------------------------------------------
-- Perfiles
-- ---------------------------------------------------------------------
-- El rol vive aquí y no en auth.users para poder cambiarlo desde el panel
-- sin tocar el proveedor de identidad. Supabase Auth expone su id como id.
create table if not exists public.profiles (
  id uuid primary key references auth.users (id) on delete cascade,
  full_name text,
  phone text,
  -- rfc se pide al momento del cobro; no es obligatorio crear la cuenta.
  rfc text,
  role text not null default 'student' check (role in ('student', 'admin')),
  created_at timestamptz not null default now()
);

comment on table public.profiles is
  'Usuarios de la plataforma. El rol lo usa RLS para proteger escritura.';

-- ---------------------------------------------------------------------
-- Catálogo de servicios
-- ---------------------------------------------------------------------
create table if not exists public.services (
  id serial primary key generated always as identity,
  slug text not null unique,
  name text not null,
  tagline text not null,
  description text not null default '',
  badge text,
  duration_minutes integer not null check (duration_minutes > 0),
  price numeric(10, 2) not null check (price >= 0),
  -- until en null significa que el dato necesita confirmación antes de usarse
  -- en un CFDI. El prototipo no factura, pero el campo queda desde ahora.
  rfc_required boolean not null default false,
  includes text[] not null default '{}',
  brands text[] not null default '{}',
  preparation text,
  image_url text,
  is_published boolean not null default true,
  sort_order integer not null default 0,
  created_at timestamptz not null default now()
);

create index if not exists services_published_idx
  on public.services (is_published, sort_order);

-- ---------------------------------------------------------------------
-- Complementos
-- ---------------------------------------------------------------------
-- supports_addons replica qué servicios admiten complementos, para no
-- duplicar la regla en la base y en el frontend.
create table if not exists public.addons (
  id serial primary key generated always as identity,
  service_id integer not null references public.services (id) on delete cascade,
  name text not null,
  description text not null default '',
  price numeric(10, 2) not null check (price >= 0),
  extra_minutes integer not null default 0 check (extra_minutes >= 0),
  is_active boolean not null default true
);

create index if not exists addons_service_idx on public.addons (service_id);

-- ---------------------------------------------------------------------
-- Cursos
-- ---------------------------------------------------------------------
create table if not exists public.courses (
  id uuid primary key default gen_random_uuid(),
  slug text not null unique,
  title text not null,
  tagline text not null default '',
  description text not null default '',
  tag text not null,
  level text not null,
  duration_label text not null,
  session_count integer not null default 1 check (session_count > 0),
  price numeric(10, 2) not null check (price >= 0),
  -- El anticipo se calcula como 30% del total; se materializa para que el
  -- importe cobrado sea siempre el que se confirmó, aunque cambie el precio.
  deposit_amount numeric(10, 2) not null check (deposit_amount >= 0),
  topics text[] not null default '{}',
  includes text[] not null default '{}',
  image_url text,
  is_published boolean not null default true,
  created_at timestamptz not null default now(),
  constraint courses_deposit_within_price check (deposit_amount <= price)
);

create index if not exists courses_published_idx on public.courses (is_published);

-- ---------------------------------------------------------------------
-- Especialistas
-- ---------------------------------------------------------------------
-- Un solo registro en el prototipo (Andy), pero la tabla existe desde ahora
-- para que agregar más gente no requiera migración.
create table if not exists public.specialists (
  id serial primary key generated always as identity,
  name text not null,
  title text not null,
  bio text,
  image_url text,
  is_active boolean not null default true
);

-- ---------------------------------------------------------------------
-- Horario de atención
-- ---------------------------------------------------------------------
-- weekday: 0 domingo … 6 sábado. El estudio abre lunes a sábado.
create table if not exists public.working_hours (
  id serial primary key generated always as identity,
  specialist_id integer not null references public.specialists (id) on delete cascade,
  weekday smallint not null check (weekday between 0 and 6),
  start_time time not null,
  end_time time not null,
  is_active boolean not null default true,
  constraint working_hours_valid_range check (start_time < end_time),
  -- Un solo tramo por especialista y día: evita generar huecos imposibles.
  constraint working_hours_unique unique (specialist_id, weekday)
);

-- ---------------------------------------------------------------------
-- Fechas bloqueadas
-- ---------------------------------------------------------------------
-- Vacaciones, feriados o cierres. Se guarda el motivo por auditoría.
create table if not exists public.blocked_periods (
  id serial primary key generated always as identity,
  specialist_id integer references public.specialists (id) on delete cascade,
  starts_at timestamptz not null,
  ends_at timestamptz not null,
  reason text,
  constraint blocked_periods_valid_range check (starts_at < ends_at)
);

create index if not exists blocked_periods_range_idx
  on public.blocked_periods using gist (tstzrange(starts_at, ends_at));

-- ---------------------------------------------------------------------
-- Reservas
-- ---------------------------------------------------------------------
-- status sigue el ciclo: pendiente → confirmada → completada.
-- El prototipo deja 'pendiente' y Andy la confirma a mano.
create type booking_status as enum (
  'pendiente',
  'confirmada',
  'completada',
  'cancelada'
);

create type booking_modality as enum ('studio', 'domicilio');

create table if not exists public.bookings (
  id uuid primary key default gen_random_uuid(),
  -- Folio corto que ve la clienta, por ejemplo ANDY-A1B2C3.
  code text not null unique default (
    'ANDY-' || upper(substr(replace(gen_random_uuid()::text, '-', ''), 1, 6))
  ),
  student_id uuid references public.profiles (id) on delete set null,
  specialist_id integer not null references public.specialists (id) on delete restrict,

  -- Exactamente uno: una cita es por servicio o por curso, nunca ambos.
  service_id integer references public.services (id) on delete restrict,
  course_id uuid references public.courses (id) on delete restrict,

  addon_ids integer[] not null default '{}',

  starts_at timestamptz not null,
  ends_at timestamptz not null,
  modality booking_modality not null default 'studio',

  status booking_status not null default 'pendiente',

  -- Importe congelado en el momento de la reserva. Si mañana suben los
  -- precios, esta cita conserva lo que se pactó con la clienta.
  price_total numeric(10, 2) not null check (price_total >= 0),
  deposit_amount numeric(10, 2) not null check (deposit_amount >= 0),

  contact_name text not null,
  contact_phone text not null,
  contact_email text not null,
  rfc_receiver text,
  notes text,

  -- Referencia del pago en Mercado Pago. Llenada por el webhook.
  payment_provider text,
  payment_reference text,
  deposit_paid_at timestamptz,

  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),

  constraint bookings_valid_range check (starts_at < ends_at),
  constraint bookings_single_subject check (
    (service_id is not null and course_id is null)
    or (service_id is null and course_id is not null)
  ),
  constraint bookings_deposit_within_total check (deposit_amount <= price_total)
);

-- Índice clave para el cálculo de disponibilidad.
create index if not exists bookings_specialist_range_idx
  on public.bookings (specialist_id, starts_at, ends_at);

create index if not exists bookings_student_idx on public.bookings (student_id);

create index if not exists bookings_status_idx on public.bookings (status);

-- ---------------------------------------------------------------------
-- Integridad de la agenda: el corazón del sistema
-- ---------------------------------------------------------------------
-- Dos clientas no pueden tomar el mismo horario. La validación en el frontend
-- es UX, no seguridad: si se omiten dos peticiones a la vez, ambas pasarían.
-- Esta constraint lo cierra en la base, que es donde no hay carrera.
--
-- El predicado WHERE sobre el estado hace que solo las reservas activas
-- bloqueen: una cita cancelada libera el horario sin borrar el registro.
alter table public.bookings
  drop constraint if exists bookings_no_overlap;

alter table public.bookings
  add constraint bookings_no_overlap
  exclude using gist (
    specialist_id with =,
    tstzrange(starts_at, ends_at) with &&
  ) where (status in ('pendiente', 'confirmada', 'completada'));

-- ---------------------------------------------------------------------
-- Productos
-- ---------------------------------------------------------------------
create table if not exists public.products (
  id uuid primary key default gen_random_uuid(),
  slug text not null unique,
  name text not null,
  brand text not null,
  category text not null,
  description text not null default '',
  icon text,
  price numeric(10, 2) not null check (price >= 0),
  -- México: estos productos pagan IVA 16%, distinto a algunos servicios.
  tax_rate numeric(4, 3) not null default 0.160,
  image_url text,
  is_published boolean not null default true,
  created_at timestamptz not null default now()
);

create index if not exists products_category_idx
  on public.products (category)
  where is_published;

-- Cada presentación (250 ml, 500 ml) es una fila con su propio stock.
create table if not exists public.product_variants (
  id serial primary key generated always as identity,
  product_id uuid not null references public.products (id) on delete cascade,
  size_label text not null,
  sku text unique,
  -- El descuento a stock solo ocurre al confirmar el pago, nunca al agregar
  -- al carrito: si el pago falla, el producto vuelve a estar disponible.
  stock_on_hand integer not null default 0 check (stock_on_hand >= 0),
  price_override numeric(10, 2) check (price_override >= 0),
  is_active boolean not null default true
);

create index if not exists product_variants_product_idx
  on public.product_variants (product_id);

-- Auditoría de stock: toda entrada y salida queda registrada.
create table if not exists public.inventory_movements (
  id bigint generated always as identity primary key,
  variant_id integer not null references public.product_variants (id) on delete restrict,
  -- delta positivo suma, negativo resta.
  delta integer not null check (delta <> 0),
  reason text not null,
  reference text,
  created_at timestamptz not null default now()
);

create index if not exists inventory_movements_variant_idx
  on public.inventory_movements (variant_id, created_at desc);

-- ---------------------------------------------------------------------
-- Pedidos
-- ---------------------------------------------------------------------
create type order_status as enum ('pendiente', 'pagado', 'entregado', 'cancelado');

create table if not exists public.orders (
  id uuid primary key default gen_random_uuid(),
  code text not null unique default (
    'PED-' || upper(substr(replace(gen_random_uuid()::text, '-', ''), 1, 6))
  ),
  customer_id uuid references public.profiles (id) on delete set null,
  status order_status not null default 'pendiente',
  contact_name text not null,
  contact_phone text not null,
  contact_email text not null,
  rfc_receiver text,
  subtotal numeric(10, 2) not null check (subtotal >= 0),
  tax_amount numeric(10, 2) not null default 0 check (tax_amount >= 0),
  total numeric(10, 2) not null check (total >= 0),
  -- En el prototipo solo pick-up en el estudio; el envío llega en producción.
  fulfillment text not null default 'pickup' check (fulfillment in ('pickup', 'envio')),
  payment_provider text,
  payment_reference text,
  paid_at timestamptz,
  created_at timestamptz not null default now(),
  constraint orders_total_matches check (total = subtotal + tax_amount)
);

create table if not exists public.order_items (
  id bigint generated always as identity primary key,
  order_id uuid not null references public.orders (id) on delete cascade,
  variant_id integer not null references public.product_variants (id) on delete restrict,
  quantity integer not null check (quantity > 0),
  -- Precio congelado al momento de comprar.
  unit_price numeric(10, 2) not null check (unit_price >= 0),
  subtotal numeric(10, 2) not null check (subtotal >= 0)
);

create index if not exists order_items_order_idx on public.order_items (order_id);

-- ---------------------------------------------------------------------
-- Reseñas y FAQ
-- ---------------------------------------------------------------------
create table if not exists public.reviews (
  id uuid primary key default gen_random_uuid(),
  author_name text not null,
  role_label text,
  initials text not null check (char_length(initials) between 1 and 3),
  rating smallint not null check (rating between 1 and 5),
  body text not null,
  is_published boolean not null default true,
  sort_order integer not null default 0,
  created_at timestamptz not null default now()
);

create table if not exists public.faqs (
  id uuid primary key default gen_random_uuid(),
  question text not null,
  answer text not null,
  category text not null default 'General',
  sort_order integer not null default 0,
  is_published boolean not null default true
);

-- ---------------------------------------------------------------------
-- updated_at automático
-- ---------------------------------------------------------------------
create or replace function public.set_updated_at()
returns trigger
language plpgsql
as $$
begin
  new.updated_at = now();
  return new;
end;
$$;

drop trigger if exists bookings_set_updated_at on public.bookings;
create trigger bookings_set_updated_at
  before update on public.bookings
  for each row execute function public.set_updated_at();