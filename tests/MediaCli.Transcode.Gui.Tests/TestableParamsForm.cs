using System.Collections.Generic;

namespace MediaCli.Transcode.Gui.Tests;

internal sealed class TestableParamsForm(string presetName, string cliArgs) : ParamsForm(presetName, cliArgs)
{
	public List<string> ValidationErrors { get; } = new List<string>();

	protected override void ShowValidationErrors(IReadOnlyList<string> errors)
	{
		ValidationErrors.AddRange(errors);
	}
}
