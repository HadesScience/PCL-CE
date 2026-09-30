using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;

namespace PCL;

/// <summary>
///     Jar-in-Jar（内嵌模组）解析。
/// </summary>
public static class ModJarInJar
{
    private const int MaxDepth = 5;
    private const int MaxNodes = 512;
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;

    private sealed class _ResolveBudget
    {
        public int NodesRemaining = MaxNodes;
        public long BytesRemaining = MaxTotalBytes;
        public bool Incomplete;
    }

    private sealed class _NestedJarInfo
    {
        public string Path;
        public string Identifier;
        public string VersionRange;
        public string ArtifactVersion;
    }

    /// <summary>
    ///     解析 <paramref name="jar" /> 内嵌套的其它 Mod jar，返回内嵌 Mod 列表。
    /// </summary>
    public static List<ModLocalComp.LocalCompFile> Resolve(string parentPath, ZipArchive jar, int depth = 0)
        => _Resolve(parentPath, jar, depth, new _ResolveBudget());

    /// <summary>
    ///     带持久化缓存的解析：按文件指纹命中缓存则直接重建，否则解析并写入缓存（批量结束后需调用
    ///     <see cref="ModJarInJarCache.Flush" /> 落盘）。
    /// </summary>
    public static List<ModLocalComp.LocalCompFile> ResolveCached(string modFilePath, ZipArchive jar,
        ModLocalComp.LocalCompFile self = null, bool deferOnMiss = false)
    {
        long lastModified, size;
        try
        {
            var fi = new FileInfo(modFilePath);
            lastModified = fi.LastWriteTimeUtc.Ticks;
            size = fi.Length;
        }
        catch
        {
            return deferOnMiss ? null : Resolve(modFilePath, jar);
        }

        var cached = ModJarInJarCache.TryGet(modFilePath, lastModified, size);
        if (cached is not null) return _FromNodes(cached, modFilePath);
        // 缓存未命中且要求延后：返回 null 交由列表加载完成后的后台线程补解析，
        // 不在首屏同步解压嵌套 jar（冷缓存下几百个 mod 的递归解压会拖慢列表出现）
        if (deferOnMiss) return null;

        var budget = new _ResolveBudget();
        var tree = _Resolve(modFilePath, jar, 0, budget);
        // 截断/失败树不入盘：宿主指纹不变会被永久复用，缺失的 id 永不再现；下次启动重扫
        // self?.BuildJijSelfNode() 直接读字段，不经属性 getter——否则会在 Load() 内重入 Load() 无限递归卡死
        if (!budget.Incomplete)
            ModJarInJarCache.Set(modFilePath, lastModified, size, _ToNodes(tree), self?.BuildJijSelfNode());
        return tree;
    }

    private static List<EmbeddedModNode> _ToNodes(List<ModLocalComp.LocalCompFile> mods)
        => mods.Select(m => new EmbeddedModNode
        {
            FileName = m.FileName,
            Name = m.Name,
            ModId = m.ModId,
            Version = m.Version,
            Loader = m.JijLoader,
            TargetMcVersion = m.JijTargetMcVersion,
            DependencyRows = m.DependencyDeclarations.Select(d => new EmbeddedDependency
                { Id = d.Id, Raw = d.Raw, Optional = d.Optional }).ToList(),
            Conflicts = m.ConflictDeclarations.Select(c => new EmbeddedConflict
                { Target = c.Target, Raw = c.Raw, Hard = c.Hard }).ToList(),
            ProvidedIds = m.ProvidedIds.ToList(),
            ProvidedVersions = new Dictionary<string, string>(m.ProvidedVersions),
            JijIdentifier = m.JijIdentifier,
            JijVersionRange = m.JijVersionRange,
            JijArtifactVersion = m.JijArtifactVersion,
            Children = _ToNodes(m.EmbeddedMods)
        }).ToList();

