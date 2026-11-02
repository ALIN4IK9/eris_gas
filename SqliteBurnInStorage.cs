using Microsoft.Data.Sqlite;

namespace GasAnalyzer.BurnIn;

public sealed class SqliteBurnInStorage
{
    private readonly string _connectionString;

    public SqliteBurnInStorage(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS Telemetry
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Address INTEGER NOT NULL,
                Timestamp TEXT NOT NULL,
                Concentration REAL NOT NULL,
                Temperature REAL NOT NULL,
                Adc INTEGER NOT NULL,
                IsOk INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_telemetry_session
                ON Telemetry (SessionId, Address);
            """;

        command.ExecuteNonQuery();
    }

    public async Task SaveBatchAsync(
        string sessionId,
        IEnumerable<DeviceSample> samples,
        CancellationToken cancellationToken = default)
    {
        var batch = samples.ToList();
        if (batch.Count == 0)
            return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Telemetry
                (SessionId, Address, Timestamp, Concentration, Temperature, Adc, IsOk)
            VALUES
                ($session, $address, $timestamp, $concentration, $temperature, $adc, $ok);
            """;

        var sessionParameter = command.Parameters.Add("$session", SqliteType.Text);
        var addressParameter = command.Parameters.Add("$address", SqliteType.Integer);
        var timestampParameter = command.Parameters.Add("$timestamp", SqliteType.Text);
        var concentrationParameter = command.Parameters.Add("$concentration", SqliteType.Real);
        var temperatureParameter = command.Parameters.Add("$temperature", SqliteType.Real);
        var adcParameter = command.Parameters.Add("$adc", SqliteType.Integer);
        var okParameter = command.Parameters.Add("$ok", SqliteType.Integer);

        foreach (var sample in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            sessionParameter.Value = sessionId;
            addressParameter.Value = sample.ModbusAddress;
            timestampParameter.Value = sample.Timestamp.ToUniversalTime().ToString("O");
            concentrationParameter.Value = sample.ConcentrationPpm;
            temperatureParameter.Value = sample.Temperature;
            adcParameter.Value = sample.RawAdc;
            okParameter.Value = sample.ResponseOk ? 1 : 0;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
