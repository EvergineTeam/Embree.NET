using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Evergine.Bindings.Embree
{
	/// <summary>
	/// Resolves the native embree4 library from the RID-specific <c>runtimes/</c> folder.
	/// </summary>
	/// <remarks>
	/// When this assembly is consumed as a NuGet package the .NET host already unpacks
	/// <c>runtimes/&lt;rid&gt;/native/</c> next to the application, and the default probing finds the
	/// library. When it is consumed through a project reference that RID resolution does not happen,
	/// the whole <c>runtimes/</c> tree is simply copied to the output folder, and the default probing
	/// misses it. This resolver covers that case so both consumption modes behave the same.
	/// </remarks>
	internal static class NativeLibraryResolver
	{
		// CA2255 warns against module initializers in libraries because consumers cannot control
		// when they run. Registering a DllImport resolver is the documented exception: it must be
		// in place before the first P/Invoke, and there is no entry point of our own to hook.
#pragma warning disable CA2255
		[ModuleInitializer]
#pragma warning restore CA2255
		internal static void Initialize()
		{
			NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
		}

		private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			var assemblyDirectory = Path.GetDirectoryName(assembly.Location);

			if (!string.IsNullOrEmpty(assemblyDirectory))
			{
				var runtimesFolder = Path.Combine(
					assemblyDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");

				if (Directory.Exists(runtimesFolder))
				{
					foreach (var candidate in Directory.GetFiles(runtimesFolder, $"*{libraryName}*"))
					{
						if (NativeLibrary.TryLoad(candidate, out var handle))
						{
							return handle;
						}
					}
				}
			}

			// Fall back to the default probing logic (NuGet deployment, system-wide install, ...).
			return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var defaultHandle)
				? defaultHandle
				: IntPtr.Zero;
		}
	}
}
