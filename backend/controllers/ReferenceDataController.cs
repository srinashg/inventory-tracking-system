using Microsoft.AspNetCore.Mvc;
using TechStore.Dto;
using TechStore.Services;

namespace TechStore.Api;

/// <summary>Lists the warehouses and customers that order and inventory forms choose from.</summary>
[ApiController]
public class ReferenceDataController : ControllerBase
{
    private readonly ReferenceDataService _referenceData;

    public ReferenceDataController(ReferenceDataService referenceData)
    {
        _referenceData = referenceData;
    }

    [HttpGet("api/warehouses")]
    public async Task<ActionResult<IReadOnlyList<WarehouseResponse>>> ListWarehouses() =>
        Ok(await _referenceData.ListWarehousesAsync());

    [HttpGet("api/customers")]
    public async Task<ActionResult<IReadOnlyList<CustomerResponse>>> ListCustomers() =>
        Ok(await _referenceData.ListCustomersAsync());
}
