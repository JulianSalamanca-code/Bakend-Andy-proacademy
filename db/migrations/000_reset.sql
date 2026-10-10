-- =====================================================================
-- 000 — Reinicio total
--
-- Deja el esquema public vacío para reaplicar las migraciones desde cero.
-- Solo para desarrollo, sobre una base de prototipo. En una base con datos
-- reales esto es destructivo.
-- =====================================================================

do $$
declare
  r record;
begin
  -- Las políticas dependen de las tablas, y las funciones pueden devolver
  -- tipos de tabla, así que el orden importa.
  --
  -- Se excluyen las funciones que pertenecen a una extensión (pg_depend):
  -- borrar una función de btree_gist deja la extensión rota y Postgres
  -- aborta con 2BP01.
  for r in select p.oid::regprocedure as sig
             from pg_proc p
             join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'public'
              and not exists (
                select 1
                  from pg_depend d
                  join pg_extension e on e.oid = d.refobjid
                 where d.classid = 'pg_proc'::regclass
                   and d.objid = p.oid
              )
  loop
    execute format('drop function if exists %s cascade', r.sig);
  end loop;

  -- Solo el esquema public. Los esquemas de Supabase (auth, storage,
  -- graphql_*) son propiedad de la plataforma y borrarlos requiere
  -- privilegios de superusuario que el usuario de la app no tiene.
  for r in select schemaname, tablename
             from pg_tables
            where schemaname = 'public'
  loop
    execute format('drop table if exists %I.%I cascade', r.schemaname, r.tablename);
  end loop;

  -- Los tipos de enumeración sobreviven a drop table.
  for r in select t.typname
             from pg_type t
             join pg_namespace n on n.oid = t.typnamespace
            where n.nspname = 'public'
              and t.typtype = 'e'
  loop
    execute format('drop type if exists %I cascade', r.typname);
  end loop;
end;
$$;

-- Se conservan btree_gist a propósito: lo necesita la constraint
-- bookings_no_overlap, y recrearla es más lento que dejarla.