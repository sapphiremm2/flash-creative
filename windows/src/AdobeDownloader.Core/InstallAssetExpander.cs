using System.IO.Compression;

namespace AdobeDownloader.Core;

/// <summary>Read-only expansion for verified hd-standard archives with payload prefix 1/.
/// Caller holds the same archive open against writes throughout verification and expansion.</summary>
public static class InstallAssetExpander
{
    public static WindowsInstallPlan Expand(WindowsInstallPlan plan, Stream verifiedArchive, string stagingRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows asset planning requires Windows.");
        if (plan.Assets.Count > 128) throw new InvalidDataException("Asset mapping limit exceeded.");
        stagingRoot = Path.GetFullPath(stagingRoot).TrimEnd('\\');
        var blockers = plan.Blockers.Where(b => b.Kind != "Assets/Overlap").ToList();
        var files = new Dictionary<string, PlannedAssetFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = new ZipArchive(verifiedArchive, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 100000) throw new InvalidDataException("Archive entry limit exceeded.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            foreach (var component in name.Split('/')) PackageDownloader.ValidateFileName(component);
            if (!entries.TryAdd(name, entry) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("Archive contains links or ambiguous paths.");
            if (name != "1" && !name.StartsWith("1/", StringComparison.Ordinal) && !name.Equals(plan.Package + ".pimx", StringComparison.OrdinalIgnoreCase))
                AddBlocker(new(null, "ArchiveLayout", "Unsupported archive entry outside the single payload root: " + name));
        }
        if (!plan.Applicable) return plan with { Files = [], Directories = [], Blockers = blockers };
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.Assets.Count; index++)
        {
            var asset = plan.Assets[index];
            if (!asset.Source.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase) && !asset.Source.StartsWith(stagingRoot + "\\", StringComparison.OrdinalIgnoreCase))
            { AddBlocker(new(index, "Assets/Source", "Asset source is outside the configured staging root.")); continue; }
            var relativeSource = Path.GetRelativePath(stagingRoot, asset.Source).Replace('\\', '/');
            var prefix = relativeSource == "." ? "1" : "1/" + relativeSource;
            if (entries.TryGetValue(prefix, out var direct) && !IsDirectory(direct))
            { AddBlocker(new(index, "Assets/File", "Direct-file asset semantics are not implemented.")); continue; }
            var matching = entries.Where(e => e.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0)
            { AddBlocker(new(index, "Assets/Missing", "Asset source is absent from the archive: " + prefix)); continue; }
            if (!asset.Ignored) AddDirectory(asset.Target);
            foreach (var (name, entry) in matching)
            {
                var relative = name.Length == prefix.Length ? "" : name[(prefix.Length + 1)..];
                if (!asset.Recursive && relative.Contains('/')) continue;
                if (asset.Ignored) { covered.Add(name); continue; }
                var target = Path.GetFullPath(Path.Combine(asset.Target, relative.Replace('/', '\\')));
                if (!target.Equals(asset.Target, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(asset.Target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Expanded asset escapes its target.");
                covered.Add(name);
                if (IsDirectory(entry))
                {
                    if (asset.Recursive || relative.Length == 0) AddDirectory(target);
                    continue;
                }
                var file = new PlannedAssetFile(entry.FullName, target, entry.Length);
                if (files.TryGetValue(target, out var previous) && previous.ArchiveEntry != file.ArchiveEntry)
                    AddBlocker(new(index, "Assets/Collision", "Multiple archive files target " + target));
                else files[target] = file;
                if (files.Count > 100000 || directories.Count > 200000) throw new InvalidDataException("Expanded asset limit exceeded.");
                var parent = Path.GetDirectoryName(target)!;
                while (parent.Equals(asset.Target, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(asset.Target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                { AddDirectory(parent); parent = Path.GetDirectoryName(parent)!; if (parent is null) break; }
            }
        }
        foreach (var target in directories.Where(files.ContainsKey)) AddBlocker(new(null, "Assets/Collision", "File/directory collision at " + target));
        foreach (var file in files.Values)
        {
            for (var parent = Path.GetDirectoryName(file.Target); parent is not null; parent = Path.GetDirectoryName(parent))
                if (files.ContainsKey(parent)) { AddBlocker(new(null, "Assets/Collision", "File occupies a required parent directory: " + parent)); break; }
        }
        foreach (var (name, entry) in entries)
            if (name.StartsWith("1/", StringComparison.Ordinal) && !IsDirectory(entry) && !covered.Contains(name))
                AddBlocker(new(null, "Assets/Unmapped", "Payload is not covered by any asset: " + name));
        // Shell artifacts share the installation namespace with payload files and directories.
        // Keep these checks read-only: successful mapping does not authorize COM or filesystem writes.
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredDirectories = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        if ((plan.Shortcuts?.Count ?? 0) + (plan.FolderIcons?.Count ?? 0) > 10000)
            throw new InvalidDataException("Shell operation limit exceeded.");
        foreach (var shortcut in plan.Shortcuts ?? [])
        {
            if (!files.ContainsKey(shortcut.Target))
                AddBlocker(new(null, "Shell/MissingTarget", "Shortcut target is not a mapped payload file: " + shortcut.Target));
            AddGenerated(shortcut.LinkPath);
        }
        foreach (var icon in plan.FolderIcons ?? [])
        {
            if (!files.ContainsKey(icon.IconPath))
                AddBlocker(new(null, "Shell/MissingTarget", "Folder icon is not a mapped payload file: " + icon.IconPath));
            if (!directories.Contains(icon.FolderPath))
                AddBlocker(new(null, "Shell/MissingFolder", "Icon folder is not a mapped payload directory: " + icon.FolderPath));
            AddGenerated(Path.Combine(icon.FolderPath, "desktop.ini"));
        }
        foreach (var path in generated)
            if (files.ContainsKey(path) || requiredDirectories.Contains(path))
                AddBlocker(new(null, "Shell/Collision", "Generated shell artifact conflicts with an installation target: " + path));
        void AddGenerated(string path)
        {
            if (!generated.Add(path)) AddBlocker(new(null, "Shell/Collision", "Multiple shell artifacts target " + path));
            for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if (files.ContainsKey(parent))
                    AddBlocker(new(null, "Shell/Collision", "File occupies a shell artifact parent directory: " + parent));
                requiredDirectories.Add(parent);
            }
        }
        return plan with { Files = files.Values.OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase).ToArray(),
            Directories = directories.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Blockers = blockers, CanExecute = false };
        void AddBlocker(InstallPlanBlocker blocker)
        {
            if (blockers.Count >= 10000) throw new InvalidDataException("Asset diagnostic limit exceeded.");
            blockers.Add(blocker);
        }
        void AddDirectory(string target)
        {
            if (directories.Add(target) && directories.Count > 200000) throw new InvalidDataException("Expanded directory limit exceeded.");
        }
    }
    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
}
