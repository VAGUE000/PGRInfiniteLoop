using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using MessagePack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.condition;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GuideGroupFinishRequest
    {
        public int GroupId;
    }

    [MessagePackObject(true)]
    public class GuideGroupFinishResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }

    [MessagePackObject(true)]
    public class GuideCompleteRequest
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class NotifyGuide
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class GuideCompleteResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class GuideModule
    {
        private static readonly Lazy<Dictionary<int, GuideGroupTable>> GuideGroups = new(() =>
            TableReaderV2.Parse<GuideGroupTable>().ToDictionary(guide => guide.Id));
        private static readonly Lazy<HashSet<int>> GuideCompletions = new(() =>
            TableReaderV2.Parse<GuideCompleteTable>().Select(completion => completion.Id).ToHashSet());
        private static readonly Lazy<Dictionary<int, ConditionTable>> GuideConditions = new(() =>
            TableReaderV2.Parse<ConditionTable>().ToDictionary(condition => condition.Id));
        private const int IncompleteStageConditionType = 10108;
        [RequestPacketHandler("GuideOpenRequest")]
        public static void GuideOpenRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = packet.Deserialize<GuideCompleteRequest>();
            bool valid = IsValidGuide(request.GuideGroupId);
            session.OpenedGuideGroupId = valid ? request.GuideGroupId : null;
            session.SendResponse(new GuideOpenResponse
            {
                Code = valid ? 0 : 1
            }, packet.Id);
        }

        [RequestPacketHandler("GuideGroupFinishRequest")]
        public static void GuideGroupFinishRequestHandler(Session session, Packet.Request packet)
        {
            GuideGroupFinishRequest request = packet.Deserialize<GuideGroupFinishRequest>();
            List<GuideGroupTable> groupGuides = GuideGroups.Value.Values
                .Where(guide => guide.GroupId == request.GroupId)
                .ToList();
            if (groupGuides.Count == 0)
            {
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            List<GuideGroupTable> addedGuides = groupGuides
                .Where(guide => !session.player.PlayerData.GuideData.Contains(guide.Id))
                .ToList();
            if (addedGuides.Count == 0)
            {
                session.SendResponse(new GuideGroupFinishResponse(), packet.Id);
                return;
            }

            List<RewardGrant> rewardGrants = new();
            foreach (GuideGroupTable guide in addedGuides.Where(guide => guide.RewardId > 0))
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                {
                    session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                    return;
                }
                rewardGrants.Add(new RewardGrant($"guide:{guide.Id}", configuredRewards));
            }

            RewardApplicationResult? rewardApplication = null;
            try
            {
                if (rewardGrants.Count > 0)
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(rewardGrants, session);
            }
            catch (Exception exception)
            {
                session.log.Error(
                    $"Failed to persist guide group rewards {request.GroupId}: {exception}");
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            List<long> addedGuideIds = addedGuides.Select(guide => (long)guide.Id).ToList();
            session.player.PlayerData.GuideData.AddRange(addedGuideIds);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.RemoveAll(addedGuideIds.Contains);
                session.log.Error(
                    $"Failed to persist guide group completion {request.GroupId}: {exception}");
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            rewardApplication?.SendPushes(session);
            session.SendResponse(new GuideGroupFinishResponse
            {
                RewardGoodsList = rewardApplication?.RewardGoods
            }, packet.Id);
        }

        [RequestPacketHandler("GuideCompleteRequest")]
        public static void GuideCompleteRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = MessagePackSerializer.Deserialize<GuideCompleteRequest>(packet.Content);
            if (!GuideGroups.Value.TryGetValue(request.GuideGroupId, out GuideGroupTable? guide)
                || !GuideCompletions.Value.Contains(guide.CompleteId))
            {
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }

            GuideCompletionResult result = CompleteGuide(session, guide);
            if (!result.Succeeded)
            {
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }
            if (result.WasAlreadyComplete)
                session.SendPush(new NotifyGuide { GuideGroupId = request.GuideGroupId });
            session.SendResponse(new GuideCompleteResponse
            {
                RewardGoodsList = result.RewardGoodsList
            }, packet.Id);
        }

        internal static void OnStageSettled(Session session, IEnumerable<uint> settledStageIds)
        {
            if (session.OpenedGuideGroupId is not int openedGuideId
                || !GuideGroups.Value.TryGetValue(openedGuideId, out GuideGroupTable? guide))
                return;

            HashSet<uint> settled = settledStageIds.ToHashSet();
            bool stageReferenced = guide.ConditionId
                .Where(GuideConditions.Value.ContainsKey)
                .Select(id => GuideConditions.Value[id])
                .Any(condition => condition.Type == IncompleteStageConditionType
                    && condition.Params.Any(param => settled.Contains(unchecked((uint)param))));
            if (!stageReferenced)
                return;

            if (CompleteGuide(session, guide).Succeeded)
                session.OpenedGuideGroupId = null;
        }

        private static GuideCompletionResult CompleteGuide(Session session, GuideGroupTable guide)
        {
            session.player.PlayerData.GuideData ??= new();
            if (session.player.PlayerData.GuideData.Contains(guide.Id))
                return new GuideCompletionResult { Succeeded = true, WasAlreadyComplete = true };

            string claimKey = $"guide:{guide.Id}";

            RewardApplicationResult? rewardApplication = null;
            if (guide.RewardId is > 0)
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                    return new GuideCompletionResult { Succeeded = false };
                try
                {
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                        [new RewardGrant(claimKey, configuredRewards)],
                        session);
                }
                catch (Exception exception)
                {
                    session.log.Error(
                        $"Failed to persist guide reward {guide.Id}: {exception}");
                    return new GuideCompletionResult { Succeeded = false };
                }
            }

            session.player.PlayerData.GuideData.Add(guide.Id);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.Remove(guide.Id);
                session.log.Error(
                    $"Failed to persist guide completion {guide.Id}: {exception}");
                return new GuideCompletionResult { Succeeded = false };
            }
            rewardApplication?.SendPushes(session);
            session.SendPush(new NotifyGuide { GuideGroupId = guide.Id });
            return new GuideCompletionResult
            {
                Succeeded = true,
                RewardGoodsList = rewardApplication?.RewardGoods
            };
        }

        internal static void SkipCommonGuides(Player player)
        {
            player.PlayerData.GuideData ??= new();
            HashSet<long> completedGuides = new(player.PlayerData.GuideData);
            foreach (GuideGroupTable guide in GuideGroups.Value.Values.Where(guide => guide.Ignore == 0 && guide.RewardId == 0))
            {
                if (completedGuides.Add(guide.Id))
                    player.PlayerData.GuideData.Add(guide.Id);
            }
        }

        private static bool IsValidGuide(int guideGroupId)
            => GuideGroups.Value.TryGetValue(guideGroupId, out GuideGroupTable? guide)
                && GuideCompletions.Value.Contains(guide.CompleteId);
    }

    internal sealed class GuideCompletionResult
    {
        public bool Succeeded;
        public bool WasAlreadyComplete;
        public List<RewardGoods>? RewardGoodsList;
    }
}
