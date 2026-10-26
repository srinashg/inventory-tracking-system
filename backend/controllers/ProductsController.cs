using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechStore.Auth;
using TechStore.Dto;
using TechStore.Services;

namespace TechStore.Api;

[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private readonly ProductService _products;

    public ProductsController(ProductService products)
    {
        _products = products;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ProductResponse>>> List(
        [FromQuery] bool activeOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(await _products.ListProductsAsync(activeOnly, page, pageSize));

    [HttpPost]
    [Authorize(Policy = Policies.ManageProducts)]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request) =>
        StatusCode(StatusCodes.Status201Created, await _products.CreateProductAsync(request));

    [HttpPatch("{id:long}")]
    [Authorize(Policy = Policies.ManageProducts)]
    public async Task<ActionResult<ProductResponse>> Update(long id, UpdateProductRequest request) =>
        Ok(await _products.UpdateProductAsync(id, request));
}
