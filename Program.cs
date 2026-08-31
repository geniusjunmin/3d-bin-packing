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
app.UseStaticFiles();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
