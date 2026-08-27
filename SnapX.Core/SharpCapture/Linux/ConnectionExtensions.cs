using SnapX.Core.SharpCapture.Linux.DBus;

namespace Tmds.DBus;

static class ConnectionExtensions
{
    public static async Task<PortalResponse> Call(this Protocol.DBusConnection connection,
        Func<Task<Protocol.ObjectPath>> request,
        CancellationToken cancel = default)
        => await PortalResponse.WaitAsync(connection, request, cancel);
}