    private static List<ModLocalComp.LocalCompFile> _FromNodes(List<EmbeddedModNode> nodes, string parentPath)
    {
        var result = new List<ModLocalComp.LocalCompFile>();
        if (nodes is null) return result;
        foreach (var node in nodes)
        {
            var childPath = parentPath + "!/" + node.FileName;
            var child = new ModLocalComp.LocalCompFile(childPath);
            child.SetJijMetadata(node.Name, node.ModId, node.Version);
            child.JijLoader = node.Loader;
            child.JijTargetMcVersion = node.TargetMcVersion;
            child.JijIdentifier = node.JijIdentifier;
            child.JijVersionRange = node.JijVersionRange;
            child.JijArtifactVersion = node.JijArtifactVersion;
            child.SetJijDependencies(node.DependencyRows);
            child.SetJijConflicts(node.Conflicts, node.ProvidedIds, node.ProvidedVersions);
            child.EmbeddedMods = _FromNodes(node.Children, childPath);
            result.Add(child);
        }

        return result;
    }

    private static List<ModLocalComp.LocalCompFile> _Resolve(string parentPath, ZipArchive jar, int depth,
        _ResolveBudget budget)
    {
        var result = new List<ModLocalComp.LocalCompFile>();
        var nested = new List<_NestedJarInfo>();
        _CollectFabricNestedJars(jar, nested);
        _CollectQuiltNestedJars(jar, nested);
        _CollectForgeNestedJars(jar, nested);
        _CollectManifestEmbeddedJars(jar, nested);
        nested = nested.Where(n => !string.IsNullOrWhiteSpace(n.Path))
            .GroupBy(n => n.Path, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(n => n.Identifier is not null).First())
            .ToList();
        if (depth >= MaxDepth)
        {
            if (nested.Count > 0) budget.Incomplete = true;
            return result;
        }

        foreach (var info in nested)
        {
            if (budget.NodesRemaining <= 0)
            {
                budget.Incomplete = true; // 节点预算耗尽，避免病态嵌套爆树
                break;
            }

            var entry = jar.GetEntry(info.Path);
            if (entry is null) continue;
            if (entry.Length > MaxEntryBytes || entry.Length > budget.BytesRemaining)
            {
                budget.Incomplete = true;
                ModBase.Log("跳过过大的内嵌 Mod（" + parentPath + " -> " + info.Path + "，" + entry.Length +
                            " bytes）", ModBase.LogLevel.Developer);
                continue;
            }
            budget.NodesRemaining--;

            var childPath = parentPath + "!/" + info.Path;
            var child = new ModLocalComp.LocalCompFile(childPath);
            child.MarkLoaded();
            child.JijIdentifier = info.Identifier;
            child.JijVersionRange = info.VersionRange;
            child.JijArtifactVersion = info.ArtifactVersion;
            // 始终列出该内嵌项：即使下方元数据解析/递归失败，也能按文件名保留（不丢节点）
            result.Add(child);

            string tmp = null;
            try
            {
                // 嵌套流不可 seek，落临时文件后再作为 zip 打开，避免大嵌套 jar 全量进内存
                tmp = Path.GetTempFileName();
                using (var es = entry.Open())
                using (var fs = File.Create(tmp))
                    _CopyWithBudget(es, fs, budget);
                using var nestedJar = ZipFile.OpenRead(tmp);
                child.LookupMetadata(nestedJar);
                child.JijLoader = DetectLoader(nestedJar);
                // 用原始约束（未 Maven 化），保留 Fabric semver 原貌供求值
                child.JijTargetMcVersion = child.DependencyRaw.TryGetValue("minecraft", out var mc) ? mc : null;
                child.EmbeddedMods = _Resolve(childPath, nestedJar, depth + 1, budget);
            }
            catch (Exception ex)
            {
                budget.Incomplete = true;
                ModBase.Log(ex, "解析内嵌 Mod 失败（" + parentPath + " -> " + info.Path + "）", ModBase.LogLevel.Developer);
            }
            finally
            {
                if (tmp is not null)
                    try { File.Delete(tmp); }
                    catch { /* 临时文件清理失败无妨 */ }
            }
        }

        return result;
    }

