using BinPacking.Web.Models;
using BinPacking.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BinPacking.Web.Controllers;

[ApiController]
[Route("api/items")]
public sealed class ItemsController(CatalogStore store) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ItemType>> GetAll() => Ok(store.GetItems());

    [HttpGet("{id:guid}")]
    public ActionResult<ItemType> Get(Guid id) =>
        store.GetItem(id) is { } item ? Ok(item) : NotFound();

    [HttpPost]
    public ActionResult<ItemType> Create(ItemInput input)
    {
        var item = store.AddItem(input);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    [HttpPut("{id:guid}")]
    public ActionResult<ItemType> Update(Guid id, ItemInput input) =>
        store.UpdateItem(id, input) is { } item ? Ok(item) : NotFound();

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id) => store.DeleteItem(id) ? NoContent() : NotFound();
}
