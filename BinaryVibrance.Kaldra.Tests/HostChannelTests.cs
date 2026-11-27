using System.Text;

using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// What a host tells a <see cref="KaldraApplication"/> besides input and frames, and what the app asks of the host:
/// suspending and resuming, the safe area and the on-screen keyboard's height, and packaged content. Driven through
/// <see cref="IHostedApplication"/> with a recording host and no engine (none of it renders).
/// </summary>
public class HostChannelTests
{
	private static (ProbeApp App, IHostedApplication Driver, ContentHost Host) Start()
	{
		var app = new ProbeApp();
		var driver = (IHostedApplication)app;
		var host = new ContentHost();
		driver.ReadOptions();
		driver.Load(null!, host);
		return (app, driver, host);
	}

	[Test]
	public void Suspend_and_resume_reach_the_app_once_each_however_often_the_host_repeats_them()
	{
		var (app, driver, _) = Start();

		driver.Resume(); // not suspended: nothing to resume
		driver.Suspend();
		driver.Suspend();
		app.Suspended.ShouldBeTrue();
		driver.Resume();
		driver.Resume();

		app.Calls.ShouldBe(["suspending", "resumed"]);
		app.Suspended.ShouldBeFalse();
	}

	[Test]
	public void The_safe_area_and_the_keyboard_height_reach_the_app_as_the_host_reports_them()
	{
		var (app, driver, _) = Start();
		app.Insets.ShouldBe(ScreenInsets.None);

		driver.SetSafeArea(new ScreenInsets(0f, 125f, 0f, 0f));
		driver.SetInputPaneHeight(773f);

		app.Insets.ShouldBe(new ScreenInsets(0f, 125f, 0f, 0f));
		app.Keyboard.ShouldBe(773f);
	}

	[Test]
	public async Task Content_is_read_through_the_host_by_its_path_with_forward_slashes()
	{
		var (app, _, host) = Start();
		host.Files["levels/1.json"] = Encoding.UTF8.GetBytes("{}");

		app.Read(@"levels\1.json").ShouldBe(Encoding.UTF8.GetBytes("{}"));
		(await app.ReadAsync("levels/1.json")).ShouldBe(Encoding.UTF8.GetBytes("{}"));
		host.Asked.ShouldBe(["levels/1.json", "levels/1.json"]);
	}

	[Test]
	[Arguments("")]
	[Arguments("../secrets.txt")]
	[Arguments("levels/../../x")]
	[Arguments("/levels/1.json")]
	[Arguments(@"C:\levels\1.json")]
	public void A_content_path_outside_the_content_root_is_refused_before_the_host_sees_it(string path)
	{
		var (app, _, host) = Start();

		Should.Throw<ArgumentException>(() => app.Read(path));
		Should.Throw<ArgumentException>(() => app.ReadAsync(path));
		host.Asked.ShouldBeEmpty();
	}

	[Test]
	public void A_host_that_packs_no_content_says_so()
	{
		var app = new ProbeApp();
		var driver = (IHostedApplication)app;
		driver.ReadOptions();
		driver.Load(null!, new BareHost());

		Should.Throw<NotSupportedException>(() => app.Read("levels/1.json"));
	}

	[Test]
	public async Task A_file_dialog_answers_with_what_the_host_chose_and_a_host_without_dialogs_answers_null()
	{
		var (app, _, host) = Start();
		app.Dialogs.ShouldBeTrue();
		var asked = new FileDialog { Kind = FileDialogKind.OpenFile, Filters = [new FileDialogFilter("Lunelle music", "lunelle")] };
		var answer = app.Show(asked);
		answer.IsCompleted.ShouldBeFalse();
		host.Dialog.ShouldBe(asked);
		host.Answer!(["C:/music/song.lunelle"]);
		(await answer).ShouldBe(["C:/music/song.lunelle"]);

		var bare = new ProbeApp();
		((IHostedApplication)bare).ReadOptions();
		((IHostedApplication)bare).Load(null!, new BareHost());
		bare.Dialogs.ShouldBeFalse();
		(await bare.Show(asked)).ShouldBeNull();
	}

	[Test]
	public void Files_dropped_on_the_window_reach_the_app()
	{
		var (app, driver, _) = Start();
		driver.DropFiles(["C:/music/a.lunelle", "C:/music/b"]);
		app.Dropped.ShouldBe(["C:/music/a.lunelle", "C:/music/b"]);
	}

	private sealed class ProbeApp : KaldraApplication
	{
		public List<string> Dropped { get; } = [];
		public bool Dialogs => CanShowFileDialogs;
		public Task<IReadOnlyList<string>?> Show(FileDialog dialog) => ShowFileDialogAsync(dialog);
		protected override void OnFilesDropped(IReadOnlyList<string> paths) => Dropped.AddRange(paths);

		public List<string> Calls { get; } = [];
		public bool Suspended => IsSuspended;
		public ScreenInsets Insets => SafeAreaInsets;
		public float Keyboard => InputPaneHeight;

		public byte[] Read(string path) => ReadContent(path);
		public Task<byte[]> ReadAsync(string path) => ReadContentAsync(path);

		protected override void OnSuspending() => Calls.Add("suspending");
		protected override void OnResumed() => Calls.Add("resumed");
	}

	// A host with packaged content held in memory, recording every path it is asked for.
	private sealed class ContentHost : IHostControl
	{
		public Dictionary<string, byte[]> Files { get; } = [];
		public List<string> Asked { get; } = [];
		public FileDialog? Dialog { get; private set; }
		public Action<IReadOnlyList<string>?>? Answer { get; private set; }

		public void RequestExit() { }

		public bool CanShowFileDialogs => true;

		public void ShowFileDialog(FileDialog dialog, Action<IReadOnlyList<string>?> done)
		{
			Dialog = dialog;
			Answer = done;
		}

		public Stream OpenContent(string path)
		{
			lock (Asked)
				Asked.Add(path);
			return Files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : throw new FileNotFoundException(path);
		}
	}

	// A host that implements nothing beyond what every host must.
	private sealed class BareHost : IHostControl
	{
		public void RequestExit() { }
	}
}
