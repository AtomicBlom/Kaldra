using System.Text.Json.Serialization;

namespace Loom.Input;

/// <summary>
/// The <see cref="JsonSerializerContext"/> that serializes <see cref="BindingsDocument"/>. Using source-generated
/// metadata keeps binding persistence reflection-free and therefore NativeAOT- and trim-safe.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BindingsDocument))]
internal sealed partial class InputBindingsJsonContext : JsonSerializerContext;
