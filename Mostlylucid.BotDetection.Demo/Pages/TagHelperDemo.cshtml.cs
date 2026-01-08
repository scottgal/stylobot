using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mostlylucid.BotDetection.Demo.Pages;

public class TagHelperDemoModel : PageModel
{
    public void OnGet()
    {
        // The tag helper reads from HttpContext.Items automatically
        // Bot detection middleware will populate the evidence
    }
}