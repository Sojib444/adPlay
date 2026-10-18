using AdPlay.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace AdPlay.Api.Features.Products;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(ProductSearchService search) : ControllerBase
{
    /// <summary>
    /// GET /api/products?keyword=phone&amp;minPrice=100&amp;maxPrice=900&amp;categoryIds=1&amp;categoryIds=4&amp;brandIds=2&amp;sortBy=price&amp;sortDirection=asc&amp;page=1&amp;pageSize=20
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "ProductSearch")]
    public async Task<ActionResult<PagedResult<ProductListItem>>> Search([FromQuery] ProductSearchQuery query, CancellationToken ct)
    {
        var errors = ProductSearchService.Validate(query);
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors));
        return Ok(await search.SearchAsync(query, ct));
    }

    /// <summary>Keyset-paginated feed for infinite scroll: GET /api/products/feed?pageSize=20&amp;cursor=...</summary>
    [HttpGet("feed")]
    [OutputCache(PolicyName = "ProductSearch")]
    public async Task<ActionResult<CursorPage<ProductListItem>>> Feed([FromQuery] ProductSearchQuery query, CancellationToken ct)
    {
        var errors = ProductSearchService.Validate(query);
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors));
        return Ok(await search.FeedAsync(query, ct));
    }
}
