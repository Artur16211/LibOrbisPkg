using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Collections;
using System.IO;

using LibOrbisPkg.PKG;
using LibOrbisPkg.Util;
using System.Threading.Tasks;
using System.IO.MemoryMappedFiles;
using LibOrbisPkg.PFS;
using LibOrbisPkg.SFO;
using System.Text.RegularExpressions;

namespace PkgEditor.Views
{
  public partial class PkgView : View
  {
    public override bool CanSave => false;
    public override bool CanSaveAs => false;

    public override void Close()
    {
      pkgFile.Dispose();
      va?.Dispose();
    }

    private Pkg pkg;
    private MemoryMappedFile pkgFile;
    private MemoryMappedViewAccessor va;
    private string passcode;
    public PkgView(string path)
    {
      InitializeComponent();
      if (path == null) return;
      pkgFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
      using (var s = pkgFile.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
        pkg = new PkgReader(s).ReadPkg();
      var pkgEntriesSum = pkg.Metas.Metas.Sum(meat => meat.DataSize);
      var pkgChunkShaFileData = 4 * (pkg.Header.package_size / 0x10000);
      var pkgSize = (long)pkg.Header.body_offset + pkgEntriesSum + (long)pkg.Header.pfs_image_size; // pfs_image_size(pfsSize) = OuterPfs.Ndblock * BlockSize
      pkgSize += ((pkgSize + 16) / 0x10000) * 4;
      var pkgMetaDataSize = (uint)(pkgSize / 0x10000L) * 4;
      ObjectPreview(pkg.Header, pkgHeaderTreeView);
      ObjectPreview(pkg.Metas, pkgHeaderTreeView, true, true);
      ObjectPreview(("pkg.EntryNames.NameList", (object)pkg.EntryNames.NameList), pkgHeaderTreeView, true);
      ObjectPreview(("pkg.EntryNames.Names", (object)pkg.EntryNames.Names), pkgHeaderTreeView, true);
      ObjectPreview(("pkg size info", (object)new string[] { 
        string.Format("pkg.Metas.Metas.Sum = 0x{0:X} ( {0} )", pkgEntriesSum),
        string.Format("pkg Size = 0x{0:X} ( {0} )", pkgSize),
        string.Format("pkg.ChunkSha.FileData = 0x{0:X} ( {0} )", pkgChunkShaFileData),
        string.Format("pkg.ChunkSha.meta.DataSize = 0x{0:X} ( {0} )", pkgMetaDataSize),
      }), pkgHeaderTreeView, true);
      try
      {
        if (pkg.Header.pfs_image_size > 0) //The PKG might be an additional content package without extra data when pfs_image_size is 0.
          using (var s = pkgFile.CreateViewStream((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size, MemoryMappedFileAccess.Read))
            ObjectPreview(PfsHeader.ReadFromStream(s), pfsHeaderTreeView);
      }
      catch (Exception e)
      {
        pkgHeaderTabControl.TabPages.Remove(outerPfsHeaderTabPage);
        MessageBox.Show("Error loading outer PFS: " + e.Message + Environment.NewLine + "Please report this issue at https://github.com/maxton/LibOrbisPkg/issues");
      }
      if (pkg.Metas.Metas.Where(entry => entry.id == EntryId.ICON0_PNG).FirstOrDefault() is MetaEntry icon0)
      {
        using (var s = pkgFile.CreateViewStream(icon0.DataOffset, icon0.DataSize, MemoryMappedFileAccess.Read))
        {
          pictureBox1.Image = Image.FromStream(s);
        }
      }
      contentIdTextBox.Text = pkg.Header.content_id;
      titleTextBox.Text = pkg.ParamSfo.ParamSfo["TITLE"]?.ToString();
      sizeLabel.Text = FileView.HumanReadableFileSize((long)pkg.Header.package_size);
      var category = pkg.ParamSfo.ParamSfo["CATEGORY"].ToString();
      typeLabel.Text = SfoData.SfoTypes.Where(x => x.Category == category).FirstOrDefault() is SfoType t ? t.Description : "Unknown";
      versionLabel.Text = pkg.ParamSfo.ParamSfo["VERSION"]?.ToString();
      if (pkg.ParamSfo.ParamSfo["APP_VER"] is Utf8Value v)
      {
        appVerLabel.Text = v.Value;
      }
      else
      {
        appVerLabelLabel.Visible = false;
        appVerLabel.Visible = false;
      }
      var sfoEditor = new SFOView(pkg.ParamSfo.ParamSfo, true);
      sfoEditor.Dock = DockStyle.Fill;
      tabPage1.Controls.Add(sfoEditor);


      if (pkg.CheckPasscode("00000000000000000000000000000000"))
      {
        passcode = "00000000000000000000000000000000";
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
      }
      else if (KeyDB.Instance.Passcodes.TryGetValue(pkg.Header.content_id, out var keydbPasscode)
        && pkg.CheckPasscode(keydbPasscode))
      {
        passcode = keydbPasscode;
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
      }
      else if (pkg.GetEkpfs() is byte[] ek && ek != null)
      {
        ekpfs = ek;
      }
      else if (KeyDB.Instance.EKPFS.TryGetValue(pkg.Header.content_id, out var keydbEkpfs)
        && keydbEkpfs.FromHexCompact() is byte[] ekpfsBytes
        && pkg.CheckEkpfs(ekpfsBytes))
      {
        ekpfs = ekpfsBytes;
      }
      else if (KeyDB.Instance.XTS.TryGetValue(
          pkg.Header.content_id + "-" + pkg.Header.pfs_image_digest.ToHexCompact().Substring(0, 8),
          out var xtsKey)
      || KeyDB.Instance.XTS.TryGetValue(pkg.Header.content_id, out xtsKey))
      {
        data = xtsKey.Data.FromHexCompact();
        tweak = xtsKey.Tweak.FromHexCompact();
      }

      if (!ReopenFileView())
      {
        ekpfs = null;
        passcode = null;
        data = null;
        tweak = null;
      }

      for (int idx = 0; idx < pkg.Metas.Metas.Count; idx++)
      {
        var entrie = pkg.Metas.Metas[idx];
        string flags = "";
        if (entrie.Flags1 > 0) flags += string.Format("1:0x{0:X} ", entrie.Flags1);
        if (entrie.Flags2 > 0) flags += string.Format("2:0x{0:X}", entrie.Flags2);
        var lvi = new ListViewItem(new[] {
          string.Format("{0:00}. {1}", idx + 1, entrie.id),
          string.Format("0x{0:X} ( {0} )", entrie.DataSize),
          string.Format("0x{0:X} ( {0} )", entrie.DataOffset),
          entrie.Encrypted ? "Yes" : "No",
          entrie.KeyIndex.ToString(),
          flags,
          string.Format("0x{0:X} ( {0} )", entrie.NameTableOffset),
        });
        lvi.Tag = entrie;
        entriesListView.Items.Add(lvi);
      }
    }

    void ObjectPreview(object obj, TreeView tv, bool isAppend = false, bool isAppendAddDivider = false)
    {
      if (isAppend)
      {
        if (isAppendAddDivider) tv.Nodes.Add("____________________");
      }
      else tv.Nodes.Clear();
      AddObjectNodes(obj, tv.Nodes);
    }

    string toString(object obj)
    {
      switch (obj)
      {
        case Byte _:
        case UInt16 _:
        case UInt32 _:
        case UInt64 _:
        case Int16 _:
        case Int32 _:
        case Int64 _:
          //Display the values of PKG Info's Header in both hexadecimal and decimal simultaneously.
          string result = string.Format("0x{0:X}", obj);
          if (result != "0x0") result += string.Format(" ( {0} )", obj);
          return result;
        default:
          return obj.ToString();
      }
    }

    /// <summary>
    /// Adds the given object's public fields to the given TreeNodeCollection.
    /// </summary>
    void AddObjectNodes(object obj, TreeNodeCollection nodes)
    {
      if (obj == null) return;
      if (obj is string str)
      {
        nodes.Add(str);
        return;
      }

      if (obj is ValueTuple<string, object> objTuple)
      {
        AddNodes(objTuple.Item2, nodes, objTuple.Item1);
      }
      else AddNodes(obj, nodes);
    }

    void AddNodes(object obj, TreeNodeCollection nodes, string descName = null)
    {
      if (obj is string str)
      {
        if (!string.IsNullOrEmpty(descName)) str = string.Format("{0} {1}", descName, str);

        nodes.Add(str);
      }
      else if(obj is Object[] objArr)
      {
        AddArrayNodes(objArr, string.IsNullOrEmpty(descName) ? "Array" : descName, nodes); //foreach (Object o1 in objArr) nodes.Add(toString(o1));
      }
      else if (obj is IList objList)
      {
        AddArrayNodes(objList.Cast<object>().ToArray(), string.IsNullOrEmpty(descName) ? "List" : descName, nodes); //foreach (Object o1 in objList) nodes.Add(toString(o1));
      }
      else if (obj is IDictionary objDict)
      {
        List<object> dictList = new List<object>();
        foreach (DictionaryEntry o1 in objDict) dictList.Add(string.Format("{0} = {1}", toString(o1.Key), toString(o1.Value)));
        AddArrayNodes(dictList.Cast<object>().ToArray(), string.IsNullOrEmpty(descName) ? "Dictionary" : descName, nodes);
      }
      else
      {
        var infoName = "";
        var fields = obj.GetType().GetFields();
        if (!string.IsNullOrEmpty(descName))
        {
          infoName = descName + " ";
          if (fields?.Length > 1)
          {
            var val = Regex.IsMatch(descName, @"\[\d+\]") ? string.Format("{0}: {1}", fields[0].Name, fields[0].GetValue(obj)) : "";
            var n = new TreeNode(descName + " " + val);
            nodes.Add(n);
            infoName = "";
            nodes = n.Nodes;
          }
        }
        foreach (var f in fields)
        {
          if (f.IsLiteral) continue;
          var val = f.GetValue(obj);
          if (val is byte[] b)
          {
            nodes.Add(infoName + f.Name + " = " + LibOrbisPkg.Util.Crypto.AsHexCompact(b));
          }
          else if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum)
          {
            if (val != null)
            {
              nodes.Add(infoName + f.Name + " = " + toString(val));
            }
          }
          else if (f.FieldType.IsArray)
          {
            AddArrayNodes(f.GetValue(obj) as Array, f.Name, nodes);
          }
          else if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>))
          {
            var internalType = f.FieldType.GetGenericArguments()[0];
            AddArrayNodes((f.GetValue(obj) as IList).Cast<object>().ToArray(), f.Name, nodes);
          }
          else
          {
            var node = new TreeNode(f.Name);
            AddObjectNodes(f.GetValue(obj), node.Nodes);
            nodes.Add(node);
          }
        }
      }
    }

