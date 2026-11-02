namespace GasAnalyzer.BurnIn;

public enum TestStatus
{
    Running,
    Passed,
    FailedCommunication,
    FailedZeroDrift,
    FailedNoise
}

public sealed record DeviceSample(
    byte ModbusAddress,
    DateTime Timestamp,
    double ConcentrationPpm,
    double Temperature,
    int RawAdc,
    bool ResponseOk);

public sealed class SlotState
{
    private const int SampleWindowSize = 60;

    private readonly List<double> _recentSamples = new(SampleWindowSize);

    public byte ModbusAddress { get; init; }
    public string SerialNumber { get; set; } = string.Empty;
    public TestStatus Status { get; private set; } = TestStatus.Running;

    public double BaselinePpm { get; private set; }
    public double CurrentPpm { get; private set; }
    public double MaxDriftPpm { get; private set; }
    public double? NoisePpm { get; private set; }
    public int ConsecutiveCommunicationErrors { get; private set; }
    public int SuccessfulSampleCount { get; private set; }

    public IReadOnlyList<double> RecentSamples => _recentSamples;

    public void RegisterSample(
        DeviceSample sample,
        double maxAllowedDrift,
        double? maxAllowedNoise,
        int communicationErrorLimit)
    {
        if (Status != TestStatus.Running)
            return;

        if (!sample.ResponseOk)
        {
            ConsecutiveCommunicationErrors++;

            if (ConsecutiveCommunicationErrors >= communicationErrorLimit)
                Status = TestStatus.FailedCommunication;

            return;
        }

        ConsecutiveCommunicationErrors = 0;
        SuccessfulSampleCount++;
        CurrentPpm = sample.ConcentrationPpm;

        if (_recentSamples.Count == 0)
            BaselinePpm = sample.ConcentrationPpm;

        _recentSamples.Add(sample.ConcentrationPpm);

        if (_recentSamples.Count > SampleWindowSize)
            _recentSamples.RemoveAt(0);

        var drift = Math.Abs(CurrentPpm - BaselinePpm);
        MaxDriftPpm = Math.Max(MaxDriftPpm, drift);

        if (MaxDriftPpm > maxAllowedDrift)
        {
            Status = TestStatus.FailedZeroDrift;
            return;
        }


        if (maxAllowedNoise.HasValue && _recentSamples.Count == SampleWindowSize)
        {
            NoisePpm = CalculateStandardDeviation(_recentSamples);

            if (NoisePpm.Value > maxAllowedNoise.Value)
                Status = TestStatus.FailedNoise;
        }
    }

    public void MarkPassed()
    {
        if (Status != TestStatus.Running)
            return;

        Status = SuccessfulSampleCount > 0
            ? TestStatus.Passed
            : TestStatus.FailedCommunication;
    }

    private static double CalculateStandardDeviation(IReadOnlyCollection<double> values)
    {
        var average = values.Average();
        var variance = values.Sum(value => Math.Pow(value - average, 2)) / values.Count;

        return Math.Sqrt(variance);
    }
}

public sealed class BurnInOptions
{
    public int DeviceCount { get; init; } = 32;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public double MaxAllowedDriftPpm { get; init; } = 3.0;

    public double? MaxAllowedNoisePpm { get; init; }

    public int CommunicationErrorLimit { get; init; } = 5;
    public TimeSpan? TestDuration { get; init; }
}
