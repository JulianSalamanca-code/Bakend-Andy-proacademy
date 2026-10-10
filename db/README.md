# Base de datos

Esquema, seguridad y datos iniciales para Supabase Postgres.

## Estructura

```
db/
├── migrations/
│   ├── 001_schema.sql      Tablas, índices, tipos y la constraint anti-doble-booking
│   ├── 002_rls.sql         Row Level Security: quién lee y quién escribe
│   ├── 003_functions.sql   Disponibilidad, creación de reservas y cobro de pedidos
│   └── 004_seed.sql        Servicios, cursos, productos, reseñas y FAQ
└── seeds/                  Datos de demostración adicionales (futuro)
```

## Aplicar las migraciones

En este orden. Cada archivo depende del anterior.

**Opción A — desde el dashboard (recomendado para empezar)**

Supabase Dashboard → SQL Editor → New query. Pega el contenido del archivo y
ejecuta. Repite con los cuatro en orden.

**Opción B — desde la línea de comandos**

```bash
npm install -g supabase
supabase login
supabase link --db-url "<tu-url-de-conexion>"
supabase db push
```

Si no quieres instalar la CLI, el dashboard funciona igual para el prototipo.

## Verificar

```bash
node scripts/check-sql.js
```

Comprueba balance de delimitadores, paréntesis, bloques `if` y caracteres fuera
del rango latino. No reemplaza a `psql`: el balance real de bloques plpgsql lo
confirma Postgres al ejecutar.

## Decisiones de diseño

### La agenda se protege en la base, no en el frontend

La tabla `bookings` tiene una constraint de exclusión:

```sql
exclude using gist (
  specialist_id with =,
  tstzrange(starts_at, ends_at) with &&
) where (status in ('pendiente', 'confirmada', 'completada'))
```

Validar el horario en el frontend es UX, no seguridad. Si dos peticiones llegan
a la vez, ambas pasarían la validación. La constraint hace que PostgreSQL
rechace la segunda, sin importar el orden ni la concurrencia.

El `WHERE` sobre el estado hace que solo las reservas activas bloqueen: una cita
cancelada libera el horario sin borrar el registro.

### El stock se descuenta al cobrar, no al agregar al carrito

`confirm_order_payment` descuenta dentro de una transacción y bloquea la fila
de la variante. Si la clienta abandona el checkout, el producto sigue
disponible sin intervención manual.

### RLS en vez de guards

Un guard de Angular se puede saltar desde la consola del navegador. Una
política RLS no. Las dos capas se necesitan, pero la base es la que sostiene
la seguridad.

La reserva se puede crear sin cuenta a propósito: exigir registro convertiría
cada visita en un muro y bajó la conversión. El contacto llega en el cuerpo de
la reserva y el folio permite reclamarla después.

### Zona horaria fija

La disponibilidad se construye con `at time zone 'America/Mexico_City'`. Si se
usara un `timestamptz` sin zona explícita, "10:00" significaría una hora
distinta según dónde corra la API, y los horarios se correrían.

## Campos preparados para facturación

El prototipo no factura, pero el esquema ya tiene lo que CFDI 4.0 va a
necesitar:

| Campo | Dónde | Para qué |
|---|---|---|
| `rfc_receiver` | `bookings`, `orders` | RFC del cliente que pide factura |
| `rfc_required` | `services` | Marca los servicios que exigen comprobante |
| `tax_rate` | `products` | IVA 16% en cosméticos |
| `payment_reference` | `bookings`, `orders` | Referencia del pago para el PAC |

## Antes de producción

 pendientes, en orden de importancia:

1. **Proveedor de facturación (PAC).** El prototipo no emite CFDI. Hace falta
   un PAC con API REST; ver `INTEGRACIONES.md` en la raíz del frontend.
2. **Promover a un usuario admin.** El primer admin se crea a mano:
   ```sql
   update public.profiles set role = 'admin' where id = '<uuid-del-admin>';
   ```
3. **Límite de intentos en el registro.** Añadir rate limiting.
4. **Confirmar los datos de contacto.** WhatsApp y dirección siguen como
   placeholder en el frontend.