-- Diagnóstico: qué hay realmente en la base tras aplicar las migraciones.
-- Útil para confirmar qué se aplicó y qué falta, sin adivinar.
-- Ejecutar desde Supabase SQL Editor.

select 'tablas' as seccion, table_name as nombre,
       (select count(*) from information_schema.columns c
         where c.table_schema = 'public' and c.table_name = t.table_name) as columnas
  from information_schema.tables t
 where table_schema = 'public'
   and table_type = 'BASE TABLE'
 order by table_name;

-- Contenido sembrado
select 'contenido' as seccion,
       (select count(*) from public.services)     as servicios,
       (select count(*) from public.addons)       as complementos,
       (select count(*) from public.courses)      as cursos,
       (select count(*) from public.products)     as productos,
       (select count(*) from public.reviews)      as resenas,
       (select count(*) from public.faqs)         as faqs,
       (select count(*) from public.specialists)  as especialistas,
       (select count(*) from public.working_hours) as horarios;

-- Objetos de seguridad
select 'seguridad' as seccion,
       (select count(*) from pg_policies where schemaname = 'public') as politicas_rls,
       (select count(*) from pg_tables
         where schemaname = 'public' and rowsecurity)               as tablas_con_rls,
       (select count(*) from pg_extension where extname = 'btree_gist') as btree_gist;

-- Funciones propias
select 'funciones' as seccion, routine_name, routine_type
  from information_schema.routines
 where routine_schema = 'public'
 order by routine_name;

-- La constraint que impide el doble booking
select 'agenda' as seccion, conname, pg_get_constraintdef(oid) as definicion
  from pg_constraint
 where conname = 'bookings_no_overlap';