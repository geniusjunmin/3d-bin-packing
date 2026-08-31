using BinPacking.Web.Models;
using BinPacking.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BinPacking.Web.Controllers;

[ApiController]
[Route("api/boxes")]
public sealed class BoxesController(CatalogStore store) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<BoxType>> GetAll() => Ok(store.GetBoxes());

    [HttpGet("{id:guid}")]
    public ActionResult<BoxType> Get(Guid id) =>
        store.GetBox(id) is { } box ? Ok(box) : NotFound();

    [HttpPost]
    public ActionResult<BoxType> Create(BoxInput input)
    {
        var box = store.AddBox(input);
        return CreatedAtAction(nameof(Get), new { id = box.Id }, box);
    }

    [HttpPut("{id:guid}")]
    public ActionResult<BoxType> Update(Guid id, BoxInput input) =>
        store.UpdateBox(id, input) is { } box ? Ok(box) : NotFound();

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id) => store.DeleteBox(id) ? NoContent() : NotFound();
}
