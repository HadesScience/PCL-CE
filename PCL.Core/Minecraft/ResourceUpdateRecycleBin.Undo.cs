using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PCL.Core.Minecraft;

public sealed partial class ResourceUpdateRecycleBin
{
    public bool CanUndo(string updatedPath)
    {
        lock (_SyncRoot)
        {
            return _TryReadRecord(updatedPath, out _, out _, out _, out _, useCachedIndex: true);
        }
    }

    /// <summary>
    /// 先暂存新版，再还原旧版，最后彻底删除新版；还原失败时恢复新版。
    /// </summary>
    public void Undo(string updatedPath)
    {
        lock (_SyncRoot)
        {
            if (!_TryReadRecord(updatedPath, out var record, out var originalPath, out var backupPath, out var recordPath))
                throw new IOException("没有找到该资源对应的旧版，或当前资源或其备份已被修改。");
            updatedPath = _GetResourcePath(updatedPath);
            if (!_SamePath(originalPath, updatedPath) && (File.Exists(originalPath) || Directory.Exists(originalPath)))
                throw new IOException("旧版的原文件名已被占用，请先处理同名文件：" + originalPath);

            var stagedPath = Path.Combine(Path.GetDirectoryName(updatedPath)!,
                ".pcl-undo-" + Guid.NewGuid().ToString("N") + ".tmp");
            // 仅旧记录可能缺少旧版哈希，不能用当前备份覆盖更新时记录的校验值。
            if (string.IsNullOrEmpty(record.OriginalHash))
                record.OriginalHash = _HashFile(backupPath);
            else if (!_Matches(backupPath, record.OriginalHash))
                throw new IOException("资源旧版备份已被修改，无法撤回。");
            var staged = false;
            var restored = false;
            try
            {
                _Move(updatedPath, stagedPath);
                staged = true;
                _Move(backupPath, originalPath);
                restored = true;
                _Delete(stagedPath);
            }
            catch (Exception undoError)
            {
                try
                {
                    if (restored) _Move(originalPath, backupPath);
                    if (staged) _Move(stagedPath, updatedPath);
                }
                catch (Exception restoreError)
                {
                    throw new AggregateException("撤回失败，请检查资源目录和软件内回收站。", undoError, restoreError);
                }
                throw;
            }
            _Delete(recordPath);
        }
    }

