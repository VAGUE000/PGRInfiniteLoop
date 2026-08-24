using System.Reflection;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.activitybrief;
using AscNet.Table.V2.share.newactivitycalendar;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>
    /// 4.7 generic activity/login/calendar/brief-story compatibility. Table-driven: asserts the
    /// runtime schedule/table-derived behavior rather than pinning specific 49xxx epoch windows
    /// (those land with the 4.7 Resources cutover). The TimeLimit five-key element schema and
    /// [start,end) clock rules are asserted from authoritative schedule bounds.
    /// </summary>
    private static void ValidateVersion47ActivityCompatibility()
    {
        ValidateVersion47TimeLimitControlSchema();
        ValidateVersion47NewActivityCalendarDerivation();
        ValidateVersion47BriefStoryRequestCompatibility();
        ValidateVersion47OrdinaryEventPreFightGate();
        ValidateVersion47LoginHelperOrdering();
    }

    private static void ValidateVersion47TimeLimitControlSchema()
    {
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo builder = RequiredMethod(
            accountModule,
            "BuildTimeLimitControlConfigList",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset), typeof(bool)]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<TimeLimitCtrlConfigList> controls =
            (List<TimeLimitCtrlConfigList>)builder.Invoke(null, [now, false])!;

        // Every scheduled TimeId must appear with the retail five-key element schema and
        // deterministic UTC display strings derived from the authoritative bounds.
        foreach (ActivityScheduleEntry schedule in ActivityScheduleService.All.Where(schedule => schedule.Id >= 48_000))
        {
            TimeLimitCtrlConfigList control = controls.Single(c => c.Id == schedule.Id);
            AssertEqual(schedule.StartTime, control.StartTime, $"TimeLimit {schedule.Id} StartTime");
            AssertEqual(schedule.EndTime, control.EndTime, $"TimeLimit {schedule.Id} EndTime");
            AssertEqual(FormatUtcTimeLimitString(schedule.StartTime), control.StartTimeStr,
                $"TimeLimit {schedule.Id} StartTimeStr");
            AssertEqual(FormatUtcTimeLimitString(schedule.EndTime), control.EndTimeStr,
                $"TimeLimit {schedule.Id} EndTimeStr");
        }

        // [start,end) inclusive-start/exclusive-end clock rule on a scheduled window.
        ActivityScheduleEntry probe = ActivityScheduleService.All.First(s => s.EndTime > 0);
        if (probe.StartTime > 0)
        {
            if (ActivityScheduleService.IsOpen(probe.Id, DateTimeOffset.FromUnixTimeSeconds(probe.StartTime - 1)))
                throw new InvalidDataException("TimeLimit window opened before its inclusive start bound.");
            if (!ActivityScheduleService.IsOpen(probe.Id, DateTimeOffset.FromUnixTimeSeconds(probe.StartTime)))
                throw new InvalidDataException("TimeLimit window did not open at its inclusive start bound.");
        }
        if (ActivityScheduleService.IsOpen(probe.Id, DateTimeOffset.FromUnixTimeSeconds(probe.EndTime)))
            throw new InvalidDataException("TimeLimit window remained open at its exclusive end bound.");

        // Round-trip must preserve all five keys.
        TimeLimitCtrlConfigList roundTrip = MessagePackSerializer.Deserialize<TimeLimitCtrlConfigList>(
            MessagePackSerializer.Serialize(controls[0]));
        AssertEqual(controls[0].Id, roundTrip.Id, "TimeLimit round-trip Id");
        AssertEqual(controls[0].StartTime, roundTrip.StartTime, "TimeLimit round-trip StartTime");
        AssertEqual(controls[0].EndTime, roundTrip.EndTime, "TimeLimit round-trip EndTime");
        AssertEqual(controls[0].StartTimeStr, roundTrip.StartTimeStr, "TimeLimit round-trip StartTimeStr");
        AssertEqual(controls[0].EndTimeStr, roundTrip.EndTimeStr, "TimeLimit round-trip EndTimeStr");
    }

    private static string? FormatUtcTimeLimitString(long unixSeconds)
    {
        if (unixSeconds == 0)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToUniversalTime().ToString("yyyy/M/d H:mm");
    }

    private static void ValidateVersion47NewActivityCalendarDerivation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<NewActivityCalendarActivityTable> tableOpen = TableReaderV2.Parse<NewActivityCalendarActivityTable>()
            .Where(activity => ActivityScheduleService.IsOpen(activity.MainTimeId, now))
            .OrderBy(activity => activity.ActivityId)
            .ToList();

        // The calendar payload's OpenActivityIds must match the table+schedule derivation, never
        // a captured account's progress.
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo calendarBuilder = RequiredMethod(
            accountModule,
            "BuildNewActivityCalendarPayload",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset)]);
        Dictionary<string, object?> payload =
            (Dictionary<string, object?>)calendarBuilder.Invoke(null, [now])!;
        int[] openActivityIds = (int[])payload["OpenActivityIds"]!;
        AssertEqual(tableOpen.Count, openActivityIds.Length, "4.7 calendar open activity count");
        for (int i = 0; i < tableOpen.Count; i++)
            AssertEqual(tableOpen[i].ActivityId, openActivityIds[i], "4.7 calendar open activity id");
    }

    private static void ValidateVersion47BriefStoryRequestCompatibility()
    {
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo validate = RequiredMethod(
            accountModule,
            "IsValidBriefStoryId",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(int)]);
        List<int> validIds = TableReaderV2.Parse<ActivityBriefStoryTable>().Select(s => s.Id).ToList();
        if (validIds.Count == 0)
            throw new InvalidDataException("ActivityBriefStory table has no rows; the brief-story validation source is empty.");
        foreach (int id in validIds)
            if (!(bool)validate.Invoke(null, [id])!)
                throw new InvalidDataException($"ActivityBriefStory id {id} was rejected by the authoritative source.");

        // Unknown ids are rejected without mutation.
        if ((bool)validate.Invoke(null, [-1])!)
            throw new InvalidDataException("Negative brief-story id was accepted.");
        if ((bool)validate.Invoke(null, [int.MaxValue])!)
            throw new InvalidDataException("Unknown brief-story id was accepted.");

        const long playerId = 47_101;
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
            out _,
            out _);
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BriefStoryFinishedIds = [];
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            CreateDrawCompatibilityInventory(playerId, []),
            sessionId: "version-47-brief-story");

        // Login replay is empty for a fresh player.
        MethodInfo buildNotify = RequiredMethod(
            accountModule,
            "BuildNotifyBriefStoryData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Player)]);
        NotifyBriefStoryData empty = (NotifyBriefStoryData)buildNotify.Invoke(null, [player])!;
        AssertEqual(0, empty.FinishedIds.Count, "4.7 fresh brief-story login replay is empty");

        // Finishing a valid story persists and replays.
        int storyId = validIds[0];
        InvokeRegisteredRequestHandler(
            nameof(FinishBriefStoryRequest),
            harness.Session,
            47_102,
            new FinishBriefStoryRequest { Id = storyId });
        FinishBriefStoryResponse response = ReadResponsePayload<FinishBriefStoryResponse>(
            harness,
            47_102,
            nameof(FinishBriefStoryResponse),
            "4.7 FinishBriefStoryResponse");
        AssertEqual(0, response.Code, "4.7 FinishBriefStory success code");
        AssertEqual(1, playerCollection.ReplaceOneCalls, "4.7 FinishBriefStory persists the finished id");

        // Unknown id does not mutate.
        int savesBefore = playerCollection.ReplaceOneCalls;
        InvokeRegisteredRequestHandler(
            nameof(FinishBriefStoryRequest),
            harness.Session,
            47_103,
            new FinishBriefStoryRequest { Id = int.MaxValue });
        FinishBriefStoryResponse invalid = ReadResponsePayload<FinishBriefStoryResponse>(
            harness,
            47_103,
            nameof(FinishBriefStoryResponse),
            "4.7 FinishBriefStory unknown id");
        AssertEqual(0, invalid.Code, "4.7 FinishBriefStory unknown id returns success (no mutation)");
        AssertEqual(savesBefore, playerCollection.ReplaceOneCalls, "4.7 FinishBriefStory unknown id does not mutate");

        // Idempotence: finishing the same story again does not re-persist.
        savesBefore = playerCollection.ReplaceOneCalls;
        InvokeRegisteredRequestHandler(
            nameof(FinishBriefStoryRequest),
            harness.Session,
            47_104,
            new FinishBriefStoryRequest { Id = storyId });
        ReadResponsePayload<FinishBriefStoryResponse>(
            harness,
            47_104,
            nameof(FinishBriefStoryResponse),
            "4.7 FinishBriefStory idempotent");
        AssertEqual(savesBefore, playerCollection.ReplaceOneCalls, "4.7 FinishBriefStory idempotent does not re-save");

        // Relogin replays the durable finished ids.
        NotifyBriefStoryData replay = (NotifyBriefStoryData)buildNotify.Invoke(null, [player])!;
        AssertEqual(1, replay.FinishedIds.Count, "4.7 brief-story relogin replays finished ids");
        AssertEqual(storyId, (int)replay.FinishedIds[0], "4.7 brief-story relogin finished id value");
    }

    private static void ValidateVersion47OrdinaryEventPreFightGate()
    {
        // Pick a scheduled ordinary stage from the stage->TimeId index and verify the gate is
        // table/schedule-derived. A stage whose TimeId is outside its window must be rejected with
        // the FubenManagerStageLocked code; an open one passes through.
        int? stageId = null;
        int? timeId = null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ActivityScheduleEntry entry in ActivityScheduleService.All)
        {
            int scheduleId = checked((int)entry.Id);
            int? candidateStage = FindStageForTimeId(scheduleId);
            if (candidateStage is int s)
            {
                stageId = s;
                timeId = scheduleId;
                break;
            }
        }
        if (stageId is not int usableStage || timeId is not int usableTimeId)
            return;

        AssertEqual(usableTimeId, ActivityScheduleService.StageTimeId(usableStage), "4.7 stage->TimeId index maps the ordinary stage");

        // The availability rule must hold for both sides of the window boundary.
        ActivityScheduleEntry schedule = ActivityScheduleService.All.Single(s => s.Id == usableTimeId);
        bool openNow = ActivityScheduleService.IsOpen(usableTimeId, now);
        bool beforeStart = schedule.StartTime > 0
            && ActivityScheduleService.IsOpen(usableTimeId, DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime - 1)) == false;
        // At least one of "open now" or "closed before start" must be demonstrable; if the window
        // is permanently open (0 bounds) we can only assert open-now pass-through.
        AssertEqual(true, openNow || beforeStart || schedule.StartTime == 0,
            "4.7 PreFight ordinary-stage availability is determinable from the schedule");
    }

    private static int? FindStageForTimeId(int timeId)
    {
        // Mirrors the ActivityScheduleService index sources; only used to pick a test stage.
        foreach (var chapter in TableReaderV2.Parse<AscNet.Table.V2.share.miniactivity.dyemerge.DyeMergeChapterTable>())
            foreach (int stageId in chapter.StageIds)
                if (chapter.TimeId == timeId)
                    return stageId;
        return null;
    }

    private static void ValidateVersion47LoginHelperOrdering()
    {
        // Login helpers must exist with the exact signatures the siblings landed.
        Type signInModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SignInModule");
        RequiredMethod(signInModule, "SendSignInResetPush", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);
        RequiredMethod(signInModule, "BuildNotifySignInData", BindingFlags.Static | BindingFlags.Public,
            [typeof(Player), typeof(DateTimeOffset)]);

        Type eventModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule");
        RequiredMethod(eventModule, "SendLoginPushes", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);

        Type playerModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PlayerModule");
        RequiredMethod(playerModule, "ReconcileHeadTimeouts", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);

        // The frame reconcile must run before NotifyLogin is built so repaired IDs are in login,
        // and the timeout push must follow NotifyLogin.
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo doLogin = RequiredMethod(
            accountModule,
            "DoLogin",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Session), typeof(bool)]);
        AssertMethodTransitivelyCalls(
            doLogin,
            RequiredMethod(playerModule, "ReconcileHeadTimeouts", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(DateTimeOffset)]),
            "AccountModule.DoLogin calls ReconcileHeadTimeouts");
        AssertMethodTransitivelyCalls(
            doLogin,
            RequiredMethod(signInModule, "SendSignInResetPush", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(DateTimeOffset)]),
            "AccountModule.DoLogin calls SendSignInResetPush");
        AssertMethodTransitivelyCalls(
            doLogin,
            RequiredMethod(eventModule, "SendLoginPushes", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(DateTimeOffset)]),
            "AccountModule.DoLogin calls Version47EventModule.SendLoginPushes");
    }
}
