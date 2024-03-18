using System;
using System.Runtime.InteropServices;
using System.IO.MemoryMappedFiles;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using LibOrbisPkg.Util;

namespace LibOrbisPkg.PFS
{
  /// <summary>
  /// This wraps a Memory mapped file view of a PFSC file so that you can access it
  /// as though it were uncompressed.
  /// </summary>
  public class PFSCReader : IMemoryReader
  {
    /// <summary>
    /// Ansi:PFSC
    /// </summary>
    public const int Magic = 0x43534650;
    public PFSCHdr Hdr { get; private set; }
    public string[] SectorOffsetInfo { get; private set; }

    private IMemoryAccessor _accessor;
    /// <summary>
    /// The SectorMap stores the offset of each sector starting from the BlockOffsets position in the PFSC Header (usually at 0x400).
    /// The initial offset is determined by the DataStart value in the PFSC Header (which is greater than or equal to 0x10000).
    /// The size of each sector is calculated by subtracting the current offset from the offset of the next sector.
    /// </summary>
    private long[] sectorMap;

    /// <summary>
    /// Creates a PFSCReader
    /// </summary>
    /// <param name="va">An IMemoryAccessor containing the PFSC file</param>
    /// <exception cref="ArgumentException">Thrown when the accessor is not a view of a PFSC file.</exception>
    public PFSCReader(IMemoryAccessor va)
    {
      _accessor = va;
      _accessor.Read(0, out PFSCHdr hdr);
      if (hdr.Magic != Magic)
        throw new ArgumentException("Not a PFSC file: missing PFSC magic");
      if (hdr.Unk4 != 0)
        throw new ArgumentException($"Not a PFSC file: unknown data at 0x4 (expected 0, got {hdr.Unk4})");
      //if (hdr.Unk8 != 6)
      //  throw new ArgumentException($"Not a PFSC file: unknown data at 0x8 (expected 6, got {hdr.Unk8})");
      if (hdr.BlockSz != (int)hdr.BlockSz2)
        throw new ArgumentException("Not a PFSC file: block size mismatch");

      var num_blocks = (int)(hdr.DataLength / hdr.BlockSz2);
      sectorMap = new long[num_blocks + 1];
      _accessor.ReadArray(hdr.BlockOffsets, sectorMap, 0, num_blocks + 1);

      long tmpOffset = 0;
      SectorOffsetInfo = new string[(sectorMap.Length / 10) + (sectorMap.Length % 10 == 0 ? 0 : 1)];
      for (int idx = 0; idx < sectorMap.Length; idx++)
      {
        int index = idx / 10;
        var sectorOffset = sectorMap[idx];

        if (idx % 10 == 0)
          SectorOffsetInfo[index] = string.Format("[{0:0000}] 0x{1:X8} ( {1} ) => 0x{2:X} ( {2} )", idx, sectorOffset, tmpOffset > 0 ? sectorOffset - tmpOffset : 0);
        else
          SectorOffsetInfo[index] += string.Format(", 0x{0:X} ( {0} )", sectorOffset - tmpOffset);

        tmpOffset = sectorOffset;
      }
      Hdr = hdr;
    }