    private bool _TryReadRecord(string updatedPath, out UpdateRecord record, out string originalPath, out string backupPath,
        out string recordPath, bool useCachedIndex = false)
    {
        record = null!;
        originalPath = backupPath = recordPath = "";
        try
        {
            updatedPath = _GetResourcePath(updatedPath);
            if (!File.Exists(updatedPath)) return false;
            var exactPath = _GetRecordPath(updatedPath);
            var normalizedPath = _NormalizeResourcePath(updatedPath);
            var candidates = new List<(UpdateRecord Record, string Path, string Original, string Backup)>();
            bool? hasSibling = null;
            foreach (var candidatePath in _GetCandidatePaths(exactPath, normalizedPath, useCachedIndex))
            {
                UpdateRecord? candidate;
                string original, recordedUpdated, backup;
                try
                {
                    candidate = JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(_GetUnlinkedPath(candidatePath)));
                    if (candidate is null) continue;
                    _ValidateRecord(candidate, out original, out recordedUpdated, out backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    continue;
                }
                if (!_SamePath(updatedPath, recordedUpdated))
                {
                    // 只有原记录的文件已消失且没有同名兄弟副本时，才将名称变化解释为启用/禁用切换。
                    if (!_SamePath(normalizedPath, _NormalizeResourcePath(recordedUpdated)) || File.Exists(recordedUpdated))
                        continue;
                    hasSibling ??= Directory.EnumerateFiles(Path.GetDirectoryName(updatedPath)!)
                        .Any(path => !_SamePath(path, updatedPath) && _SamePath(_NormalizeResourcePath(path), normalizedPath));
                    if (hasSibling.Value) continue;
                }
                if (!File.Exists(backup)) continue;
                if (_SamePath(candidatePath, exactPath) && _SamePath(updatedPath, recordedUpdated))
                {
                    candidates.Clear();
                    candidates.Add((candidate, candidatePath, original, backup));
                    break;
                }
                candidates.Add((candidate, candidatePath, original, backup));
            }
            if (candidates.Count != 1) return false;
            (record, recordPath, originalPath, backupPath) = candidates[0];
            return _HashFile(updatedPath) == record.UpdatedHash &&
                (string.IsNullOrEmpty(record.OriginalHash) || _Matches(backupPath, record.OriginalHash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private IEnumerable<string> _GetCandidatePaths(string exactPath, string normalizedPath, bool useCachedIndex)
    {
        if (File.Exists(exactPath)) yield return exactPath;
        var directory = Path.GetDirectoryName(exactPath)!;
        if (!Directory.Exists(directory)) yield break;
        // 执行更新或撤回时重新读取磁盘上的记录，确保外部修改不会绕过歧义检查。
        IEnumerable<string>? paths = useCachedIndex
            ? _GetRecordIndex(directory).GetValueOrDefault(normalizedPath)
            : Directory.EnumerateFiles(directory, "*.json");
        if (paths is null) yield break;
        foreach (var path in paths)
            if (!_SamePath(path, exactPath)) yield return path;
    }

    private Dictionary<string, List<string>> _GetRecordIndex(string directory)
    {
        var lastWriteTime = Directory.GetLastWriteTimeUtc(directory);
        if (_CachedRecordIndex is { } cached && _SamePath(cached.Directory, directory) && cached.LastWriteTime == lastWriteTime)
            return cached.PathsByResource;

        var pathsByResource = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            string resource;
            try
            {
                var record = JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(_GetUnlinkedPath(path)));
                if (record is null || string.IsNullOrWhiteSpace(record.UpdatedPath)) continue;
                resource = _NormalizeResourcePath(Path.GetFullPath(Path.Combine(_GameDirectory, record.UpdatedPath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                continue;
            }
            if (!pathsByResource.TryGetValue(resource, out var paths))
                pathsByResource.Add(resource, paths = new List<string>());
            paths.Add(path);
        }

        _CachedRecordIndex = new RecordIndex(directory, lastWriteTime, pathsByResource);
        return pathsByResource;
    }

    private static string _NormalizeResourcePath(string path)
    {
        while (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            path = path[..path.LastIndexOf('.')];
        return path;
    }

    private void _ValidateRecord(UpdateRecord record, out string original, out string updated, out string backup)
    {
        if (string.IsNullOrWhiteSpace(record.OriginalPath) || string.IsNullOrWhiteSpace(record.UpdatedPath) ||
            string.IsNullOrWhiteSpace(record.BackupPath) || record.UpdatedHash is not { Length: 64 })
            throw new IOException("资源撤回记录不完整。");
        original = _GetResourcePath(Path.Combine(_GameDirectory, record.OriginalPath));
        updated = _GetResourcePath(Path.Combine(_GameDirectory, record.UpdatedPath));
        backup = _GetContainedPath(_RecycleDirectory, Path.Combine(_RecycleDirectory, record.BackupPath));
        var expected = Path.Combine(_RecycleDirectory, Path.GetRelativePath(_GameDirectory, Path.GetDirectoryName(original)!));
        if (!_SamePath(Path.GetDirectoryName(original)!, Path.GetDirectoryName(updated)!) ||
            !_SamePath(Path.GetDirectoryName(backup)!, expected) ||
            !(Path.GetFileName(backup) == Path.GetFileName(original) ||
                Path.GetFileName(backup).StartsWith(Path.GetFileName(original) + ".", StringComparison.OrdinalIgnoreCase)) ||
            record.UpdatedHash.Length != 64)
            throw new IOException("资源撤回记录的路径或校验信息无效。");
    }

    private static bool _Matches(string path, string hash) => File.Exists(_GetUnlinkedPath(path)) && _HashFile(path) == hash;

    private sealed record RecordIndex(string Directory, DateTime LastWriteTime,
        Dictionary<string, List<string>> PathsByResource);
}
