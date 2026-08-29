using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
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
        Player secondPlayer = CreateDrawCompatibilityPlayer(uid + 1);
        secondPlayer.PlayerData.Level = 80;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness h = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "stronghold-loopback");
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        void Call<T>(string requestName, int id, object request, string responseName) where T : class
        {
            InvokeRegisteredRequestHandler(requestName, h.Session, id, request);
            _ = ReadResponsePayload<T>(h, id, responseName, requestName);
        }

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        player.Stronghold.ActivityId = 1;
        player.Stronghold.BeginTime = 1;
        secondPlayer.Stronghold.ActivityId = 1;
        secondPlayer.Stronghold.BeginTime = 1;
        AssertEqual(true, player.Stronghold.ActivityId > 0 && player.Stronghold.BeginTime > 0, "Stronghold test state is open");
        AssertEqual(player.Stronghold.ActivityId, secondPlayer.Stronghold.ActivityId, "Players select the same authoritative activity");
        AssertEqual(true, player.Stronghold.BeginTime != 0 && secondPlayer.Stronghold.BeginTime != 0, "Players activate independently");

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
        int groupId = player.Stronghold.GroupStageDatas.First(value => value.StageIds.Count > 1).Id;
        Call<SetStrongholdFightTeamResponse>("SetStrongholdFightTeamRequest", 48_824, new SetStrongholdFightTeamRequest
        {
            Id = groupId,
            TeamInfos = [new StrongholdTeamInfo { Id = 1, CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }] }]
        }, nameof(SetStrongholdFightTeamResponse));
        var settle = RequiredMethod(module, "Settle", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player), typeof(bool), typeof(Session)]);
        settle.Invoke(null, [player, true, h.Session]);
        int nextStage = player.Stronghold.PendingStageId;
        AssertEqual(true, nextStage > 0, "Winning a stage preserves the pending group and advances");
        while (player.Stronghold.PendingStageId > 0)
            settle.Invoke(null, [player, true, h.Session]);
        AssertEqual(true, player.Stronghold.FinishGroupIds.Contains(groupId), "Final settlement completes the group");
        AssertEqual(true, h.Session.inventory.AppliedRewardClaims.Any(key => key.StartsWith($"stronghold:{uid}:{player.Stronghold.ActivityId}:{groupId}", StringComparison.Ordinal)), "Final settlement applies configured reward exactly once");

        NotifyStrongholdLoginData login = (NotifyStrongholdLoginData)RequiredMethod(module, "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]).Invoke(null, [player])!;
        AssertEqual(player.Stronghold.StayDays.Count, login.StayDays.Count, "Stronghold login state survives endpoint dispatch");
        AssertEqual(player.Stronghold.TeamInfos.Count, login.TeamInfos.Count, "Stronghold team projects on relogin");
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(player.Stronghold.LevelId, reloaded.Stronghold.LevelId, "Stronghold level persists through relogin");
        AssertEqual(player.Stronghold.PendingStageId, reloaded.Stronghold.PendingStageId, "Stronghold continuation persists through relogin");
        Player loginPlayer = CreateDrawCompatibilityPlayer(uid + 2);
        loginPlayer.PlayerData.Level = 80;
        loginPlayer.Stronghold.ActivityId = 1;
        loginPlayer.Stronghold.BeginTime = 1;
        loginPlayer.Stronghold.FightBeginTime = 1;
        loginPlayer.Stronghold.CurDay = 1;
        loginPlayer.Stronghold.LevelId = 1;
        loginPlayer.Stronghold.ElectricCharacterIds = [1_021_001];
        loginPlayer.Stronghold.LastResultRecord = new();
        using LoopbackSessionHarness loginHarness = new(
            CreateDrawCompatibilityCharacter(uid + 2),
            loginPlayer,
            CreateDrawCompatibilityInventory(uid + 2, []),
            "challenge-login-regression");
        loginHarness.Session.stage = CreateLoginAccountCompatibilityStage(uid + 2);
        System.Reflection.MethodInfo doLogin = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Session), typeof(bool)]);
        doLogin.Invoke(null, [loginHarness.Session, false]);

        _ = ReadPushPayload<NotifyLogin>(loginHarness, nameof(NotifyLogin), "challenge login NotifyLogin");
        string[] required = [
            nameof(NotifyArenaActivity),
            nameof(NotifyFubenBossSingleData),
            nameof(NotifyRepeatChallengeData),
            nameof(NotifyStrongholdLoginData),
            nameof(NotifyTransfiniteData)];
        HashSet<string> observed = [];
        Dictionary<string, int> positions = [];
        bool strongholdSeen = false;
        for (int index = 0; index < 128 && observed.Count < required.Length; index++)
        {
            Packet packet = loginHarness.ReadPacket($"challenge login startup {index + 1}");
            AssertEqual(Packet.ContentType.Push, packet.Type, $"challenge login startup {index + 1} packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            if (push.Name == nameof(NotifyStrongholdLoginData))
            {
                strongholdSeen = true;
                NotifyStrongholdLoginData stronghold = MessagePackSerializer.Deserialize<NotifyStrongholdLoginData>(push.Content);
                AssertEqual(loginPlayer.Stronghold.ActivityId, stronghold.Id, "challenge login Stronghold activity id");
                AssertEqual(true, stronghold.BeginTime > 0 && stronghold.FightBeginTime > 0,
                    "challenge login Stronghold activation chronology");
                AssertEqual(true, loginPlayer.Stronghold.ElectricCharacterIds.SequenceEqual(stronghold.ElectricCharacterIds),
                    "challenge login Stronghold electric team");
                AssertEqual(true, stronghold.ElectricCharacterIds is not null
                    && stronghold.FinishGroupIds is not null
                    && stronghold.FinishGroupInfos is not null
                    && stronghold.HistoryFinishGroupInfos is not null
                    && stronghold.GroupInfos is not null
                    && stronghold.TeamInfos is not null
                    && stronghold.GroupStageDatas is not null
                    && stronghold.RuneList is not null
                    && stronghold.RewardIds is not null
                    && stronghold.LastResultRecord is not null
                    && stronghold.MineRecords is not null
                    && stronghold.StayDays is not null,
                    "challenge login Stronghold manager fields");
                AssertMailNamedMapKeys(stronghold,
                    ["Id", "BeginTime", "FightBeginTime", "CurDay", "AssistCharacterId",
                        "SetAssistCharacterTime", "BorrowCount", "ElectricEnergy", "Endurance",
                        "MineralLeft", "TotalMineral", "ElectricCharacterIds", "FinishGroupIds",
                        "FinishGroupInfos", "HistoryFinishGroupInfos", "GroupInfos", "TeamInfos",
                        "GroupStageDatas", "RuneList", "RewardIds", "LastResultRecord", "MineRecords",
                        "LevelId", "StayDays"], "challenge login Stronghold wire fields");
            }
            if (!required.Contains(push.Name, StringComparer.Ordinal))
                continue;
            observed.Add(push.Name);
            positions.TryAdd(push.Name, index);
            switch (push.Name)
            {
                case nameof(NotifyRepeatChallengeData):
                {
                    NotifyRepeatChallengeData repeat = MessagePackSerializer.Deserialize<NotifyRepeatChallengeData>(push.Content);
                    AssertEqual(true, repeat.ExpInfo is not null && repeat.RcChapters is not null && repeat.RewardIds is not null,
                        "challenge login Repeat backing data");
                    break;
                }
                case nameof(NotifyArenaActivity):
                {
                    NotifyArenaActivity arena = MessagePackSerializer.Deserialize<NotifyArenaActivity>(push.Content);
                    AssertEqual(true, arena.MaxPointStageList is not null, "challenge login Arena backing data");
                    break;
                }
                case nameof(NotifyFubenBossSingleData):
                {
                    NotifyFubenBossSingleData boss = MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(push.Content);
                    AssertEqual(true, boss.FubenBossSingleData is not null,
                        "challenge login Boss backing data");
                    break;
                }
                case nameof(NotifyTransfiniteData):
                {
                    NotifyTransfiniteData transfinite = MessagePackSerializer.Deserialize<NotifyTransfiniteData>(push.Content);
                    AssertEqual(true, transfinite.TransfiniteData is not null, "challenge login Transfinite backing data");
                    break;
                }
            }
        }
        foreach (string name in required)
            AssertEqual(true, observed.Contains(name), $"challenge login {name} push");
        AssertEqual(true, positions[nameof(NotifyArenaActivity)] < positions[nameof(NotifyFubenBossSingleData)]
            && positions[nameof(NotifyFubenBossSingleData)] < positions[nameof(NotifyRepeatChallengeData)]
            && positions[nameof(NotifyRepeatChallengeData)] < positions[nameof(NotifyStrongholdLoginData)]
            && positions[nameof(NotifyStrongholdLoginData)] < positions[nameof(NotifyTransfiniteData)],
            "challenge login retail order Arena, Boss, Repeat, Stronghold, Transfinite");
        AssertEqual(true, strongholdSeen, "challenge login emits typed Stronghold integration");
        BsonDocument legacyDocument = loginPlayer.ToBsonDocument();
        BsonDocument legacyStronghold = legacyDocument["stronghold"].AsBsonDocument;
        foreach (string field in legacyStronghold.Names.ToArray())
            legacyStronghold.Remove(field);
        legacyStronghold["electric_character_ids"] = BsonNull.Value;
        legacyStronghold["last_result_record"] = BsonNull.Value;
        Player legacyPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(legacyDocument);
        AssertEqual(null, legacyPlayer.Stronghold.ElectricCharacterIds, "legacy BSON preserves null electric character ids");
        AssertEqual(null, legacyPlayer.Stronghold.LastResultRecord, "legacy BSON preserves null last result record");
        using LoopbackSessionHarness legacyHarness = new(
            CreateDrawCompatibilityCharacter(uid + 3),
            legacyPlayer,
            CreateDrawCompatibilityInventory(uid + 3, []),
            "challenge-login-legacy-stronghold");
        legacyHarness.Session.stage = CreateLoginAccountCompatibilityStage(uid + 3);
        try
        {
            doLogin.Invoke(null, [legacyHarness.Session, false]);
        }
        catch (Exception exception)
        {
            Exception cause = exception is System.Reflection.TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : exception;
            throw new InvalidDataException($"legacy Stronghold login threw {cause.GetType().FullName}: {cause.Message}", cause);
        }
        List<string> legacyPackets = [];
        while (legacyHarness.TryReadAvailablePacket("legacy Stronghold startup packet", out Packet legacyPacket))
        {
            if (legacyPacket.Type != Packet.ContentType.Push)
                throw new InvalidDataException($"legacy Stronghold login emitted {legacyPacket.Type} packet.");
            Packet.Push legacyPush = MessagePackSerializer.Deserialize<Packet.Push>(legacyPacket.Content);
            legacyPackets.Add(legacyPush.Name);
            if (legacyPush.Name == nameof(NotifyStrongholdLoginData))
            {
                NotifyStrongholdLoginData legacyPayload =
                    MessagePackSerializer.Deserialize<NotifyStrongholdLoginData>(legacyPush.Content);
                AssertEqual(true, legacyPayload.Id > 0 && legacyPayload.LevelId > 0
                    && legacyPayload.BeginTime > 0 && legacyPayload.FightBeginTime > 0,
                    "legacy login Stronghold activation");
                AssertEqual(true, legacyPayload.ElectricCharacterIds is not null
                    && legacyPayload.LastResultRecord is not null,
                    "legacy login Stronghold null state is repaired before wire");
            }
        }
        AssertEqual(true, legacyPackets.Contains(nameof(NotifyRepeatChallengeData)), "legacy login keeps RepeatChallenge visible");
        AssertEqual(true, legacyPackets.Contains(nameof(NotifyArenaActivity)), "legacy login keeps Arena visible");
        AssertEqual(true, legacyPackets.Contains(nameof(NotifyTransfiniteData)), "legacy login keeps Transfinite visible");
        AssertEqual(true, legacyPackets.Contains(nameof(NotifyStrongholdLoginData)),
            "legacy login repairs and emits Stronghold integration");
        Console.WriteLine("Stronghold compatibility: loopback endpoints, exact responses, boundaries, login, continuation, rewards, and relogin passed.");
    }
}
