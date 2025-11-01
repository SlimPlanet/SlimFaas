using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ThreadPoolTuner : BackgroundService
{
    private readonly ILogger<ThreadPoolTuner> _logger;
    private readonly int _cores = Environment.ProcessorCount;

    // Bornes & pas d'ajustement
    private readonly int _minFloor;
    private readonly int _maxCeil;
    private readonly int _step;

    // Hystérésis
    private int _consecHigh;
    private int _consecLow;

    public ThreadPoolTuner(ILogger<ThreadPoolTuner> logger)
    {
        _logger = logger;

        // Bornes en fonction des cœurs (à ajuster selon ton contexte)
        _minFloor = Math.Clamp(_cores * 2, 32, 32_767);
        _maxCeil  = Math.Clamp(_cores * 32, 128, 32_767);
        _step     = Math.Max(_cores, 8);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Point de départ : “mixte”
        ThreadPool.GetMinThreads(out _, out var minIO);
        var target = Math.Clamp(_cores * 6, _minFloor, _maxCeil);
        ThreadPool.SetMinThreads(target, minIO);
        _logger.LogInformation("ThreadPoolTuner start: cores={cores}, MinW={min}, MinIO={io}", _cores, target, minIO);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Mesures
                ThreadPool.GetAvailableThreads(out var availW, out var availIO);
                ThreadPool.GetMinThreads(out var minW, out var minIO2);

                // Heuristiques simples :
                // - “High pressure” si < 10% de workers dispo ET le ThreadPool a dû s’agrandir récemment (approx via deltas)
                //   Ici on ne lit pas les EventCounters, on s’appuie sur la faible dispo de workers.
                bool highPressure = availW < Math.Max(1, (int)(minW * 0.10));

                // - “Low pressure” si beaucoup de marge (> 50% dispo)
                bool lowPressure = availW > (int)(minW * 0.50);

                if (highPressure && minW < _maxCeil)
                {
                    _consecHigh++;
                    _consecLow = 0;

                    if (_consecHigh >= 3) // 3 itérations consécutives ~ 3 s
                    {
                        var newMin = Math.Min(minW + _step, _maxCeil);
                        ThreadPool.SetMinThreads(newMin, minIO2);
                        _logger.LogInformation("↑ Raise MinW: {old} -> {new} (availW={avail})", minW, newMin, availW);
                        _consecHigh = 0;
                    }
                }
                else if (lowPressure && minW > _minFloor)
                {
                    _consecLow++;
                    _consecHigh = 0;

                    if (_consecLow >= 10) // baisse plus lente (hystérésis)
                    {
                        var newMin = Math.Max(minW - _step, _minFloor);
                        ThreadPool.SetMinThreads(newMin, minIO2);
                        _logger.LogInformation("↓ Lower MinW: {old} -> {new} (availW={avail})", minW, newMin, availW);
                        _consecLow = 0;
                    }
                }
                else
                {
                    _consecHigh = 0;
                    _consecLow = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ThreadPoolTuner loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
