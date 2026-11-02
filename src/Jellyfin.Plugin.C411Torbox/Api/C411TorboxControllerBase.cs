using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.C411Torbox.Api;

/// <summary>Base commune : identité de l'utilisateur Jellyfin et sérialisation JSON (camelCase) maîtrisée.</summary>
public abstract class C411TorboxControllerBase : ControllerBase
{
    // Claims posés par l'authentification Jellyfin (Jellyfin.Api.Constants.InternalClaimTypes / UserRoles).
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";
    private const string AdministratorRole = "Administrator";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    protected Guid CurrentUserId
    {
        get
        {
            var value = User.FindFirstValue(UserIdClaim);
            return Guid.TryParse(value, out var id) ? id : Guid.Empty;
        }
    }

    protected string CurrentUserName => User.FindFirstValue(ClaimTypes.Name) ?? "?";

    protected bool IsAdmin => User.IsInRole(AdministratorRole);

    /// <summary>Les demandes doivent venir d'un vrai utilisateur (pas d'une clé API serveur).</summary>
    protected bool IsRealUser
        => CurrentUserId != Guid.Empty
            && !string.Equals(User.FindFirstValue(IsApiKeyClaim), bool.TrueString, StringComparison.OrdinalIgnoreCase);

    protected static JsonResult JsonOk(object? value) => new(value, JsonOptions);

    protected static ObjectResult ErrorResult(int statusCode, string message)
        => new(new { error = message }) { StatusCode = statusCode };

    protected static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
