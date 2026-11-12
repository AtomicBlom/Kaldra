using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;

namespace Loom.Hosting.Android;

/// <summary>
/// The surface the frame is drawn on, which is also the view Android's on-screen keyboard types into: it takes focus,
/// says it edits text, and hands the keyboard a <see cref="LoomInputConnection"/>.
/// </summary>
internal sealed class LoomSurfaceView : SurfaceView
{
	private readonly AndroidInputPump _input;

	public LoomSurfaceView(Context context, AndroidInputPump input)
		: base(context)
	{
		_input = input;
		Focusable = true;
		FocusableInTouchMode = true;
	}

	/// <summary>The connection the keyboard last opened, or null before it has opened one.</summary>
	public LoomInputConnection? Connection { get; private set; }

	public override bool OnCheckIsTextEditor() => true;

	public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
	{
		if (outAttrs is not null)
		{
			// Plain text without the suggestion strip, whose corrections would rewrite text the app already holds; an
			// action key that is Enter; and never the full-screen editor a landscape keyboard would otherwise open over the
			// app.
			outAttrs.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
			outAttrs.ImeOptions = ImeFlags.NoFullscreen | ImeFlags.NoExtractUi | (ImeFlags)ImeAction.Done;
		}
		Connection = new LoomInputConnection(this, _input);
		return Connection;
	}
}
