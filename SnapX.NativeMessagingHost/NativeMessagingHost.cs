using System.Text;
using System.Buffers.Binary;

namespace SnapX.NativeMessagingHost;

public class NativeMessagingHost
{
    public const int MaximumInputBytes = 64 * 1024 * 1024;

    public string? Read() => Read(Console.OpenStandardInput());

    public string? Read(Stream inputStream)
    {
        Span<byte> bytesLength = stackalloc byte[4];
        int first = inputStream.ReadByte();
        if (first < 0) return null;
        bytesLength[0] = (byte)first;
        inputStream.ReadExactly(bytesLength[1..]);
        int inputLength = BinaryPrimitives.ReadInt32LittleEndian(bytesLength);
        if (inputLength <= 0 || inputLength > MaximumInputBytes)
            throw new InvalidDataException("Native message length must be between 1 byte and 64 MiB.");

        // Validate the browser's untrusted length before allocating memory.
        byte[] bytesInput = new byte[inputLength];
        inputStream.ReadExactly(bytesInput);
        return new UTF8Encoding(false, true).GetString(bytesInput);
    }

    public void Write(string data)
    {
        Stream outputStream = Console.OpenStandardOutput();

        byte[] bytesData = Encoding.UTF8.GetBytes(data);
        byte[] bytesLength = BitConverter.GetBytes(bytesData.Length);

        outputStream.Write(bytesLength, 0, bytesLength.Length);

        if (bytesData.Length > 0)
        {
            outputStream.Write(bytesData, 0, bytesData.Length);
        }

        outputStream.Flush();
    }
}


