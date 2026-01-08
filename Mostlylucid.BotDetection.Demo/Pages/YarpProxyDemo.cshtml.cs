using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mostlylucid.BotDetection.Demo.Pages;

public class YarpProxyDemoModel : PageModel
{
    public string? RequestId { get; set; }
    public bool IsProxied { get; set; }

    public void OnGet()
    {
        RequestId = HttpContext.TraceIdentifier;

        // Check if request came through YARP proxy by looking for detection headers
        IsProxied = HttpContext.Request.Headers.ContainsKey("X-Bot-Detection-Result") ||
                    HttpContext.Request.Headers.ContainsKey("X-Bot-Detection-Probability");
    }
}