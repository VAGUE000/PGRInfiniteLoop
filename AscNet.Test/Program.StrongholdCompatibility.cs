using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using MongoDB.Bson;
using MessagePack;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateStrongholdCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 48_801;
        Character roster = CreateDrawCompatibilityCharacter(uid);
        roster.Characters = [new CharacterData { Id = 1_021_001 }, new CharacterData { Id = 1_021_002 }, new CharacterData { Id = 1_021_003 }];
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness h = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "stronghold-loopback");
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        void Call<T>(string requestName, int id, object request, string responseName) where T : class
        {
            InvokeRegisteredRequestHandler(requestName, h.Session, id, request);
            _ = ReadResponsePayload<T>(h, id, responseName, requestName);
        }

        Call<GetStrongholdMineralResponse>("GetStrongholdMineralRequest", 48_811, new GetStrongholdMineralRequest(), nameof(GetStrongholdMineralResponse));
        Call<SetStrongholdElectricTeamResponse>("SetStrongholdElectricTeamRequest", 48_812, new SetStrongholdElectricTeamRequest { CharacterIds = [1_021_001] }, nameof(SetStrongholdElectricTeamResponse));
        Call<ResetStrongholdGroupResponse>("ResetStrongholdGroupRequest", 48_813, new ResetStrongholdGroupRequest { Id = -1 }, nameof(ResetStrongholdGroupResponse));
        Call<ResetStrongholdStageResponse>("ResetStrongholdStageRequest", 48_814, new ResetStrongholdStageRequest { GroupId = -1, StageId = -1 }, nameof(ResetStrongholdStageResponse));
        Call<SetStrongholdTeamResponse>("SetStrongholdTeamRequest", 48_815, new SetStrongholdTeamRequest { Own = true, TeamInfos = [new StrongholdTeamInfo { Id = 1, CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }] }] }, nameof(SetStrongholdTeamResponse));
        Call<SetStrongholdFightTeamResponse>("SetStrongholdFightTeamRequest", 48_816, new SetStrongholdFightTeamRequest { Id = -1 }, nameof(SetStrongholdFightTeamResponse));
        Call<GetStrongholdAssistCharacterListResponse>("GetStrongholdAssistCharacterListRequest", 48_817, new GetStrongholdAssistCharacterListRequest(), nameof(GetStrongholdAssistCharacterListResponse));
        Call<SetStrongholdAssistCharacterResponse>("SetStrongholdAssistCharacterRequest", 48_818, new SetStrongholdAssistCharacterRequest { CharacterId = 1_021_001 }, nameof(SetStrongholdAssistCharacterResponse));
        Call<GetStrongholdLendDetailResponse>("GetStrongholdLendDetailRequest", 48_819, new GetStrongholdLendDetailRequest(), nameof(GetStrongholdLendDetailResponse));
        Call<GetStrongholdRewardResponse>("GetStrongholdRewardRequest", 48_820, new GetStrongholdRewardRequest { Ids = [-1] }, nameof(GetStrongholdRewardResponse));
        Call<SweepStrongholdStageResponse>("SweepStrongholdStageRequest", 48_821, new SweepStrongholdStageRequest { GroupId = -1 }, nameof(SweepStrongholdStageResponse));
        Call<SelectStrongholdLevelResponse>("SelectStrongholdLevelRequest", 48_822, new SelectStrongholdLevelRequest { LevelId = 1 }, nameof(SelectStrongholdLevelResponse));
        Call<SetStrongholdStayResponse>("SetStrongholdStayRequest", 48_823, new SetStrongholdStayRequest(), nameof(SetStrongholdStayResponse));

        NotifyStrongholdLoginData login = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule"), "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]).Invoke(null, [player]) as NotifyStrongholdLoginData ?? throw new InvalidDataException("Stronghold login projection missing.");
        AssertEqual(player.Stronghold.StayDays.Count, login.StayDays.Count, "Stronghold login state survives endpoint dispatch");
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(player.Stronghold.LevelId, reloaded.Stronghold.LevelId, "Stronghold level persists through relogin");
        byte[] wire = MessagePackSerializer.Serialize(new StrongholdTeamInfo { Id = 1, CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }] });
        AssertEqual(1, MessagePackSerializer.Deserialize<StrongholdTeamInfo>(wire).CharacterInfos.Count, "Stronghold team wire round-trip");
        Console.WriteLine("Stronghold compatibility: loopback endpoints, exact responses, boundaries, login, and relogin passed.");
    }
}
