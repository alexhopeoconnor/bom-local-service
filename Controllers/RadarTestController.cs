using BomLocalService.Models;
using BomLocalService.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace BomLocalService.Controllers;

/// <summary>
/// MVC controller for serving the radar test/demo page.
/// </summary>
[Route("radar")]
public class RadarTestController : Controller
{
    [HttpGet("{suburb}/{state}")]
    public IActionResult Index(string suburb, string state)
    {
        if (!LocationId.TryCreate(suburb, state, out var location, out var error))
        {
            return BadRequest(error);
        }

        ViewBag.Suburb = location!.Suburb;
        ViewBag.State = location.State;
        ViewBag.ApiBaseUrl = $"/api/radar/{Uri.EscapeDataString(location.Suburb)}/{Uri.EscapeDataString(location.State)}";

        return View();
    }
}
