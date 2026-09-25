using System.Security.Cryptography;
using System.Text;

namespace AdobeDownloader.Core.Tests;

public class FileTransactionTests
{
    [Theory] [InlineData("normal")] [InlineData("changed")] [InlineData("backup")]
    [InlineData("baseline")] [InlineData("source")] [InlineData("budget")] [InlineData("cancel")]
    [InlineData("traversal")] [InlineData("duplicate")]
    public async Task ReplacementAndRecoveryPreserveData(string scenario)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "flash-transaction-test-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(workspace, "target"); var journal = Path.Combine(workspace, "undo");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "file"); var source = Path.Combine(workspace, "source");
            await File.WriteAllTextAsync(target, "old"); await File.WriteAllTextAsync(source, "new");
            var items = new List<FileReplacement> { new(scenario == "traversal" ? "../escape" : "file", source,
                scenario == "source" ? Hash("wrong") : Hash("new"), scenario == "baseline" ? Hash("wrong") : Hash("old")),
                new("added", source, Hash("new"), null) };
            if (scenario == "duplicate") items.Add(items[0]);
            var run = () => FileTransaction.ApplyAsync(root, items, journal, scenario == "budget" ? 1 : 100, new CancellationToken(scenario == "cancel"));
            if (scenario is "baseline" or "source" or "budget" or "traversal" or "duplicate" or "cancel")
            {
                if (scenario == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(run);
                else await Assert.ThrowsAsync<InvalidDataException>(run);
                Assert.Equal("old", await File.ReadAllTextAsync(target)); Assert.False(File.Exists(Path.Combine(root, "added"))); return;
            }
            await run(); Assert.Equal("new", await File.ReadAllTextAsync(target));
            Assert.Equal("Committed", (await JsonFiles.ReadAsync<FileUndoJournal>(Path.Combine(journal, "journal.json"))).State);
            if (scenario == "changed") await File.WriteAllTextAsync(target, "user edit");
            if (scenario == "backup") await File.WriteAllTextAsync(Path.Combine(journal, "0.old"), "corrupt");
            if (scenario != "normal")
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => FileTransaction.RollbackAsync(root, journal));
                Assert.Equal(scenario == "changed" ? "user edit" : "new", await File.ReadAllTextAsync(target));
                Assert.True(File.Exists(Path.Combine(root, "added"))); // all conflicts checked before any rollback mutation
            }
            else
            {
                await FileTransaction.RollbackAsync(root, journal); await FileTransaction.RollbackAsync(root, journal);
                Assert.Equal("old", await File.ReadAllTextAsync(target)); Assert.False(File.Exists(Path.Combine(root, "added")));
                Assert.Equal("RolledBack", (await JsonFiles.ReadAsync<FileUndoJournal>(Path.Combine(journal, "journal.json"))).State);
            }
        }
        finally { Directory.Delete(workspace, true); }
    }
    [Fact] public async Task PreparedJournalRecoversPartiallyAppliedTransaction()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "flash-recovery-test-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(workspace, "target"); var journal = Path.Combine(workspace, "undo");
        Directory.CreateDirectory(root); Directory.CreateDirectory(journal);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one"), "new");
            await File.WriteAllTextAsync(Path.Combine(root, "two"), "old");
            await File.WriteAllTextAsync(Path.Combine(journal, "0.old"), "old");
            await File.WriteAllTextAsync(Path.Combine(journal, "1.old"), "old");
            await JsonFiles.WriteAsync(Path.Combine(journal, "journal.json"), new FileUndoJournal(1, root, "Prepared",
                [new("one", Hash("new"), Hash("old"), "0.old"), new("two", Hash("new"), Hash("old"), "1.old")]));
            await FileTransaction.RollbackAsync(root, journal);
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(root, "one")));
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(root, "two")));
        }
        finally { Directory.Delete(workspace, true); }
    }
    [Fact] public async Task ExistingLockPreventsConcurrentTransactionWithoutDeletingLock()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "flash-lock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var lockPath = Path.Combine(workspace, ".flash-transaction.lock");
            await File.WriteAllTextAsync(lockPath, "existing owner");
            await Assert.ThrowsAsync<IOException>(() => FileTransaction.ApplyAsync(workspace,
                [new("file", "unused", Hash("new"), null)], workspace + "-journal", 100));
            Assert.Equal("existing owner", await File.ReadAllTextAsync(lockPath));
        }
        finally { Directory.Delete(workspace, true); }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
