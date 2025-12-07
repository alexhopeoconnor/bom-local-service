using BomLocalService.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace BomLocalService.Controllers;

/// <summary>
/// MVC controller for serving the radar test/demo page.
/// This demonstrates best practices for consuming the radar API.
/// </summary>
[Route("radar")]
public class RadarTestController : Controller
{
    /// <summary>
    /// Serves the test page for viewing radar data for a specific location.
    /// Route: /radar/{suburb}/{state}
    /// </summary>
    [HttpGet("{suburb}/{state}")]
    public IActionResult Index(string suburb, string state)
    {
        var validationError = ValidationHelper.ValidateLocation(suburb, state);
        if (validationError != null)
        {
            return BadRequest(validationError);
        }

        ViewBag.Suburb = suburb;
        ViewBag.State = state;
        ViewBag.ApiBaseUrl = $"/api/radar/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}";
        
        return View();
    }
}

