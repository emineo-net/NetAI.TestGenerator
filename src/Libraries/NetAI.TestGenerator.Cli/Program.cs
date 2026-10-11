using NetAI.TestGenerator.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(cfg =>
{
    cfg.SetApplicationName("netai");
    cfg.ValidateExamples();

    cfg.AddCommand<GenerateCommand>("generate")
        .WithDescription("Generate unit tests for a C# source file.")
        .WithExample("generate", "src/Foo/Bar.cs")
        .WithExample("generate", "src/Foo/Bar.cs", "--test-project", "tests/Bar.Tests")
        .WithExample("generate", "src/Foo/Bar.cs", "--prompt-only")
        .WithExample("generate", "src/Foo/Bar.cs", "--json");

    cfg.AddCommand<AnalyseCommand>("analyse")
        .WithDescription("Analyse a C# source file without generating tests.")
        .WithExample("analyse", "src/Foo/Bar.cs")
        .WithExample("analyse", "src/Foo/Bar.cs", "--json");

    cfg.AddCommand<InitCommand>("init")
        .WithDescription("Create an aisettings.json next to a .csproj/.sln.");
});

return await app.RunAsync(args);