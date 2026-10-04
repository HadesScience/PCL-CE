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

    private string _Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
