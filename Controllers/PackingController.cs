using BinPacking.Web.Models;
using BinPacking.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BinPacking.Web.Controllers;

[ApiController]
[Route("api/packing")]
public sealed class PackingController(
    BoxSelectionService selectionService,
    RandomOrderService randomOrderService) : ControllerBase
{
    [HttpPost("pack")]
    public ActionResult<PackingResult> Pack(PackOrderRequest request)
    {
        var result = selectionService.Pack(request);
        return result.Success ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("random")]
    public ActionResult<RandomOrderResponse> Random() => Ok(randomOrderService.GenerateAndPack());
}
