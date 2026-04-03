using Microsoft.AspNetCore.SignalR;

namespace AeroStream.Ingestion;

public class TelemetryHub : Hub
{
    // In a real system, you might join drones into "groups" by DeviceId.
    // For now, we'll broadcast to everyone.
    public async Task JoinDroneGroup(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, deviceId);
    }
}