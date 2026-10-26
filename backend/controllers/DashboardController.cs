using Microsoft.AspNetCore.Mvc;
using TechStore.Dto;
using TechStore.Services;

namespace TechStore.Api;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryResponse>> GetSummary() =>
        Ok(await _dashboard.GetSummaryAsync());
}
