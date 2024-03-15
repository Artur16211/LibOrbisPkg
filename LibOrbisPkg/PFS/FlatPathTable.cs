using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using LibOrbisPkg.Util;
using System.Runtime.InteropServices;

namespace LibOrbisPkg.PFS
{
  /// <summary>
  /// Represents the flat_path_table file, which is a mapping of filename hash to inode number
  /// that the Orbis OS can use to speed up lookups.
  /// </summary>
  public class FlatPathTable
  {
    public static bool HasCollision(List<FSNode> nodes)
    {
      var hashSet = new HashSet<uint>();
      foreach(var n in nodes)
      {
        var hash = HashFunction(n.FullPath());
        if (hashSet.Contains(hash))
          return true;
        hashSet.Add(hash);
      }
      return false;
    }

    public static (FlatPathTable, CollisionResolver) Create(List<FSNode> nodes)
    {
      var hashMap = new SortedDictionary<uint, uint>();
      var nodeMap = new Dictionary<uint, List<FSNode>>();
      bool collision = false;
      foreach (var n in nodes)
      {
        var hash = HashFunction(n.FullPath());
        if (hashMap.ContainsKey(hash))
        {
          hashMap[hash] = (uint)FlatType.Collision;
          nodeMap[hash].Add(n);
          collision = true;
        }
        else
        {
          hashMap[hash] = n.ino.Number;
          if (n is FSDir)
            hashMap[hash] |= (n.FullPath().StartsWith("/sce_sys") ? (uint)FlatType.SceSysDir : (uint)FlatType.Dir);
          else if (n.FullPath().StartsWith("/sce_sys"))
            hashMap[hash] |= (uint)FlatType.SceSysFile;
          nodeMap[hash] = new List<FSNode>();
          nodeMap[hash].Add(n);
        }
      }
      if(!collision)
      {
        return (new FlatPathTable(hashMap), (CollisionResolver)null);
      }

      uint offset = 0;
      var colEnts = new List<List<PfsDirent>>();
      foreach(var kv in hashMap.Where(kv => kv.Value == (uint)FlatType.Collision).ToList())
      {
        hashMap[kv.Key] = (uint)FlatType.Collision | offset;
        var entList = new List<PfsDirent>();
        colEnts.Add(entList);
        foreach(var node in nodeMap[kv.Key])
        {
          var d = new PfsDirent()
          {
            InodeNumber = node.ino.Number,
            Type = node is FSDir ? DirentType.Directory : DirentType.File,
            Name = node.FullPath(),
          };
          entList.Add(d);
          offset += (uint)d.EntSize;
        }
        offset += 0x18;
      }
      return (new FlatPathTable(hashMap), new CollisionResolver(colEnts));
    }

    public FlatPathTable(IMemoryAccessor r, long size, PfsReader.Dir root)
    {
      try
      {
        int unit = sizeof(uint) * 2;
        if (size < unit) throw new ArgumentException(string.Format("The file read is not a FlatPathTable; the length is only {0}, which must be greater than {1}", size, unit));
        if (size % unit > 0) throw new ArgumentException(string.Format("The file read is not a FlatPathTable; the length({0}) is incorrect and not divisible by {1}", size, unit));

        Dictionary<uint, string> hashNames = new Dictionary<uint, string>();
        GetAllDirHash(root, hashNames);

        HashMap = new SortedDictionary<uint, uint>();

        FlatRaw[] FlatRows = new FlatRaw[size / unit];
        r.ReadArray(0, FlatRows, 0, FlatRows.Length);
        FlatInfos = new List<FlatInfo>();
        foreach (FlatRaw raw in FlatRows)
        {
          HashMap[raw.Hash] = raw.Value;
          uint InodeNumber = raw.Value & ~0xF0000000u;
          FlatType flatType = (FlatType)(raw.Value & 0xF0000000u);
          string fullName = hashNames.TryGetValue(raw.Hash, out string nodeName) ? string.Format("{0} ( 0x{1:X} )", nodeName, raw.Hash) : raw.Hash.ToString("X");
          FlatInfos.Add(new FlatInfo(InodeNumber, flatType, fullName, raw));
        }
        FlatInfos.Sort((FlatInfo a, FlatInfo b) => {
          int compare;
          compare = a.Type.CompareTo(b.Type);
          if (compare == 0) compare = a.InodeNumber.CompareTo(b.InodeNumber);
          return compare;
        });
      }
      finally
      {
        r?.Dispose();
      }
    }

