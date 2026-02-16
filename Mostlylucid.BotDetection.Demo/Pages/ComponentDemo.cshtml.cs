using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mostlylucid.BotDetection.Demo.Pages;

public class ComponentDemoModel : PageModel
{
    public void OnGet()
    {
        // sb-* TagHelpers read from HttpContext automatically
    }

    public void OnPost()
    {
        // Honeypot validation demo
    }
}
