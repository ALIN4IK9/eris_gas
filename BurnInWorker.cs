using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GasAnalyzer.BurnIn;

public sealed class BurnInWorker : BackgroundService
{
    private readonly ISlotReader _reader;
    private readonly SqliteBurnInStorage _storage;
    private readonly ILogger<BurnInWorker> _logger;
    private readonly BurnInOptions _options;

    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<byte, SlotState> _slots = new();

    public BurnInWorker(
        ISlotReader reader,
        SqliteBurnInStorage storage,
        ILogger<BurnInWorker> logger,
        BurnInOptions? options = null)
    {
        _reader = reader;
        _storage = storage;
        _logger = logger;
        _options = options ?? new BurnInOptions();

        if (_options.DeviceCount is < 1 or > 247)
            throw new ArgumentOutOfRangeException(nameof(options), "DeviceCount must be between 1 and 247.");

        if (_options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must be greater than zero.");

        if (_options.CommunicationErrorLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "CommunicationErrorLimit must be at least 1.");

        for (byte address = 1; address <= _options.DeviceCount; address++)
        {
            _slots.Add(address, new SlotState { ModbusAddress = address });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Burn-in session {SessionId} started. Devices: {DeviceCount}",
            _sessionId,
            _slots.Count);

        using var timer = new PeriodicTimer(_options.PollInterval);
        var samplesToSave = new List<DeviceSample>(_slots.Count);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                samplesToSave.Clear();

                foreach (var (address, slot) in _slots)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    if (slot.Status != TestStatus.Running)
                        continue;

                    var sample = await _reader.ReadSlotAsync(address, stoppingToken);

                    slot.RegisterSample(
                        sample,
                        _options.MaxAllowedDriftPpm,
                        _options.MaxAllowedNoisePpm,
                        _options.CommunicationErrorLimit);

                    samplesToSave.Add(sample);

                    if (slot.Status != TestStatus.Running)
                    {
                        _logger.LogWarning(
                            "Device at address {Address} failed: {Status}",
                            address,
                            slot.Status);
                    }
                }

                if (samplesToSave.Count > 0)
                {
                    await _storage.SaveBatchAsync(
                        _sessionId,
                        samplesToSave,
                        stoppingToken);
                }

                if (_options.TestDuration.HasValue &&
                    DateTime.UtcNow - startedAt >= _options.TestDuration.Value)
                {
                    FinishRemainingDevices();
                    break;
                }

                if (_slots.Values.All(slot => slot.Status != TestStatus.Running))
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Burn-in session {SessionId} was stopped.", _sessionId);
        }

        LogFinalResults();
    }

    public IReadOnlyDictionary<byte, SlotState> GetRackSnapshot()
    {
        return new Dictionary<byte, SlotState>(_slots);
    }

    private void FinishRemainingDevices()
    {
        foreach (var slot in _slots.Values)
            slot.MarkPassed();
    }

    private void LogFinalResults()
    {
        foreach (var slot in _slots.Values)
        {
            _logger.LogInformation(
                "Device {Address}: {Status}, current={CurrentPpm:F2} ppm, max drift={Drift:F2} ppm, noise={Noise}",
                slot.ModbusAddress,
                slot.Status,
                slot.CurrentPpm,
                slot.MaxDriftPpm,
                slot.NoisePpm?.ToString("F2") ?? "not measured");
        }
    }
}
