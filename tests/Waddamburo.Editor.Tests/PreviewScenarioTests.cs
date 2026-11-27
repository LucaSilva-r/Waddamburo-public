using Waddamburo.Editor;

namespace Waddamburo.Editor.Tests;

public sealed class PreviewScenarioTests
{
    [Fact]
    public void ScenariosReadNamesCheckedAgainstTheEngineAndEventsLoop()
    {
        var (scenarios, problems) = PreviewScenario.Parse("""
            { "format": 1, "scenarios": [ { "name": "A",
                "state": { "result.score": 5, "player.name@2": "Wada", "made.up": 1, "note.judged": 1 },
                "lists": { "scores.name": ["x", "y"] },
                "events": [ { "at": 0.5, "name": "combo.milestone", "args": [100] } ], "loop": 2 } ] }
            """);
        var only = Assert.Single(scenarios);
        Assert.Equal(2, problems.Count); // made.up is no name; note.judged is an event, not a state
        Assert.Contains(only.States, state => state.Name == "player.name" && state.Drum == 2);
        Assert.Contains(only.States, state => state.Name == "scores.count" && state.Value.AsNumber() == 2);
        Assert.Equal(2, only.Items.Count);
        Assert.Single(only.Due(0.4, 0.6));
        Assert.Empty(only.Due(0.6, 1.9));
        Assert.Single(only.Due(2.4, 2.6)); // the next loop
    }

    // The scenarios the skin ships read without a problem.
    [Fact]
    public void TheShippedScenariosRead()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "movies"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, PreviewScenario.Folder), "*.json"))
        {
            var (scenarios, problems) = PreviewScenario.Load(root, Path.GetFileNameWithoutExtension(file));
            Assert.Empty(problems);
            Assert.NotEmpty(scenarios);
        }
    }
}
