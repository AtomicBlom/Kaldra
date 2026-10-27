using System.Diagnostics;
using System.Text.Json;

namespace Loom.Tests;

/// <summary>
/// The Loom SDK's <c>UseSUIsei</c> switch gives an app SUIsei's XAML, not only its runtime. Built through the SDK from
/// source, an app with a <c>.xaml</c> view gets the view's typed tree generated and can run it; a XAML error fails the
/// build at the <c>.xaml</c>'s own file and line; and the views are <c>dotnet watch</c> inputs, which is what XAML Hot
/// Reload runs on. A view under a folder the app keeps out of its items (<c>DefaultItemExcludes</c>), as a game keeps
/// out the Loom submodule beneath it, stays out of its XAML too: the app's fixture holds a broken one there. The apps are
/// the fixtures under <c>SdkFixtures</c>, each its own project, built with the test run's own configuration so their
/// Loom references are the ones this run already built. The packaged SDK points the same switch at the SUIsei package,
/// which carries the same generator and <c>.xaml</c> registration; no test here builds against packages.
/// </summary>
[NotInParallel] // the fixtures build against the same referenced projects
public class SdkXamlTests
{
	[Test]
	public void An_app_that_turns_SUIsei_on_gets_its_xaml_views_generated()
	{
		var app = Fixture("XamlApp");
		var (build, buildOutput) = Dotnet(app, "build", "XamlApp.csproj", "-c", Configuration, "-nologo");
		build.ShouldBe(0, "the fixture app failed to build:\n" + buildOutput);

		// The generator wrote the view's half of the partial class...
		Directory.GetFiles(Path.Combine(app, "obj", Configuration), "XamlApp.Views.HelloView.g.cs", SearchOption.AllDirectories)
			.ShouldNotBeEmpty("SUIsei's generator wrote no XamlApp.Views.HelloView.g.cs");

		// ...and the app runs it: the typed x:Name field holds what the XAML set.
		var (run, runOutput) = Dotnet(app, Path.Combine(app, "bin", Configuration, "net10.0", "XamlApp.dll"));
		run.ShouldBe(0, runOutput);
		runOutput.ShouldContain("Hello from XAML");
	}

	[Test]
	public void A_xaml_error_fails_the_build_at_the_views_file_and_line()
	{
		var app = Fixture("BrokenXamlApp");
		var (exit, output) = Dotnet(app, "build", "BrokenXamlApp.csproj", "-c", Configuration, "-nologo");
		exit.ShouldNotBe(0, "a view with an unknown element built");
		output.ShouldContain(Path.Combine("Views", "BrokenView.xaml") + "(7,3): error SUISEI003");
	}

	[Test]
	public void An_apps_xaml_views_are_dotnet_watch_inputs_for_hot_reload()
	{
		var app = Fixture("XamlApp");
		var (exit, output) = Dotnet(app, "msbuild", "XamlApp.csproj", "-getItem:Watch", "-getItem:AdditionalFiles", "-nologo");
		exit.ShouldBe(0, output);

		using var json = JsonDocument.Parse(output);
		var items = json.RootElement.GetProperty("Items");
		var view = Path.Combine(app, "Views", "HelloView.xaml");
		foreach (var kind in new[] { "Watch", "AdditionalFiles" })
			items.GetProperty(kind).EnumerateArray().Select(i => i.GetProperty("FullPath").GetString())
				.ShouldContain(view, $"HelloView.xaml is not one of the app's {kind} items");
	}

	// The test run's own configuration, the folder above the target framework's: Loom.Tests\bin\<Configuration>\net10.0.
	private static string Configuration =>
		Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)))!;

	private static string Fixture(string name) =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SdkFixtures", name));

	// Run the dotnet CLI in `directory` and return its exit code and everything it wrote. MSBuild's own environment, which
	// a test host started by an MSBuild-driven command can inherit, is dropped, so the child resolves its SDK afresh.
	private static (int ExitCode, string Output) Dotnet(string directory, params string[] arguments)
	{
		var info = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var argument in arguments)
			info.ArgumentList.Add(argument);
		foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
			info.Environment.Remove(name);

		using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start dotnet");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException($"dotnet {string.Join(' ', arguments)} ran past ten minutes.");
		}
		return (process.ExitCode, stdout.Result + stderr.Result);
	}
}
