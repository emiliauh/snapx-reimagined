# Native messaging framing checks

Run `dotnet run --project tests/SnapX.NativeMessaging.Fuzz` from the repository root.

The deterministic seed 23063 checks 10,000 valid Unicode frames delivered in
partial reads, 10,000 truncated frames, invalid UTF-8, clean EOF, and invalid or
oversized lengths. It does not start SnapX, modify the clipboard, or contact a
browser or upload service. The reader rejects lengths outside 1 byte–64 MiB
before allocating the payload.
