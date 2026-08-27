using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.bfrt;

namespace AscNet.GameServer.Handlers;

internal static class BfrtModule
{
    private const int Invalid = 20113018;
    private static List<BfrtGroupTable> Groups => TableReaderV2.Parse<BfrtGroupTable>();
    private static BfrtState State(Session session) => session.player.Bfrt;
    private static void Save(Session session) => session.player.Save();
    private static bool Owns(Session session, uint id) => id > 0 && session.character.Characters.Any(character => character.Id == id);
    private static BfrtGroupTable? GroupForStage(uint stageId) => Groups.FirstOrDefault(group => group.BaseStage == stageId || group.StageId.Contains((int)stageId));

    private static NotifyBfrtData.NotifyBfrtDataBfrtData Data(BfrtState state) => new()
    {
        BfrtGroupRecords = state.Groups.Select(group => new BfrtGroupRecord { Id = group.Id, Count = group.Count, IsRecvReward = group.IsRecvReward }).ToList(),
        BfrtTeamInfos = state.Teams.Select(team => new BfrtTeamInfo
        {
            Id = team.Id,
            FightTeamList = team.FightTeamList.Select(row => row.ToList()).ToList(),
            LogisticsTeamList = team.LogisticsTeamList.Select(row => row.ToList()).ToList(),
            CaptainPosList = team.CaptainPosList.ToList(),
            FirstFightPosList = team.FirstFightPosList.ToList()
        }).ToList(),
        BfrtProgressInfo = state.ProgressGroupId > 0 ? new BfrtProgressInfo { GroupId = state.ProgressGroupId, StageIds = state.ProgressStageIds.ToList() } : null,
        CourseRewardStar = state.CourseRewardStar
    };

    internal static NotifyBfrtData BuildLoginData(Player player) => new() { BfrtData = Data(player.Bfrt) };

    internal static bool TryAuthorizePreFight(Player player, uint stageId, out int code)
    {
        code = 0;
        return GroupForStage(stageId) is not null;
    }
    internal static void ReconcileTaskStages(Session session)
    {
        session.stage.Stages ??= new();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool changed = false;
        foreach (BfrtGroupState record in State(session).Groups.Where(group => group.Count > 0))
        {
            BfrtGroupTable? config = Groups.FirstOrDefault(group => group.GroupId == record.Id);
            if (config is null || session.stage.Stages.TryGetValue((uint)config.BaseStage, out StageDatum? stage) && stage.Passed)
                continue;
            session.stage.AddStage(new StageDatum
            {
                StageId = (uint)config.BaseStage,
                Passed = true,
                PassTimesTotal = 1,
                CreateTime = now,
                LastPassTime = now
            });
            changed = true;
        }
        if (changed) session.stage.Save();
    }


    private static void CompleteGroup(Session session, BfrtGroupTable config, BfrtGroupState record)
    {
        record.Count++;
        State(session).ProgressGroupId = 0;
        State(session).ProgressStageIds.Clear();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        session.stage.Stages ??= new();
        StageDatum stage = session.stage.Stages.GetValueOrDefault((uint)config.BaseStage) ?? new StageDatum
        {
            StageId = (uint)config.BaseStage,
            CreateTime = now
        };
        stage.Passed = true;
        stage.PassTimesTotal++;
        stage.LastPassTime = now;
        session.stage.AddStage(stage);
        session.stage.Save();
        Save(session);
        TaskModule.SendStoryTaskSync(session);
    }

