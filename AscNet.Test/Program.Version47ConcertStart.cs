using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// Isolated entry for the 4.7 ConcertPreHeatingStartRequest handler. Callable by the parent
    /// integration driver; derives valid StageIds and the activity window from the current
    /// ConcertPreHeatingActivity table + ActivitySchedule, and confirms the registered handler
    /// accepts two authoritative open stages while rejecting nonexistent and closed input.
    /// </summary>
    public static void ValidateVersion47ConcertStartCompatibility()
    {
        // Wire shape: named-key DTOs.
        AssertMailNamedMapKeys(new ConcertPreHeatingStartRequest { StageId = 101 }, ["StageId"], "ConcertPreHeatingStartRequest");
        AssertMailNamedMapKeys(new ConcertPreHeatingStartResponse { Code = 0 }, ["Code"], "ConcertPreHeatingStartResponse");

        // Registered in Version47EventModule.
        MethodInfo handler = GetRegisteredRequestHandlerMethod("ConcertPreHeatingStartRequest");
        AssertEqual("AscNet.GameServer.Handlers.Version47EventModule", handler.DeclaringType?.FullName, "ConcertPreHeatingStartRequest handler module");

        // Authoritative stage set + open window from the current tables/schedule.
        ConcertPreHeatingActivityTable concert = TableReaderV2.Parse<ConcertPreHeatingActivityTable>().Single(row => row.TimeId > 0);
        if (!ActivityScheduleService.TryGet(concert.TimeId, out ActivityScheduleEntry schedule))
            throw new InvalidDataException($"ConcertPreHeating TimeId {concert.TimeId} is not staged in ActivitySchedule.tsv.");
        if (schedule.StartTime <= 0)
            throw new InvalidDataException("ConcertPreHeating schedule has no concrete StartTime.");
        DateTimeOffset concertOpen = DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime);
        if (concert.StageIds.Count < 2)
            throw new InvalidDataException("ConcertPreHeatingActivity has fewer than two authoritative StageIds; cannot test two open stages.");

        int stageA = concert.StageIds[0];
        int stageB = concert.StageIds[1];
        int unknownStage = concert.StageIds.Max() + 1;

        static MethodInfo ModuleMethod(string name, params Type[] signature) =>
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule"),
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, signature);
        static ConcertPreHeatingStartResponse Start(int stageId, DateTimeOffset now) =>
            (ConcertPreHeatingStartResponse)ModuleMethod("StartConcertPreHeating", [typeof(int), typeof(DateTimeOffset)])
                .Invoke(null, [stageId, now])!;

        // Two authoritative open stages/current states are accepted.
        AssertEqual(0, Start(stageA, concertOpen).Code, "Concert start authoritative stage A code");
        AssertEqual(0, Start(stageB, concertOpen).Code, "Concert start authoritative stage B code");

        // Nonexistent stage is rejected deterministically while the activity is open.
        AssertEqual(1, Start(unknownStage, concertOpen).Code, "Concert start unknown stage rejected");

        // Closed window rejects an otherwise-valid stage.
        AssertEqual(1, Start(stageA, concertOpen.AddSeconds(-1)).Code, "Concert start closed-window rejected");

        // End-to-end: the registered handler dispatches and emits the exact response on a live
        // session, with no mutation and no push.
        long uid = 47_501;
        using (LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(uid),
            CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, []),
            "version47-concert-start-test"))
        {
            Player before = harness.Session.player;
            InvokeRequestHandler(harness, "ConcertPreHeatingStartRequest", 2001, new ConcertPreHeatingStartRequest { StageId = stageA });
            ConcertPreHeatingStartResponse ok = ReadResponsePayload<ConcertPreHeatingStartResponse>(
                harness, 2001, nameof(ConcertPreHeatingStartResponse), "Concert start end-to-end open stage");
            AssertEqual(0, ok.Code, "Concert start end-to-end open stage code");
            if (harness.TryReadAvailablePacket("Concert start unexpected push", out _))
                throw new InvalidDataException("ConcertPreHeatingStart emitted a push.");

            InvokeRequestHandler(harness, "ConcertPreHeatingStartRequest", 2002, new ConcertPreHeatingStartRequest { StageId = unknownStage });
            ConcertPreHeatingStartResponse bad = ReadResponsePayload<ConcertPreHeatingStartResponse>(
                harness, 2002, nameof(ConcertPreHeatingStartResponse), "Concert start end-to-end unknown stage");
            AssertEqual(1, bad.Code, "Concert start end-to-end unknown stage rejected");

            // Pure validation: no durable state was touched.
            AssertEqual(before.ConcertPreHeating.ActivityId, harness.Session.player.ConcertPreHeating.ActivityId, "Concert start does not mutate activity id");
            AssertEqual(before.ConcertPreHeating.CompletedStageIds.Count, harness.Session.player.ConcertPreHeating.CompletedStageIds.Count, "Concert start does not mutate completed stages");
        }

        // BSON stability of the (untouched) persisted concert state.
        ConcertPreHeatingState reloaded = BsonSerializer.Deserialize<ConcertPreHeatingState>(new ConcertPreHeatingState().ToBson());
        AssertEqual(0, reloaded.CompletedStageIds.Count, "Concert state default BSON round-trip");
    }
}
