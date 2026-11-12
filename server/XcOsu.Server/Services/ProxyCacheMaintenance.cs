namespace XcOsu.Server.Services;

public sealed class ProxyCacheMaintenance(IConfiguration configuration, ILogger<ProxyCacheMaintenance> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string directory = Path.Combine(configuration["DataDirectory"] ?? "data", "proxy-cache");
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            await Api.Proxy.cache_gate.WaitAsync(stoppingToken);
            try
            {
                if (Directory.Exists(directory))
                {
                    var files = new DirectoryInfo(directory).GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToArray();
                    long total = files.Sum(f => f.Length);
                    foreach (var file in files)
                    {
                        if (total <= 2L * 1024 * 1024 * 1024 && file.LastAccessTimeUtc >= DateTime.UtcNow.AddDays(-7)
                            && !(file.Extension == ".tmp" && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) continue;
                        long length = file.Length;
                        file.Delete();
                        total -= length;
                    }
                }
            }
            catch (IOException) { log.LogWarning("Proxy cache cleanup will retry later"); }
            finally { Api.Proxy.cache_gate.Release(); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
