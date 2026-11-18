using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>verify_pending, on received and verified files as Verify writes them, in a directory of their own.</summary>
public class VerifyPendingTests
{
    private DirectoryInfo directory = null!;

    private string In(params string[] parts) => Path.Combine([directory.FullName, .. parts]);

    [SetUp]
    public void WriteSnapshots()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-verify-");
        Directory.CreateDirectory(In("Tests"));
        Directory.CreateDirectory(In("Tests", "bin"));
        File.WriteAllText(In("Tests", "OrderTests.Total.verified.txt"), "total: 10\nitems: 2\n");
        File.WriteAllText(In("Tests", "OrderTests.Total.received.txt"), "total: 12\nitems: 2\n");
        File.WriteAllText(In("Tests", "OrderTests.New.received.json"), "{ \"id\": 1 }");
        // Build output copies them, and is not where they are reviewed.
        File.WriteAllText(In("Tests", "bin", "OrderTests.Total.received.txt"), "total: 12\n");
    }

    [TearDown]
    public void Delete() => directory.Delete(recursive: true);

    [Test]
    public async Task Pending_HasEachReceivedFile_WithItsDiff_OrItsTextWhenNew()
    {
        var pending = (await VerifyPendingTool.ReviewAsync(directory.FullName, [], [], CancellationToken.None)).Pending.OrderBy(snapshot => snapshot.Received).ToList();

        pending.Select(snapshot => (snapshot.Received, snapshot.Verified, snapshot.New)).Should().Equal(
            (In("Tests", "OrderTests.New.received.json"), In("Tests", "OrderTests.New.verified.json"), true),
            (In("Tests", "OrderTests.Total.received.txt"), In("Tests", "OrderTests.Total.verified.txt"), false));
        pending[0].Diff.Should().Be("{ \"id\": 1 }");
        pending[1].Diff.Should().Contain("-total: 10").And.Contain("+total: 12").And.NotContain("-items");
    }

    [Test]
    public async Task Accept_MakesTheReceivedFileVerified_AndRejectDeletesIt()
    {
        var result = await VerifyPendingTool.ReviewAsync(directory.FullName, [In("Tests", "OrderTests.Total.received.txt")], [In("Tests", "OrderTests.New.received.json")], CancellationToken.None);

        result.Pending.Should().BeEmpty();
        (result.Accepted.Single(), result.Rejected.Single()).Should().Be((In("Tests", "OrderTests.Total.received.txt"), In("Tests", "OrderTests.New.received.json")));
        File.ReadAllText(In("Tests", "OrderTests.Total.verified.txt")).Should().Be("total: 12\nitems: 2\n");
        File.Exists(In("Tests", "OrderTests.Total.received.txt")).Should().BeFalse();
        File.Exists(In("Tests", "OrderTests.New.received.json")).Should().BeFalse();
        File.Exists(In("Tests", "OrderTests.New.verified.json")).Should().BeFalse();
    }

    [Test]
    public async Task AcceptAll_AcceptsEveryPendingFile()
    {
        (await VerifyPendingTool.ReviewAsync(directory.FullName, ["*"], [], CancellationToken.None)).Accepted.Should().HaveCount(2);

        File.ReadAllText(In("Tests", "OrderTests.New.verified.json")).Should().Be("{ \"id\": 1 }");
        File.Exists(In("Tests", "bin", "OrderTests.Total.received.txt")).Should().BeTrue();
    }

    [Test]
    public async Task AFileThatIsNotPending_ChangesNothing()
    {
        await FluentActions.Awaiting(() => VerifyPendingTool.ReviewAsync(directory.FullName, ["*"], [In("Tests", "Other.received.txt")], CancellationToken.None))
            .Should().ThrowAsync<McpException>().WithMessage("*is not a received file that is pending*");

        File.Exists(In("Tests", "OrderTests.New.verified.json")).Should().BeFalse();
    }

    [TestCase("A.B.received.txt", "A.B.verified.txt")]
    [TestCase("A.B#00.received.cs", "A.B#00.verified.cs")]
    [TestCase("received.A.received.txt", "received.A.verified.txt")]
    public void Verified_IsNamedAsVerifyNamesIt(string received, string verified)
    {
        Path.GetFileName(VerifyPendingTool.VerifiedOf(Path.Combine("x", received))).Should().Be(verified);
    }
}
