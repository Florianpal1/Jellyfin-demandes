using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.C411Torbox.Api;

/// <summary>
/// Sert la page web autonome du plugin. Accès anonyme : la page ne contient aucune donnée,
/// elle récupère ensuite le jeton de session Jellyfin du navigateur pour appeler l'API.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("C411Torbox/ui")]
public class UiController : ControllerBase
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.html"] = "text/html; charset=utf-8",
        ["app.js"] = "text/javascript; charset=utf-8",
        ["app.css"] = "text/css; charset=utf-8",
    };

    [HttpGet]
    public ActionResult Index()
    {
        // Les ressources sont référencées en relatif (« ui/app.js ») : l'URL ne doit pas finir par « / ».
        var path = Request.PathBase + Request.Path;
        if (path.Value is { Length: > 1 } p && p.EndsWith('/'))
        {
            return Redirect(p.TrimEnd('/') + Request.QueryString);
        }

        return Serve("app.html");
    }

    [HttpGet("{file}")]
    public ActionResult Asset([FromRoute] string file) => Serve(file);

    private ActionResult Serve(string file)
    {
        if (!Files.TryGetValue(file, out var contentType))
        {
            return NotFound();
        }

        var stream = typeof(UiController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.C411Torbox.Web." + file);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return File(stream, contentType);
    }
}
