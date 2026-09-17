using BillAssistant.Api.Endpoints;
using BillAssistant.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Every adapter - model provider, vector store, database, PDF reader - is registered here.
builder.Services.AddBillAssistantInfrastructure(builder.Configuration);

// The Angular dev server proxies /api, so CORS only matters if the app is hosted separately.
const string DevCors = "dev";
builder.Services.AddCors(options => options.AddPolicy(DevCors, policy => policy
    .WithOrigins("http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCors);
    app.MapOpenApi();
    app.MapScalarApiReference();

    // Development convenience: create or update the SQLite file on start.
    await app.Services.MigrateDatabaseAsync();
}

app.MapBillEndpoints();
app.MapChatEndpoints();
app.MapHealthEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can spin the API up with WebApplicationFactory.</summary>
public partial class Program;
