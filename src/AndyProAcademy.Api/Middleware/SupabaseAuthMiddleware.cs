using System.Security.Claims;
using AndyProAcademy.Api.Services;
using Npgsql;

namespace AndyProAcademy.Api.Middleware;

/// <summary>
/// Identifica al usuario de cada petición a partir del JWT de Supabase.
///
/// No bloquea por sí solo: deja el resultado en el contexto para que el
/// filtro de admin decida. Separar las dos cosas permite que las rutas
/// públicas sigan funcionando aunque el JWT no venga, que es lo normal
/// en el catálogo.
/// </summary>
public sealed class SupabaseAuthMiddleware(
    RequestDelegate next,
    SupabaseJwtValidator validator,
    NpgsqlConnectionStringBuilder connectionString,
    ILogger<SupabaseAuthMiddleware> logger)
{
    public const string UserIdKey = "supabase.user_id";
    public const string JwtRoleKey = "supabase.jwt_role";
    public const string ProfileRoleKey = "supabase.profile_role";

    public async Task InvokeAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        // Sin encabezado, la petición sigue como anónima. No es un error:
        // el catálogo es público.
        if (!string.IsNullOrWhiteSpace(header))
        {
            var (user, error) = validator.Validate(header);

            if (user is not null)
            {
                context.Items[UserIdKey] = user.UserId;
                context.Items[JwtRoleKey] = user.Role;

                // El rol del JWT (authenticated, anon...) no es el rol de la
                // aplicación. El admin vive en profiles, así que se consulta
                // ahí. Sin fila, la cuenta existe pero no completó su
                // perfil: se trata como 'student'.
                context.Items[ProfileRoleKey] =
                    await GetProfileRoleAsync(user.UserId, context.RequestAborted);
            }
            else if (error is not null)
            {
                logger.LogDebug("Petición sin usuario válido: {Error}", error);
            }
        }

        await next(context);
    }

    private async Task<string> GetProfileRoleAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(
                "select role from public.profiles where id = $1", connection);
            command.Parameters.AddWithValue(userId);

            var role = await command.ExecuteScalarAsync(ct);
            return role as string ?? "student";
        }
        catch (Exception ex)
        {
            // Un fallo al leer el perfil degrada a 'student', nunca a 'admin'.
            // Fallar hacia el lado permisivo sería un agujero de seguridad.
            logger.LogError(ex, "No se pudo leer el perfil del usuario {UserId}", userId);
            return "student";
        }
    }
}

/// <summary>Exige una sesión iniciada.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireAuthAttribute : Attribute;

/// <summary>Exige rol de administrador.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireAdminAttribute : Attribute;

/// <summary>
/// Aplica [RequireAuth] y [RequireAdmin] sobre un endpoint.
/// </summary>
public sealed class SupabaseAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var endpoint = context.HttpContext.GetEndpoint();

        var requireAuth = endpoint?.Metadata.GetMetadata<RequireAuthAttribute>() is not null;
        var requireAdmin = endpoint?.Metadata.GetMetadata<RequireAdminAttribute>() is not null;

        if (!requireAuth && !requireAdmin) return await next(context);

        var http = context.HttpContext;

        // 1. ¿Hay una sesión iniciada?
        if (http.Items[SupabaseAuthMiddleware.UserIdKey] is not Guid userId)
        {
            return Results.Unauthorized();
        }

        // 2. ¿El token es de un usuario y no la anon key?
        //    La anon key es un JWT firmado con role 'anon'. Sin este chequeo
        //    bastaría la clave pública del proyecto para pasar el filtro.
        var jwtRole = http.Items[SupabaseAuthMiddleware.JwtRoleKey] as string;

        if (jwtRole is not ("authenticated" or "service_role"))
        {
            return Results.Unauthorized();
        }

        // 3. ¿Es admin según la base?
        if (requireAdmin)
        {
            var profileRole = http.Items[SupabaseAuthMiddleware.ProfileRoleKey] as string;

            if (profileRole != "admin")
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Sin permisos",
                    detail: "Esta sección es solo para el administrador del estudio.");
            }
        }

        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "Supabase"));

        return await next(context);
    }
}