    /// <summary>
    /// The ParseSectorOffsetInfo method requires the inner PFS SuperRoot as its input parameter.
    /// </summary>
    /// <param name="root">inner PFS SuperRoot Dir</param>
    public void ParseSectorOffsetInfo(PfsReader.Dir root)
    {
      if (root == null) return;

      var sectorStart     = (int)(root.offset / Hdr.BlockSz);
      var sectorEnd       = (int)((root.offset + root.size) / Hdr.BlockSz);
      var sectorOffset    = sectorMap[sectorStart];
      var sectorOffsetEnd = sectorMap[sectorEnd];

      List<string> sectorInfo = new List<string>
      {
        string.Format("{0:000000}-______ => 0x{1:X8} ( {1:0000000000} ) ~ 0x________ ( __________ ) {2}",
        0, sectorMap[0], "Inner PFS Header"),
        string.Format("{0:000000}-{1:000000} => 0x{2:X8} ( {2:0000000000} ) ~ 0x{3:X8} ( {3:0000000000} ) {4}",
        1, sectorStart - 1, sectorMap[1], sectorMap[sectorStart - 1], "Inner PFS Inodes"), //sectorEnd = hdr.DinodeBlockCount * hdr.BlockSize
        string.Format("{0:000000}-{1:000000} => 0x{2:X8} ( {2:0000000000} ) ~ 0x{3:X8} ( {3:0000000000} ) Ino:{4:0000} Dir:{5}",
        sectorStart, sectorEnd, sectorOffset, sectorOffsetEnd, root.ino, root.FullName != "" ? root.FullName : "super_root")
      };

      foreach (var node in root.GetAllNodes())
      {
        sectorStart     = (int)(node.offset / Hdr.BlockSz);
        sectorEnd = (int)((node.offset + node.size) / Hdr.BlockSz);
        sectorOffset = sectorMap[sectorStart];
        if (sectorStart == sectorEnd || (node is PfsReader.Dir && sectorEnd - sectorStart == 1))
        {
          sectorInfo.Add(string.Format("{0:000000}-______ => 0x{1:X8} ( {1:0000000000} ) ~ 0x________ ( __________ ) Ino:{2:0000} {3}:{4}",
            sectorStart, sectorOffset, node.ino, node is PfsReader.Dir ? "Dir" : "File", node.FullName));
        }
        else
        {
          sectorOffsetEnd = sectorMap[sectorEnd];
          sectorInfo.Add(string.Format("{0:000000}-{1:000000} => 0x{2:X8} ( {2:0000000000} ) ~ 0x{3:X8} ( {3:0000000000} ) Ino:{4:0000} {5}:{6}",
            sectorStart, sectorEnd, sectorOffset, sectorOffsetEnd, node.ino, node is PfsReader.Dir ? "Dir" : "File", node.FullName));
        }
      }

      SectorOffsetInfo = sectorInfo.OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// Creates a PFSCReader
    /// </summary>
    /// <param name="va">A ViewAccessor containing the PFSC file</param>
    /// <exception cref="ArgumentException">Thrown when the accessor is not a view of a PFSC file.</exception>
    public PFSCReader(MemoryMappedViewAccessor va) : this(new MemoryMappedViewAccessor_(va))
    { }

    public PFSCReader(IMemoryReader r) : this(new MemoryAccessor(r))
    { }

    public int SectorSize => Hdr.BlockSz;
    
    /// <summary>
    /// Reads the sector at the given index into the given byte array.
    /// </summary>
    /// <param name="idx">sector index (multiply by SectorSize to get the byte offset)</param>
    /// <param name="output">byte array where sector will be written</param>
    public void ReadSector(int idx, byte[] output)
    {
      if (idx < 0 || idx > sectorMap.Length - 1)
        throw new ArgumentException("Invalid index", nameof(idx));

      var sectorOffset = sectorMap[idx];
      var sectorSize = sectorMap[idx + 1] - sectorOffset;

      if (sectorSize == Hdr.BlockSz2)
      {
        // fast case: uncompressed sector
        _accessor.Read(sectorOffset, output, 0, Hdr.BlockSz);
      }
      else if (sectorSize > Hdr.BlockSz2)
      {
        Array.Clear(output, 0, Hdr.BlockSz);
      }
      else
      {
        // slow case: compressed sector
        var sectorBuf = new byte[(int)sectorSize - 2];
        _accessor.Read(sectorOffset + 2, sectorBuf, 0, (int)sectorSize - 2);
        using (var bufStream = new MemoryStream(sectorBuf))
        using (var ds = new DeflateStream(bufStream, CompressionMode.Decompress))
        {
          var totalRead = 0;
          var byteRead = 0;
          while (totalRead < output.Length)
          {
            byteRead = ds.Read(output, totalRead, output.Length - totalRead); //ds.Read(output, 0, hdr.BlockSz);
            if (byteRead == 0) break;
            totalRead += byteRead;
          }
          //Is there any workaround for .Net 6 System.IO.Compression issue.
          //https://stackoverflow.com/a/72955102
          //Breaking changes in .NET 6: Partial and zero-byte reads in DeflateStream, GZipStream, and CryptoStream
          //https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/partial-byte-reads-in-streams
        }
      }
    }
    
    /// <summary>
    /// The parameters for the Write Action are as follows:
    /// sectorBuffer    : Sector data read from the current src position
    /// offsetIntoSector: Convert the src position to the current relative offset within the Sector Block.
    /// bufferedRead    : The remaining size that can be read from the current Sector Block, or the specified count value; only the smaller of the two will be read.
    /// </summary>
    /// <param name="src"></param>
    /// <param name="count"></param>
    /// <param name="Write">The Write Action will start reading values from the offsetIntoSector position of sectorBuffer, with a quantity of bufferedRead, and copy them to the buffer array.</param>
    /// <exception cref="ArgumentException"></exception>
    private void Read(long src, long count, Action<byte[],int,int> Write)
    {
      if (src + count > Hdr.DataLength)
        throw new ArgumentException("Attempt to read beyond end of file");
      var sectorSize = Hdr.BlockSz;
      var sectorBuffer = new byte[sectorSize];
      var currentSector = (int)(src / sectorSize);
      var offsetIntoSector = (int)(src - (sectorSize * currentSector));
      ReadSector(currentSector, sectorBuffer);
      while (count > 0 && src < Hdr.DataLength)
      {
        if (offsetIntoSector >= sectorSize)
        {
          currentSector++;
          ReadSector(currentSector, sectorBuffer);
          offsetIntoSector = 0;
        }
        int bufferedRead = (int)Math.Min(sectorSize - offsetIntoSector, count);
        Write(sectorBuffer, offsetIntoSector, bufferedRead);
        count -= bufferedRead;
        offsetIntoSector += bufferedRead;
        src += bufferedRead;
      }
    }

    /// <summary>
    /// Read `count` bytes at location `src` into the writeable Stream `dest`
    /// </summary>
    /// <param name="src">Byte offset into PFSC</param>
    /// <param name="count">Number of bytes to read</param>
    /// <param name="dest">Output stream</param>
    public void Read(long src, long count, Stream dest)
    {
      Read(src, count, dest.Write);
    }

    /// <summary>
    /// Read `count` bytes at location `src` into the byte array at offset `offset`
    /// </summary>
    /// <param name="src">Byte offset into PFSC</param>
    /// <param name="buffer">Output byte array</param>
    /// <param name="offset">Offset into byte array</param>
    /// <param name="count">Number of bytes to read</param>
    public void Read(long src, byte[] buffer, int offset, int count)
    {
      // The Write Action will start reading values from the offsetIntoSector position of sectorBuffer, with a quantity of bufferedRead, and copy them to the buffer array.
      // sectorBuffer: Sector data read from the current src position
      // offsetIntoSector: Convert the src position to the current relative offset within the Sector Block.
      // bufferedRead: The remaining size that can be read from the current Sector Block, or the specified count value; only the smaller of the two will be read.
      Read(src, count, (sectorBuffer, offsetIntoSector, bufferedRead) =>
      {
        Buffer.BlockCopy(sectorBuffer, offsetIntoSector, buffer, offset, bufferedRead);
        offset += bufferedRead;
      });
    }

    public void Dispose()
    {
      _accessor.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 0x30)]
    public struct PFSCHdr
    {
      /// <summary>
      /// Ansi:PFSC／BigEndian:0x50465343／LittleEndian:0x43534650
      /// </summary>
      public int Magic;
      public int Unk4;
      /// <summary>
      /// The Unk8 in PFSC's Header is 6 when the PKG is uncompressed, and 2 if it is compressed.
      /// </summary>
      public int Unk8;
      public int BlockSz;
      public long BlockSz2;
      public long BlockOffsets;
      public ulong DataStart;
      public long DataLength;
    }
  }
}
