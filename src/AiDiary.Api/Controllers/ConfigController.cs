using AiDiary.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

/// <summary>Public bootstrap config for the SPA (captcha wiring only — nothing sensitive).</summary>
[ApiController]
[AllowAnonymous]
[Route("api/config")]
public sealed class ConfigController(DiaryOptions options) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        captcha_provider = options.CaptchaProvider == "none" ? "" : options.CaptchaProvider,
        captcha_site_key = options.CaptchaProvider == "none" ? "" : options.CaptchaSiteKey,
    });
}