    /// <summary>
    /// Adds the given array to the given TreeNodeCollection.
    /// </summary>
    void AddArrayNodes(Array arr, string name, TreeNodeCollection nodes)
    {
      var node = new TreeNode($"{name} ({arr.Length})");
      var eType = arr.GetType().GetElementType();
      if (eType.IsPrimitive || eType == typeof(string) || eType.IsEnum)
        for (var i = 0; i < arr.Length; i++)
        {
          var n = new TreeNode($"[{i:00}] {toString(arr.GetValue(i))}");
          node.Nodes.Add(n);
        }
      else for (var i = 0; i < arr.Length; i++)
        {
          var myName = $"[{i:00}]";
          if (eType.IsArray)
            AddArrayNodes(arr.GetValue(i) as Array, myName, node.Nodes);
          else
          {
            var obj = arr.GetValue(i);

            System.Reflection.FieldInfo nameField;
            if (null != (nameField = obj.GetType().GetField("Name")))
            {
              myName += $" (Name: {nameField.GetValue(obj)})";
            }
            var item = arr.GetValue(i);
            AddObjectNodes((myName, (object)item), node.Nodes);
          }
        }
      nodes.Add(node);
    }

    private byte[] ekpfs, data = null, tweak = null;

    /// <summary>
    /// Tries to load the PFS image with the ekpfs / data+tweak keys
    /// </summary>
    /// <returns>True if the PFS opened successfully</returns>
    private bool ReopenFileView()
    {
      if (!pkg.CheckEkpfs(ekpfs) && (data == null || tweak == null))
        return false;
      if (va != null)
        return false;
      if (pkg.Header.pfs_image_size == 0) //The PKG might be an additional content package without extra data when pfs_image_size is 0.
        return false;
      try
      {
        va = pkgFile.CreateViewAccessor((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size, MemoryMappedFileAccess.Read);
        var outerPfs = new PfsReader(va, pkg.Header.pfs_flags, ekpfs, tweak, data);
        var innerPfsView = new PFSCReader(outerPfs.GetFile("pfs_image.dat").GetView());

        ObjectPreview(("PFSC Header ( pfs_image.dat )", (object)innerPfsView.Hdr), pfsHeaderTreeView, true, true);

        TreeView innerTreeView = PreviewInnerPfsHeader(innerPfsView);

        var inner = new PfsReader(innerPfsView);

        if (outerPfs.GetSuperRoot() is PfsReader.Dir outerRoot &&
          outerRoot.GetPath("flat_path_table") is PfsReader.File outerFlatFile &&
          outerRoot.GetPath("uroot") is PfsReader.Dir outerURoot &&
          outerFlatFile.GetView() is IMemoryAccessor outerFlatAccessor)
        {
          var outFlatPathTable = new FlatPathTable(outerFlatAccessor, outerFlatFile.compressed_size, outerURoot);
          ObjectPreview(("flat_path_table raw", (object)outFlatPathTable.HashMap), pfsHeaderTreeView, true);
          ObjectPreview(("flat_path_table parse", (object)outFlatPathTable.FlatInfos), pfsHeaderTreeView, true);
        }
        if (inner.GetSuperRoot() is PfsReader.Dir innerRoot && 
          innerRoot.GetPath("flat_path_table") is PfsReader.File innerFlatFile &&
          innerRoot.GetPath("uroot") is PfsReader.Dir innerURoot &&
          innerFlatFile.GetView() is IMemoryAccessor innerFlatAccessor)
        {
          var innerFlatPathTable = new FlatPathTable(innerFlatAccessor, innerFlatFile.compressed_size, innerURoot);
          ObjectPreview(("flat_path_table raw", (object)innerFlatPathTable.HashMap), innerTreeView, true, true);
          ObjectPreview(("flat_path_table parse", (object)innerFlatPathTable.FlatInfos), innerTreeView, true);
        }

        var view = new FileView();
        view.AddRoot(outerPfs, "Outer PFS Image");
        view.AddRoot(inner, "Inner PFS Image");
        view.Dock = DockStyle.Fill;
        filesTab.Controls.Clear();
        filesTab.Controls.Add(view);
        return true;
      }
      catch(Exception)
      {
        va?.Dispose();
        va = null;
      }
      return false;
    }

    private TreeView PreviewInnerPfsHeader(IMemoryReader innerPfs)
    {
      var tp = new TabPage("Inner PFS Header")
      { //Same as the attributes setting of the Outer PFS Tab.
        Padding = new Padding(3),
        UseVisualStyleBackColor = true
      };
      var tv = new TreeView()
      {
        Dock = DockStyle.Fill,
        Location = new Point(3, 3),
      };
      ObjectPreview(PfsHeader.ReadFromStream(new StreamWrapper(innerPfs, 0x10000)), tv);
      tp.Controls.Add(tv);
      pkgHeaderTabControl.TabPages.Add(tp);
      
      return tv;
    }

    private void openWithPasscodeBtn_Click(object sender, EventArgs e)
    {
      if(pkg.CheckPasscode(passcodeTextBox.Text))
      {
        passcode = passcodeTextBox.Text;
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
        if (ReopenFileView())
        {
          KeyDB.Instance.Passcodes[pkg.Header.content_id] = passcode;
          KeyDB.Instance.Save();
        }
      }
      else
      {
        MessageBox.Show("Invalid passcode!");
      }
    }

    private void openWithEkpfsBtn_Click(object sender, EventArgs e)
    {
      ekpfs = ekpfsTextBox.Text.FromHexCompact();
      if(!ReopenFileView())
      {
        ekpfs = null;
        MessageBox.Show("Invalid EKPFS!");
      }
      else
      {
        KeyDB.Instance.EKPFS[pkg.Header.content_id] = ekpfs.ToHexCompact();
        KeyDB.Instance.Save();
      }
    }

    private void openWithXtsKeysBtn_Click(object sender, EventArgs e)
    {
      data = xtsDataTextBox.Text.FromHexCompact();
      tweak = xtsTweakTextBox.Text.FromHexCompact();
      if (!ReopenFileView())
      {
        data = tweak = null;
        MessageBox.Show("Invalid data / tweak keys!");
      }
      else
      {
        KeyDB.Instance.XTS[pkg.Header.content_id + "-" + pkg.Header.pfs_image_digest.ToHexCompact().Substring(0, 8)] = new KeyDB.XTSKey
        {
          Data = data.ToHexCompact(),
          Tweak = tweak.ToHexCompact()
        };
        KeyDB.Instance.Save();
      }
    }

    private void entriesListView_DoubleClick(object sender, EventArgs e)
    {
      if (entriesListView.SelectedItems[0].Tag is MetaEntry meta && meta.id.ToString() == "PARAM_SFO")
      {
        mainWin.OpenTab(new SFOView(pkg.ParamSfo.ParamSfo, true), "param.sfo [Read-Only]");
      }
    }

    private async void checkDigests_Click(object sender, EventArgs _)
    {
      validateResult.Text = "Checking PKG digests...";
      listView1.Enabled = false;
      checkDigestsButton.Enabled = false;
      var validator = new PkgValidator(pkg);
      listView1.Items.Clear();
      await Task.Run(() =>
      {
        using (var s = pkgFile.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
        {
          foreach ((PkgValidator.Validation validation, PkgValidator.ValidationResult Validate) in validator.Validate(s).OrderBy((a)=>a.validation.Location))
          {
            var item = new ListViewItem(validation.Name);
            if (Validate == PkgValidator.ValidationResult.Ok)
              item.BackColor = Color.LightGreen;
            else if (Validate == PkgValidator.ValidationResult.Fail)
              item.BackColor = Color.LightSalmon;
            item.Tag = (validation, Validate);
            listView1.BeginInvoke((Action)(() => listView1.Items.Add(item)));
          }
        }
      });

      listView1.Enabled = true;
      checkDigestsButton.Enabled = true;
      validateResult.Text = "Done!";
      foreach(ListViewItem x in listView1.Items)
      {
        if(x.BackColor == Color.LightSalmon)
        {
          validateResult.Text += Environment.NewLine + "Failed: " + x.Text;
        }
      }
    }

    private void ListView1_SelectedIndexChanged(object sender, EventArgs e)
    {
      if(listView1.SelectedItems.Count != 1)
      {
        validateResult.Text = "";
      }
      else
      {
        if (listView1.SelectedItems[0].Tag is ValueTuple<PkgValidator.Validation, PkgValidator.ValidationResult> t)
        {
          var resultTxt = "";
          switch (t.Item2)
          {
            case PkgValidator.ValidationResult.Ok:
              resultTxt = "This was validated successfully";
              break;
            case PkgValidator.ValidationResult.Fail:
              resultTxt = "This was found to be invalid";
              break;
            case PkgValidator.ValidationResult.NoKey:
              resultTxt = "This may be valid but could not be checked due to lack of necessary key(s)";
              break;
          }
          validateResult.Text =
            $"Type: {t.Item1.Type}{Environment.NewLine}" +
            $"Name: {t.Item1.Name}{Environment.NewLine}" +
            $"Description: {t.Item1.Description}{Environment.NewLine}" +
            $"Offset: 0x{t.Item1.Location:X}{Environment.NewLine}" +
           resultTxt;
        }
      }
    }

    bool RequestPasscode()
    {
      if(new PasscodeEntry() is PasscodeEntry p && p.ShowDialog() == DialogResult.OK && pkg.CheckPasscode(p.Passcode))
      {
        passcode = p.Passcode;
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
        ReopenFileView();
        return true;
      }
      return false;
    }

    void ExtractEntry(MetaEntry entry, bool decrypt = false)
    {
      if (decrypt && entry.Encrypted && passcode == null && entry.KeyIndex != 3)
      {
        var gotPasscode = false;
        while (gotPasscode == false)
        {
          gotPasscode = RequestPasscode();
          if (gotPasscode) break;
          var result = MessageBox.Show(
            "Sorry, that passcode was incorrect." + Environment.NewLine
            + "Abort to cancel extraction, ignore to extract in encrypted form, retry to try a new passcode.",
            "Invalid Passcode",
            MessageBoxButtons.AbortRetryIgnore,
            MessageBoxIcon.Warning);
          if (result == DialogResult.Abort)
            return;
          if (result == DialogResult.Ignore)
            break;
        }
      }
      var name = entry.NameTableOffset != 0 ? pkg.EntryNames.GetName(entry.NameTableOffset) : (Enum.TryParse(entry.id.ToString(), out EntryId entryId) && 
        EntryNames.IdToName.ContainsKey(entryId) ? EntryNames.IdToName[entryId] : entry.id.ToString());
      if (new SaveFileDialog() { FileName = name } is SaveFileDialog s && s.ShowDialog() == DialogResult.OK)
      {
        var totalEntrySize = entry.Encrypted ? (entry.DataSize + 15) & ~15 : entry.DataSize;
        using (var f = File.OpenWrite(s.FileName))
        using (var entryStream = pkgFile.CreateViewStream(entry.DataOffset, totalEntrySize, MemoryMappedFileAccess.Read))
        {
          if(entry.Encrypted && decrypt && (passcode != null || entry.KeyIndex == 3))
          {
            var tmp = new byte[totalEntrySize];
            entryStream.Read(tmp, 0, tmp.Length);
            tmp = entry.KeyIndex == 3 ? Entry.Decrypt(tmp, pkg, entry) : Entry.Decrypt(tmp, pkg.Header.content_id, passcode, entry);
            f.Write(tmp, 0, (int)entry.DataSize);
          }
          else
          {
            entryStream.CopyTo(f);
          }
        }
      }
    }

    private void ExtractToolStripMenuItem_Click(object sender, EventArgs e)
    {
      if(entriesListView.SelectedItems.Count == 1)
      {
        ExtractEntry(entriesListView.SelectedItems[0].Tag as MetaEntry);       
      }
    }
    private void ToolStripMenuItem1_Click(object sender, EventArgs e)
    {
      if (entriesListView.SelectedItems.Count == 1)
      {
        ExtractEntry(entriesListView.SelectedItems[0].Tag as MetaEntry, true);
      }
    }

    private void ContextMenuStrip1_Opening(object sender, CancelEventArgs e)
    {
      if (entriesListView.SelectedItems.Count != 1)
      {
        extractDecryptedMenuItem.Enabled = extractToolStripMenuItem.Enabled = false;
      }
      else
      {
        extractToolStripMenuItem.Enabled = true;
        extractDecryptedMenuItem.Enabled = (entriesListView.SelectedItems[0].Tag as MetaEntry)?.Encrypted ?? false;
      }
    }

    Task<bool> exportTask = null;
    private void ExportGP4Project_Click(object sender, EventArgs e)
    {
      if (!ExportGP4Project.Enabled) return;

      var sfd = new SaveFileDialog()
      {
        FileName = "Project.gp4",
        Filter = "GP4 Projects|Project.gp4",
        Title = "Choose a location for the exported PKG and project file",
      };
      if (sfd.ShowDialog() != DialogResult.OK) return;

      ExportProgressBar.Visible = true;
      ExportGP4Project.Enabled = false;
      System.Diagnostics.Stopwatch tickerMajor = System.Diagnostics.Stopwatch.StartNew();

      var outputDir = Path.GetDirectoryName(sfd.FileName.Replace("Project.gp4", pkg.Header.content_id + Path.DirectorySeparatorChar + "Project.gp4"));
      exportTask = LibOrbisPkg.GP4.Gp4Creator.CreateProjectFromPKG(outputDir, pkgFile, passcode, DecryptEntryCheckBox.Checked, tickerMajor,
        //progress reporting using Async/Await: https://stackoverflow.com/a/19980151/22545576
        new Progress<(int percent, string message)>(value => {
          ExportProgressMsg.Text = value.message;
          ExportProgressBar.Value = value.percent;
        }));
      exportTask.ContinueWith(t => {
        if (t.Exception != null)
        {
          Invoke(new MethodInvoker(() => {
            MessageBox.Show(this, "ExportGP4ProjectTask Exception", t.Exception.ToString(), MessageBoxButtons.OK, MessageBoxIcon.Error);
          }));
        }
      }, TaskContinuationOptions.OnlyOnFaulted)
      .ContinueWith(t => Invoke(new MethodInvoker(() => {
        tickerMajor?.Stop();
        ExportProgressMsg.Text = "PKG Exported to " + outputDir;
        ExportProgressBar.Value = 100;
        ExportProgressBar.Visible = false;
        ExportGP4Project.Enabled = true;
      })))
      .ContinueWith(t => {
        exportTask?.Dispose();
        exportTask = null;
      });
    }
  }
}
