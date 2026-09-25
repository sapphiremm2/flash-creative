using Microsoft.Win32;
using System.Runtime.Versioning;

namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class RegistryTransactionTests
{
    [Theory]
    [InlineData(RegistryValueKind.String)] [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.Binary)] [InlineData(RegistryValueKind.None)]
    [InlineData(RegistryValueKind.MultiString)] [InlineData(RegistryValueKind.DWord)] [InlineData(RegistryValueKind.QWord)]
    public async Task RestoresOriginalTypeAndBytesAndDeletesOnlyCreatedEmptyKeys(RegistryValueKind kind)
    {
        await WithScope(async (scope, directory) =>
        {
            var before = Snapshot(kind);
            using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, writable: true)!) key.SetValue("", Data(before), kind);
            AssertSnapshot(before, RegistryTransaction.Read(scope, "", ""));
            var changes = new[] { new RegistryReplacement("", "", before, new(RegistryValueKind.String, Text: "installed")),
                new RegistryReplacement("Nested\\Leaf", "added", null, new(RegistryValueKind.Binary, Bytes: [1, 2, 3])) };
            await RegistryTransaction.ApplyAsync(scope, changes, directory);
            Assert.Equal("installed", RegistryTransaction.Read(scope, "", "")!.Text);
            await RegistryTransaction.RollbackAsync(scope, directory); await RegistryTransaction.RollbackAsync(scope, directory);
            AssertSnapshot(before, RegistryTransaction.Read(scope, "", ""));
            using var removed = Registry.CurrentUser.OpenSubKey(scope.Root + "\\Nested"); Assert.Null(removed);
            using var root = Registry.CurrentUser.OpenSubKey(scope.Root); Assert.NotNull(root);
        });
    }
    [Theory] [InlineData("changed")] [InlineData("baseline")] [InlineData("duplicate")] [InlineData("cancel")]
    public async Task ConflictsDoNotOverwriteUserValues(string scenario)
    {
        await WithScope(async (scope, directory) =>
        {
            var old = new RegistryValueSnapshot(RegistryValueKind.String, Text: "old");
            using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!) key.SetValue("value", "old");
            var entries = new List<RegistryReplacement> { new("", "value", scenario == "baseline" ? null : old, new(RegistryValueKind.String, Text: "new")) };
            if (scenario == "duplicate") entries.Add(entries[0]);
            if (scenario == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => RegistryTransaction.ApplyAsync(scope, entries, directory, new(true)));
            else if (scenario is "baseline" or "duplicate") await Assert.ThrowsAsync<InvalidDataException>(() => RegistryTransaction.ApplyAsync(scope, entries, directory));
            else
            {
                await RegistryTransaction.ApplyAsync(scope, entries, directory);
                using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!) key.SetValue("value", "user edit");
                await Assert.ThrowsAsync<InvalidDataException>(() => RegistryTransaction.RollbackAsync(scope, directory));
            }
            Assert.Equal(scenario == "changed" ? "user edit" : "old", RegistryTransaction.Read(scope, "", "value")!.Text);
        });
    }
    [Fact] public async Task AddedUserValuesKeepCreatedKeysAlive()
    {
        await WithScope(async (scope, directory) =>
        {
            await RegistryTransaction.ApplyAsync(scope, [new("NewKey", "installed", null, new(RegistryValueKind.String, Text: "new"))], directory);
            using (var key = Registry.CurrentUser.OpenSubKey(scope.Root + "\\NewKey", true)!) key.SetValue("user", "keep");
            await RegistryTransaction.RollbackAsync(scope, directory);
            Assert.Null(RegistryTransaction.Read(scope, "NewKey", "installed"));
            Assert.Equal("keep", RegistryTransaction.Read(scope, "NewKey", "user")!.Text);
        });
    }
    [Fact] public async Task PreparedJournalRecoversPartialApplication()
    {
        await WithScope(async (scope, directory) =>
        {
            Directory.CreateDirectory(directory);
            using (var key = Registry.CurrentUser.CreateSubKey(scope.Root + "\\Child")) key.SetValue("one", "new");
            var entries = new[] { new RegistryReplacement("Child", "one", null, new(RegistryValueKind.String, Text: "new")),
                new RegistryReplacement("Child", "two", null, new(RegistryValueKind.String, Text: "new")) };
            await JsonFiles.WriteAsync(Path.Combine(directory, "registry-journal.json"), new RegistryUndoJournal(1, scope, "Prepared", entries, ["Child"]));
            await RegistryTransaction.RollbackAsync(scope, directory);
            using var keyAfter = Registry.CurrentUser.OpenSubKey(scope.Root + "\\Child"); Assert.Null(keyAfter);
        });
    }
    [Fact] public async Task WrongScopeAndEscapingKeysAreRejected()
    {
        await WithScope(async (scope, directory) =>
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RegistryTransaction.ApplyAsync(scope,
                [new("..\\escape", "value", null, new(RegistryValueKind.String, Text: "bad"))], directory));
            await RegistryTransaction.ApplyAsync(scope, [new("", "value", null, new(RegistryValueKind.String, Text: "new"))], directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => RegistryTransaction.RollbackAsync(scope with { Root = scope.Root + "Other" }, directory));
            Assert.Equal("new", RegistryTransaction.Read(scope, "", "value")!.Text);
        });
    }
    private static async Task WithScope(Func<RegistryScope, string, Task> action)
    {
        var id = Guid.NewGuid().ToString("N");
        var scope = new RegistryScope(RegistryHive.CurrentUser, RegistryView.Registry64, "Software\\FlashCreativeTests\\" + id);
        var workspace = Path.Combine(Path.GetTempPath(), "flash-registry-test-" + id); Directory.CreateDirectory(workspace);
        using (Registry.CurrentUser.CreateSubKey(scope.Root)) { }
        try { await action(scope, Path.Combine(workspace, "journal")); }
        finally { Registry.CurrentUser.DeleteSubKeyTree(scope.Root, false); Directory.Delete(workspace, true); }
    }
    private static RegistryValueSnapshot Snapshot(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => new(kind, Text: "original"), RegistryValueKind.ExpandString => new(kind, Text: "%TEMP%\\literal"),
        RegistryValueKind.Binary or RegistryValueKind.None => new(kind, Bytes: [0, 255, 17]),
        RegistryValueKind.MultiString => new(kind, Strings: ["one", "two"]),
        RegistryValueKind.DWord => new(kind, Number: -1), _ => new(kind, Number: long.MinValue)
    };
    private static object Data(RegistryValueSnapshot value) => value.Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => value.Text!, RegistryValueKind.Binary or RegistryValueKind.None => value.Bytes!,
        RegistryValueKind.MultiString => value.Strings!, RegistryValueKind.DWord => (int)value.Number!.Value, _ => value.Number!.Value
    };
    private static void AssertSnapshot(RegistryValueSnapshot expected, RegistryValueSnapshot? actual) =>
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));
}
