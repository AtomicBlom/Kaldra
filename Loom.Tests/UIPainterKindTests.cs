using Loom.Core.Hosting;
using Loom.UI.Vysara;

using DrawCommand = BinaryVibrance.Vysara.Drawing.DrawCommand;
using DrawListId = BinaryVibrance.Vysara.Drawing.DrawListId;

namespace Loom.Tests;

/// <summary>
/// A draw command's tag picks the <c>ui</c> shader's branch, and a shader cannot reject a value it does not know: an
/// unknown kind falls through its <c>switch</c> to a plain fill. So the painter checks the kinds itself before a list is
/// drawn — every one the shader draws, plus the two layer markers the walker consumes — and refuses anything else by
/// command and surface. Device-free: the check runs on the command stream alone.
/// </summary>
public class UIPainterKindTests
{
	private static readonly DrawListId Surface = new(3);

	[Test]
	public void Every_kind_the_ui_shader_draws_and_both_layer_markers_are_accepted()
	{
		DrawCommand[] commands =
		[
			Command(0), Command(1), Command(2), Command(3), Command(DrawListGeometry.KindImage), Command(7),
			Command(DrawListGeometry.TagBeginLayer), Command(DrawListGeometry.TagEndLayer),
		];
		UIPainter.ValidateKinds(Surface, commands);
	}

	[Test]
	public void An_unknown_kind_is_refused_by_command_and_surface()
	{
		// 7 is the vector kind, numbered past the layer markers; 8 is the first kind the shader does not draw.
		DrawCommand[] commands = [Command(0), Command(7), Command(8)];

		var refused = Should.Throw<InvalidOperationException>(() => UIPainter.ValidateKinds(Surface, commands));

		refused.Message.ShouldContain("command 2");
		refused.Message.ShouldContain("surface 3");
		refused.Message.ShouldContain("kind 8");
	}

	[Test]
	public void A_command_naming_a_slot_the_table_lacks_is_refused()
	{
		// A command's TextureIndex indexes its own list's slot table. One past the end would index the backend's texture
		// array out of range mid-recording; the painter names the command and the slot instead.
		DrawCommand[] commands = [Command(0, slot: 0), Command(4, slot: 1), Command(4, slot: 2)];
		UIPainter.ValidateSlots(Surface, commands.AsSpan(0, 2), slotCount: 2);

		var refused = Should.Throw<InvalidOperationException>(() => UIPainter.ValidateSlots(Surface, commands, slotCount: 2));

		refused.Message.ShouldContain("command 2");
		refused.Message.ShouldContain("slot 2");
		refused.Message.ShouldContain("2 slot(s)");
	}

	private static DrawCommand Command(uint tag, int slot = 0) =>
		new(new BinaryVibrance.Vysara.Rect(0f, 0f, 8f, 8f), TextureIndex: slot, IndexOffset: 0, IndexCount: 6, VertexOffset: 0, Tag: tag);
}
