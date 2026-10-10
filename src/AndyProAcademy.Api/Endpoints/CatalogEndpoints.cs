using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace AndyProAcademy.Api.Endpoints;

/// <summary>
/// Endpoints de lectura del catálogo.
///
/// Todos son públicos: el contenido del estudio no es secreto y el sitio
/// debe poder renderizarse sin sesión. Lo que sí requiere sesión (crear una
/// reserva, ver reservas propias) vive en otros endpoints.
///
/// Cada endpoint declara su tipo con [ProducesResponseType]. Sin eso,
/// Minimal API no lo propaga al documento OpenAPI y el frontend no puede
/// generar sus interfaces a partir del contrato.
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog")
            .WithTags("Catalog");

        // -----------------------------------------------------------------
        // Servicios
        // -----------------------------------------------------------------

        group.MapGet("/services", async (
                CatalogRepository repo,
                CancellationToken ct) =>
            {
                var services = await repo.GetServicesAsync(ct: ct);
                return Results.Ok(services);
            })
            .Produces<IReadOnlyList<ServiceWithAddOnsDto>>(StatusCodes.Status200OK)
            .WithName("GetServices")
            .WithSummary("Lista los servicios publicados con sus complementos");

        group.MapGet("/services/{slug}", async (
                string slug,
                CatalogRepository repo,
                CancellationToken ct) =>
            {
                var service = await repo.GetServiceBySlugAsync(slug, ct);

                // 404 explícito en vez de null: sin él el cliente recibe
                // un 200 con cuerpo vacío y no puede distinguir "no existe"
                // de "error del servidor".
                return service is null
                    ? Results.NotFound(new { message = "Servicio no encontrado" })
                    : Results.Ok(service);
            })
            .Produces<ServiceWithAddOnsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetServiceBySlug")
            .WithSummary("Detalle de un servicio");

        // -----------------------------------------------------------------
        // Cursos
        // -----------------------------------------------------------------

        group.MapGet("/courses", async (
                CatalogRepository repo,
                CancellationToken ct) =>
            {
                var courses = await repo.GetCoursesAsync(ct: ct);
                return Results.Ok(courses);
            })
            .Produces<IReadOnlyList<CourseDto>>(StatusCodes.Status200OK)
            .WithName("GetCourses")
            .WithSummary("Lista los cursos publicados");

        group.MapGet("/courses/{slug}", async (
                string slug,
                CatalogRepository repo,
                CancellationToken ct) =>
            {
                var course = await repo.GetCourseBySlugAsync(slug, ct);
                return course is null
                    ? Results.NotFound(new { message = "Curso no encontrado" })
                    : Results.Ok(course);
            })
            .Produces<CourseDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetCourseBySlug")
            .WithSummary("Detalle de un curso con temario completo");

        // -----------------------------------------------------------------
        // Productos
        // -----------------------------------------------------------------

        group.MapGet("/products", async (
                CatalogRepository repo,
                [FromQuery] string? category,
                CancellationToken ct) =>
            {
                var products = await repo.GetProductsAsync(category, ct);
                return Results.Ok(products);
            })
            .Produces<IReadOnlyList<ProductDto>>(StatusCodes.Status200OK)
            .WithName("GetProducts")
            .WithSummary("Lista los productos, opcionalmente por categoría");

        // -----------------------------------------------------------------
        // Contenido editorial
        // -----------------------------------------------------------------

        group.MapGet("/reviews", async (
                CatalogRepository repo,
                [FromQuery] int limit,
                CancellationToken ct) =>
            {
                // Límite acotado: sin tope, un limit=999999 traería la tabla
                // entera y la página se arrastraría.
                var capped = Math.Clamp(limit == 0 ? 6 : limit, 1, 50);
                return Results.Ok(await repo.GetReviewsAsync(capped, ct));
            })
            .Produces<IReadOnlyList<ReviewDto>>(StatusCodes.Status200OK)
            .WithName("GetReviews")
            .WithSummary("Reseñas publicadas");

        group.MapGet("/faqs", async (CatalogRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetFaqsAsync(ct)))
            .Produces<IReadOnlyList<FaqDto>>(StatusCodes.Status200OK)
            .WithName("GetFaqs")
            .WithSummary("Preguntas frecuentes");

        group.MapGet("/specialists", async (CatalogRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetSpecialistsAsync(ct)))
            .Produces<IReadOnlyList<SpecialistDto>>(StatusCodes.Status200OK)
            .WithName("GetSpecialists")
            .WithSummary("Especialistas activos");

        // -----------------------------------------------------------------
        // Compuesto: una sola llamada para la home
        // -----------------------------------------------------------------

        group.MapGet("/home", async (CatalogRepository repo, CancellationToken ct) =>
            {
                // Antes eran tres peticiones (cursos, reseñas, servicios).
                // Con una, la home no tiene estado intermedio donde se ve
                // medio pintada.
                var servicesTask = repo.GetServicesAsync(ct: ct);
                var coursesTask = repo.GetCoursesAsync(ct: ct);
                var reviewsTask = repo.GetReviewsAsync(3, ct);

                await Task.WhenAll(servicesTask, coursesTask, reviewsTask);

                var services = await servicesTask;
                var courses = await coursesTask;
                var reviews = await reviewsTask;

                return Results.Ok(new HomeDataDto(
                    FeaturedCourses: courses
                        .Select(c => new CourseSummaryDto(
                            c.Id, c.Slug, c.Title, c.Tagline, c.Tag, c.Level,
                            c.DurationLabel, c.Price, c.DepositAmount, c.ImageUrl))
                        .ToList(),
                    Reviews: reviews.ToList(),
                    Services: services
                        .Select(s => new ServiceSummaryDto(
                            s.Service.Id, s.Service.Slug, s.Service.Name,
                            s.Service.Tagline, s.Service.Badge,
                            s.Service.DurationMinutes, s.Service.Price,
                            s.Service.ImageUrl))
                        .ToList()));
            })
            .Produces<HomeDataDto>(StatusCodes.Status200OK)
            .WithName("GetHomeData")
            .WithSummary("Todo lo que la página de inicio necesita en una llamada");
    }
}