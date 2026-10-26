using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechStore.Auth;
using TechStore.Dto;
using TechStore.Services;

namespace TechStore.Api;

/// <summary>Creates and reads orders, and exposes each status-changing action as its own endpoint.</summary>
[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orders;
    private readonly ReservationService _reservations;
    private readonly FulfillmentService _fulfillment;

    public OrdersController(OrderService orders, ReservationService reservations, FulfillmentService fulfillment)
    {
        _orders = orders;
        _reservations = reservations;
        _fulfillment = fulfillment;
    }

    [HttpPost]
    [Authorize(Policy = Policies.ManageOrders)]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request)
    {
        var order = await _orders.CreateDraftOrderAsync(request);
        return CreatedAtAction(nameof(Get), new { id = order.OrderId }, order); // 201 + Location header
    }

    // status can be repeated (?status=Reserved&status=Picking). Validation of every value happens in the service.
    [HttpGet]
    public async Task<ActionResult<PagedResponse<OrderListItem>>> List(
        [FromQuery] string[] status, [FromQuery] long? customerId, [FromQuery] long? warehouseId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(await _orders.ListOrdersAsync(status, customerId, warehouseId, page, pageSize));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<OrderResponse>> Get(long id) =>
        Ok(await _orders.GetOrderAsync(id));

    [HttpPost("{id:long}/submit")]
    [Authorize(Policy = Policies.ManageOrders)]
    public async Task<ActionResult<OrderResponse>> Submit(long id) =>
        Ok(await _reservations.SubmitOrderAsync(id));

    [HttpPost("{id:long}/pick")]
    [Authorize(Policy = Policies.FulfillOrders)]
    public async Task<ActionResult<OrderResponse>> Pick(long id) =>
        Ok(await _fulfillment.PickOrderAsync(id));

    [HttpPost("{id:long}/pack")]
    [Authorize(Policy = Policies.FulfillOrders)]
    public async Task<ActionResult<OrderResponse>> Pack(long id) =>
        Ok(await _fulfillment.PackOrderAsync(id));

    [HttpPost("{id:long}/ship")]
    [Authorize(Policy = Policies.FulfillOrders)]
    public async Task<ActionResult<OrderResponse>> Ship(long id) =>
        Ok(await _fulfillment.ShipOrderAsync(id));

    [HttpPost("{id:long}/cancel")]
    [Authorize(Policy = Policies.ManageOrders)]
    public async Task<ActionResult<OrderResponse>> Cancel(long id) =>
        Ok(await _reservations.CancelOrderAsync(id));
}
