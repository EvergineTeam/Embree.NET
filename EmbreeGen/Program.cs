using CppAst;
using System;
using System.IO;

namespace EmbreeGen
{
	internal class Program
	{
		private static int Main(string[] args)
		{
			var headersDir = Path.Combine(AppContext.BaseDirectory, "Headers");
			var headerFile = Path.Combine(headersDir, "embree4", "rtcore.h");

			// The parse is pinned to an x86_64 Windows triple and to the stub system headers in
			// Headers/stubs so the generated bindings come out identical whether the generator
			// runs on a Windows dev box or on the linux-x64 CI runner. The Windows triple also
			// makes clang define _WIN32, which is what rtcore_common.h expects for its ssize_t
			// typedef.
			var options = new CppParserOptions
			{
				ParseAsCpp = false,             // skip the __cplusplus / SYCL blocks in the headers
				ParseMacros = true,
				ParseComments = true,
				TargetCpu = CppTargetCpu.X86_64,
				TargetVendor = "pc",
				TargetSystem = "windows",
			};

			// CppAst only passes -xc++ when ParseAsCpp is set; its synthetic root file has no
			// recognizable extension, so C mode has to be requested explicitly.
			options.AdditionalArguments.Add("-xc");
			options.AdditionalArguments.Add("-std=c99");

			// Stubs first: they must shadow any libc/MSVC headers present on the machine.
			options.IncludeFolders.Add(Path.Combine(headersDir, "stubs"));
			options.IncludeFolders.Add(headersDir);

			var compilation = CppParser.ParseFile(headerFile, options);

			if (compilation.HasErrors)
			{
				Console.Error.WriteLine($"Failed to parse {headerFile}:");
				foreach (var message in compilation.Diagnostics.Messages)
				{
					Console.Error.WriteLine($"  {message}");
				}

				return 1;
			}

			foreach (var message in compilation.Diagnostics.Messages)
			{
				Console.WriteLine($"  {message}");
			}

			// bin/<Configuration>/<tfm>/<rid>/ -> repository root
			var outputPath = Path.GetFullPath(Path.Combine(
				AppContext.BaseDirectory,
				"..", "..", "..", "..", "..",
				"Evergine.Bindings.Embree", "Generated"));

			if (args.Length > 0)
			{
				outputPath = Path.GetFullPath(args[0]);
			}

			Directory.CreateDirectory(outputPath);

			Console.WriteLine($"Output: {outputPath}");
			CsCodeGenerator.Instance.Generate(compilation, outputPath);
			Console.WriteLine("Done.");

			return 0;
		}
	}
}
