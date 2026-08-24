using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.miniactivity.musicplayer;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    /// <summary>
    /// AudioPlayer (music CD) favorites and background playlist. State is durable per-player
    /// (ordered lists), mutated then saved before a Code=0 response; no pushes. Table-driven
    /// caps, default song, and valid song ids come from the authoritative MusicPlayer tables.
    /// </summary>
    internal static class AudioPlayerModule
    {
        private const int SuccessCode = 0;
        private const int ErrorCode = 1; // retail failure code unobserved; any non-zero signals failure

        // MusicPlayerConfig rows are singletons keyed by name.
        private static readonly Lazy<IReadOnlyDictionary<string, int>> ConfigByName = new(() =>
            TableReaderV2.Parse<MusicPlayerConfigTable>()
                .ToDictionary(row => row.Key, row => row.Values));

        // Ungated songs only: non-zero ConditionId marks event/milestone-scene-gated songs with
        // no ownership source here, so they are excluded (never silently granted).
        private static readonly Lazy<HashSet<int>> ValidSongIds = new(() =>
            new HashSet<int>(TableReaderV2.Parse<MusicPlayerAlbumTable>()
                .Where(row => row.ConditionId is null || row.ConditionId == 0)
                .Select(row => row.Id)));

        private static int Config(string key)
            => ConfigByName.Value.TryGetValue(key, out int value) ? value : 0;

        private static int FavoriteMaxCount => Config("FavoriteSongMaxCount");
        private static int BackgroundMaxCount => Config("BackgroundSongMaxCount");

        /// <summary>Default background song; also serves as the implicit owned baseline.</summary>
        internal static int DefaultBackgroundSongId
        {
            get
            {
                int id = Config("DefaultBackgroundSongId");
                // Fall back to a stable default if the table is missing so login never breaks.
                return ValidSongIds.Value.Contains(id) ? id : 1;
            }
        }

        /// <summary>True when the id is an ungated album song (or the default). Scene-gated songs
        /// (non-zero ConditionId) are excluded because no ownership source exists here.</summary>
        internal static bool IsValidSongId(int songId)
            => ValidSongIds.Value.Contains(songId) || songId == DefaultBackgroundSongId;

        /// <summary>Builds the durable login payload from player state, seeding the default background song.</summary>
        internal static AudioPlayerLoginData BuildLoginData(Player player)
        {
            List<int> background = player.BackgroundSongs ?? new();
            int defaultId = DefaultBackgroundSongId;
            if (!background.Contains(defaultId))
            {
                background = background.Prepend(defaultId).ToList();
                player.BackgroundSongs = background;
            }
            return new AudioPlayerLoginData
            {
                FavoriteSongs = player.FavoriteSongs ?? new(),
                BackgroundSongs = background
            };
        }
        private static bool TrySave(Player player, List<int> songs, List<int> previous)
        {
            try
            {
                player.SaveChecked();
                return true;
            }
            catch
            {
                songs.Clear();
                songs.AddRange(previous);
                return false;
            }
        }

        [RequestPacketHandler("AddAudioPlayerFavoriteSongRequest")]
        public static void AddFavoriteSong(Session session, Packet.Request packet)
        {
            var request = MessagePackSerializer.Deserialize<AddAudioPlayerFavoriteSongRequest>(packet.Content);
            var response = new AddAudioPlayerFavoriteSongResponse();
            if (!IsValidSongId(request.SongId))
            {
                response.Code = ErrorCode;
                session.SendResponse(response, packet.Id);
                return;
            }
            List<int> songs = session.player.FavoriteSongs ??= new();
            if (songs.Contains(request.SongId))
            {
                // Retail duplicate semantics unobserved; conservatively idempotent no-op success.
                response.Code = SuccessCode;
                session.SendResponse(response, packet.Id);
                return;
            }
            List<int> previous = songs.ToList();
            songs.Insert(0, request.SongId);
            if (FavoriteMaxCount > 0 && songs.Count > FavoriteMaxCount)
                songs.RemoveRange(FavoriteMaxCount, songs.Count - FavoriteMaxCount);
            response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("RemoveAudioPlayerFavoriteSongRequest")]
        public static void RemoveFavoriteSong(Session session, Packet.Request packet)
        {
            var request = MessagePackSerializer.Deserialize<RemoveAudioPlayerFavoriteSongRequest>(packet.Content);
            var response = new RemoveAudioPlayerFavoriteSongResponse();
            List<int> songs = session.player.FavoriteSongs ??= new();
            List<int> previous = songs.ToList();
            if (songs.Remove(request.SongId))
                response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("AddAudioPlayerBackgroundSongRequest")]
        public static void AddBackgroundSongs(Session session, Packet.Request packet)
        {
            var request = MessagePackSerializer.Deserialize<AddAudioPlayerBackgroundSongRequest>(packet.Content);
            var response = new AddAudioPlayerBackgroundSongResponse();
            List<int> songs = session.player.BackgroundSongs ??= new();
            List<int> previous = songs.ToList();
            bool mutated = false;
            int max = BackgroundMaxCount;
            foreach (int songId in request.SongIds)
            {
                if (!IsValidSongId(songId) || songs.Contains(songId))
                    continue;
                songs.Insert(0, songId);
                mutated = true;
                if (max > 0 && songs.Count > max)
                    songs.RemoveRange(max, songs.Count - max);
            }
            if (mutated)
                response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("RemoveAudioPlayerBackgroundSongRequest")]
        public static void RemoveBackgroundSong(Session session, Packet.Request packet)
        {
            var request = MessagePackSerializer.Deserialize<RemoveAudioPlayerBackgroundSongRequest>(packet.Content);
            var response = new RemoveAudioPlayerBackgroundSongResponse();
            List<int> songs = session.player.BackgroundSongs ??= new();
            List<int> previous = songs.ToList();
            _ = songs.Remove(request.SongId);
            if (songs.Count == 0)
                songs.Add(DefaultBackgroundSongId);
            if (!songs.SequenceEqual(previous))
                response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("ResetAudioPlayerBackgroundSongRequest")]
        public static void ResetBackgroundSongs(Session session, Packet.Request packet)
        {
            MessagePackSerializer.Deserialize<ResetAudioPlayerBackgroundSongRequest>(packet.Content);
            var response = new ResetAudioPlayerBackgroundSongResponse();
            List<int> songs = session.player.BackgroundSongs ??= new();
            List<int> previous = songs.ToList();
            songs.Clear();
            songs.Add(DefaultBackgroundSongId);
            response.Code = songs.SequenceEqual(previous) || TrySave(session.player, songs, previous)
                ? SuccessCode
                : ErrorCode;
            response.BackgroundSongs = new List<int>(songs);
            session.SendResponse(response, packet.Id);
        }
    }
}