    private static void _CopyWithBudget(Stream input, Stream output, _ResolveBudget budget)
    {
        var buffer = new byte[81920];
        long entryBytes = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) return;
            entryBytes += read;
            budget.BytesRemaining -= read;
            if (entryBytes > MaxEntryBytes || budget.BytesRemaining < 0)
            {
                budget.Incomplete = true;
                throw new InvalidDataException("内嵌 Mod 解压体积超过安全上限");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static void _CollectFabricNestedJars(ZipArchive jar, List<_NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("fabric.mod.json");
            if (entry is null) return;
            var obj = (JsonObject)ModBase.GetJson(ModBase.ReadFile(entry.Open()));
            if (obj.TryGetPropertyValue("jars", out var jars) && jars is JsonArray arr)
                foreach (var j in arr)
                    if (j is JsonObject jo && jo.TryGetPropertyValue("file", out var file) && file is not null)
                        found.Add(new _NestedJarInfo { Path = file.ToString() });
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "解析 fabric.mod.json 内嵌清单失败", ModBase.LogLevel.Developer);
        }
    }

    private static void _CollectForgeNestedJars(ZipArchive jar, List<_NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("META-INF/jarjar/metadata.json");
            if (entry is null) return;
            var obj = (JsonObject)ModBase.GetJson(ModBase.ReadFile(entry.Open()));
            if (obj.TryGetPropertyValue("jars", out var jars) && jars is JsonArray arr)
                foreach (var j in arr)
                    if (j is JsonObject jo && jo.TryGetPropertyValue("path", out var p) && p is not null)
                    {
                        var identifier = jo["identifier"] as JsonObject;
                        var version = jo["version"] as JsonObject;
                        var group = identifier?["group"]?.ToString();
                        var artifact = identifier?["artifact"]?.ToString();
                        found.Add(new _NestedJarInfo
                        {
                            Path = p.ToString(),
                            Identifier = string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(artifact)
                                ? null
                                : group + ":" + artifact,
                            VersionRange = version?["range"]?.ToString(),
                            ArtifactVersion = version?["artifactVersion"]?.ToString()
                        });
                    }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "解析 META-INF/jarjar/metadata.json 内嵌清单失败", ModBase.LogLevel.Developer);
        }
    }

    // Quilt：quilt.mod.json 的 quilt_loader.jars（字符串数组，直接为内嵌 jar 路径）
    private static void _CollectQuiltNestedJars(ZipArchive jar, List<_NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("quilt.mod.json");
            if (entry is null) return;
            var obj = (JsonObject)ModBase.GetJson(ModBase.ReadFile(entry.Open()));
            if (obj.TryGetPropertyValue("quilt_loader", out var ql) && ql is JsonObject qlo
                && qlo.TryGetPropertyValue("jars", out var jars) && jars is JsonArray arr)
                foreach (var j in arr)
                {
                    var s = j?.ToString();
                    if (!string.IsNullOrEmpty(s)) found.Add(new _NestedJarInfo { Path = s });
                }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "解析 quilt.mod.json 内嵌清单失败", ModBase.LogLevel.Developer);
        }
    }

    // JAR manifest 的 Embedded-Dependencies-Mod：无 mods.toml 的“包装 jar”仅通过它声明内嵌 mod
    private static void _CollectManifestEmbeddedJars(ZipArchive jar, List<_NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("META-INF/MANIFEST.MF");
            if (entry is null) return;
            // JAR manifest 按 72 字节折行，续行以单个空格开头且无分隔符续接上一行；
            // 需先展开再解析，否则 Connector 等长内嵌路径会在续行处被截断而找不到条目
            var sb = new System.Text.StringBuilder();
            foreach (var raw in ModBase.ReadFile(entry.Open()).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith(" ")) sb.Append(line, 1, line.Length - 1);
                else sb.Append('\n').Append(line);
            }

            foreach (var line in sb.ToString().Split('\n'))
            {
                if (!line.StartsWith("Embedded-Dependencies-Mod:", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring("Embedded-Dependencies-Mod:".Length).Trim();
                if (!string.IsNullOrEmpty(value)) found.Add(new _NestedJarInfo { Path = value });
                return;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "解析 MANIFEST.MF 内嵌声明失败", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>按存在的清单文件判断 jar 声明的加载器（Fabric/Quilt/Forge/NeoForge）；无从判断返回 null。</summary>
    public static string DetectLoader(ZipArchive jar)
    {
        if (jar.GetEntry("fabric.mod.json") is not null) return "Fabric";
        if (jar.GetEntry("quilt.mod.json") is not null) return "Quilt";
        if (jar.GetEntry("META-INF/neoforge.mods.toml") is not null) return "NeoForge";
        if (jar.GetEntry("META-INF/mods.toml") is not null) return "Forge";
        if (jar.GetEntry("mcmod.info") is not null) return "Forge";
        return null;
    }
}
