using System.ComponentModel;
using SolutionGrader.Legacy.Model;

namespace SolutionGrader.Legacy.Recorder
{
    public interface IRecorderContext
    {
        BindingList<Input_Client> InputClients { get; }
        BindingList<OutputClient> OutputClients { get; }
        BindingList<OutputServer> OutputServers { get; }
        void AddActionStage(string action, string input = "", string dataType = "", int? stageOverride = null);
    }
}