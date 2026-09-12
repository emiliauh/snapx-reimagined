using System.Buffers.Binary;
using System.Text;
using SnapX.NativeMessagingHost;

var host = new NativeMessagingHost();
var random = new Random(23063);
int checks = 0;
void Check(bool value) { checks++; if (!value) throw new Exception($"Check {checks} failed"); }
byte[] Frame(byte[] bytes)
{
    byte[] result = new byte[bytes.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(result, bytes.Length);
    bytes.CopyTo(result, 4);
    return result;
}
void Reject(byte[] bytes)
{
    try { host.Read(new MemoryStream(bytes)); throw new Exception("Malformed frame accepted"); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException) { checks++; }
}
Check(host.Read(new MemoryStream()) is null);
foreach (int length in new[] { int.MinValue, -1, 0, NativeMessagingHost.MaximumInputBytes + 1, int.MaxValue })
{
    byte[] header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, length);
    Reject(header);
}
Reject(Frame(new byte[] { 0xff, 0xfe }));
for (int i = 0; i < 10000; i++)
{
    string value = "{\"text\":\"Unicode 🦊 日本語 \\" + random.Next() + new string('x', random.Next(1, 512)) + "\"}";
    byte[] frame = Frame(Encoding.UTF8.GetBytes(value));
    Check(host.Read(new FragmentedStream(frame)) == value);
    int cut = random.Next(1, frame.Length);
    Reject(frame[..cut]);
}
Console.WriteLine($"PASS: {checks:N0} native messaging framing checks; seed=23063. No GUI launches or network requests.");

// Pipes may deliver fewer bytes than requested even when more remain.
sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 3)]);
}
