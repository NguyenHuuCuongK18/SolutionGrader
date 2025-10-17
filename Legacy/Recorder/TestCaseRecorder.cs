using System.Collections.Generic;
using SolutionGrader.Legacy.Model;

namespace SolutionGrader.Legacy.Recorder;

// no using
public class TestCaseRecorder
{
    private readonly List<TestStep> _steps = new List<TestStep>();
    private int _stepCounter = 0;

    public void AddStep(string clientInput, string clientOutput, string serverOutput)
    {
        _stepCounter++;
        _steps.Add(new TestStep
        {
            StepNumber = _stepCounter,
            ClientInput = clientInput,
            ClientOutput = clientOutput,
            ServerOutput = serverOutput
        });
    }

    public List<TestStep> GetAllSteps() => _steps;
}
