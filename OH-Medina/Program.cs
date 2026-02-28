using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OH_Medina.Models;
using OH_Medina.Services;

var isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));

Console.WriteLine("Installing Chromium (if needed)...");
Microsoft.Playwright.Program.Main(["install", "chromium"]);

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<OhMedinaScraperService>();
    })
    .Build();

// Try Apify Key-Value store first; if that fails or returns null (e.g. running locally without Apify CLI), read from local input.json.
var config = await ApifyHelper.GetInputFromApifyAsync<InputConfig>();
if (config == null)
    config = await ApifyHelper.LoadLocalInputAsync<InputConfig>();
config ??= new InputConfig();

var service = host.Services.GetRequiredService<OhMedinaScraperService>();

try
{
    Console.WriteLine("Launching Medina County (OH) scraper with input config...");
    await service.RunAsync(config);
    Console.WriteLine("Done.");
    if (!isApify)
    {
        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
}
finally
{
    await service.StopAsync();
}
