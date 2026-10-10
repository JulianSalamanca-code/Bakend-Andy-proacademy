using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AndyProAcademy.Api.Services;

/// <summary>
/// Claims extraídos de un JWT válido de Supabase.
/// </summary>
public sealed record AuthenticatedUser(
    Guid UserId,
    string Role,
    string? Email)
{
    /// <summary>
    /// true solo para usuarios que iniciou sesión.
    ///
    /// Importa por un motivo concreto: la anon key de Supabase ES un JWT
    /// firmado y válido, con role 'anon'. Si se aceptara cualquier token
    /// con firma correcta, la anon key — que es pública y va en el
    /// frontend — bastaría para llamar a los endpoints de admin.
    /// </summary>
    public bool IsAuthenticatedUser => Role == "authenticated";

    public bool IsAnon => Role == "anon";

    public bool IsServiceRole => Role == "service_role";
}

/// <summary>
/// Valida los JWT que emite Supabase Auth.
///
/// Decodificar un token NO es validarlo. Aquí se comprueba la firma con la
/// clave del proyecto y la expiración, que es lo que impide que alguien
/// falsifique un token con rol 'admin' dentro del payload.
///
/// Nota sobre algoritmos: se fija explícitamente a HS256. Sin esa
/// restricción, un atacante podría mandar un token con alg 'none' o
/// 'RS256' y evitar por completo la verificación de firma.
/// </summary>
public sealed class SupabaseJwtValidator(IConfiguration config, ILogger<SupabaseJwtValidator> logger)
{
    private string? _secret => config["Supabase:JwtSecret"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secret);

    /// <summary>
    /// Valida el token y devuelve el usuario, o el motivo del rechazo.
    /// </summary>
    public (AuthenticatedUser? User, string? Error) Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (null, "Falta el token de autenticación.");

        if (!IsConfigured)
        {
            // No se degrada a 'permitir todo': si el secreto no está, la
            // API queda inutilizable para el admin, que es preferible a
            // dejarla abierta.
            logger.LogError(
                "Supabase:JwtSecret no está configurado; no se puede validar ningún token.");
            return (null, "La autenticación no está configurada en el servidor.");
        }

        // Solo se acepta el esquema Bearer.
        if (!token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, "El encabezado Authorization debe usar el esquema Bearer.");

        var rawToken = token["Bearer ".Length..].Trim();

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // Supabase emite con dos emisores distintos: la URL del proyecto
            // y la de auth. Se aceptan ambos porque según la versión y el
            // flujo puede venir uno u otro.
            ValidIssuers = new[]
            {
                config["Supabase:JwtIssuer"],
                config["Supabase:ProjectUrl"],
                $"https://{config["Supabase:JwtIssuer"]}",
            }.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToArray(),

            ValidateAudience = false,
            ValidateLifetime = true,
            // Margen de 30 s para desfases de reloj entre servicios.
            ClockSkew = TimeSpan.FromSeconds(30),

            // Algoritmo fijo. Sin esto, un token con alg 'none' —que no
            // lleva firma— sería aceptado.
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_secret!)),
        };

        // Si no se configuraron emisores, se relajan en vez de rechazar todo: la
        // firma y la expiración siguen validándose, que es lo que importa
        // para la seguridad.
        var issuers = parameters.ValidIssuers?.ToArray() ?? Array.Empty<string>();

        if (issuers.Length == 0)
            parameters.ValidateIssuer = false;

        try
        {
            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = false,
            };

            // El principal ya trae los claims validados, así que no hace falta
            // convertir el token: la versión de la librería cambia si el
            // tipo es JwtSecurityToken o JsonWebToken, pero los claims
            // validados son los mismos.
            var principal = handler.ValidateToken(rawToken, parameters, out _);

            var userIdClaim = principal.FindFirst("sub")?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!Guid.TryParse(userIdClaim, out var userId))
                return (null, "El token no trae un identificador de usuario válido.");

            var role = principal.FindFirst("role")?.Value
                ?? principal.FindFirst(ClaimTypes.Role)?.Value
                ?? "desconocido";

            var email = principal.FindFirst("email")?.Value;

            return (new AuthenticatedUser(userId, role, email), null);
        }
        catch (SecurityTokenExpiredException)
        {
            return (null, "La sesión expiró. Inicia sesión de nuevo.");
        }
        catch (SecurityTokenException ex)
        {
            // El detalle del error de firma no se devuelve al cliente: podría
            // ayudar a alguien a probar variations sobre el token.
            logger.LogWarning("Token rechazado: {Message}", ex.Message);
            return (null, "Sesión inválida.");
        }
        catch (ArgumentException)
        {
            return (null, "Sesión inválida.");
        }
    }
}