using System.Diagnostics.CodeAnalysis;
using AElf.Sdk.CSharp;

namespace AElf.Contracts.Consensus.AEDPoS
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class AEDPoSContractTestConstants
    {
        internal const int TinySlots = 8;

        internal const int InitialMinersCount = 5;

        internal const int SupposedMinersCount = 17;

        internal const int MiningInterval = 4000;

        internal static readonly int SmallBlockMiningInterval = MiningInterval.Div(TinySlots)
            .Mul(AEDPoSContractConstants.LimitBlockExecutionTimeWeight)
            .Div(AEDPoSContractConstants.LimitBlockExecutionTimeTotalShares);

        /// <summary>
        /// 7 days.
        /// </summary>
        internal const long PeriodMinutes = 120;// 7 * 60 * 60 * 24
    }
}