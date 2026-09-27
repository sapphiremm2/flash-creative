namespace AdobeDownloader.Core.Tests;
public class DirectoryTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(root, "target");
    private string Journal => Path.Combine(root, "journal");
    public DirectoryTransactionTests() => Directory.CreateDirectory(Target);
    [Fact] public async Task CreatesParentsAndRollbackKeepsPreexistingAndPopulatedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(Target, "existing"));
        await DirectoryTransaction.ApplyAsync(Target, [@"new\leaf", @"keep\leaf", "existing"], Journal);
        File.WriteAllText(Path.Combine(Target, "keep", "leaf", "user.txt"), "user");
        await DirectoryTransaction.RollbackAsync(Target, Journal); await DirectoryTransaction.RollbackAsync(Target, Journal);
        Assert.False(Directory.Exists(Path.Combine(Target, "new"))); Assert.True(Directory.Exists(Path.Combine(Target, "existing")));
        Assert.Equal("user", File.ReadAllText(Path.Combine(Target, "keep", "leaf", "user.txt")));
    }
    [Theory] [InlineData(@"..\escape")] [InlineData("CON")] [InlineData("file:stream")]
    public async Task RejectsUnsafeTargets(string relative) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => DirectoryTransaction.ApplyAsync(Target, [relative], Journal));
    [Fact] public async Task PreparedRecoveryHandlesOnlySomeDirectoriesCreated()
    {
        Directory.CreateDirectory(Journal); Directory.CreateDirectory(Path.Combine(Target, "new"));
        await JsonFiles.WriteAsync(Path.Combine(Journal, "directories.json"), new DirectoryUndoJournal(1, Target, "Prepared", ["new", @"new\leaf"]));
        await DirectoryTransaction.RollbackAsync(Target, Journal); Assert.False(Directory.Exists(Path.Combine(Target, "new")));
    }
    public void Dispose() => Directory.Delete(root, true);
}
