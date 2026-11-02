using System.IO.Ports;

namespace GasAnalyzer.BurnIn;

public interface ISlotReader
{
    Task<DeviceSample> ReadSlotAsync(byte address, CancellationToken cancellationToken);
}

public sealed class ModbusSlotReader : ISlotReader, IDisposable
{
    private const byte ReadHoldingRegisters = 0x03;
    private const ushort FirstRegister = 0x0000;
    private const ushort RegisterCount = 3;
    private const int ResponseTimeoutMs = 300;

    private readonly SerialPort _port;
    private readonly SemaphoreSlim _busLock = new(1, 1);
    private bool _disposed;

    public ModbusSlotReader(string portName, int baudRate = 19200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = ResponseTimeoutMs,
            WriteTimeout = ResponseTimeoutMs
        };

        _port.Open();
    }

    public async Task<DeviceSample> ReadSlotAsync(
        byte address,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _busLock.WaitAsync(cancellationToken);

        try
        {
            var request = BuildReadRequest(address);
            _port.DiscardInBuffer();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResponseTimeoutMs);

            try
            {
                await _port.BaseStream.WriteAsync(request, timeout.Token);

                var header = new byte[3];
                if (!await ReadExactlyAsync(header, timeout.Token))
                    return FailedSample(address);

                // Modbus exception responses contain function code with bit 7 set.
                if (header[1] == (ReadHoldingRegisters | 0x80))
                {
                    var exceptionAndCrc = new byte[2];
                    await ReadExactlyAsync(exceptionAndCrc, timeout.Token);
                    return FailedSample(address);
                }

                if (header[0] != address ||
                    header[1] != ReadHoldingRegisters ||
                    header[2] != RegisterCount * 2)
                {
                    return FailedSample(address);
                }

                var body = new byte[header[2] + 2];
                if (!await ReadExactlyAsync(body, timeout.Token))
                    return FailedSample(address);

                var response = new byte[header.Length + body.Length];
                Buffer.BlockCopy(header, 0, response, 0, header.Length);
                Buffer.BlockCopy(body, 0, response, header.Length, body.Length);

                if (!HasValidCrc(response))
                    return FailedSample(address);

                return ParseResponse(address, response);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return FailedSample(address);
            }
            catch (IOException)
            {
                return FailedSample(address);
            }
            catch (TimeoutException)
            {
                return FailedSample(address);
            }
        }
        finally
        {
            _busLock.Release();
        }
    }

    private static byte[] BuildReadRequest(byte address)
    {
        var request = new byte[8];

        request[0] = address;
        request[1] = ReadHoldingRegisters;
        request[2] = (byte)(FirstRegister >> 8);
        request[3] = (byte)FirstRegister;
        request[4] = (byte)(RegisterCount >> 8);
        request[5] = (byte)RegisterCount;

        var crc = CalculateCrc(request, 6);
        request[6] = (byte)crc;
        request[7] = (byte)(crc >> 8);

        return request;
    }

    private static DeviceSample ParseResponse(byte address, byte[] response)
    {
        var rawAdc = ReadUInt16(response, 3);
        var rawTemperature = unchecked((short)ReadUInt16(response, 5));
        var rawGas = ReadUInt16(response, 7);

        return new DeviceSample(
            address,
            DateTime.UtcNow,
            rawGas / 10.0,
            rawTemperature / 10.0,
            rawAdc,
            true);
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private async Task<bool> ReadExactlyAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var bytesRead = 0;

        while (bytesRead < buffer.Length)
        {
            var read = await _port.BaseStream.ReadAsync(
                buffer[bytesRead..],
                cancellationToken);

            if (read == 0)
                return false;

            bytesRead += read;
        }

        return true;
    }

    private static bool HasValidCrc(byte[] frame)
    {
        var expected = CalculateCrc(frame, frame.Length - 2);
        var received = (ushort)(frame[^2] | (frame[^1] << 8));

        return expected == received;
    }

    private static ushort CalculateCrc(byte[] data, int length)
    {
        ushort crc = 0xFFFF;

        for (var i = 0; i < length; i++)
        {
            crc ^= data[i];

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private static DeviceSample FailedSample(byte address)
    {
        return new DeviceSample(address, DateTime.UtcNow, 0, 0, 0, false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _port.Dispose();
        _busLock.Dispose();
        _disposed = true;
    }
}