    public enum FlatType : uint
    {
      File       = 0,
      Dir        = 0x20000000u,
      SceSysFile = 0x40000000u,
      SceSysDir  = 0x60000000u,
      Collision  = 0x80000000u,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FlatRaw
    {
      public uint Hash;
      public uint Value;
    }

    public class FlatInfo
    {
      public string Info;
      public uint InodeNumber;
      public FlatType Type;
      public string FullPath;
      public FlatRaw Raw;

      public FlatInfo(uint inodeNumber, FlatType type, string fullPath, FlatRaw raw)
      {
        InodeNumber = inodeNumber;
        Type = type;
        FullPath = fullPath;
        Raw = raw;
        Info = string.Format("{0:0000}{1} = {2}", InodeNumber, Type, FullPath);
      }
    }

    public List<FlatInfo> FlatInfos {  get; private set; }

    public SortedDictionary<uint, uint> HashMap { get; private set; }

    public int Size => HashMap.Count * 8;

    /// <summary>
    /// Construct a flat_path_table out of the given filesystem nodes.
    /// </summary>
    /// <param name="nodes"></param>
    public FlatPathTable(SortedDictionary<uint, uint> hashMap)
    {
      HashMap = hashMap;
    }

    /// <summary>
    /// Write this file to the stream.
    /// </summary>
    /// <param name="s"></param>
    public void WriteToStream(Stream s)
    {
      foreach (var hash in HashMap.Keys)
      {
        s.WriteUInt32LE(hash);
        s.WriteUInt32LE((uint)HashMap[hash]);
      }
    }

    /// <summary>
    /// Retrieve the hash values of all nodes recursively and store them in a Dictionary.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="hashNames"></param>
    private void GetAllDirHash(PfsReader.Node root, Dictionary<uint, string> hashNames)
    {
      if (root == null) return;

      PfsReader.Node node = root;
      if (hashNames == null) hashNames = new Dictionary<uint, string>();

      if (!(node is PfsReader.Dir dir) || dir.children?.Count <= 0) return;

      foreach (var subNode in dir.children)
      {
        string fullName = subNode.FullName;
        if (fullName.StartsWith("/uroot")) fullName = fullName.Replace("/uroot", "");

        var subHash = HashFunction(fullName);
        if (!hashNames.ContainsKey(subHash)) hashNames.Add(subHash, fullName);
        else
          Console.WriteLine("Node: {0}, ContainsKey: {1}", subNode, subHash);

        if (!(subNode is PfsReader.Dir subDir) || !(subDir.children?.Count > 0)) continue;

        GetAllDirHash(subDir, hashNames);
      }
    }

    /// <summary>
    /// Hashes the given name for the table.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    private static uint HashFunction(string name)
    {
      uint hash = 0;
      foreach (var c in name)
        hash = char.ToUpper(c) + (31 * hash);
      return hash;
    }
  }

  public class CollisionResolver
  {
    public int Size { get; }

    public CollisionResolver(List<List<PfsDirent>> ents)
    {
      Entries = ents;
      var size = 0;
      foreach(var l in ents)
      {
        foreach(var e in l)
        {
          size += e.EntSize;
        }
        size += 0x18;
      }
      Size = size;
    }

    private List<List<PfsDirent>> Entries;
    public void WriteToStream(Stream s)
    {
      foreach(var d in Entries)
      {
        foreach(var e in d)
        {
          e.WriteToStream(s);
        }
        s.Position += 0x18;
      }
    }
  }
}