# Andy Pro Academy — API

API de reservaciones y tienda para **Andy Cosmetología • Puebla Studio**.

- **Frontend:** Angular 21 (repo `PaginaWeb-Andy-Pro-Academy`)
- **Backend:** ASP.NET Core 10 Minimal API
- **Base de datos:** Supabase Postgres

## Requisitos

- .NET SDK 10 o superior
- Un proyecto de Supabase con la contraseña de la base configurada

## Configuración

La conexión se lee de `src/AndyProAcademy.Api/appsettings.Development.json`, que **está en `.gitignore`** (nunca se sube la contraseña real).

```json
{
  "Supabase": {
    "DbHost": "aws-0-us-east-1.pooler.supabase.com",
    "DbPort": "6543",
    "DbName": "postgres",
    "DbUser": "postgres.xxxxxxxxxxxxxx",
    "DbPassword": "TU_CONTRASENA"
  }
}
```

### Sobre el puerto 6543

Supabase ofrece dos puntos de conexión:

| Puerto | Modo | Notas |
|---|---|---|
| 5432 | Directo | Compatible con prepared statements |
| **6543** | **Pooler de transacciones (PgBouncer)** | **NO** soporta prepared statements |

Npgsql usa prepared statements por defecto. Por eso `ConnectionStringFactory` fuerza `Pooling = false`; sin eso la conexión falla con *"prepared statements are not supported"*.

La plantilla de todas las variables está en `.env.example`.

## Ejecutar

```bash
dotnet run --project src/AndyProAcademy.Api
```

- API: <http://localhost:5080>
- Swagger: <http://localhost:5080/swagger>

## Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/health` | Verifica que la API esté viva |
| GET | `/api/health/database` | Ejecuta `SELECT 1` contra Supabase |

El endpoint de base de datos devuelve el **SQLSTATE** de Postgres cuando falla, que indica la causa exacta:

- `28P01` → contraseña incorrecta
- `28000` → el rol de usuario no existe
- `3D000` → la base de datos no existe

## Estructura

```
src/AndyProAcademy.Api/
├── Endpoints/       ← un archivo por módulo (catalogo, booking, tienda, admin)
├── Models/          ← DTOs; fuente de los tipos TypeScript del frontend
├── Data/            ← conexión a Postgres y funciones SQL
├── Middleware/      ← validación de JWT de Supabase
├── Services/        ← lógica de negocio
└── Infrastructure/  ← configuración compartida
```

## Ramas

```
main  ← producción, siempre desplegable
dev   ← integración, siempre compila
feature/catalogo
feature/booking
feature/tienda
feature/admin
feature/auth
```

Las features salen siempre de `dev`. Para empezar un módulo:

```bash
git checkout dev && git pull
git checkout -b feature/nombre
```

## Generar el cliente TypeScript

El frontend obtiene sus tipos del documento OpenAPI de este backend. Tras cambiar un endpoint:

```bash
curl -o openapi.json http://localhost:5080/swagger/v1/swagger.json
```

y en el frontend:

```bash
npx openapi-typescript openapi.json -o src/app/core/api/schema.d.ts
```