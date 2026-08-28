using System.Buffers.Binary;
using System.Text;

namespace SnapX.NativeMessagingHost;

public class NativeMessagingHost
{
    // Native messaging is a trusted-extension boundary, not an unbounded IPC channel.
    // This still permits substantial text and data-URL payloads while preventing a
    // malformed length prefix from forcing an arbitrary allocation.
    public const int MaximumMessageSize = 32 * 1024 * 1024;

    public string? Read() => Read(Console.OpenStandardInput());

    public static string? Read(Stream inputStream)
    {
        ArgumentNullException.ThrowIfNull(inputStream);

        byte[] bytesLength = new byte[4];
        inputStream.ReadExactly(bytesLength);
        int inputLength = BinaryPrimitives.ReadInt32LittleEndian(bytesLength);

        if (inputLength < 0 || inputLength > MaximumMessageSize)
        {
            throw new InvalidDataException(
                $"Native messaging payload length {inputLength} is outside the allowed range.");
        }

        if (inputLength == 0)
        {
            return null;
        }

        byte[] bytesInput = GC.AllocateUninitializedArray<byte>(inputLength);
        inputStream.ReadExactly(bytesInput);
        return new UTF8Encoding(false, true).GetString(bytesInput);
    }

    public void Write(string data) => Write(Console.OpenStandardOutput(), data);

    public static void Write(Stream outputStream, string data)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(data);

        byte[] bytesData = new UTF8Encoding(false, true).GetBytes(data);
        if (bytesData.Length > MaximumMessageSize)
        {
            throw new InvalidDataException(
                $"Native messaging payload length {bytesData.Length} exceeds the allowed maximum.");
        }

        byte[] bytesLength = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytesLength, bytesData.Length);

        outputStream.Write(bytesLength, 0, bytesLength.Length);

        if (bytesData.Length > 0)
        {
            outputStream.Write(bytesData, 0, bytesData.Length);
        }

        outputStream.Flush();
    }
}

