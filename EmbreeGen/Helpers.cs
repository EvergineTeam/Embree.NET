using CppAst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EmbreeGen
{
	public static class Helpers
	{
		/// <summary>
		/// Opaque pointer typedefs (RTCDevice, RTCScene, ...) that become handle structs.
		/// </summary>
		public static HashSet<string> HandleTypedefs = new HashSet<string>();

		/// <summary>
		/// Function pointer typedefs (RTCErrorFunction, RTCFilterFunctionN, ...).
		/// </summary>
		public static Dictionary<string, CppFunctionType> FunctionPointerTypedefs = new Dictionary<string, CppFunctionType>();

		/// <summary>
		/// Struct tags that are only forward declared in the public headers (RTCRayN, RTCHitN, ...).
		/// They have no layout, so they can only ever be used behind a pointer.
		/// </summary>
		public static HashSet<string> OpaqueStructs = new HashSet<string>();

		private static readonly Dictionary<string, string> csNameMappings = new Dictionary<string, string>()
		{
			{ "bool", "byte" },
			{ "uint8_t", "byte" },
			{ "uint16_t", "ushort" },
			{ "uint32_t", "uint" },
			{ "uint64_t", "ulong" },
			{ "int8_t", "sbyte" },
			{ "int16_t", "short" },
			{ "int32_t", "int" },
			{ "int64_t", "long" },
			{ "char", "byte" },
			{ "size_t", "nuint" },
			{ "ssize_t", "nint" },
			{ "intptr_t", "nint" },
			{ "uintptr_t", "nuint" },
		};

		public static string ConvertToCSharpType(CppType type, bool isPointer = false)
		{
			return GetCsTypeName(type, isPointer);
		}

		private static string GetCsTypeName(CppType type, bool isPointer = false)
		{
			if (type is CppPrimitiveType primitiveType)
			{
				return GetCsTypeName(primitiveType, isPointer);
			}

			if (type is CppQualifiedType qualifiedType)
			{
				return GetCsTypeName(qualifiedType.ElementType, isPointer);
			}

			if (type is CppEnum enumType)
			{
				var enumCsName = GetCsCleanName(enumType.Name);
				return isPointer ? enumCsName + "*" : enumCsName;
			}

			if (type is CppTypedef typedef)
			{
				// Function pointer typedef -> C# function pointer.
				if (FunctionPointerTypedefs.TryGetValue(typedef.Name, out var fnType))
				{
					var fnCsType = GetCsFunctionPointerType(fnType);
					return isPointer ? fnCsType + "*" : fnCsType;
				}

				// Opaque handle typedef (RTCDevice, RTCScene, ...) -> handle struct.
				if (HandleTypedefs.Contains(typedef.Name))
				{
					var handleName = StripPrefix(typedef.Name);
					return isPointer ? handleName + "*" : handleName;
				}

				if (csNameMappings.TryGetValue(typedef.Name, out string mapped))
				{
					return isPointer ? mapped + "*" : mapped;
				}

				// Anything else: resolve through the aliased type.
				return GetCsTypeName(typedef.ElementType, isPointer);
			}

			if (type is CppClass @class)
			{
				// Forward-declared-only structs have no C# representation; they are always
				// used behind a pointer, so surface them as void.
				var className = OpaqueStructs.Contains(@class.Name) ? "void" : StripPrefix(@class.Name);
				return isPointer ? className + "*" : className;
			}

			if (type is CppPointerType pointerType)
			{
				return GetCsTypeName(pointerType);
			}

			if (type is CppFunctionType functionType)
			{
				return GetCsFunctionPointerType(functionType);
			}

			if (type is CppArrayType arrayType)
			{
				return GetCsTypeName(arrayType.ElementType, isPointer);
			}

			return string.Empty;
		}

		private static string GetCsTypeName(CppPointerType pointerType)
		{
			if (pointerType.ElementType is CppFunctionType functionType)
			{
				return GetCsFunctionPointerType(functionType);
			}

			var element = pointerType.ElementType;
			if (element is CppQualifiedType qualified)
			{
				element = qualified.ElementType;
			}

			if (element is CppPointerType innerPointer)
			{
				return GetCsTypeName(innerPointer) + "*";
			}

			return GetCsTypeName(element, true);
		}

		private static string GetCsTypeName(CppPrimitiveType primitiveType, bool isPointer)
		{
			string result;

			switch (primitiveType.Kind)
			{
				case CppPrimitiveKind.Void: result = "void"; break;
				case CppPrimitiveKind.Bool: result = "byte"; break;      // C _Bool is 1 byte
				case CppPrimitiveKind.Char: result = "byte"; break;
				case CppPrimitiveKind.UnsignedChar: result = "byte"; break;
				case CppPrimitiveKind.WChar: result = "char"; break;
				case CppPrimitiveKind.Short: result = "short"; break;
				case CppPrimitiveKind.UnsignedShort: result = "ushort"; break;
				case CppPrimitiveKind.Int: result = "int"; break;
				case CppPrimitiveKind.UnsignedInt: result = "uint"; break;
				case CppPrimitiveKind.Long: result = "int"; break;       // 4 bytes on the win/linux LP64+LLP64 targets we ship
				case CppPrimitiveKind.UnsignedLong: result = "uint"; break;
				case CppPrimitiveKind.LongLong: result = "long"; break;
				case CppPrimitiveKind.UnsignedLongLong: result = "ulong"; break;
				case CppPrimitiveKind.Float: result = "float"; break;
				case CppPrimitiveKind.Double: result = "double"; break;
				case CppPrimitiveKind.LongDouble: result = "double"; break;
				default: result = string.Empty; break;
			}

			return isPointer ? result + "*" : result;
		}

		public static string GetCsFunctionPointerType(CppFunctionType functionType)
		{
			var sb = new StringBuilder("delegate* unmanaged[Cdecl]<");

			foreach (var param in functionType.Parameters)
			{
				sb.Append(ConvertToCSharpType(param.Type));
				sb.Append(", ");
			}

			sb.Append(ConvertToCSharpType(functionType.ReturnType));
			sb.Append('>');

			return sb.ToString();
		}

		public static string GetCsCleanName(string name)
		{
			if (HandleTypedefs.Contains(name))
				return StripPrefix(name);

			if (csNameMappings.TryGetValue(name, out string mappedName))
				return mappedName;

			return StripPrefix(name);
		}

		public enum Family
		{
			param,
			field,
			ret,
		}

		/// <summary>
		/// Adjusts a converted type for the context it is emitted in.
		/// C <c>bool</c> is already mapped to <c>byte</c>, so only strings need special casing.
		/// </summary>
		public static string ShowAsMarshalType(string type, Family family)
		{
			switch (type)
			{
				case "byte*":
					return family == Family.param ? "[MarshalAs(UnmanagedType.LPStr)] string" : "byte*";
				default:
					return type;
			}
		}

		/// <summary>
		/// Strip the Embree C prefix from a name: RTC_ / RTC / rtc.
		/// e.g. RTCFormat -> Format, rtcNewDevice -> NewDevice, RTC_MAX_TIME_STEP_COUNT -> MAX_TIME_STEP_COUNT.
		/// </summary>
		public static string StripPrefix(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name;

			if (name.StartsWith("RTC_", StringComparison.Ordinal) && name.Length > 4)
				return name.Substring(4);

			if (name.StartsWith("RTC", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
				return name.Substring(3);

			if (name.StartsWith("rtc", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
				return name.Substring(3);

			return name;
		}

		/// <summary>
		/// Capitalize the first letter of a struct field name (camelCase/snake_case -> PascalCase).
		/// "lower_x" -> "LowerX", "geomID" -> "GeomID", "instStackSize" -> "InstStackSize".
		/// </summary>
		public static string PascalCaseField(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name;

			if (name.Contains('_'))
			{
				var parts = name.Split('_');
				var sb = new StringBuilder();
				foreach (var part in parts)
				{
					if (part.Length == 0) continue;
					sb.Append(char.ToUpperInvariant(part[0]));
					sb.Append(part.Substring(1));
				}
				return sb.ToString();
			}

			return char.ToUpperInvariant(name[0]) + name.Substring(1);
		}

		/// <summary>
		/// Longest common prefix at underscore boundaries among SCREAMING_CASE names.
		/// e.g. RTC_FORMAT_UCHAR / RTC_FORMAT_FLOAT -> "RTC_FORMAT_".
		/// </summary>
		public static string FindCommonPrefix(IEnumerable<string> names)
		{
			var list = names.ToList();
			if (list.Count < 2) return string.Empty;

			string first = list[0];
			int prefixLen = first.Length;

			for (int i = 1; i < list.Count; i++)
			{
				prefixLen = Math.Min(prefixLen, list[i].Length);
				for (int j = 0; j < prefixLen; j++)
				{
					if (first[j] != list[i][j])
					{
						prefixLen = j;
						break;
					}
				}
			}

			string commonPrefix = first.Substring(0, prefixLen);

			// Trim back to the last underscore so we never cut a word in half.
			int lastUnderscore = commonPrefix.LastIndexOf('_');
			if (lastUnderscore < 0)
				return string.Empty;

			return commonPrefix.Substring(0, lastUnderscore + 1);
		}

		/// <summary>
		/// SCREAMING_CASE -> PascalCase. "INVALID_PARAM" -> "InvalidParam", "FLOAT2X2_ROW_MAJOR" -> "Float2x2RowMajor".
		/// Segments starting with a digit are kept verbatim so "2D" stays "2D".
		/// </summary>
		public static string ScreamingToPascalCase(string screaming)
		{
			if (string.IsNullOrEmpty(screaming))
				return screaming;

			if (!screaming.Contains('_') && !screaming.All(c => char.IsUpper(c) || char.IsDigit(c)))
				return screaming;

			var parts = screaming.Split('_');
			var sb = new StringBuilder();
			foreach (var part in parts)
			{
				if (part.Length == 0) continue;
				if (char.IsDigit(part[0]))
				{
					sb.Append(part.ToUpperInvariant());
				}
				else
				{
					sb.Append(char.ToUpperInvariant(part[0]));
					sb.Append(part.Substring(1).ToLowerInvariant());
				}
			}

			var result = sb.ToString();

			// A leading digit is not a valid C# identifier start.
			if (char.IsDigit(result[0]))
				result = "_" + result;

			return result;
		}

		/// <summary>
		/// Total element count of a (possibly multi dimensional) C array, and its innermost element type.
		/// float[1][16] -> 16, float.
		/// </summary>
		public static int GetFlattenedArraySize(CppArrayType arrayType, out CppType elementType)
		{
			int size = arrayType.Size;
			CppType element = arrayType.ElementType;

			while (element is CppArrayType inner)
			{
				size *= inner.Size;
				element = inner.ElementType;
			}

			elementType = element;
			return size;
		}

		private static readonly HashSet<string> fixedBufferTypes = new HashSet<string>
		{
			"bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char", "float", "double",
		};

		public static bool CanBeFixedBuffer(string csType) => fixedBufferTypes.Contains(csType);

		public static string EscapeReservedKeyword(string name)
		{
			switch (name)
			{
				case "abstract": case "as": case "base": case "bool": case "break": case "byte":
				case "case": case "catch": case "char": case "checked": case "class": case "const":
				case "continue": case "decimal": case "default": case "delegate": case "do":
				case "double": case "else": case "enum": case "event": case "explicit": case "extern":
				case "false": case "finally": case "fixed": case "float": case "for": case "foreach":
				case "goto": case "if": case "implicit": case "in": case "int": case "interface":
				case "internal": case "is": case "lock": case "long": case "namespace": case "new":
				case "null": case "object": case "operator": case "out": case "override": case "params":
				case "private": case "protected": case "public": case "readonly": case "ref":
				case "return": case "sbyte": case "sealed": case "short": case "sizeof":
				case "stackalloc": case "static": case "string": case "struct": case "switch":
				case "this": case "throw": case "true": case "try": case "typeof": case "uint":
				case "ulong": case "unchecked": case "unsafe": case "ushort": case "using":
				case "virtual": case "void": case "volatile": case "while":
					return "@" + name;
				default:
					return name;
			}
		}

		public static void PrintComments(StreamWriter file, CppComment comment, string tabs = "", bool newLine = false)
		{
			if (comment == null)
				return;

			var lines = new List<string>();
			GetText(comment, lines);

			// Drop leading/trailing blank lines produced by the doxygen-less block comments.
			while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
			while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);

			if (lines.Count == 0)
				return;

			if (newLine) file.WriteLine();

			file.WriteLine($"{tabs}/// <summary>");
			foreach (var line in lines)
			{
				file.WriteLine($"{tabs}/// {Escape(line)}");
			}
			file.WriteLine($"{tabs}/// </summary>");
		}

		private static string Escape(string text) =>
			text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

		private static void GetText(CppComment comment, List<string> lines)
		{
			switch (comment.Kind)
			{
				case CppCommentKind.Text:
					lines.Add(((CppCommentTextBase)comment).Text);
					break;
				case CppCommentKind.Paragraph:
				case CppCommentKind.Full:
					if (comment.Children != null)
					{
						foreach (var child in comment.Children)
						{
							GetText(child, lines);
						}
					}
					break;
				default:
					break;
			}
		}
	}
}
