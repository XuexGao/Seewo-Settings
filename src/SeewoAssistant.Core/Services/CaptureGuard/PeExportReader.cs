using System.Buffers.Binary;

namespace SeewoAssistant.Core.Services.CaptureGuard;

/// <summary>
/// Reads an export's relative virtual address from a PE file on disk.
/// </summary>
/// <remarks>
/// Used only for cross-bitness injection: the address of <c>LoadLibraryW</c> inside
/// a target process of the other bitness is that process's <c>kernel32.dll</c> base
/// plus the export RVA, which must come from the matching on-disk binary. It is a
/// small, self-contained parser rather than a dependency because nothing else in the
/// app needs PE reading.
/// </remarks>
internal static class PeExportReader
{
    /// <summary>
    /// Returns the RVA of <paramref name="exportName"/> in the given PE file, or 0
    /// when it is absent or the file cannot be read.
    /// </summary>
    internal static int GetExportRva(string filePath, string exportName)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return 0;
            }

            // ---- DOS header --------------------------------------------------
            stream.Position = 0;
            if (reader.ReadUInt16() != 0x5A4D) // 'MZ'
            {
                return 0;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 0x18 > stream.Length)
            {
                return 0;
            }

            // ---- PE signature and COFF header --------------------------------
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) // 'PE\0\0'
            {
                return 0;
            }

            // COFF header: Machine(2) NumberOfSections(2) TimeDateStamp(4)
            // PointerToSymbolTable(4) NumberOfSymbols(4) SizeOfOptionalHeader(2) Characteristics(2)
            _ = reader.ReadUInt16(); // Machine - bitness is implied by the optional header magic
            var numberOfSections = reader.ReadUInt16();
            stream.Position += 12;
            var sizeOfOptionalHeader = reader.ReadUInt16();
            stream.Position += 2;

            var optionalHeaderOffset = stream.Position;
            var magic = reader.ReadUInt16();

            // 0x20B = PE32+, 0x10B = PE32
            var is64 = magic == 0x20B;
            if (!is64 && magic != 0x10B)
            {
                return 0;
            }

            // The export data directory is entry 0 of the data directory array.
            // Its offset within the optional header differs between PE32 and PE32+.
            var dataDirectoryOffset = optionalHeaderOffset + (is64 ? 112 : 96);
            stream.Position = dataDirectoryOffset;
            var exportDirectoryRva = reader.ReadInt32();
            var exportDirectorySize = reader.ReadInt32();

            if (exportDirectoryRva == 0 || exportDirectorySize == 0)
            {
                return 0;
            }

            // ---- section table -----------------------------------------------
            var sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
            var sections = new List<(uint VirtualAddress, uint VirtualSize, uint PointerToRawData, uint SizeOfRawData)>(numberOfSections);

            stream.Position = sectionTableOffset;
            for (var i = 0; i < numberOfSections; i++)
            {
                stream.Position += 8; // Name[8]
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var sizeOfRawData = reader.ReadUInt32();
                var pointerToRawData = reader.ReadUInt32();
                stream.Position += 16; // relocations, line numbers, characteristics

                sections.Add((virtualAddress, virtualSize, pointerToRawData, sizeOfRawData));
            }

            // ---- export directory --------------------------------------------
            var exportOffset = RvaToFileOffset((uint)exportDirectoryRva, sections);
            if (exportOffset < 0)
            {
                return 0;
            }

            stream.Position = exportOffset;

            // IMAGE_EXPORT_DIRECTORY begins with six fields that are not needed here.
            // They are consumed individually, with their real sizes written next to them,
            // instead of by one summed constant.
            //
            // A summed constant is exactly what broke this parser. The prefix is 20 bytes
            // - Characteristics 4, TimeDateStamp 4, MajorVersion 2, MinorVersion 2, Name
            // 4, Base 4 - but the code skipped 24, so every field from NumberOfFunctions
            // onwards was read four bytes late. numberOfNames then picked up
            // AddressOfFunctions, an RVA in the hundreds of thousands, and the
            // plausibility guard rejected it. GetExportRva returned 0 for every module,
            // which made cross-process capture protection impossible and reported the
            // cause to the user as an OS version problem.
            stream.Position += 4;      // Characteristics
            stream.Position += 4;      // TimeDateStamp
            stream.Position += 2 + 2;  // MajorVersion, MinorVersion
            stream.Position += 4;      // Name (RVA of the module name)
            stream.Position += 4;      // Base (ordinal bias)
                                       // = 20 bytes; NumberOfFunctions follows

            var numberOfFunctions = reader.ReadUInt32();
            var numberOfNames = reader.ReadUInt32();
            var addressOfFunctions = reader.ReadUInt32();
            var addressOfNames = reader.ReadUInt32();
            var addressOfNameOrdinals = reader.ReadUInt32();

            // Sanity bounds. A real module has at most a few thousand exports, so a
            // larger value means the directory was misread rather than that the module
            // genuinely has that many. This guard is what silently absorbed the offset
            // bug above, so it is kept - but the failure it hides must be visible, which
            // is why the caller now distinguishes "not found" from "unparseable".
            const uint MaxReasonableExports = 65536;

            if (numberOfNames == 0 ||
                numberOfNames > MaxReasonableExports ||
                numberOfFunctions > MaxReasonableExports)
            {
                return 0;
            }

            var namesOffset = RvaToFileOffset(addressOfNames, sections);
            var ordinalsOffset = RvaToFileOffset(addressOfNameOrdinals, sections);
            var functionsOffset = RvaToFileOffset(addressOfFunctions, sections);

            if (namesOffset < 0 || ordinalsOffset < 0 || functionsOffset < 0)
            {
                return 0;
            }

            for (var i = 0; i < numberOfNames; i++)
            {
                stream.Position = namesOffset + (i * 4);
                var nameRva = reader.ReadUInt32();

                var nameOffset = RvaToFileOffset(nameRva, sections);
                if (nameOffset < 0)
                {
                    continue;
                }

                stream.Position = nameOffset;
                var name = ReadNullTerminatedAscii(reader, 256);

                if (!string.Equals(name, exportName, StringComparison.Ordinal))
                {
                    continue;
                }

                stream.Position = ordinalsOffset + (i * 2);
                var ordinal = reader.ReadUInt16();

                if (ordinal >= numberOfFunctions)
                {
                    return 0;
                }

                stream.Position = functionsOffset + (ordinal * 4);
                var functionRva = reader.ReadInt32();

                // A forwarder has its RVA inside the export directory itself; such an
                // export has no code at that address in this module.
                if (functionRva >= exportDirectoryRva &&
                    functionRva < exportDirectoryRva + exportDirectorySize)
                {
                    return 0;
                }

                return functionRva;
            }

            return 0;
        }
        catch (IOException)
        {
            // Also covers EndOfStreamException, which derives from IOException.
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>Maps an RVA to a file offset using the section table.</summary>
    private static int RvaToFileOffset(
        uint rva,
        List<(uint VirtualAddress, uint VirtualSize, uint PointerToRawData, uint SizeOfRawData)> sections)
    {
        foreach (var section in sections)
        {
            // Use the larger of VirtualSize and SizeOfRawData: some linkers leave
            // VirtualSize smaller than the raw data for the last section.
            var span = Math.Max(section.VirtualSize, section.SizeOfRawData);

            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + span)
            {
                return (int)(section.PointerToRawData + (rva - section.VirtualAddress));
            }
        }

        // An RVA inside the headers maps one-to-one.
        return rva < 0x1000 ? (int)rva : -1;
    }

    private static string ReadNullTerminatedAscii(BinaryReader reader, int maxLength)
    {
        var bytes = new List<byte>(64);

        for (var i = 0; i < maxLength; i++)
        {
            var b = reader.ReadByte();
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }
}
