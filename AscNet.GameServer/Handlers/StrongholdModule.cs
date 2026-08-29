using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.stronghold;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers;

internal static class StrongholdModule
{
    private const int Invalid = 20113018;
    private static List<T> Rows<T>() where T : class, AscNet.Common.Util.ITable => TableReaderV2.Parse<T>();
    private static StrongholdState State(Session s) => s.player.Stronghold;
    private static void Save(Session s) => s.player.Save();
    private static bool Own(Session s, int id) => id > 0 && s.character.Characters.Any(c => c.Id == (uint)id);
    private static int First(string? value) =>
        int.TryParse((value ?? string.Empty).Trim('[', ']').Split('|')[0], out int result) ? result : 0;
    private static int Config(string key) => Rows<StrongholdCfgTable>().FirstOrDefault(row => row.Key == key)?.Value ?? 0;
    private static int Pick(string candidates, long playerId, int groupId, int index)
    {
        int[] values = candidates.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out int parsed) ? parsed : 0).Where(value => value > 0).ToArray();
        if (values.Length == 0) return 0;
        long seed = unchecked(playerId * 397L + groupId * 31L + index);
        return values[(int)((ulong)seed % (uint)values.Length)];
    }


    internal static NotifyStrongholdLoginData BuildLoginData(Player p)
    {
        StrongholdState state = p.Stronghold;
        Normalize(state);
        return new()
        {
            Id = state.ActivityId,
            BeginTime = state.BeginTime,
            FightBeginTime = state.FightBeginTime,
            CurDay = state.CurDay,
            AssistCharacterId = state.AssistCharacterId,
            SetAssistCharacterTime = state.SetAssistCharacterTime,
            BorrowCount = state.BorrowCount,
            ElectricEnergy = (uint)Math.Max(0, state.ElectricEnergy),
            Endurance = state.Endurance,
            MineralLeft = state.MineralLeft,
            TotalMineral = state.TotalMineral,
            ElectricCharacterIds = state.ElectricCharacterIds.ToList(),
            FinishGroupIds = state.FinishGroupIds.ToList(),
            FinishGroupInfos = state.FinishGroupInfos.ToList(),
            HistoryFinishGroupInfos = state.HistoryFinishGroupInfos.ToList(),
            GroupInfos = state.GroupInfos.ToList(),
            TeamInfos = state.TeamInfos.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList(),
            GroupStageDatas = state.GroupStageDatas.ToList(),
            RuneList = state.RuneList.ToList(),
            RewardIds = state.RewardIds.ToList(),
            LastResultRecord = state.LastResultRecord,
            MineRecords = state.MineRecords.ToList(),
            LevelId = state.LevelId,
            StayDays = state.StayDays.ToList()
        };
    }

    private static void Normalize(StrongholdState state)
    {
        state.ElectricCharacterIds ??= [];
        state.FinishGroupIds ??= [];
        state.FinishGroupInfos ??= [];
        state.HistoryFinishGroupInfos ??= [];
        state.GroupInfos ??= [];
        state.GroupStageDatas ??= [];
        state.TeamInfos ??= [];
        state.FightTeamInfos ??= [];
        state.RuneList ??= [];
        state.RewardIds ??= [];
        state.StayDays ??= [];
        state.MineRecords ??= [];
        state.ClaimedRewardIds ??= [];
        state.LastResultRecord ??= new();
        foreach (StrongholdGroupInfo group in state.GroupInfos)
            group.FinishStageIds ??= [];
        foreach (StrongholdGroupStageData group in state.GroupStageDatas)
        {
            group.StageIds ??= [];
            group.StageBuffId ??= [];
        }
        foreach (StrongholdTeamInfo team in state.TeamInfos.Values)
        {
            team.CharacterInfos ??= [];
            team.PluginInfos ??= [];
        }
        foreach (List<StrongholdTeamInfo> teams in state.FightTeamInfos.Values)
        foreach (StrongholdTeamInfo team in teams ?? [])
        {
            team.CharacterInfos ??= [];
            team.PluginInfos ??= [];
        }
    }


    internal static void PrepareLogin(Player p)
    {
        StrongholdState state = p.Stronghold;
        Normalize(state);
        StrongholdActivityTable? activity = Rows<StrongholdActivityTable>().OrderByDescending(row => row.Id).FirstOrDefault();
        StrongholdLevelTable? level = Rows<StrongholdLevelTable>()
            .Where(row => p.PlayerData.Level >= row.MinLevel && p.PlayerData.Level <= row.MaxLevel)
            .OrderBy(row => row.Id).FirstOrDefault();
        if (state.ActivityId > 0 && Rows<StrongholdLevelTable>().Any(row => row.Id == state.LevelId))
            return;
        if (activity is null || level is null)
            return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        state.ActivityId = state.ActivityId > 0 ? state.ActivityId : activity.Id;
        state.BeginTime = state.BeginTime > 0 ? state.BeginTime : checked((uint)now);
        state.FightBeginTime = state.FightBeginTime > 0 ? state.FightBeginTime : checked((int)Math.Min(now, int.MaxValue));
        state.LevelId = level.Id;
        state.ElectricEnergy = level.InitElectricEnergy;
        state.Endurance = level.InitEndurance;
        state.CurDay = 0;
        var chapters = Rows<StrongholdChapterTable>().ToDictionary(row => row.Id);
        var groups = Rows<StrongholdGroupTable>().ToDictionary(row => row.Id);
        state.GroupInfos.Clear();
        state.GroupStageDatas.Clear();
        foreach (int chapterId in level.Chapter.Where(id => id > 0))
        {
            if (!chapters.TryGetValue(chapterId, out var chapter))
                continue;
            foreach (int groupId in chapter.GroupId.Where(id => id > 0))
            {
                if (!groups.TryGetValue(groupId, out var group))
                    continue;
                List<uint> stageIds = group.StageIdGroup
                    .Select((candidates, index) => Pick(candidates, p.PlayerData.Id, groupId, index))
                    .Where(id => id > 0).Distinct().Select(id => (uint)id).ToList();
                if (stageIds.Count == 0)
                    continue;
                state.GroupInfos.Add(new() { Id = groupId });
                state.GroupStageDatas.Add(new() { Id = groupId, StageIds = stageIds, SupportId = First(group.SupportId) });
            }
        }
        p.Save();
    }

    internal static StrongholdFightResult Settle(Player p, bool win, Session session)
    {
        StrongholdState state = p.Stronghold;
        StrongholdFightResult result = new();
        if (state.PendingGroupId <= 0 || state.PendingStageId <= 0) return result;

        int groupId = state.PendingGroupId;
        int stageId = state.PendingStageId;
        StrongholdGroupInfo group = state.GroupInfos.FirstOrDefault(value => value.Id == groupId)
            ?? new StrongholdGroupInfo { Id = groupId };
        if (!state.GroupInfos.Contains(group)) state.GroupInfos.Add(group);
        state.Endurance = Math.Max(0, state.Endurance - 1);
        if (!win)
        {
            state.PendingGroupId = state.PendingStageId = 0;
            p.Save();
            result.GroupFightResultInfos.Add(new() { GroupId = groupId });
            return result;
        }

        if (!group.FinishStageIds.Contains(stageId)) group.FinishStageIds.Add(stageId);
        int next = Next(state, groupId);
        if (next != 0)
        {
            state.PendingStageId = next;
            result.AllFinished = false;
        }
        else
        {
            if (!state.FinishGroupIds.Contains(groupId)) state.FinishGroupIds.Add(groupId);
            state.PendingGroupId = state.PendingStageId = 0;
            result.AllFinished = state.GroupStageDatas.Count > 0
                && state.GroupStageDatas.All(value =>
                    state.GroupInfos.FirstOrDefault(done => done.Id == value.Id)?.FinishStageIds.Count
                    >= value.StageIds.Count);
            StrongholdGroupTable? row = Rows<StrongholdGroupTable>().FirstOrDefault(value => value.Id == groupId);
            int rewardId = row?.RewardId.Select(value => value is int id ? id : 0).FirstOrDefault(id => id > 0) ?? 0;
            if (rewardId > 0)
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
                RewardApplicationResult grant = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"stronghold:{p.PlayerData.Id}:{state.ActivityId}:{groupId}", goods)], session);
                result.GroupFightResultInfos.Add(new() { GroupId = groupId, RewardGoodsList = grant.RewardGoods });
                grant.SendPushes(session);
            }
        }
        if (result.GroupFightResultInfos.Count == 0)
            result.GroupFightResultInfos.Add(new() { GroupId = groupId });
        p.Save();
        return result;
    }

    private static int Next(StrongholdState x,int id) { var stages=x.GroupStageDatas.FirstOrDefault(g=>g.Id==id)?.StageIds; var done=x.GroupInfos.FirstOrDefault(g=>g.Id==id)?.FinishStageIds??[]; return stages?.Select(v=>(int)v).FirstOrDefault(v=>!done.Contains(v))??0; }

    internal static bool TryAuthorizePreFight(Player p, uint stageId, out int code)
    {
        code=0; StrongholdState x=p.Stronghold;
        if (x.PendingGroupId<=0 || x.PendingStageId!=(int)stageId) return false;
        if (x.GroupStageDatas.FirstOrDefault(g=>g.Id==x.PendingGroupId)?.StageIds.Contains(stageId)!=true) { code=Invalid; return true; }
        if (x.Endurance<=0) { code=20113054; return true; }
        return true;
    }

    [RequestPacketHandler("GetStrongholdMineralRequest")]
    public static void GetMineral(Session s, Packet.Request p)
    {
        StrongholdState state = State(s);
        int mineral = state.MineralLeft;
        state.MineralLeft = 0;
        Save(s);
        s.SendResponse(new GetStrongholdMineralResponse { Code = mineral > 0 ? 0 : Invalid, MineralCount = mineral }, p.Id);
    }

    [RequestPacketHandler("SetStrongholdElectricTeamRequest")]
    public static void SetElectric(Session s, Packet.Request p)
    {
        SetStrongholdElectricTeamRequest request = p.Deserialize<SetStrongholdElectricTeamRequest>();
        if (request.CharacterIds.Count > Config("MaxElectricTeamMemberCount")
            || request.CharacterIds.Distinct().Count() != request.CharacterIds.Count
            || request.CharacterIds.Any(id => !Own(s, id)))
        {
            s.SendResponse(new SetStrongholdElectricTeamResponse { Code = 20113014 }, p.Id);
            return;
        }
        State(s).ElectricCharacterIds = request.CharacterIds.ToList();
        Save(s);
        s.SendResponse(new SetStrongholdElectricTeamResponse(), p.Id);
    }

    [RequestPacketHandler("ResetStrongholdGroupRequest")]
    public static void ResetGroup(Session s, Packet.Request p)
    {
        ResetStrongholdGroupRequest request = p.Deserialize<ResetStrongholdGroupRequest>();
        StrongholdGroupInfo? group = State(s).GroupInfos.FirstOrDefault(value => value.Id == request.Id);
        if (group is null)
        {
            s.SendResponse(new ResetStrongholdGroupResponse { Code = Invalid }, p.Id);
            return;
        }
        group.FinishStageIds.Clear();
        Save(s);
        s.SendResponse(new ResetStrongholdGroupResponse(), p.Id);
    }

    [RequestPacketHandler("ResetStrongholdStageRequest")]
    public static void ResetStage(Session s, Packet.Request p)
    {
        ResetStrongholdStageRequest request = p.Deserialize<ResetStrongholdStageRequest>();
        StrongholdGroupInfo? group = State(s).GroupInfos.FirstOrDefault(value => value.Id == request.GroupId);
        int code = group is not null && group.FinishStageIds.Remove(request.StageId) ? 0 : 20113021;
        if (code == 0) Save(s);
        s.SendResponse(new ResetStrongholdStageResponse { Code = code }, p.Id);
    }
    [RequestPacketHandler("SetStrongholdFightTeamRequest")]
    public static void SetFightTeam(Session s, Packet.Request p)
    {
        SetStrongholdFightTeamRequest request = p.Deserialize<SetStrongholdFightTeamRequest>();
        StrongholdState state = State(s);
        bool valid = request.Id > 0
            && Next(state, request.Id) > 0
            && request.TeamInfos.Count > 0
            && request.TeamInfos.Count <= Config("MaxPreTeamCount")
            && request.TeamInfos.Select(team => team.Id).Distinct().Count() == request.TeamInfos.Count
            && request.TeamInfos.All(team => team.CharacterInfos.Count > 0
                && team.CharacterInfos.Count <= 3
                && team.CharacterInfos.Select(character => character.Id).Distinct().Count() == team.CharacterInfos.Count
                && team.CharacterInfos.All(character => Own(s, character.Id)));
        if (valid)
        {
            state.FightTeamInfos[request.Id] = request.TeamInfos;
            state.PendingGroupId = request.Id;
            state.PendingStageId = Next(state, request.Id);
            Save(s);
        }
        s.SendResponse(new SetStrongholdFightTeamResponse { Code = valid ? 0 : 20113030 }, p.Id);
    }
    [RequestPacketHandler("SetStrongholdTeamRequest")]
    public static void SetTeam(Session s, Packet.Request p)
    {
        SetStrongholdTeamRequest request = p.Deserialize<SetStrongholdTeamRequest>();
        bool valid = request.TeamInfos.Count > 0
            && request.TeamInfos.Count <= Config("MaxPreTeamCount")
            && request.TeamInfos.Select(team => team.Id).Distinct().Count() == request.TeamInfos.Count
            && request.TeamInfos.All(team => team.CharacterInfos.Count <= 3
                && team.CharacterInfos.Select(character => character.Id).Distinct().Count() == team.CharacterInfos.Count
                && team.CharacterInfos.All(character => Own(s, character.Id)));
        if (valid)
        {
            State(s).TeamInfos = request.TeamInfos.ToDictionary(team => team.Id);
            Save(s);
        }
        s.SendResponse(new SetStrongholdTeamResponse { Code = valid ? 0 : 20113023 }, p.Id);
    }
    [RequestPacketHandler("GetStrongholdAssistCharacterListRequest")]
    public static void AssistList(Session s,Packet.Request p){s.SendResponse(new GetStrongholdAssistCharacterListResponse{Code=0,CharacterDetails=[]},p.Id);}
    [RequestPacketHandler("SetStrongholdAssistCharacterRequest")]
    public static void Assist(Session s,Packet.Request p){var r=p.Deserialize<SetStrongholdAssistCharacterRequest>();bool ok=r.CharacterId==0||Own(s,r.CharacterId);if(ok){var x=State(s);x.AssistCharacterId=r.CharacterId;x.SetAssistCharacterTime=(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();Save(s);}s.SendResponse(new SetStrongholdAssistCharacterResponse{Code=ok?0:20113003},p.Id);}
    [RequestPacketHandler("GetStrongholdLendDetailRequest")]
    public static void Lend(Session s,Packet.Request p)=>s.SendResponse(new GetStrongholdLendDetailResponse{Code=0,LendDayInfos=[]},p.Id);
    [RequestPacketHandler("GetStrongholdRewardRequest")]
    public static void Reward(Session s,Packet.Request p){var r=p.Deserialize<GetStrongholdRewardRequest>();var x=State(s);var rows=Rows<StrongholdRewardTable>();var ids=r.Ids.Distinct().Where(id=>!x.ClaimedRewardIds.Contains(id)).ToList();var goods=ids.SelectMany(id=>rows.FirstOrDefault(v=>v.Id==id) is {RewardId:int reward} ? RewardHandler.GetRewardGoods(reward):[]).ToList();if(goods.Count==0){s.SendResponse(new GetStrongholdRewardResponse{Code=Invalid},p.Id);return;}RewardHandler.ApplyRewardsOnceAndPersist(ids.Select(id=>new RewardGrant($"stronghold:{s.player.PlayerData.Id}:{id}",rows.First(v=>v.Id==id).RewardId is int q?RewardHandler.GetRewardGoods(q):[])).ToList(),s);x.ClaimedRewardIds.AddRange(ids);Save(s);s.SendResponse(new GetStrongholdRewardResponse{Code=0,SuccessIds=ids,RewardGoodsList=goods.Select(g=>new RewardGoods{Id=g.Id,TemplateId=g.TemplateId,Count=g.Count}).ToList()},p.Id);}
    [RequestPacketHandler("SweepStrongholdStageRequest")]
    public static void Sweep(Session s, Packet.Request p)
    {
        SweepStrongholdStageRequest request = p.Deserialize<SweepStrongholdStageRequest>();
        StrongholdState state = State(s);
        if (Next(state, request.GroupId) == 0 || state.Endurance <= 0)
        {
            s.SendResponse(new SweepStrongholdStageResponse { Code = 20113054 }, p.Id);
            return;
        }
        state.PendingGroupId = request.GroupId;
        state.PendingStageId = Next(state, request.GroupId);
        StrongholdFightResult result = Settle(s.player, true, s);
        s.SendResponse(new SweepStrongholdStageResponse { Code = 0, StrongholdFightResult = result }, p.Id);
    }
    [RequestPacketHandler("SelectStrongholdLevelRequest")]
    public static void SelectLevel(Session s,Packet.Request p){var r=p.Deserialize<SelectStrongholdLevelRequest>();var x=State(s);var level=Rows<StrongholdLevelTable>().FirstOrDefault(v=>v.Id==r.LevelId&&(s.player.PlayerData.Level>=v.MinLevel&&s.player.PlayerData.Level<=v.MaxLevel));if(x.LevelId!=0||level is null){s.SendResponse(new SelectStrongholdLevelResponse{Code=20113056},p.Id);return;}x.LevelId=r.LevelId;x.Endurance=level.InitEndurance;x.ElectricEnergy=level.InitElectricEnergy;var chapters=Rows<StrongholdChapterTable>().ToDictionary(v=>v.Id);var groups=Rows<StrongholdGroupTable>().ToDictionary(v=>v.Id);x.GroupInfos.Clear();x.GroupStageDatas.Clear();foreach(int cid in level.Chapter.Where(v=>v>0))if(chapters.TryGetValue(cid,out var c))foreach(int gid in c.GroupId.Where(v=>v>0))if(groups.TryGetValue(gid,out var g)){var ids=g.StageIdGroup.Select((candidates,index)=>Pick(candidates,s.player.PlayerData.Id,gid,index)).Where(v=>v>0).Distinct().Select(v=>(uint)v).ToList();if(ids.Count>0){x.GroupInfos.Add(new(){Id=gid});x.GroupStageDatas.Add(new(){Id=gid,StageIds=ids,SupportId=First(g.SupportId)});}}Save(s);s.SendResponse(new SelectStrongholdLevelResponse{Code=0,ElectricEnergy=x.ElectricEnergy,Endurance=x.Endurance,GroupStageDatas=x.GroupStageDatas},p.Id);}
    // ponytail: server-only distribution rules are unavailable; use the typed eligible stage groups, deterministic per player, and persist the result.
    [RequestPacketHandler("SetStrongholdStayRequest")]
    public static void Stay(Session s,Packet.Request p){var x=State(s);int d=++x.CurDay;if(!x.StayDays.Contains(d))x.StayDays.Add(d);Save(s);s.SendResponse(new SetStrongholdStayResponse{Code=0,StayDays=x.StayDays},p.Id);}
}