    internal static void Settle(Session session, uint stageId, bool win)
    {
        if (!win || GroupForStage(stageId) is not { } config) return;
        BfrtState state = State(session);
        if (state.ProgressGroupId != config.GroupId)
        {
            state.ProgressGroupId = config.GroupId;
            state.ProgressStageIds.Clear();
        }
        if (!state.ProgressStageIds.Contains((int)stageId)) state.ProgressStageIds.Add((int)stageId);
        int[] required = config.StageId.Where(id => id > 0).Distinct().ToArray();
        if (required.All(state.ProgressStageIds.Contains))
        {
            BfrtGroupState record = state.Groups.FirstOrDefault(group => group.Id == config.GroupId) ?? new BfrtGroupState { Id = config.GroupId };
            if (!state.Groups.Contains(record)) state.Groups.Add(record);
            CompleteGroup(session, config, record);
        }
        else
        {
            Save(session);
        }
        session.SendPush(new NotifyBfrtProgressInfo
        {
            BfrtProgressInfo = state.ProgressGroupId > 0 ? new BfrtProgressInfo { GroupId = state.ProgressGroupId, StageIds = state.ProgressStageIds.ToList() } : null
        });
    }

    [RequestPacketHandler("GetBfrtDataRequest")]
    public static void GetData(Session session, Packet.Request packet) =>
        session.SendResponse(new GetBfrtDataResponse { BfrtData = Data(State(session)) }, packet.Id);

    [RequestPacketHandler("BfrtTeamSetRequest")]
    public static void TeamSet(Session session, Packet.Request packet)
    {
        BfrtTeamSetRequest request = packet.Deserialize<BfrtTeamSetRequest>();
        List<List<uint>> fightTeams = request.FightTeam ?? [];
        List<List<uint>> logisticsTeams = request.LogisticsTeam ?? [];
        List<int> captainPositions = request.CaptainPosList ?? [];
        List<int> firstFightPositions = request.FirstFightPosList ?? [];
        bool valid = Groups.Any(group => group.GroupId == request.BfrtGroupId)
            && fightTeams.Count > 0
            && fightTeams.SelectMany(row => row ?? []).Concat(logisticsTeams.SelectMany(row => row ?? [])).Where(id => id > 0).All(id => Owns(session, id))
            && fightTeams.All(row => row is not null && row.Where(id => id > 0).Distinct().Count() == row.Count(id => id > 0) && row.Count <= 3)
            && logisticsTeams.All(row => row is not null && row.Where(id => id > 0).Distinct().Count() == row.Count(id => id > 0) && row.Count <= 3)
            && captainPositions.Count == fightTeams.Count
            && firstFightPositions.Count == fightTeams.Count;
        if (valid)
        {
            BfrtTeamState state = State(session).Teams.FirstOrDefault(team => team.Id == request.BfrtGroupId) ?? new BfrtTeamState { Id = request.BfrtGroupId };
            if (!State(session).Teams.Contains(state)) State(session).Teams.Add(state);
            state.FightTeamList = fightTeams.Select(row => row.ToList()).ToList();
            state.LogisticsTeamList = logisticsTeams.Select(row => row.ToList()).ToList();
            state.CaptainPosList = captainPositions.ToList();
            state.FirstFightPosList = firstFightPositions.ToList();
            Save(session);
        }
        session.SendResponse(new BfrtTeamSetResponse { Code = valid ? 0 : Invalid }, packet.Id);
    }

    [RequestPacketHandler("BfrtOneKeyPassGroupRequest")]
    public static void OneKey(Session session, Packet.Request packet)
    {
        BfrtOneKeyPassGroupRequest request = packet.Deserialize<BfrtOneKeyPassGroupRequest>();
        BfrtChapterTable? chapter = TableReaderV2.Parse<BfrtChapterTable>().FirstOrDefault(row => row.ChapterId == request.BfrtChapterId && row.GroupId.Contains(request.BfrtGroupId));
        BfrtGroupTable? group = Groups.FirstOrDefault(row => row.GroupId == request.BfrtGroupId);
        BfrtTeamState? team = State(session).Teams.FirstOrDefault(value => value.Id == request.BfrtGroupId);
        uint[] characterIds = team?.FightTeamList.Concat(team.LogisticsTeamList).SelectMany(row => row ?? []).Where(id => id > 0).Distinct().ToArray() ?? [];
        int averageAbility = characterIds.Length == 0 ? 0 : (int)characterIds.Average(id => session.character.Characters.First(character => character.Id == id).Ability);
        bool valid = chapter is not null && group is not null && team is not null && averageAbility >= group.NeedPoint;
        BfrtGroupState? record = null;
        if (valid)
        {
            record = State(session).Groups.FirstOrDefault(value => value.Id == request.BfrtGroupId) ?? new BfrtGroupState { Id = request.BfrtGroupId };
            if (!State(session).Groups.Contains(record)) State(session).Groups.Add(record);
            CompleteGroup(session, group!, record);
        }
        session.SendResponse(new BfrtOneKeyPassGroupResponse { Code = valid ? 0 : Invalid, BfrtGroupRecord = valid ? new BfrtGroupRecord { Id = record!.Id, Count = record.Count, IsRecvReward = record.IsRecvReward } : null }, packet.Id);
    }

