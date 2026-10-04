using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class ResourceUpdateRecycleBinTest
{
    private string _root = null!;
    private ResourceUpdateRecycleBin _bin = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCL-Recycle-Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _bin = new ResourceUpdateRecycleBin(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void CreatesResourceFolders()
    {
        _bin.InitializeFolders();
        foreach (var folder in new[] { "mods", "resourcepacks", "shaderpacks", "schematics", "datapacks" })
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "recycle", folder)));
    }

    [TestMethod]
    [DataRow("mods")]
    [DataRow("resourcepacks")]
    [DataRow("shaderpacks")]
    [DataRow("saves/world/datapacks")]
    [DataRow("mods/nested")]
    [DataRow("labymod-neo/fabric/1.21/mods")]
    public void UpdateAndUndoPreserveDirectoryAndContent(string folder)
    {
        var old = _Write(folder + "/resource-1.jar", "old version");
        var updated = Path.Combine(_root, folder, "resource-2.jar");
        var downloaded = _Write("download/new.jar", "new version");
        Assert.IsFalse(_bin.CanUndo(old));

        _bin.Replace(old, updated, downloaded);
        var backup = Path.Combine(_root, "recycle", folder, "resource-1.jar");
        Assert.IsFalse(File.Exists(old));
        Assert.IsFalse(File.Exists(downloaded));
        Assert.AreEqual("old version", File.ReadAllText(backup));
        Assert.AreEqual("new version", File.ReadAllText(updated));
        // 从磁盘重新加载，模拟重启后的撤回。
        var reopened = new ResourceUpdateRecycleBin(_root);
        Assert.IsTrue(reopened.CanUndo(updated));
        reopened.Undo(updated);
        Assert.AreEqual("old version", File.ReadAllText(old));
        Assert.IsFalse(File.Exists(updated));
        Assert.IsFalse(File.Exists(backup));
        Assert.IsFalse(reopened.CanUndo(old));
        Assert.AreEqual(0, Directory.GetFiles(_root, ".pcl-undo-*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void SameFileNameCanBeUpdatedAndUndone()
    {
        var resource = _Write("mods/example.jar", "v1");
        _bin.Replace(resource, resource, _Write("download/new.jar", "v2"));
        Assert.AreEqual("v2", File.ReadAllText(resource));
        Assert.IsTrue(_bin.CanUndo(resource));
        _bin.Undo(resource);
        Assert.AreEqual("v1", File.ReadAllText(resource));
        Assert.IsFalse(_bin.CanUndo(resource));
    }

    [TestMethod]
    public void PreservesDisabledOriginalAfterTogglingUpdatedResource()
    {
        var old = _Write("mods/example-1.jar.disabled", "v1");
        var updated = Path.Combine(_root, "mods/example-2.jar.disabled");
        _bin.Replace(old, updated, _Write("download/new.jar", "v2"));
        var enabled = updated[..^".disabled".Length];
        File.Move(updated, enabled);
        Assert.IsTrue(_bin.CanUndo(enabled));
        _bin.Undo(enabled);
        Assert.AreEqual("v1", File.ReadAllText(old));
        Assert.IsFalse(File.Exists(enabled));
        Assert.IsFalse(_bin.CanUndo(old));
    }

    [TestMethod]
    public void RetainsExistingBackupWithSameName()
    {
        _Write("recycle/mods/example.jar", "older backup");
        var old = _Write("mods/example.jar", "v1");
        var updated = Path.Combine(_root, "mods/new.jar");
        _bin.Replace(old, updated, _Write("download/new.jar", "v2"));
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(_root, "recycle/mods")).Length);
        _bin.Undo(updated);
        Assert.AreEqual("v1", File.ReadAllText(old));
        Assert.AreEqual("older backup", File.ReadAllText(Path.Combine(_root, "recycle/mods/example.jar")));
    }

    [TestMethod]
    public void OnlyLastUpdateCanBeUndone()
    {
        var old = _Write("mods/example-1.jar", "v1");
        var second = Path.Combine(_root, "mods/example-2.jar");
        var third = Path.Combine(_root, "mods/example-3.jar");
        _bin.Replace(old, second, _Write("download/second.jar", "v2"));
        _bin.Replace(second, third, _Write("download/third.jar", "v3"));
        _bin.Undo(third);
        Assert.AreEqual("v2", File.ReadAllText(second));
        Assert.IsFalse(_bin.CanUndo(second));
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(_root, "recycle/mods/example-1.jar")));
    }

    [TestMethod]
    public void UnrelatedDestinationIsNotOverwritten()
    {
        var old = _Write("mods/old.jar", "old");
        var updated = _Write("mods/new.jar", "unrelated");
        var downloaded = _Write("download/new.jar", "new");
        Assert.ThrowsExactly<IOException>(() => _bin.Replace(old, updated, downloaded));
        Assert.AreEqual("old", File.ReadAllText(old));
        Assert.AreEqual("unrelated", File.ReadAllText(updated));
        Assert.AreEqual("new", File.ReadAllText(downloaded));
    }

    [TestMethod]
    public void FailedInstallationRestoresOldFile()
    {
        var old = _Write("mods/old.jar", "old");
        var updated = Path.Combine(_root, "mods/new.jar");
        var downloaded = _Write("download/new.jar", "new");
        using (File.Open(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsExactly<IOException>(() => _bin.Replace(old, updated, downloaded));
        Assert.AreEqual("old", File.ReadAllText(old));
        Assert.AreEqual("new", File.ReadAllText(downloaded));
        Assert.IsFalse(File.Exists(updated));
        Assert.IsFalse(_bin.CanUndo(updated));
    }

    [TestMethod]
    public void FailedUndoRestoresUpdatedFileAndKeepsBackup()
    {
        var old = _Write("mods/old.jar", "old");
        var updated = Path.Combine(_root, "mods/new.jar");
        _bin.Replace(old, updated, _Write("download/new.jar", "new"));
        var backup = Path.Combine(_root, "recycle/mods/old.jar");
        using (File.Open(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsExactly<IOException>(() => _bin.Undo(updated));
        Assert.AreEqual("new", File.ReadAllText(updated));
        Assert.AreEqual("old", File.ReadAllText(backup));
        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(_bin.CanUndo(updated));
        _bin.Undo(updated);
        Assert.AreEqual("old", File.ReadAllText(old));
    }

    [TestMethod]
    public void CannotUndoWhenOriginalNameHasBeenReused()
    {
        var old = _Write("mods/old.jar", "old");
        var updated = Path.Combine(_root, "mods/new.jar");
        _bin.Replace(old, updated, _Write("download/new.jar", "new"));
        File.WriteAllText(old, "unrelated");
        Assert.ThrowsExactly<IOException>(() => _bin.Undo(updated));
        Assert.AreEqual("unrelated", File.ReadAllText(old));
        Assert.AreEqual("new", File.ReadAllText(updated));
        Assert.IsTrue(_bin.CanUndo(updated));
    }

    [TestMethod]
    public void MissingBackupOrModifiedResourceHidesUndo()
    {
        var old = _Write("mods/old.jar", "old");
        var updated = Path.Combine(_root, "mods/new.jar");
        _bin.Replace(old, updated, _Write("download/new.jar", "new"));
        File.WriteAllText(updated, "manually replaced");
        Assert.IsFalse(_bin.CanUndo(updated));
        Assert.ThrowsExactly<IOException>(() => _bin.Undo(updated));
        File.WriteAllText(updated, "new");
        Assert.IsTrue(_bin.CanUndo(updated));
        File.Delete(Path.Combine(_root, "recycle/mods/old.jar"));
        Assert.IsFalse(_bin.CanUndo(updated));
    }

    [TestMethod]
    public void UnsafeRecordCannotRestoreOutsideGameDirectory()
    {
        var old = _Write("mods/old.jar", "old");
        var updated = Path.Combine(_root, "mods/new.jar");
        _bin.Replace(old, updated, _Write("download/new.jar", "new"));
        var recordPath = Directory.GetFiles(Path.Combine(_root, "recycle/.records"), "*.json").Single();
        var record = JsonNode.Parse(File.ReadAllText(recordPath))!;
        record["OriginalPath"] = "../outside.jar";
        File.WriteAllText(recordPath, record.ToJsonString());
        Assert.IsFalse(_bin.CanUndo(updated));
        Assert.ThrowsExactly<IOException>(() => _bin.Undo(updated));
        Assert.AreEqual("new", File.ReadAllText(updated));
    }

    [TestMethod]
    public void MissingDownloadDoesNotRemoveOldFile()
    {
        var old = _Write("mods/old.jar", "old");
        Assert.ThrowsExactly<FileNotFoundException>(() => _bin.Replace(old,
            Path.Combine(_root, "mods/new.jar"), Path.Combine(_root, "download/missing.jar")));
        Assert.AreEqual("old", File.ReadAllText(old));
    }

    private string _Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
