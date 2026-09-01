using BinPacking.Web.Algorithms;
using BinPacking.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddSingleton<IPackingAlgorithm, ExtremePointPackingAlgorithm>();
builder.Services.AddSingleton<BoxSelectionService>();
builder.Services.AddSingleton<RandomOrderService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-cache, no-store, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
        }
        else if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else
        {
            headers.CacheControl = "no-cache, must-revalidate";
        }
    }
});
app.MapControllers();
app.MapHealthChecks("/health");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
