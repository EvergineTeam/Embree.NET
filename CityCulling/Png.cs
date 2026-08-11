using System;
using System.IO;
using System.IO.Compression;

namespace CityCulling
{
	/// <summary>
	/// A minimal PNG writer, so the sample stays dependency-free and runs on every RID the
	/// binding ships. System.Drawing would not do: it is Windows-only on modern .NET.
	/// </summary>
	internal static class Png
	{
		/// <summary>
		/// Writes an RGB image. <paramref name="rgb"/> is three bytes per pixel, row-major.
		/// </summary>
		public static void Write(string path, int width, int height, byte[] rgb)
		{
			// PNG wants a filter byte at the start of every scanline; 0 means "no filter".
            var raw = new byte[height * ((width * 3) + 1)];
			for (int y = 0; y < height; y++)
			{
				int source = y * width * 3;
				int destination = y * ((width * 3) + 1);
				raw[destination] = 0;
				Array.Copy(rgb, source, raw, destination + 1, width * 3);
			}

			using var file = File.Create(path);
			file.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

			var header = new byte[13];
			WriteBigEndian(header, 0, (uint)width);
			WriteBigEndian(header, 4, (uint)height);
			header[8] = 8;  // bit depth
			header[9] = 2;  // colour type: truecolour
			WriteChunk(file, "IHDR", header);

			WriteChunk(file, "IDAT", Deflate(raw));
			WriteChunk(file, "IEND", Array.Empty<byte>());
		}

		/// <summary>
		/// zlib stream: a two-byte header, raw deflate, and an Adler-32 of the uncompressed
		/// data. DeflateStream produces the middle part; the wrapper has to be added by hand
		/// because ZLibStream's header bytes are not what every decoder expects from a PNG.
		/// </summary>
		private static byte[] Deflate(byte[] data)
		{
			using var output = new MemoryStream();
			output.WriteByte(0x78); // CM = deflate, CINFO = 32K window
			output.WriteByte(0x01); // no preset dictionary, fastest compression

			using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
			{
				deflate.Write(data, 0, data.Length);
			}

			uint adler = Adler32(data);
			output.WriteByte((byte)(adler >> 24));
			output.WriteByte((byte)(adler >> 16));
			output.WriteByte((byte)(adler >> 8));
			output.WriteByte((byte)adler);

			return output.ToArray();
		}

		private static void WriteChunk(Stream stream, string type, byte[] data)
		{
			var length = new byte[4];
			WriteBigEndian(length, 0, (uint)data.Length);
			stream.Write(length);

			var payload = new byte[4 + data.Length];
			for (int i = 0; i < 4; i++)
			{
				payload[i] = (byte)type[i];
			}

			Array.Copy(data, 0, payload, 4, data.Length);
			stream.Write(payload);

			var crc = new byte[4];
			WriteBigEndian(crc, 0, Crc32(payload));
			stream.Write(crc);
		}

		private static void WriteBigEndian(byte[] buffer, int offset, uint value)
		{
			buffer[offset + 0] = (byte)(value >> 24);
			buffer[offset + 1] = (byte)(value >> 16);
			buffer[offset + 2] = (byte)(value >> 8);
			buffer[offset + 3] = (byte)value;
		}

		private static uint Adler32(byte[] data)
		{
			uint a = 1, b = 0;
			foreach (byte value in data)
			{
				a = (a + value) % 65521;
				b = (b + a) % 65521;
			}

			return (b << 16) | a;
		}

		private static readonly uint[] CrcTable = BuildCrcTable();

		private static uint[] BuildCrcTable()
		{
			var table = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				uint c = n;
				for (int k = 0; k < 8; k++)
				{
					c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				}

				table[n] = c;
			}

			return table;
		}

		private static uint Crc32(byte[] data)
		{
			uint c = 0xFFFFFFFFu;
			foreach (byte value in data)
			{
				c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
			}

			return c ^ 0xFFFFFFFFu;
		}
	}
}
