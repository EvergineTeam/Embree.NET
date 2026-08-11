using CppAst;
using System;
using System.IO;

namespace EmbreeGen
{
	internal class Program
	{
		private const string BindingProject = "Evergine.Bindings.Embree";

		/// <summary>
		/// Walks up from the executable looking for the binding project, and returns its Generated
		/// folder. Counting "../" segments instead would break as soon as the depth changes: the CI
		/// script runs the generator from bin/&lt;cfg&gt;/&lt;tfm&gt;/&lt;rid&gt;/publish/, one level deeper than a
		/// local `dotnet run`, and the generator would happily write the bindings to the wrong
		/// place without failing.
		/// </summary>
		private static bool TryFindBindingProject(out string generatedPath)
		{
			var directory = new DirectoryInfo(AppContext.BaseDirectory);

			while (directory != null)
			{
				var candidate = Path.Combine(directory.FullName, BindingProject);
				if (File.Exists(Path.Combine(candidate, $"{BindingProject}.csproj")))
				{
					generatedPath = Path.Combine(candidate, "Generated");
					return true;
				}

				directory = directory.Parent;
			}

			generatedPath = null;
			return false;
		}

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

			string outputPath;

			if (args.Length > 0)
			{
				outputPath = Path.GetFullPath(args[0]);
			}
			else if (!TryFindBindingProject(out outputPath))
			{
				Console.Error.WriteLine(
					$"Could not locate {BindingProject} above {AppContext.BaseDirectory}. " +
					"Pass the output directory as the first argument.");

				return 1;
			}

			Directory.CreateDirectory(outputPath);

			Console.WriteLine($"Output: {outputPath}");
			CsCodeGenerator.Instance.Generate(compilation, outputPath);
			Console.WriteLine("Done.");

			return 0;
		}
	}
}
