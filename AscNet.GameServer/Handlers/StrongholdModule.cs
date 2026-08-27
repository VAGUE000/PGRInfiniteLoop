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
        StrongholdState x = p.Stronghold;
        return new() { Id=x.ActivityId, BeginTime=x.BeginTime, FightBeginTime=x.FightBeginTime, CurDay=x.CurDay,
            AssistCharacterId=x.AssistCharacterId, SetAssistCharacterTime=x.SetAssistCharacterTime, BorrowCount=x.BorrowCount,
            ElectricEnergy=(uint)Math.Max(0,x.ElectricEnergy), Endurance=x.Endurance, MineralLeft=x.MineralLeft, TotalMineral=x.TotalMineral,
            ElectricCharacterIds=x.ElectricCharacterIds.ToList(), FinishGroupIds=x.FinishGroupIds.ToList(), FinishGroupInfos=x.FinishGroupInfos.ToList(),
            HistoryFinishGroupInfos=x.HistoryFinishGroupInfos.ToList(), GroupInfos=x.GroupInfos.ToList(), GroupStageDatas=x.GroupStageDatas.ToList(),
            RuneList=x.RuneList.ToList(), RewardIds=x.RewardIds.ToList(), LastResultRecord=x.LastResultRecord, MineRecords=x.MineRecords.ToList(), LevelId=x.LevelId, StayDays=x.StayDays.ToList() };
    }

    internal static bool TryAuthorizePreFight(Player p, uint stageId, out int code)
    {
        code=0; StrongholdState x=p.Stronghold;
        if (x.PendingGroupId<=0 || x.PendingStageId!=(int)stageId) return false;
        if (x.GroupStageDatas.FirstOrDefault(g=>g.Id==x.PendingGroupId)?.StageIds.Contains(stageId)!=true) { code=Invalid; return true; }
        if (x.Endurance<=0) { code=20113054; return true; }
        return true;
    }
    internal static void Settle(Player p, bool win)
    {
        StrongholdState x=p.Stronghold; if (x.PendingGroupId<=0) return;
        if (win) { var g=x.GroupInfos.FirstOrDefault(v=>v.Id==x.PendingGroupId); if(g is null){g=new(){Id=x.PendingGroupId};x.GroupInfos.Add(g);} if(!g.FinishStageIds.Contains(x.PendingStageId))g.FinishStageIds.Add(x.PendingStageId); x.Endurance=Math.Max(0,x.Endurance-1); }
        x.PendingGroupId=x.PendingStageId=0; p.Save();
    }
    private static int Next(StrongholdState x,int id) { var stages=x.GroupStageDatas.FirstOrDefault(g=>g.Id==id)?.StageIds; var done=x.GroupInfos.FirstOrDefault(g=>g.Id==id)?.FinishStageIds??[]; return stages?.Select(v=>(int)v).FirstOrDefault(v=>!done.Contains(v))??0; }

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
    [RequestPacketHandler("SetStrongholdFightTeamRequest")]
    public static void SetFightTeam(Session s,Packet.Request p){var r=p.Deserialize<SetStrongholdFightTeamRequest>();bool ok=r.Id>0&&r.TeamInfos.Count>0&&r.TeamInfos.All(t=>t.CharacterInfos.Count<=3&&t.CharacterInfos.All(c=>Own(s,c.Id)));var x=State(s);if(ok){x.FightTeamInfos[r.Id]=r.TeamInfos;x.PendingGroupId=r.Id;x.PendingStageId=Next(x,r.Id);Save(s);}s.SendResponse(new SetStrongholdFightTeamResponse{Code=ok?0:20113030},p.Id);}
    [RequestPacketHandler("GetStrongholdAssistCharacterListRequest")]
    public static void AssistList(Session s,Packet.Request p){s.SendResponse(new GetStrongholdAssistCharacterListResponse{Code=0,CharacterDetails=[]},p.Id);}
    [RequestPacketHandler("SetStrongholdAssistCharacterRequest")]
    public static void Assist(Session s,Packet.Request p){var r=p.Deserialize<SetStrongholdAssistCharacterRequest>();bool ok=r.CharacterId==0||Own(s,r.CharacterId);if(ok){var x=State(s);x.AssistCharacterId=r.CharacterId;x.SetAssistCharacterTime=(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();Save(s);}s.SendResponse(new SetStrongholdAssistCharacterResponse{Code=ok?0:20113003},p.Id);}
    [RequestPacketHandler("GetStrongholdLendDetailRequest")]
    public static void Lend(Session s,Packet.Request p)=>s.SendResponse(new GetStrongholdLendDetailResponse{Code=0,LendDayInfos=[]},p.Id);
    [RequestPacketHandler("GetStrongholdRewardRequest")]
    public static void Reward(Session s,Packet.Request p){var r=p.Deserialize<GetStrongholdRewardRequest>();var x=State(s);var rows=Rows<StrongholdRewardTable>();var ids=r.Ids.Distinct().Where(id=>!x.ClaimedRewardIds.Contains(id)).ToList();var goods=ids.SelectMany(id=>rows.FirstOrDefault(v=>v.Id==id) is {RewardId:int reward} ? RewardHandler.GetRewardGoods(reward):[]).ToList();if(goods.Count==0){s.SendResponse(new GetStrongholdRewardResponse{Code=Invalid},p.Id);return;}RewardHandler.ApplyRewardsOnceAndPersist(ids.Select(id=>new RewardGrant($"stronghold:{s.player.PlayerData.Id}:{id}",rows.First(v=>v.Id==id).RewardId is int q?RewardHandler.GetRewardGoods(q):[])).ToList(),s);x.ClaimedRewardIds.AddRange(ids);Save(s);s.SendResponse(new GetStrongholdRewardResponse{Code=0,SuccessIds=ids,RewardGoodsList=goods.Select(g=>new RewardGoods{Id=g.Id,TemplateId=g.TemplateId,Count=g.Count}).ToList()},p.Id);}
    [RequestPacketHandler("SweepStrongholdStageRequest")]
    public static void Sweep(Session s,Packet.Request p){var r=p.Deserialize<SweepStrongholdStageRequest>();var x=State(s);int stage=Next(x,r.GroupId);if(stage==0||x.Endurance<=0){s.SendResponse(new SweepStrongholdStageResponse{Code=20113054},p.Id);return;}x.PendingGroupId=r.GroupId;x.PendingStageId=stage;Settle(s.player,true);s.SendResponse(new SweepStrongholdStageResponse{Code=0,StrongholdFightResult=new(){AllFinished=false,GroupFightResultInfos=[new(){GroupId=r.GroupId}]}},p.Id);}
    [RequestPacketHandler("SelectStrongholdLevelRequest")]
    public static void SelectLevel(Session s,Packet.Request p){var r=p.Deserialize<SelectStrongholdLevelRequest>();var x=State(s);var level=Rows<StrongholdLevelTable>().FirstOrDefault(v=>v.Id==r.LevelId&&(s.player.PlayerData.Level>=v.MinLevel&&s.player.PlayerData.Level<=v.MaxLevel));if(x.LevelId!=0||level is null){s.SendResponse(new SelectStrongholdLevelResponse{Code=20113056},p.Id);return;}x.LevelId=r.LevelId;x.Endurance=level.InitEndurance;x.ElectricEnergy=level.InitElectricEnergy;var chapters=Rows<StrongholdChapterTable>().ToDictionary(v=>v.Id);var groups=Rows<StrongholdGroupTable>().ToDictionary(v=>v.Id);x.GroupInfos.Clear();x.GroupStageDatas.Clear();foreach(int cid in level.Chapter.Where(v=>v>0))if(chapters.TryGetValue(cid,out var c))foreach(int gid in c.GroupId.Where(v=>v>0))if(groups.TryGetValue(gid,out var g)){var ids=g.StageIdGroup.Select((candidates,index)=>Pick(candidates,s.player.PlayerData.Id,gid,index)).Where(v=>v>0).Distinct().Select(v=>(uint)v).ToList();if(ids.Count>0){x.GroupInfos.Add(new(){Id=gid});x.GroupStageDatas.Add(new(){Id=gid,StageIds=ids,SupportId=First(g.SupportId)});}}Save(s);s.SendResponse(new SelectStrongholdLevelResponse{Code=0,ElectricEnergy=x.ElectricEnergy,Endurance=x.Endurance,GroupStageDatas=x.GroupStageDatas},p.Id);}
    // ponytail: server-only distribution rules are unavailable; use the typed eligible stage groups, deterministic per player, and persist the result.
    [RequestPacketHandler("SetStrongholdStayRequest")]
    public static void Stay(Session s,Packet.Request p){var x=State(s);int d=++x.CurDay;if(!x.StayDays.Contains(d))x.StayDays.Add(d);Save(s);s.SendResponse(new SetStrongholdStayResponse{Code=0,StayDays=x.StayDays},p.Id);}
}
