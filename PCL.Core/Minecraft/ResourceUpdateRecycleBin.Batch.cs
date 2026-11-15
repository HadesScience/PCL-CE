using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft;

public sealed partial class ResourceUpdateRecycleBin
{
    /// <summary>多选时跳过没有备份的资源；单个资源失败不影响其余资源。</summary>
    public UndoBatchResult UndoMany(IEnumerable<string> updatedPaths)
    {
        var result = new UndoBatchResult();
        lock (_SyncRoot)
        {
            foreach (var path in updatedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!_TryReadRecord(path, out _, out _, out _, out _))
                    {
                        result.SkippedPaths.Add(path);
                        continue;
                    }
                    Undo(path);
                    result.RestoredPaths.Add(path);
                }
                catch (Exception ex)
                {
                    result.Failures.Add(path, ex);
                }
            }
        }
        return result;
    }

    public sealed class UndoBatchResult
    {
        public List<string> RestoredPaths { get; } = new();
        public List<string> SkippedPaths { get; } = new();
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public UpdateBatchResult ReplaceMany(IEnumerable<(string Original, string Downloaded, string Updated)> replacements)
    {
        var result = new UpdateBatchResult();
        foreach (var replacement in replacements)
        {
            try
            {
                Replace(replacement.Original, replacement.Updated, replacement.Downloaded);
                result.UpdatedPaths.Add(replacement.Updated);
            }
            catch (Exception ex)
            {
                result.Failures[replacement.Original] = ex;
            }
        }
        return result;
    }

    public sealed class UpdateBatchResult
    {
        public List<string> UpdatedPaths { get; } = new();
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
