using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateVersion47MemoryCompatibility()
    {
        List<EquipTable> equips = TableReaderV2.Parse<EquipTable>();
        EquipConfigTable discount = TableReaderV2.Parse<EquipConfigTable>().Single();
        EquipTable[] santiago = equips
            .Where(row => row.Type == 0 && row.SuitId == discount.SuitId)
            .OrderBy(row => row.Site)
            .ToArray();
        AssertEqual(6, santiago.Length, "4.7 Santiago memory piece count");
        AssertIntegerList([1L, 2L, 3L, 4L, 5L, 6L], santiago.Select(row => (long)row.Site).ToArray(),
            "4.7 Santiago memory sites");

        HashSet<int> awakeIds = TableReaderV2.Parse<EquipAwakeTable>().Select(row => row.Id).ToHashSet();
        HashSet<int> resonanceIds = TableReaderV2.Parse<EquipResonanceTable>().Select(row => row.Id).ToHashSet();
        HashSet<int> materialIds = TableReaderV2.Parse<EquipResonanceUseItemTable>().Select(row => row.Id).ToHashSet();
        foreach (EquipTable memory in santiago)
        {
            AssertEqual(true, awakeIds.Contains(memory.Id), $"Santiago {memory.Id} awake recipe");
            AssertEqual(true, resonanceIds.Contains(memory.Id), $"Santiago {memory.Id} resonance pools");
            AssertEqual(true, materialIds.Contains(memory.Id), $"Santiago {memory.Id} resonance materials");
            AssertEqual(5, TableReaderV2.Parse<EquipBreakThroughTable>().Count(row => row.EquipId == memory.Id),
                $"Santiago {memory.Id} breakthrough stages");
        }

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipModule");
        MethodInfo resolveCost = RequiredMethod(module, "ResolveEquipResonanceCost",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(EquipTable), typeof(EquipResonanceUseItemTable), typeof(int)]);
        int Cost(EquipTable equip) => (int)(resolveCost.Invoke(null,
            [equip, TableReaderV2.Parse<EquipResonanceUseItemTable>().Single(row => row.Id == equip.Id), discount.ItemId]) ?? 0);

        AssertEqual(discount.DiscountCount, Cost(santiago[0]), "Santiago grid 1 discounted resonance cost");
        AssertEqual(discount.DiscountCount, Cost(santiago[5]), "Santiago grid 6 discounted resonance cost");
        EquipTable ordinary = equips.First(row => row.Type == 0 && row.Quality == 6 && row.SuitId != discount.SuitId
            && TableReaderV2.Parse<EquipResonanceUseItemTable>().Any(cost => cost.Id == row.Id
                && cost.ItemId.Contains(discount.ItemId)));
        EquipResonanceUseItemTable ordinaryCost = TableReaderV2.Parse<EquipResonanceUseItemTable>()
            .Single(row => row.Id == ordinary.Id);
        AssertEqual(ordinaryCost.ItemCount[ordinaryCost.ItemId.IndexOf(discount.ItemId)], Cost(ordinary),
            "ordinary memory retains configured resonance cost");
        AssertEqual(true, Cost(ordinary) > discount.DiscountCount,
            "Santiago discount is lower than ordinary memory cost");

        AssertResonance(santiago[0], discount.DiscountCount, expectedCode: 0, expectedRemaining: 0, "Santiago exact discount");
        AssertResonance(santiago[1], discount.DiscountCount - 1, expectedCode: 20012004,
            expectedRemaining: discount.DiscountCount - 1, "Santiago insufficient discount");

        static void AssertResonance(
            EquipTable memory,
            long materialCount,
            int expectedCode,
            long expectedRemaining,
            string name)
        {
            const int characterId = 1071005;
            EquipData equip = new()
            {
                Id = checked((uint)(memory.Id + 10_000_000)),
                TemplateId = checked((uint)memory.Id)
            };
            AscNet.Common.Database.Character character = new()
            {
                Uid = equip.Id,
                Characters = [new CharacterData { Id = characterId }],
                Equips = [equip],
                Fashions = []
            };
            AscNet.Common.Database.Inventory inventory = new()
            {
                Uid = character.Uid,
                Items = [new Item { Id = 62738, Count = materialCount }]
            };
            using LoopbackSessionHarness harness = new(character, inventory: inventory);
            InvokeRequestHandler(harness, nameof(EquipResonanceRequest), checked((int)equip.Id),
                new EquipResonanceRequest
                {
                    UseItemId = 62738,
                    EquipId = checked((int)equip.Id),
                    CharacterId = characterId,
                    Slots = [1],
                    SelectSkillIds = null
                });
            if (expectedCode == 0)
            {
                NotifyItemDataList push = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), $"{name} item push");
                AssertEqual(expectedRemaining, push.ItemDataList.Single(item => item.Id == 62738).Count,
                    $"{name} deducted material");
            }
            EquipResonanceResponse response = ReadResponsePayload<EquipResonanceResponse>(
                harness.ReadPacket($"{name} response"), nameof(EquipResonanceResponse));
            AssertEqual(expectedCode, response.Code, $"{name} response Code");
            AssertEqual(expectedRemaining, inventory.Items.Single(item => item.Id == 62738).Count,
                $"{name} inventory material");
        }
    }
}
