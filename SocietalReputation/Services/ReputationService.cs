using FFXIVClientStructs.FFXIV.Client.Game;
using SocietalReputation.Models;

namespace SocietalReputation.Services;

public sealed class ReputationService
{
    private const byte RankIncreasedTodayFlag = 0x80;
    private const byte RankMask = 0x7F;
    private const int PerSocietyDailyQuestLimit = 3;
    private const int TotalDailyAllowances = 12;

    private readonly IReadOnlyList<ReputationRank> standardRanks =
    [
        new("Locked", 0, 1),
        new("Neutral", 0, 150),
        new("Recognized", 0, 360),
        new("Friendly", 0, 510),
        new("Trusted", 0, 720),
        new("Respected", 0, 990),
        new("Honored", 0, 1320),
        new("Sworn", 0, 1730),
        new("Bloodsworn", 0, 0),
        new("Allied", 0, 0),
    ];

    private readonly IReadOnlyList<SocietyInfo> societies;

    public ReputationService()
    {
        this.societies =
        [
            CreateSociety(EAlliedSociety.Amaljaa, "Amalj'aa", "A Realm Reborn", "Combat", true, 1222, 1251),
            CreateSociety(EAlliedSociety.Sylphs, "Sylphs", "A Realm Reborn", "Combat", true, 1257, 1286),
            CreateSociety(EAlliedSociety.Kobolds, "Kobolds", "A Realm Reborn", "Combat", true, 1325, 1373),
            CreateSociety(EAlliedSociety.Sahagin, "Sahagin", "A Realm Reborn", "Combat", true),
            CreateSociety(EAlliedSociety.Ixal, "Ixali", "A Realm Reborn", "Crafting", true),
            CreateSociety(EAlliedSociety.VanuVanu, "Vanu Vanu", "Heavensward", "Combat", false, 2171, 2200),
            CreateSociety(EAlliedSociety.Vath, "Vath", "Heavensward", "Combat", false, 2261, 2280),
            CreateSociety(EAlliedSociety.Moogles, "Moogles", "Heavensward", "Crafting", false, 2290, 2319),
            CreateSociety(EAlliedSociety.Kojin, "Kojin", "Stormblood", "Combat", false, 2979, 3002),
            CreateSociety(EAlliedSociety.Ananta, "Ananta", "Stormblood", "Combat", false, 3042, 3069),
            CreateSociety(EAlliedSociety.Namazu, "Namazu", "Stormblood", "Crafting/Gathering", false, 3103, 3130),
            CreateSociety(EAlliedSociety.Pixies, "Pixies", "Shadowbringers", "Combat", false, 3689, 3716),
            CreateSociety(EAlliedSociety.Qitari, "Qitari", "Shadowbringers", "Gathering", false, 3806, 3833),
            CreateSociety(EAlliedSociety.Dwarves, "Dwarves", "Shadowbringers", "Crafting", false, 3902, 3929),
            CreateSociety(EAlliedSociety.Arkasodara, "Arkasodara", "Endwalker", "Combat", false, 4551, 4578),
            CreateSociety(EAlliedSociety.Omicrons, "Omicrons", "Endwalker", "Gathering", false, 4607, 4634),
            CreateSociety(EAlliedSociety.Loporrits, "Loporrits", "Endwalker", "Crafting", false, 4687, 4714),
            CreateSociety(EAlliedSociety.Pelupelu, "Pelupelu", "Dawntrail", "Combat", false, 5199, 5226),
            CreateSociety(EAlliedSociety.MamoolJa, "Mamool Ja", "Dawntrail", "Gathering", false, 5261, 5288),
            CreateSociety(EAlliedSociety.YokHuy, "Yok Huy", "Dawntrail", "Crafting", false, 5336, 5363),
        ];
    }

    public unsafe ReputationSnapshot GetSnapshot()
    {
        var questManager = QuestManager.Instance();
        if (questManager == null)
        {
            return new ReputationSnapshot(
                this.societies.Select(CreateLockedProgress).ToArray(),
                0,
                TotalDailyAllowances,
                0);
        }

        return new ReputationSnapshot(
            this.societies.Select(society => CreateProgress(questManager, society)).ToArray(),
            (int)questManager->GetBeastTribeAllowance(),
            TotalDailyAllowances,
            questManager->NumAcceptedDailyQuests);
    }

    private SocietyProgress CreateLockedProgress(SocietyInfo society)
    {
        return new SocietyProgress(
            society,
            this.standardRanks[0],
            0,
            false,
            false,
            0,
            0,
            PerSocietyDailyQuestLimit);
    }

    private unsafe SocietyProgress CreateProgress(QuestManager* questManager, SocietyInfo society)
    {
        var reputation = questManager->GetBeastReputationById((uint)society.Id);
        if (reputation == null)
        {
            return CreateLockedProgress(society);
        }

        var rankData = reputation->Rank;
        var rank = (byte)(rankData & RankMask);
        if (rank == 0)
        {
            return CreateLockedProgress(society);
        }

        var rankedUpToday = (rankData & RankIncreasedTodayFlag) != 0;
        var rankInfo = GetRankInfo(society, rank);
        var currentReputation = rankInfo.MaximumReputation == 0
            ? 0
            : Math.Clamp(reputation->Value, 0, rankInfo.MaximumReputation);
        var (acceptedDailyQuestCount, completedDailyQuestCount) = GetDailyQuestCounts(questManager, society);

        return new SocietyProgress(
            society,
            rankInfo,
            currentReputation,
            true,
            rankedUpToday,
            acceptedDailyQuestCount,
            completedDailyQuestCount,
            PerSocietyDailyQuestLimit);
    }

    private unsafe (int AcceptedDailyQuestCount, int CompletedDailyQuestCount) GetDailyQuestCounts(QuestManager* questManager, SocietyInfo society)
    {
        if (society.DailyQuestStart == 0 || society.DailyQuestEnd == 0)
        {
            return (0, 0);
        }

        var accepted = 0;
        var completed = 0;
        for (ushort questId = society.DailyQuestStart; questId <= society.DailyQuestEnd; questId++)
        {
            var dailyQuest = questManager->GetDailyQuestById(questId);
            if (dailyQuest == null)
            {
                continue;
            }

            accepted++;
            if (dailyQuest->IsCompleted)
            {
                completed++;
            }
        }

        return (accepted, completed);
    }

    private ReputationRank GetRankInfo(SocietyInfo society, byte rank)
    {
        if (society.UsesArrAlliedRank && rank == 8)
        {
            return new ReputationRank("Allied", 0, 0);
        }

        if (rank < this.standardRanks.Count)
        {
            return this.standardRanks[rank];
        }

        return this.standardRanks[^1];
    }

    private SocietyInfo CreateSociety(
        EAlliedSociety id,
        string name,
        string expansion,
        string activity,
        bool usesArrAlliedRank = false,
        ushort dailyQuestStart = 0,
        ushort dailyQuestEnd = 0)
    {
        return new SocietyInfo(id, name, expansion, activity, usesArrAlliedRank, dailyQuestStart, dailyQuestEnd, this.standardRanks);
    }
}
