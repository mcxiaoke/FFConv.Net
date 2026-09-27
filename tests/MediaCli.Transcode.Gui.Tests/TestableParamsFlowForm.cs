using System.Collections.Generic;

namespace MediaCli.Transcode.Gui.Tests;

internal sealed class TestableParamsFlowForm(string presetName, string cliArgs) : ParamsFlowForm(presetName, cliArgs)
{
    public List<string> ValidationErrors { get; } = new();

    protected override void ShowValidationErrors(IReadOnlyList<string> errors)
    {
        ValidationErrors.AddRange(errors);
    }
}
