using System.Text;

using Android.OS;
using Android.Views;
using Android.Views.InputMethods;

using Loom.Input;

namespace Loom.Hosting.Android;

/// <summary>
/// The connection Android's on-screen keyboard types through, turned into the app's neutral input: committed and
/// composing text become <see cref="InputEventKind.Text"/> events, and the keyboard's deletions and its action key
/// become Backspace, Delete and Enter presses, which is how a text field already edits from a physical keyboard.
/// </summary>
/// <remarks>
/// The app owns the text, so this keeps none of it. A keyboard composes a word before committing it, rewriting it as it
/// goes (autocorrect, swipe typing); the word being composed is sent to the app as it is typed, and each rewrite is sent
/// as backspaces over the part that changed followed by its new text, so the field always shows what the keyboard
/// shows. Keys the keyboard sends as key events (its Backspace, often) go to the activity like a physical keyboard's.
/// </remarks>
internal sealed class LoomInputConnection(View view, AndroidInputPump input) : BaseInputConnection(view, fullEditor: false)
{
	// The text being composed, as the app has already been sent it.
	private string _composing = "";

	/// <summary>Forget any text being composed, for the keyboard closing: the app keeps what it was sent.</summary>
	public void Reset() => _composing = "";

	public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
	{
		ReplaceComposing(text?.ToString() ?? "");
		_composing = "";
		return true;
	}

	public override bool SetComposingText(Java.Lang.ICharSequence? text, int newCursorPosition)
	{
		ReplaceComposing(text?.ToString() ?? "");
		return true;
	}

	public override bool FinishComposingText()
	{
		_composing = ""; // the composed text stays, as it was sent
		return true;
	}

	public override bool DeleteSurroundingText(int beforeLength, int afterLength)
	{
		var timestamp = SystemClock.UptimeMillis();
		for (var i = 0; i < beforeLength; i++)
			input.PressKey(Key.Backspace, timestamp);
		for (var i = 0; i < afterLength; i++)
			input.PressKey(Key.Delete, timestamp);
		return true;
	}

	public override bool PerformEditorAction(ImeAction actionCode)
	{
		input.PressKey(Key.Enter, SystemClock.UptimeMillis());
		return true;
	}

	// Rewrite the composing text the app was sent into `text`: backspace over what differs from the end of the common
	// start, then send the rest of `text`.
	private void ReplaceComposing(string text)
	{
		var common = 0;
		while (common < _composing.Length && common < text.Length && _composing[common] == text[common])
			common++;
		if (common > 0 && char.IsHighSurrogate(_composing[common - 1]))
			common--; // never split a pair: the app counts characters by code point

		var timestamp = SystemClock.UptimeMillis();
		foreach (var _ in _composing.AsSpan(common).EnumerateRunes())
			input.PressKey(Key.Backspace, timestamp);
		input.QueueText(text.AsSpan(common), timestamp);
		_composing = text;
	}
}
