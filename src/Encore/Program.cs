using Encore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<EncoreWorker>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

namespace Encore
{
    public partial class Program;
}
