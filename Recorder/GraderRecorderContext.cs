using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using SolutionGrader.Legacy.Model;
using SolutionGrader.Legacy.Recorder;

namespace SolutionGrader.Recorder
{
    public class GraderRecorderContext : IRecorderContext
    {
        private readonly HashSet<string> _ignoreTexts;
        private readonly object _syncRoot = new();
        private int _stepCounter;

        public GraderRecorderContext(HashSet<string> ignoreTexts)
        {
            _ignoreTexts = ignoreTexts;
        }

        public BindingList<Input_Client> InputClients { get; } = new();
        public BindingList<OutputClient> OutputClients { get; } = new();
        public BindingList<OutputServer> OutputServers { get; } = new();

        public event Action<Input_Client>? StageAdded;

        public void Reset()
        {
            _stepCounter = 0;
            InputClients.Clear();
            OutputClients.Clear();
            OutputServers.Clear();
        }

        public void AddActionStage(string action, string input = "", string dataType = "", int? stageOverride = null)
        {
            if (stageOverride.HasValue)
            {
                _stepCounter = stageOverride.Value;
            }
            else
            {
                _stepCounter++;
            }

            var step = new Input_Client
            {
                Stage = _stepCounter,
                Input = input,
                DataType = dataType,
                Action = action
            };

            InputClients.Add(step);
            StageAdded?.Invoke(step);
        }

        public void AppendClientOutput(string data)
        {
            if (ShouldIgnore(data))
            {
                return;
            }

            var stage = GetCurrentStage();
            if (stage == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                var existingIndex = OutputClients.ToList().FindLastIndex(c => c.Stage == stage.Value);
                if (existingIndex >= 0)
                {
                    var existing = OutputClients[existingIndex];
                    existing.Output = (existing.Output ?? string.Empty) + data + "\n";
                    OutputClients.ResetItem(existingIndex);
                }
                else
                {
                    OutputClients.Add(new OutputClient
                    {
                        Stage = stage.Value,
                        Output = data + "\n"
                    });
                }
            }
        }

        public void AppendServerOutput(string data)
        {
            if (ShouldIgnore(data))
            {
                return;
            }

            var stage = GetCurrentStage();
            if (stage == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                var existingIndex = OutputServers.ToList().FindLastIndex(s => s.Stage == stage.Value);
                if (existingIndex >= 0)
                {
                    var existing = OutputServers[existingIndex];
                    existing.Output = (existing.Output ?? string.Empty) + data + "\n";
                    OutputServers.ResetItem(existingIndex);
                }
                else
                {
                    OutputServers.Add(new OutputServer
                    {
                        Stage = stage.Value,
                        Output = data + "\n"
                    });
                }
            }
        }

        private int? GetCurrentStage()
        {
            if (InputClients.Count == 0)
            {
                return null;
            }

            return InputClients.Last().Stage;
        }

        private bool ShouldIgnore(string data)
        {
            if (_ignoreTexts == null || _ignoreTexts.Count == 0)
            {
                return false;
            }

            foreach (var ignore in _ignoreTexts)
            {
                if (!string.IsNullOrWhiteSpace(ignore) && data.Contains(ignore, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}