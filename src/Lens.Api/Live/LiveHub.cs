using Microsoft.AspNetCore.SignalR;

namespace Lens.Api.Live;

/// <summary>Server-to-browser channel for live detections. Clients only listen; there are no client-invoked methods.</summary>
public sealed class LiveHub : Hub
{
}
