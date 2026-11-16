using Encore.Jellyfin;
using Encore.Trigger;

namespace Encore.Orchestration;

/// <summary>Maps the Jellyfin session DTO onto the trigger's input, so the trigger stays independent of the wire format.</summary>
internal static class SessionMapping
{
    public static TriggerSession ToTrigger(SessionInfoDto dto) => new()
    {
        Id = dto.Id,
        UserId = dto.UserId == Guid.Empty ? null : dto.UserId,
        UserName = dto.UserName,
        Client = dto.Client,
        DeviceId = dto.DeviceId,
        SupportsRemoteControl = dto.SupportsRemoteControl,
        NowPlayingItem = dto.NowPlayingItem is { } item ? new NowPlaying(item.Id, item.Type, item.Name, item.RunTimeTicks) : null,
        NowPlayingQueue = dto.NowPlayingQueue?.Select(q => new QueueEntry(q.Id, q.PlaylistItemId)).ToList() ?? [],
        PlaylistItemId = dto.PlaylistItemId,
        // Only the two repeat modes that stop the queue matter. Unknown or missing values count as RepeatNone.
        RepeatMode = dto.PlayState?.RepeatMode is JellyfinRepeatMode.RepeatAll or JellyfinRepeatMode.RepeatOne
            ? dto.PlayState.RepeatMode.ToString()
            : null,
        IsPaused = dto.PlayState?.IsPaused ?? false,
        PositionTicks = dto.PlayState?.PositionTicks,
        PlaybackOrder = dto.PlayState?.PlaybackOrder.ToString(),
    };
}
