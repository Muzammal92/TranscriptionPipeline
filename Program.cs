using Microsoft.EntityFrameworkCore;
using TranscriptionPipeline.Data;
using TranscriptionPipeline.Endpoints;
using TranscriptionPipeline.Security;
using TranscriptionPipeline.Services;
using TranscriptionPipeline.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=transcription.db"));

// Shared-secret API key auth on every endpoint - the key comes from configuration
// (environment variable / user-secrets / Key Vault), never hard-coded here.
builder.Services
    .AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme,
        options => options.ApiKey = builder.Configuration["Security:ApiKey"] ?? string.Empty);
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IAudioValidator, AudioValidator>();
builder.Services.AddSingleton<IAudioStorage, LocalFileStorage>();
builder.Services.AddSingleton<IAudioProcessor, FfmpegAudioProcessor>();
builder.Services.AddSingleton<ITranscriptionQueue, TranscriptionQueue>();

// Engine is swappable via config: "Mock" needs no credentials and is the default for
// this assessment; "AzureSpeech" is the production-realistic implementation behind
// the same ITranscriptionEngine interface.
var engineChoice = builder.Configuration["TranscriptionEngine"] ?? "Mock";
if (engineChoice.Equals("AzureSpeech", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<ITranscriptionEngine, AzureSpeechTranscriptionEngine>();
}
else
{
    builder.Services.AddScoped<ITranscriptionEngine, MockTranscriptionEngine>();
}

builder.Services.AddHostedService<TranscriptionWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapTranscriptionEndpoints();

app.Run();

public partial class Program;
