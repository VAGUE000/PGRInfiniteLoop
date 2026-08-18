using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateAssignCompatibility()
    {
        PacketFactory.LoadPacketHandlers();

        AssertEqual("AssignGetDataRequestHandler", GetRegisteredRequestHandlerMethod("AssignGetDataRequest").Name, "AssignGetDataRequest registered handler method");
        AssertEqual("AssignSetTeamRequestHandler", GetRegisteredRequestHandlerMethod("AssignSetTeamRequest").Name, "AssignSetTeamRequest registered handler method");
        AssertEqual("AssignSetCharacterRequestHandler", GetRegisteredRequestHandlerMethod("AssignSetCharacterRequest").Name, "AssignSetCharacterRequest registered handler method");
        AssertEqual("AssignResetStageRequestHandler", GetRegisteredRequestHandlerMethod("AssignResetStageRequest").Name, "AssignResetStageRequest registered handler method");
        AssertEqual("AssignGetRewardRequestHandler", GetRegisteredRequestHandlerMethod("AssignGetRewardRequest").Name, "AssignGetRewardRequest registered handler method");
        AssertMailNamedMapKeys(typeof(AssignGetDataResponse), null, ["AssignInfo"], "AssignGetDataResponse");
        AssertMailNamedMapKeys(typeof(AssignInfo), null, ["ChapterRecords", "GroupRecords", "GroupTeamRecords"], "AssignInfo");
        AssertMailNamedMapKeys(typeof(AssignGroupInfo), null, ["GroupId", "Count", "IsPerfect", "FinishStageIds"], "Assign group record");
        AssertMailNamedMapKeys(typeof(AssignGroupTeamInfo), null, ["GroupId", "TeamInfoList", "CaptainPosList", "FirstFightPosList"], "Assign team record");
        AssertMailNamedMapKeys(typeof(AssignSetTeamRequest), null, ["GroupId", "TeamList", "FirstFightPosList", "CaptainPosList"], "AssignSetTeamRequest");
        AssertMailNamedMapKeys(typeof(AssignResetStageRequest), null, ["GroupId", "StageId"], "AssignResetStageRequest");
        AssertMailNamedMapKeys(typeof(AssignGetRewardResponse), null, ["Code", "RewardList"], "AssignGetRewardResponse");


        const long playerId = 88_101;
        Character character = CreateDrawCompatibilityCharacter(playerId);
        character.Characters.AddRange(
        [
            new CharacterData { Id = 1_531_005, Level = 80, Ability = 6_500 },
            new CharacterData { Id = 1_011_004, Level = 80, Ability = 6_500 },
            new CharacterData { Id = 1_021_005, Level = 80, Ability = 6_500 },
            new CharacterData { Id = 1_321_003, Level = 80, Ability = 6_500 }
        ]);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Player player = CreateDrawCompatibilityPlayer(playerId);

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _))
        using (LoopbackSessionHarness harness = new(character, player, inventory, "assign-compat-test"))
        {
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
            AssignInfo initial = AssignInfoPayload(harness, 88_100);
            AssertEqual(0, initial.ChapterRecords.Count, "fresh Border Pact has no completed chapter records");
            AssertEqual(24, initial.GroupRecords.Count, "all Border Pact groups are initialized");
            AssertEqual(101, initial.GroupRecords[0].GroupId, "first Border Pact group id");
            AssertEqual(803, initial.GroupRecords[^1].GroupId, "final Border Pact group id");
            AssertEqual(0, initial.GroupTeamRecords.Count, "fresh Border Pact has no saved team records");

            List<List<long>> firstGroupTeam = [[1_531_005, 1_011_004], [1_021_005], [1_321_003]];
            InvokeRegisteredRequestHandler("AssignSetTeamRequest", harness.Session, 88_102, new AssignSetTeamRequest
            {
                GroupId = 101,
                FirstFightPosList = [1, 1, 1],
                TeamList = firstGroupTeam,
                CaptainPosList = [1, 1, 1]
            });
            AssertEqual(0, ReadResponsePayload<AssignSetTeamResponse>(harness, 88_102, "AssignSetTeamResponse", "AssignSetTeamResponse").Code, "AssignSetTeamResponse code");

            AssignInfo saved = AssignInfoPayload(harness, 88_103);
            AssignGroupTeamInfo savedTeam = saved.GroupTeamRecords.Single(record => record.GroupId == 101);
            AssertEqual(true, savedTeam.TeamInfoList.SelectMany(team => team).SequenceEqual(firstGroupTeam.SelectMany(team => team)), "AssignGetDataResponse returns TeamInfoList using the client contract");

            byte[] savedState = harness.Session.player.ToBson();
            InvokeRegisteredRequestHandler("AssignSetTeamRequest", harness.Session, 88_104, new AssignSetTeamRequest
            {
                GroupId = 101,
                FirstFightPosList = [1, 1, 1],
                TeamList = [[99_999_999, 1_011_004], [1_021_005], [1_321_003]],
                CaptainPosList = [1, 1, 1]
            });
            AssertEqual(true, ReadResponsePayload<AssignSetTeamResponse>(harness, 88_104, "AssignSetTeamResponse", "AssignSetTeamResponse").Code != 0, "unowned Assign member rejected");
            AssertEqual(Convert.ToHexString(savedState), Convert.ToHexString(harness.Session.player.ToBson()), "invalid Assign team does not mutate player");

            PreFightRequest mismatch = new() { PreFightData = new() { StageId = 14_010_111, CardIds = [1_011_004, 1_531_005], CaptainPos = 1, FirstFightPos = 1 } };
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 88_105, mismatch);
            AssertEqual(true, ReadResponsePayload<PreFightResponse>(harness, 88_105, nameof(PreFightResponse), "Assign mismatched pre-fight").Code != 0, "Border Pact rejects a team different from the saved formation");
            AssertEqual(null, harness.Session.fight, "rejected Border Pact pre-fight does not start a fight");

            PreFightRequest preFight = new() { PreFightData = new() { StageId = 14_010_111, CardIds = [1_531_005, 1_011_004], CaptainPos = 1, FirstFightPos = 1 } };
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 88_106, preFight);
            PreFightResponse preFightResponse = ReadResponsePayload<PreFightResponse>(harness, 88_106, nameof(PreFightResponse), "Assign saved-team pre-fight");
            AssertEqual(0, preFightResponse.Code, "Border Pact sub-stage pre-fight code");

            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, 88_107, CreateMissingStageSettleRequest(14_010_111, preFightResponse.FightData.FightId, playerId));
            FightSettleResponse settle = ReadAssignSettle(harness, 88_107);
            AssertEqual(0, settle.Code, "Border Pact sub-stage settle code");
            AssignGroupInfo progressed = AssignInfoPayload(harness, 88_108).GroupRecords.Single(record => record.GroupId == 101);
            AssertEqual(true, progressed.FinishStageIds.SequenceEqual([14_010_111]), "Border Pact persists the completed sub-stage");
            AssertEqual(0, progressed.Count, "Border Pact group remains incomplete after its first sub-stage");

            InvokeRegisteredRequestHandler("AssignResetStageRequest", harness.Session, 88_109, new AssignResetStageRequest { GroupId = 101, StageId = 14_010_111 });
            AssertEqual(0, ReadResponsePayload<AssignResetStageResponse>(harness, 88_109, "AssignResetStageResponse", "AssignResetStageResponse").Code, "AssignResetStageResponse code");
            AssertEqual(0, AssignInfoPayload(harness, 88_110).GroupRecords.Single(record => record.GroupId == 101).FinishStageIds.Count, "Border Pact reset removes stage progress");

            AssignGroupState firstGroup = harness.Session.player.Assign.Groups.Single(group => group.GroupId == 101);
            firstGroup.Count = 1;
            firstGroup.IsPerfect = true;
            harness.Session.player.Assign.Groups.AddRange(
            [
                new AssignGroupState { GroupId = 102, Count = 1, IsPerfect = true },
                new AssignGroupState { GroupId = 103, Count = 1, IsPerfect = true }
            ]);
            InvokeRegisteredRequestHandler("AssignSetCharacterRequest", harness.Session, 88_111, new AssignSetCharacterRequest { ChapterId = 2_001, CharacterId = 1_531_005 });
            AssertEqual(0, ReadResponsePayload<AssignSetCharacterResponse>(harness, 88_111, "AssignSetCharacterResponse", "AssignSetCharacterResponse").Code, "completed Border Pact chapter accepts an occupying character");
            List<dynamic> loginRecords = InvokeAssignLoginRecords(harness.Session);
            AssertEqual(1, loginRecords.Count, "NotifyLogin includes only completed Border Pact chapters");
            AssertEqual(2_001, Convert.ToInt32(loginRecords[0].ChapterId), "NotifyLogin completed Border Pact chapter id");
        }
    }

    private static AssignInfo AssignInfoPayload(LoopbackSessionHarness harness, int packetId)
    {
        InvokeRegisteredRequestHandler("AssignGetDataRequest", harness.Session, packetId, new AssignGetDataRequest());
        AssignGetDataResponse response = ReadResponsePayload<AssignGetDataResponse>(harness, packetId, "AssignGetDataResponse", "AssignGetDataResponse");
        if (response.AssignInfo.GroupRecords is null || response.AssignInfo.GroupTeamRecords is null)
            throw new InvalidDataException("AssignGetDataResponse must contain GroupRecords and GroupTeamRecords.");
        return response.AssignInfo;
    }

    private static List<dynamic> InvokeAssignLoginRecords(Session session)
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AssignModule");
        return (List<dynamic>)(RequiredMethod(module, "BuildLoginChapterRecords", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [session])
            ?? throw new InvalidDataException("AssignModule.BuildLoginChapterRecords returned nil."));
    }

    private static FightSettleResponse ReadAssignSettle(LoopbackSessionHarness harness, int packetId)
    {
        for (int index = 0; index < 8; index++)
        {
            Packet packet = harness.ReadPacket("Assign settle packet");
            if (packet.Type == Packet.ContentType.Push)
                continue;
            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
            AssertEqual(packetId, response.Id, "Assign settle response id");
            AssertEqual(nameof(FightSettleResponse), response.Name, "Assign settle response name");
            return MessagePackSerializer.Deserialize<FightSettleResponse>(response.Content);
        }
        throw new InvalidDataException("Assign settle response was not emitted.");
    }
}
