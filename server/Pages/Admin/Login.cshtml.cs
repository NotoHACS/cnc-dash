using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace CncDash.Server.Pages.Admin;

public class LoginModel : PageModel
{
    public bool Failed { get; set; }

    [BindProperty] public string? Password { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true
            ? Redirect("/dashboard/admin")
            : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var expected = Environment.GetEnvironmentVariable("CNC_DASH_ADMIN_PASSWORD") ?? "changeme";
        if (string.IsNullOrEmpty(Password) || !FixedTimeEquals(Password, expected))
        {
            Failed = true;
            return Page();
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });
        return Redirect("/dashboard/admin");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ha = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(a));
        var hb = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(b));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}