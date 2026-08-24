using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    /// <summary>Ordered favorite song ids (most-recent first); capped by MusicPlayerConfig FavoriteSongMaxCount.</summary>
    [BsonElement("favorite_songs")]
    public List<int> FavoriteSongs { get; set; } = new();

    /// <summary>Ordered background song ids; always contains the configured default, capped by BackgroundSongMaxCount.</summary>
    [BsonElement("background_songs")]
    public List<int> BackgroundSongs { get; set; } = new();
}