    [RequestPacketHandler("BfrtResetGroupStageRequest")]
    public static void ResetStage(Session session, Packet.Request packet)
    {
        BfrtResetGroupStageRequest request = packet.Deserialize<BfrtResetGroupStageRequest>();
        BfrtState state = State(session);
        bool valid = request.IsClear || state.ProgressStageIds.Remove(request.BfrtStageId);
        if (request.IsClear) { state.ProgressGroupId = 0; state.ProgressStageIds.Clear(); }
        if (valid) Save(session);
        session.SendResponse(new BfrtResetGroupStageResponse { Code = valid ? 0 : Invalid }, packet.Id);
    }

    [RequestPacketHandler("BfrtReceiveCourseRewardRequest")]
    public static void CourseReward(Session session, Packet.Request packet)
    {
        BfrtState state = State(session);
        int passed = state.Groups.Sum(group => group.Count);
        BfrtCourseRewardTable? row = TableReaderV2.Parse<BfrtCourseRewardTable>()
            .Where(reward => reward.RewardIds > 0 && reward.CourseStars > state.CourseRewardStar && reward.CourseStars <= passed)
            .OrderBy(reward => reward.CourseStars).FirstOrDefault();
        List<RewardGoods> goods = [];
        if (row is not null)
        {
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"bfrt-course:{session.player.PlayerData.Id}:{row.Id}", RewardHandler.GetRewardGoods(row.RewardIds))], session);
            goods = result.RewardGoods;
            state.CourseRewardStar = row.CourseStars;
            Save(session);
            result.SendPushes(session);
        }
        session.SendResponse(new BfrtReceiveCourseRewardResponse { Code = row is null ? Invalid : 0, CourseRewardStar = state.CourseRewardStar, RewardGoodsList = goods }, packet.Id);
    }

    [RequestPacketHandler("BfrtReceiveChapterGroupRewardRequest")]
    public static void ChapterReward(Session session, Packet.Request packet)
    {
        BfrtReceiveChapterGroupRewardRequest request = packet.Deserialize<BfrtReceiveChapterGroupRewardRequest>();
        BfrtChapterTable? chapter = TableReaderV2.Parse<BfrtChapterTable>().FirstOrDefault(row => row.ChapterId == request.BfrtChapterId);
        int index = chapter?.GroupId.IndexOf(request.BfrtGroupId) ?? -1;
        BfrtGroupState? record = State(session).Groups.FirstOrDefault(group => group.Id == request.BfrtGroupId);
        List<RewardGoods> goods = [];
        bool valid = chapter is not null && index >= 0 && index < chapter.RewardIds.Count && record is { Count: > 0, IsRecvReward: false };
        if (valid)
        {
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"bfrt-group:{session.player.PlayerData.Id}:{request.BfrtChapterId}:{request.BfrtGroupId}", RewardHandler.GetRewardGoods(chapter!.RewardIds[index]))], session);
            goods = result.RewardGoods;
            record!.IsRecvReward = true;
            Save(session);
            result.SendPushes(session);
        }
        session.SendResponse(new BfrtReceiveChapterGroupRewardResponse { Code = valid ? 0 : Invalid, RewardGoodsList = goods }, packet.Id);
    }
}
