using AndyProAcademy.Api.Data;
using AndyProAcademy.Api.Endpoints;
using AndyProAcademy.Api.Middleware;
using AndyProAcademy.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------
// Servicios
// ------------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Andy Pro Academy API",
        Version = "v1",
        Description =
            "API de reservaciones y tienda para Andy Cosmetología • Puebla Studio. " +
            "Este documento OpenAPI es la fuente de la que el frontend Angular genera sus tipos.",
    });

    // Las anotaciones XML no vienen por defecto; las activamos si existen.
    var xmlFile = $"{typeof(Program).Assembly.GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);

    // Minimal API no propaga el tipo de retorno al documento OpenAPI: los
    // endpoints aparecen sin schema y el cliente TypeScript del frontend no
    // tendría de dónde sacar las interfaces. Swashbuckle resuelve esto solo
    // si cada endpoint declara su tipo con [ProducesResponseType], que es lo
    // que hacen los endpoints de catálogo.
});

// Valida la configuración de Supabase al arrancar, en lugar de fallar en el
// primer request. Si falta algo, el mensaje dice exactamente qué campo es.
builder.Services.AddSingleton(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    // Construir aquí valida los campos. El string resultante se guarda.
    return ConnectionStringFactory.Build(config);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:4200",
                "http://127.0.0.1:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<DatabaseInspector>();
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<BookingSmokeTests>();
builder.Services.AddSingleton<CatalogRepository>();
builder.Services.AddSingleton<BookingRepository>();
builder.Services.AddSingleton<AdminRepository>();
builder.Services.AddSingleton<ShopRepository>();
builder.Services.AddSingleton<SupabaseJwtValidator>();

// Mercado Pago necesita su propio HttpClient con timeout holgado: una
// llamada al proveedor puede tardar varios segundos y el default de 100s
// de HttpClient aplica, pero conviene dejarlo explicito.
builder.Services.AddHttpClient<MercadoPagoClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    // BaseAddress vacío a propósito: cada método compone su URL completa
    // porque sandbox y producción usan dominios distintos según el caso.
    client.BaseAddress = new Uri("https://api.mercadopago.com");
});

var app = builder.Build();

// ------------------------------------------------------------------
// Pipeline
// ------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Andy Pro Academy API v1");
        options.DocumentTitle = "Andy Pro Academy API";
    });
}

app.UseCors("AngularDev");

// Identifica al usuario antes de los endpoints. No bloquea: solo deja el
// resultado en el contexto, y el filtro de cada ruta decide si lo exige.
app.UseMiddleware<SupabaseAuthMiddleware>();

app.MapHealthEndpoints();
app.MapDiagnosticsEndpoints();
app.MapMigrationEndpoints();
app.MapCatalogEndpoints();
app.MapBookingEndpoints();
app.MapAdminEndpoints();
app.MapShopEndpoints();

app.Run();

/// <summary>
/// Punto de entrada. Expuesto para que las pruebas de integración puedan
/// referenciar el assembly con WebApplicationFactory.
/// </summary>
public partial class Program { }