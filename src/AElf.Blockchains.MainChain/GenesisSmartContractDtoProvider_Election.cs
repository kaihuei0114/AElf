using System.Collections.Generic;
using AElf.Contracts.Election;
using AElf.Kernel;
using AElf.Kernel.Consensus.AElfConsensus;
using AElf.Kernel.Token;
using AElf.OS.Node.Application;

namespace AElf.Blockchains.MainChain
{
    public partial class GenesisSmartContractDtoProvider
    {
        public IEnumerable<GenesisSmartContractDto> GetGenesisSmartContractDtosForElection(Address zeroContractAddress)
        {
            var l = new List<GenesisSmartContractDto>();

            l.AddGenesisSmartContract<ElectionContract>(
               ElectionSmartContractAddressNameProvider.Name, GenerateElectionInitializationCallList());

            return l;
        }

        private SystemContractDeploymentInput.Types.SystemTransactionMethodCallList
            GenerateElectionInitializationCallList()
        {
            var electionContractMethodCallList =
                new SystemContractDeploymentInput.Types.SystemTransactionMethodCallList();
            electionContractMethodCallList.Add(nameof(ElectionContract.InitialElectionContract),
                new InitialElectionContractInput
                {
                    // Create Treasury profit item and register sub items.
                    TokenContractSystemName = TokenSmartContractAddressNameProvider.Name,
                    VoteContractSystemName = VoteSmartContractAddressNameProvider.Name,
                    ProfitContractSystemName = ProfitSmartContractAddressNameProvider.Name,
                    
                    // Get current miners.
                    AelfConsensusContractSystemName = ConsensusSmartContractAddressNameProvider.Name
                });
            return electionContractMethodCallList;
        }
    }
}