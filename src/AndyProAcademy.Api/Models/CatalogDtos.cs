namespace AndyProAcademy.Api.Models;

// =====================================================================
// Catálogo
//
// Estos records son la fuente de verdad del contrato con el frontend.
// El documento OpenAPI se genera a partir de ellos y de ahí Angular
// obtiene sus tipos, así que un cambio aquí se propaga solo.
//
// Convenciones:
//   - Sin nullable reference en lo que la base nunca devuelve null.
//   - decimal para dinero, nunca double: 1850.10 en double no es exacto y
//     el redondeo termina en un cobro de un centavo de diferencia.
//   - Los nombres van en camelCase en la respuesta (ver config de JSON).
// =====================================================================

// ---------------------------------------------------------------------
// Servicios
// ---------------------------------------------------------------------

/// <summary>
/// Servicio del catálogo, tal como se muestra en la web.
/// </summary>
public sealed record ServiceDto(
    int Id,
    string Slug,
    string Name,
    string Tagline,
    string Description,
    string? Badge,
    int DurationMinutes,
    decimal Price,
    bool RfcRequired,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Brands,
    string? Preparation,
    string? ImageUrl,
    int SortOrder);

/// <summary>
/// Complemento de un servicio. Suma precio y tiempo de cabina.
/// </summary>
public sealed record AddOnDto(
    int Id,
    int ServiceId,
    string Name,
    string Description,
    decimal Price,
    int ExtraMinutes);

/// <summary>
/// Servicio con sus complementos anidados, que es como lo consume el
/// asistente de reserva en un solo paso.
/// </summary>
public sealed record ServiceWithAddOnsDto(
    ServiceDto Service,
    IReadOnlyList<AddOnDto> AddOns);

// ---------------------------------------------------------------------
// Cursos
// ---------------------------------------------------------------------

/// <summary>
/// Curso del catálogo con su temario completo.
/// </summary>
public sealed record CourseDto(
    Guid Id,
    string Slug,
    string Title,
    string Tagline,
    string Description,
    string Tag,
    string Level,
    string DurationLabel,
    int SessionCount,
    decimal Price,
    decimal DepositAmount,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Includes,
    string? ImageUrl);

/// <summary>
/// Curso más breve, para las tarjetas de la home.
/// </summary>
public sealed record CourseSummaryDto(
    Guid Id,
    string Slug,
    string Title,
    string Tagline,
    string Tag,
    string Level,
    string DurationLabel,
    decimal Price,
    decimal DepositAmount,
    string? ImageUrl);

// ---------------------------------------------------------------------
// Tienda
// ---------------------------------------------------------------------

public sealed record ProductVariantDto(
    int Id,
    string SizeLabel,
    string? Sku,
    int StockOnHand,
    decimal? PriceOverride)
{
    /// <summary>
    /// Precio efectivo de la variante. El override permite, por ejemplo,
    /// que la presentación grande cueste más sin duplicar el producto.
    /// </summary>
    public decimal EffectivePrice(decimal basePrice) => PriceOverride ?? basePrice;
}

public sealed record ProductDto(
    Guid Id,
    string Slug,
    string Name,
    string Brand,
    string Category,
    string Description,
    string? Icon,
    decimal Price,
    decimal TaxRate,
    string? ImageUrl,
    IReadOnlyList<ProductVariantDto> Variants);

/// <summary>
/// Producto de la lista: precio desde la presentación más barata, que es
/// lo que se muestra en la tarjeta.
/// </summary>
public sealed record ProductSummaryDto(
    Guid Id,
    string Slug,
    string Name,
    string Brand,
    string Category,
    string Description,
    string? Icon,
    decimal PriceFrom,
    bool InStock,
    string? ImageUrl);

// ---------------------------------------------------------------------
// Contenido
// ---------------------------------------------------------------------

public sealed record ReviewDto(
    Guid Id,
    string AuthorName,
    string? RoleLabel,
    string Initials,
    int Rating,
    string Body,
    int SortOrder);

public sealed record FaqDto(
    Guid Id,
    string Question,
    string Answer,
    string Category,
    int SortOrder);

public sealed record SpecialistDto(
    int Id,
    string Name,
    string Title,
    string? Bio,
    string? ImageUrl);

// ---------------------------------------------------------------------
// Respuestas compuestas
// ---------------------------------------------------------------------

/// <summary>
/// Todo lo que la home necesita, en una sola llamada.
///
/// Antes eran seis peticiones para pintar la página. Con una, el render es
/// más rápido y no hay estado intermedio donde la pantalla se ve a medias.
/// </summary>
public sealed record HomeDataDto(
    IReadOnlyList<CourseSummaryDto> FeaturedCourses,
    IReadOnlyList<ReviewDto> Reviews,
    IReadOnlyList<ServiceSummaryDto> Services);

public sealed record ServiceSummaryDto(
    int Id,
    string Slug,
    string Name,
    string Tagline,
    string? Badge,
    int DurationMinutes,
    decimal Price,
    string? ImageUrl);