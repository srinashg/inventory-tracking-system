using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechStore.Auth;
using TechStore.Dto;
using TechStore.Services;

namespace TechStore.Api;

[ApiController]
[Route("api/inventory")]
public class InventoryController : ControllerBase
{
    private readonly InventoryService _inventory;

    public InventoryController(InventoryService inventory)
    {
        _inventory = inventory;
    }

    // A missing warehouseId binds to 0, which the service rejects with a 400.
    [HttpGet]
    public async Task<ActionResult<PagedResponse<InventoryItemResponse>>> Get(
        [FromQuery] long warehouseId = 0, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(await _inventory.GetInventoryAsync(warehouseId, page, pageSize));

    [HttpPost("receipts")]
    [Authorize(Policy = Policies.ManageStock)]
    public async Task<ActionResult<StockChangeResponse>> Receive(ReceiveStockRequest request) =>
        StatusCode(StatusCodes.Status201Created, await _inventory.ReceiveStockAsync(request));

    [HttpPost("adjustments")]
    [Authorize(Policy = Policies.ManageStock)]
    public async Task<ActionResult<StockChangeResponse>> Adjust(AdjustStockRequest request) =>
        StatusCode(StatusCodes.Status201Created, await _inventory.AdjustStockAsync(request));
}
