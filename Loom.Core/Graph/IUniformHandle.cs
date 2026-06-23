namespace Loom.Core.Graph;

/// <summary>
/// A handle that can be <c>.Reads</c>'d as a shader uniform/constant input — an app-writable parameter buffer
/// (<see cref="ConstantsHandle"/>). The marker lets <see cref="PassBuilder.Reads(IUniformHandle)"/> reject category errors at
/// compile time, exactly as <see cref="ITextureHandle"/> / <see cref="IStorageHandle"/> do for their verbs.
/// </summary>
public interface IUniformHandle;
