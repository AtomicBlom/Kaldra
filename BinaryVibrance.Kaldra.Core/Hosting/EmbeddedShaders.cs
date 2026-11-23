using System.Reflection;

namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// Finds a compiled shader artifact embedded in the app's own assemblies, for a host with no shader folder beside the
/// app (an Android APK, a browser bundle). The Kaldra shader build embeds each artifact under
/// <c>"&lt;AssemblyName&gt;.Shaders.&lt;file&gt;"</c>; the app's entry assembly is looked in first, then every other loaded
/// assembly, so a UI library's shaders (BinaryVibrance.Kaldra.UI.Vysara's) are found once that library is loaded. Backend-neutral: it
/// deals only in file names and bytes, whatever the artifact's format.
/// </summary>
public static class EmbeddedShaders
{
	/// <summary>
	/// The bytes of the artifact <paramref name="nameWithExtension"/> (e.g. <c>"ui.spv"</c>), or null when no assembly
	/// embeds it. Fails loud when two libraries other than the app embed the same name, rather than picking one.
	/// </summary>
	public static byte[]? Resolve(string nameWithExtension)
	{
		ArgumentException.ThrowIfNullOrEmpty(nameWithExtension);
		var entry = Assembly.GetEntryAssembly();
		if (entry is not null && Read(entry, nameWithExtension) is { } own)
			return own;

		Assembly? found = null;
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly == entry || assembly.IsDynamic || assembly.GetManifestResourceInfo(ResourceName(assembly, nameWithExtension)) is null)
				continue;
			if (found is not null)
				throw new InvalidOperationException(
					$"Shader '{nameWithExtension}' is embedded by both {found.GetName().Name} and {assembly.GetName().Name}; " +
					"rename one, or embed the one the app means in the app itself, which is looked in first.");
			found = assembly;
		}
		return found is null ? null : Read(found, nameWithExtension);
	}

	private static string ResourceName(Assembly assembly, string nameWithExtension) =>
		assembly.GetName().Name + ".Shaders." + nameWithExtension;

	private static byte[]? Read(Assembly assembly, string nameWithExtension)
	{
		using var stream = assembly.GetManifestResourceStream(ResourceName(assembly, nameWithExtension));
		if (stream is null)
			return null;
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}
}
