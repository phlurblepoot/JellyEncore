namespace Encore.Tests.Jellyfin;

/// <summary>Sample JSON copied from the T5 spike (.knowledge/encore/api.md) plus the Finamp queue shapes from T4 (clients.md).</summary>
internal static class JellyfinFixtures
{
    /// <summary>Jellyfin Web session playing song A with a two-song queue, one Encore session with no now-playing data.</summary>
    public const string Sessions = """
    [{"Id":"e3a7f0c2b1d94a5e8f6c7b2a1d0e9f8c","UserId":"4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f","UserName":"nathan","Client":"Jellyfin Web","DeviceName":"Firefox","DeviceId":"abc123","ApplicationVersion":"12.4.0","IsActive":true,"SupportsRemoteControl":true,"SupportsMediaControl":true,"LastActivityDate":"2026-11-13T18:00:00.0000000Z","PlayableMediaTypes":["Audio","Video"],"SupportedCommands":["DisplayMessage","SetRepeatMode","SetShuffleQueue"],"NowPlayingItem":{"Id":"9d8c7b6a5f4e3d2c1b0a998877665544","Name":"Song A","Type":"Audio","RunTimeTicks":2100000000},"NowPlayingQueue":[{"Id":"9d8c7b6a5f4e3d2c1b0a998877665544","PlaylistItemId":"playlistItem0"},{"Id":"1122334455667788990aabbccddeeff0","PlaylistItemId":"playlistItem1"}],"PlaylistItemId":"playlistItem0","PlayState":{"PositionTicks":123450000,"CanSeek":true,"IsPaused":false,"IsMuted":false,"VolumeLevel":80,"PlayMethod":"DirectPlay","RepeatMode":"RepeatAll","PlaybackOrder":"Shuffle","MediaSourceId":"9d8c7b6a5f4e3d2c1b0a998877665544"}},{"Id":"f00dbabe00000000000000000000beef","UserId":"00000000-0000-0000-0000-000000000000","Client":"JellyEncore","DeviceName":"QBStream","DeviceId":"jellyencore-1","IsActive":true,"SupportsRemoteControl":false,"SupportsMediaControl":false,"PlayableMediaTypes":[],"SupportedCommands":[],"NowPlayingQueue":[],"PlayState":{"CanSeek":false,"IsPaused":false,"IsMuted":false,"RepeatMode":"RepeatNone","PlaybackOrder":"Default"}}]
    """;

    /// <summary>Finamp at the last song: its upcoming queue is an empty list, not null.</summary>
    public const string FinampLastSong = """
    [{"Id":"aaaa0000bbbb1111cccc2222dddd3333","UserId":"4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f","UserName":"nathan","Client":"Finamp","DeviceName":"Pixel","DeviceId":"fin-1","IsActive":true,"SupportsRemoteControl":true,"NowPlayingItem":{"Id":"9d8c7b6a5f4e3d2c1b0a998877665544","Name":"Song Z","Type":"Audio","RunTimeTicks":1800000000,"AlbumId":"11112222333344445555666677778888","Album":"Album One","ArtistItems":[{"Id":"abcdefabcdefabcdefabcdefabcdefab","Name":"Artist One"}],"Genres":["Rock"]},"NowPlayingQueue":[],"PlaylistItemId":"11112222333344445555666677778888","PlayState":{"IsPaused":false,"RepeatMode":"RepeatNone","PlaybackOrder":"Default"}}]
    """;

    /// <summary>Finamp mid-queue: one upcoming entry, with the queue type name as its PlaylistItemId.</summary>
    public const string FinampMidQueue = """
    [{"Id":"aaaa0000bbbb1111cccc2222dddd3333","Client":"Finamp","NowPlayingQueue":[{"Id":"1122334455667788990aabbccddeeff0","PlaylistItemId":"queue"}],"PlaylistItemId":"11112222333344445555666677778888","PlayState":{"IsPaused":false,"RepeatMode":"RepeatNone","PlaybackOrder":"Default"}}]
    """;

    /// <summary>Session with no queue field at all: null, which means "no information".</summary>
    public const string SessionWithoutQueue = """
    [{"Id":"bbbb0000","UserId":"4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f","Client":"Some Client","IsActive":true}]
    """;

    /// <summary>A repeat mode and playback order this build does not know. They must not break the poll.</summary>
    public const string FutureEnumValues = """
    [{"Id":"cccc0000","UserId":"4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f","Client":"Future Client","PlayState":{"IsPaused":true,"RepeatMode":"RepeatEverythingForever","PlaybackOrder":42}}]
    """;

    public const string Users = """
    [{"Name":"nathan","Id":"4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f","ServerId":"x","HasPassword":true,"Policy":{"IsAdministrator":true,"IsDisabled":false,"IsHidden":false},"LastActivityDate":"2026-11-13T18:00:00.0000000Z"},{"Name":"Kid","Id":"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d","Policy":{"IsAdministrator":false,"IsDisabled":true,"IsHidden":false}},{"Name":"Dup","Id":"11111111-2222-4333-8444-555555555555"},{"Name":"DUP","Id":"66666666-7777-4888-8999-000000000000"}]
    """;

    public const string SimilarArtists = """
    {"Items":[{"Id":"abcdefabcdefabcdefabcdefabcdefab","Name":"Artist Two","Type":"MusicArtist"}],"TotalRecordCount":1}
    """;

    public const string Songs = """
    {"Items":[{"Id":"9d8c7b6a5f4e3d2c1b0a998877665544","Name":"Song A","Type":"Audio","RunTimeTicks":2100000000,"AlbumId":"11112222333344445555666677778888","Album":"Album One","ArtistItems":[{"Id":"abcdefabcdefabcdefabcdefabcdefab","Name":"Artist Two"}],"AlbumArtists":[{"Id":"abcdefabcdefabcdefabcdefabcdefab","Name":"Artist Two"}],"Genres":["Rock","Indie"]}],"TotalRecordCount":1}
    """;
}
