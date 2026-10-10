using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AndyProAcademy.Api.Models;

namespace AndyProAcademy.Api.Services;

/// <summary>
/// Cliente de Mercado Pago.
///
/// Modo sandbox por defecto: las credenciales de prueba generan enlaces a
/// un dominio de pruebas donde no se mueve dinero. Cambiar a producción
/// requiere tanto las llaves reales como poner Sandbox en false, para que
/// un cambio de una cosa no arrastre a la otra.
///
/// El Access Token se obtiene cada llamada con Basic auth contra
/// /oauth/token, que es lo que pide la API y no requiere SDK. Se cachea en
/// memoria porque el token vive una hora y el panel hace varias consultas
/// seguidas: pedirlo en cada una sumaría latencia sin necesidad.
/// </summary>
public sealed class MercadoPagoClient(
    HttpClient httpClient,
    IConfiguration config,
    ILogger<MercadoPagoClient> logger)
{
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    /// <summary>Las credenciales vienen de configuración, nunca del código.</summary>
    private string AccessToken => config["MercadoPago:AccessToken"] ?? string.Empty;
    private string PublicKey => config["MercadoPago:PublicKey"] ?? string.Empty;
    private string BackendUrl => config["MercadoPago:BackendUrl"] ?? "http://localhost:5080";

    /// <summary>
    /// true mientras use credenciales de prueba. El banner de la interfaz
    /// depende de esto para no dejar cobrar de verdad en una demo.
    /// </summary>
    public bool IsSandbox =>
        !config.GetValue("MercadoPago:Production", false);

    /// <summary>
    /// Si hay credenciales cargadas.
    ///
    /// Lo consulta el endpoint de configuración para que el frontend sepa
    /// si puede ofrecer el checkout o debe explicar que faltan las llaves.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>
    /// Llave pública, que sí puede ir en el frontend.
    ///
    /// La del access token nunca sale del backend: es el secreto que
    /// permite mover dinero.
    /// </summary>
    public string GetPublicKey() => PublicKey;

    private string CheckoutUrlBase =>
        IsSandbox ? "https://sandbox.mercadopago.com.mx/checkout/v1/redirect" : "https://www.mercadopago.com.mx/checkout/v1/redirect";

    /// <summary>
    /// Crea una preferencia de pago y devuelve la URL de checkout.
    ///
    /// El external_reference lleva el código del pedido para poder
    /// reconciliar la notificación del webhook con el pedido. Sin eso,
    /// un pago que llega sin referencia sería imposible de atribuir.
    /// </summary>
    public async Task<MercadoPagoPreferenceDto> CreatePreferenceAsync(
        string orderCode,
        decimal total,
        string description,
        string payerEmail,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new MercadoPagoConfigurationException(
                "Faltan las credenciales de Mercado Pago. Revisa el .env.example.");
        }

        var payload = new
        {
            items = new[]
            {
                new
                {
                    title = description.Length > 120 ? description[..120] : description,
                    quantity = 1,
                    currency_id = "MXN",
                    unit_price = total,
                },
            },
            payer = new { email = payerEmail },
            // Por dónde vuelve la clienta tras pagar. En sandbox debe apuntar
            // a un dominio que Mercado Pago permita.
            back_urls = new
            {
                success = $"{BackendUrl}/tienda?pago=exitoso&orden={orderCode}",
                pending = $"{BackendUrl}/tienda?pago=pendiente&orden={orderCode}",
                failure = $"{BackendUrl}/tienda?pago=fallido&orden={orderCode}",
            },
            // Con auto_return el navegador regresa solo tras aprobar; sin
            // él, la clienta queda viendo una pantalla de Mercado Pago y
            // tiene que hacer clic manual para volver.
            auto_return = "approved",
            external_reference = orderCode,
            notification_url = $"{BackendUrl}/api/shop/webhooks/mercadopago",
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/users/me")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }),
        };

        // Sandbox y producción comparten endpoint; lo que cambia es la llave.
        request.RequestUri = new Uri(
            IsSandbox
                ? "https://api.mercadopago.com/checkout/preferences"
                : "https://api.mercadopago.com/checkout/preferences");

        var token = await GetAccessTokenAsync(ct);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Mercado Pago rechazó la preferencia ({Status}): {Body}",
                (int)response.StatusCode, body);

            throw new MercadoPagoException(
                "Mercado Pago no pudo generar el enlace de pago.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var preferenceId = root.GetProperty("id").GetString()
            ?? throw new MercadoPagoException("Mercado Pago no devolvió un id de preferencia.");

        var checkoutUrl = root.TryGetProperty("init_point", out var initPoint) &&
                          initPoint.ValueKind == JsonValueKind.String
            ? initPoint.GetString()!
            : root.TryGetProperty("sandbox_init_point", out var sandboxPoint) &&
              sandboxPoint.ValueKind == JsonValueKind.String
                ? sandboxPoint.GetString()!
                : $"{CheckoutUrlBase}?pref_id={preferenceId}";

        return new MercadoPagoPreferenceDto(
            PreferenceId: preferenceId,
            CheckoutUrl: checkoutUrl,
            Sandbox: IsSandbox);
    }

    /// <summary>
    /// Consulta el estado de un pago.
    ///
    /// Se usa para confirmar el pedido cuando la clienta vuelve del
    /// checkout. No sustituye al webhook: si cierra la pestaña antes de
    /// regresar, el webhook sigue siendo quien confirma.
    /// </summary>
    public async Task<(string Status, string? Reference)> GetPaymentStatusAsync(
        string paymentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(AccessToken)) return ("unknown", null);

        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/payments/{paymentId}");

        request.RequestUri = new Uri(
            IsSandbox
                ? $"https://api.mercadopago.com/v1/payments/{paymentId}"
                : $"https://api.mercadopago.com/v1/payments/{paymentId}");

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return ("unknown", null);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
        var reference = root.TryGetProperty("external_reference", out var r) &&
                        r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

        return (status, reference);
    }

    /// <summary>
    /// Obtiene el access token con credenciales de la app.
    ///
    /// Se cachea hasta un minuto antes de expirar: pedirlo en cada llamada
    /// sumaría una ida extra a la API por cada operación del panel.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{PublicKey}:{AccessToken}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_secret", AccessToken),
            }),
        };
        request.RequestUri = new Uri(
            IsSandbox
                ? "https://api.mercadopago.com/oauth/token"
                : "https://api.mercadopago.com/oauth/token");

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Falló la autenticación con Mercado Pago: {Body}", body);
            throw new MercadoPagoException("No se pudo autenticar con Mercado Pago.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _cachedToken = root.GetProperty("access_token").GetString()
            ?? throw new MercadoPagoException("Mercado Pago no devolvió un token.");

        // El campo expires_in viene en segundos. Se deja un minuto de
        // margen porque el token caduca en el servidor, no en el local.
        var expiresIn = root.TryGetProperty("expires_in", out var e)
            ? e.GetInt32()
            : 3600;

        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);

        return _cachedToken;
    }
}

/// <summary>Fallo de Mercado Pago que puede mostrarse al usuario.</summary>
public sealed class MercadoPagoException(string message) : Exception(message);

/// <summary>Faltan credenciales de Mercado Pago en la configuración.</summary>
public sealed class MercadoPagoConfigurationException(string message) : Exception(message);