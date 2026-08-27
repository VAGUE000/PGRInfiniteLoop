using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.fuben.bfrt;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateBfrtCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 48_902;
        Character roster = CreateDrawCompatibilityCharacter(uid);
        roster.Characters = [new CharacterData { Id = 1_021_001, Ability = int.MaxValue }, new CharacterData { Id = 1_021_002, Ability = int.MaxValue }, new CharacterData { Id = 1_021_003, Ability = int.MaxValue }];
        Player player = CreateDrawCompatibilityPlayer(uid);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out _);
        using LoopbackSessionHarness h = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "bfrt-loopback");
        h.Session.stage = new Stage { Uid = uid, Stages = new() };
        BfrtChapterTable chapter = TableReaderV2.Parse<BfrtChapterTable>().First();
        var row = TableReaderV2.Parse<BfrtGroupTable>().Single(group => group.GroupId == chapter.GroupId.Last());
        int group = row.GroupId, stage = row.StageId.First();
        void Call<T>(string name, int id, object request, string response) where T : class
        { InvokeRegisteredRequestHandler(name, h.Session, id, request); _ = ReadResponsePayload<T>(h, id, response, name); }
        Call<GetBfrtDataResponse>("GetBfrtDataRequest", 49_001, new GetBfrtDataRequest(), nameof(GetBfrtDataResponse));
        Call<BfrtTeamSetResponse>("BfrtTeamSetRequest", 49_002, new BfrtTeamSetRequest { BfrtGroupId = group, FightTeam = [[1_021_001, 1_021_002]], LogisticsTeam = null!, CaptainPosList = [1], FirstFightPosList = [1] }, nameof(BfrtTeamSetResponse));
        Call<BfrtTeamSetResponse>("BfrtTeamSetRequest", 49_003, new BfrtTeamSetRequest { BfrtGroupId = group, FightTeam = [[999]], CaptainPosList = [1], FirstFightPosList = [1] }, nameof(BfrtTeamSetResponse));
        InvokeRegisteredRequestHandler(nameof(BfrtOneKeyPassGroupRequest), h.Session, 49_004,
            new BfrtOneKeyPassGroupRequest { BfrtChapterId = chapter.ChapterId, BfrtGroupId = group });
        _ = ReadPushPayload<NotifyTask>(h, nameof(NotifyTask), "Bfrt quick-clear task progress");
        BfrtOneKeyPassGroupResponse quickClear = ReadResponsePayload<BfrtOneKeyPassGroupResponse>(
            h, 49_004, nameof(BfrtOneKeyPassGroupResponse), "Bfrt quick-clear response");
        AssertEqual(0, quickClear.Code, "Bfrt quick-clear code");
        AssertEqual(1, quickClear.BfrtGroupRecord?.Count ?? 0, "Bfrt quick-clear group count");
        AssertEqual(true, h.Session.stage.Stages.TryGetValue((uint)row.BaseStage, out StageDatum? clearedStage)
            && clearedStage.Passed, "Bfrt quick-clear tracks chapter task stage");
        h.Session.stage.Stages.Remove((uint)row.BaseStage);
        RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BfrtModule"),
            "ReconcileTaskStages", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
            .Invoke(null, [h.Session]);
        AssertEqual(true, h.Session.stage.Stages.TryGetValue((uint)row.BaseStage, out clearedStage)
            && clearedStage.Passed, "Bfrt login repairs existing chapter task progress");
        player.Bfrt.ProgressGroupId = group;
        player.Bfrt.ProgressStageIds = [stage];
        Call<BfrtResetGroupStageResponse>("BfrtResetGroupStageRequest", 49_005, new BfrtResetGroupStageRequest { BfrtStageId = stage }, nameof(BfrtResetGroupStageResponse));
        Call<BfrtReceiveCourseRewardResponse>("BfrtReceiveCourseRewardRequest", 49_006, new BfrtReceiveCourseRewardRequest(), nameof(BfrtReceiveCourseRewardResponse));
        Call<BfrtReceiveChapterGroupRewardResponse>("BfrtReceiveChapterGroupRewardRequest", 49_007, new BfrtReceiveChapterGroupRewardRequest { BfrtChapterId = -1, BfrtGroupId = -1 }, nameof(BfrtReceiveChapterGroupRewardResponse));
        MethodInfo authorize = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BfrtModule"),
            "TryAuthorizePreFight", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player), typeof(uint), typeof(int).MakeByRefType()]);
        object?[] authorizeArgs = [player, (uint)stage, 0];
        AssertEqual(true, (bool)authorize.Invoke(null, authorizeArgs)!, "Bfrt configured stage is authorized");
        AssertEqual(0, (int)authorizeArgs[2]!, "Bfrt configured stage authorization code");
        object?[] baseStageArgs = [player, (uint)row.BaseStage, 0];
        AssertEqual(true, (bool)authorize.Invoke(null, baseStageArgs)!, "Bfrt base mission stage is authorized");
        AssertEqual(0, (int)baseStageArgs[2]!, "Bfrt base mission authorization code");
        NotifyBfrtData login = BfrtModuleLogin(player);
        AssertEqual(1, login.BfrtData.BfrtTeamInfos.Count, "Bfrt team survives login projection");
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(player.Bfrt.Teams.Count, reloaded.Bfrt.Teams.Count, "Bfrt state survives relogin");
        byte[] wire = MessagePackSerializer.Serialize(login);
        _ = MessagePackSerializer.Deserialize<NotifyBfrtData>(wire);
        Console.WriteLine("Bfrt compatibility: endpoints, validation, one-key/reset, login, and relogin passed.");
    }
    private static NotifyBfrtData BfrtModuleLogin(Player player) =>
        (NotifyBfrtData)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BfrtModule"), "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]).Invoke(null, [player])!;
}
