using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PCL.Core.Minecraft;

public sealed partial class ResourceUpdateRecycleBin
{
    private string _WriteTransaction(PendingOperation operation)
    {
        var path = _GetContainedPath(_RecycleDirectory,
            Path.Combine(_RecycleDirectory, ".transactions", Guid.NewGuid().ToString("N") + ".json"));
        Directory.CreateDirectory(_GetUnlinkedPath(Path.GetDirectoryName(path)!));
        _WriteRecord(path, operation);
        return path;
    }

    /// <summary>在后台加载资源列表前恢复中断的文件移动；冲突时保留日志和所有文件。</summary>
    public void RecoverPendingOperations()
    {
        lock (_SyncRoot)
        {
            // 资源列表刷新及记录写入前都会进入这里，重建索引时重新读取记录内容。
            if (_CachedRecordIndex is { } cached &&
                _SamePath(cached.Directory, Path.Combine(_RecycleDirectory, ".records")))
                _CachedRecordIndex = null;
            var directory = _GetContainedPath(_RecycleDirectory, Path.Combine(_RecycleDirectory, ".transactions"));
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var operation = JsonSerializer.Deserialize<PendingOperation>(File.ReadAllText(_GetUnlinkedPath(path)))
                    ?? throw new IOException("资源恢复日志无效：" + path);
                _RecoverOperation(operation);
                _Delete(path);
            }
        }
    }

    private void _RecoverOperation(PendingOperation operation)
    {
        var record = operation.Record;
        _ValidateRecord(record, out var original, out var updated, out var backup);
        if (record.OriginalHash is not { Length: 64 }) throw new IOException("资源恢复日志缺少旧版校验信息。");
        if (operation.Operation == "Replace")
        {
            var recordPath = _GetRecordPath(updated);
            var previous = string.IsNullOrEmpty(operation.PreviousRecordFile) ? "" :
                _GetRecordedFile(operation.PreviousRecordFile);
            if (previous.Length > 0 && !_SamePath(previous, recordPath) && File.Exists(previous))
            {
                var previousRecord = JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(_GetUnlinkedPath(previous)))
                    ?? throw new IOException("上一次资源撤回记录无效。");
                _ValidateRecord(previousRecord, out _, out var previousUpdated, out _);
                if (!_SamePath(_NormalizeResourcePath(previousUpdated), _NormalizeResourcePath(original)) ||
                    previousRecord.UpdatedHash != record.OriginalHash)
                    throw new IOException("上一次资源撤回记录与恢复日志不符。");
            }
            var staged = string.IsNullOrEmpty(operation.StagedPath) ? "" :
                _GetStagedPath(operation.StagedPath, updated, ".pcl-update-");
            if (staged.Length > 0 && File.Exists(staged) && !_Matches(staged, record.UpdatedHash))
                throw new IOException("更新暂存文件已被修改。");
            if (_Matches(backup, record.OriginalHash) && _Matches(updated, record.UpdatedHash))
            {
                _WriteRecord(recordPath, record);
                if (previous.Length > 0 && !_SamePath(previous, recordPath)) _Delete(previous);
                if (staged.Length > 0) _Delete(staged);
                return;
            }
            if (_Matches(backup, record.OriginalHash) && !File.Exists(original) && !File.Exists(updated))
            {
                _Move(backup, original);
                if (staged.Length > 0) _Delete(staged);
                return;
            }
            if (!File.Exists(backup) && _Matches(original, record.OriginalHash))
            {
                if (staged.Length > 0) _Delete(staged);
                return;
            }
        }
        else if (operation.Operation == "Undo")
        {
            updated = _GetResourcePath(Path.Combine(_GameDirectory, operation.ResourcePath));
            var staged = _GetStagedPath(operation.StagedPath, updated, ".pcl-undo-");
            if (!_SamePath(_NormalizeResourcePath(updated), _NormalizeResourcePath(
                    _GetResourcePath(Path.Combine(_GameDirectory, record.UpdatedPath)))))
                throw new IOException("资源撤回暂存路径无效。");
            var recordPath = _GetRecordedFile(operation.UndoRecordFile);
            if (File.Exists(recordPath))
            {
                var undoRecord = JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(_GetUnlinkedPath(recordPath)))
                    ?? throw new IOException("资源撤回记录无效。");
                _ValidateRecord(undoRecord, out var recordedOriginal, out var recordedUpdated, out var recordedBackup);
                if (!_SamePath(recordedOriginal, original) || !_SamePath(recordedBackup, backup) ||
                    !_SamePath(recordedUpdated, _GetResourcePath(Path.Combine(_GameDirectory, record.UpdatedPath))) ||
                    undoRecord.UpdatedHash != record.UpdatedHash)
                    throw new IOException("资源撤回记录与恢复日志不符。");
            }
            if (_Matches(staged, record.UpdatedHash) && _Matches(backup, record.OriginalHash) && !File.Exists(original))
                _Move(backup, original);
            if (_Matches(original, record.OriginalHash) && !File.Exists(backup))
            {
                if (File.Exists(staged))
                {
                    if (!_Matches(staged, record.UpdatedHash)) throw new IOException("撤回暂存文件已被修改。");
                    _Delete(staged);
                }
                _Delete(recordPath);
                return;
            }
            if (!File.Exists(staged) && _Matches(updated, record.UpdatedHash) && _Matches(backup, record.OriginalHash))
                return;
        }
        throw new IOException("资源恢复遇到文件冲突，已保留文件和恢复日志：" + original);
    }

    private string _GetRecordedFile(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || fileName.Length != 69 ||
            !fileName.EndsWith(".json", StringComparison.Ordinal) || !fileName[..64].All(Uri.IsHexDigit))
            throw new IOException("资源记录文件名无效。");
        return _GetContainedPath(_RecycleDirectory, Path.Combine(_RecycleDirectory, ".records", fileName));
    }

    private string _GetStagedPath(string relativePath, string updated, string prefix)
    {
        var path = _GetResourcePath(Path.Combine(_GameDirectory, relativePath));
        var name = Path.GetFileName(path);
        if (!_SamePath(Path.GetDirectoryName(path)!, Path.GetDirectoryName(updated)!) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..^4], "N", out _))
            throw new IOException("资源暂存路径无效。");
        return path;
    }

    private sealed class PendingOperation
    {
        public PendingOperation() { }
        public string Operation { get; set; } = "";
        public UpdateRecord Record { get; set; } = new();
        public string ResourcePath { get; set; } = "";
        public string StagedPath { get; set; } = "";
        public string PreviousRecordFile { get; set; } = "";
        public string UndoRecordFile { get; set; } = "";
    }
}
