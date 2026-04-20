using FieldCure.Mcp.Rag.Search;

namespace FieldCure.Mcp.Rag.Tests.Search;

[TestClass]
public class RrfFusionTests
{
    [TestMethod]
    public void SingleList_PreservesOrder()
    {
        var list = new List<string> { "a", "b", "c" };
        var result = RrfFusion.Fuse([list], topK: 3);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual("a", result[0].Id);
        Assert.AreEqual("b", result[1].Id);
        Assert.AreEqual("c", result[2].Id);
        Assert.IsTrue(result[0].Score > result[1].Score);
    }

    [TestMethod]
    public void TwoIdenticalLists_BoostsScores()
    {
        var list1 = new List<string> { "a", "b" };
        var list2 = new List<string> { "a", "b" };
        var result = RrfFusion.Fuse([list1, list2], topK: 2);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("a", result[0].Id);
        // Score should be doubled compared to single list
        var singleResult = RrfFusion.Fuse([list1], topK: 2);
        Assert.IsTrue(Math.Abs(result[0].Score - 2 * singleResult[0].Score) < 1e-10);
    }

    [TestMethod]
    public void DisjointLists_InterleavedByScore()
    {
        var list1 = new List<string> { "a", "b" };
        var list2 = new List<string> { "c", "d" };
        var result = RrfFusion.Fuse([list1, list2], topK: 4);

        Assert.AreEqual(4, result.Count);
        // "a" and "c" both at rank 0 in their lists → same score → interleaved
        // "b" and "d" both at rank 1 → same score
        var rank0Score = result[0].Score;
        var rank1Score = result[1].Score;
        Assert.AreEqual(rank0Score, rank1Score); // a and c tie
    }

    [TestMethod]
    public void TopK_LimitsResults()
    {
        var list = new List<string> { "a", "b", "c", "d", "e" };
        var result = RrfFusion.Fuse([list], topK: 2);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("a", result[0].Id);
        Assert.AreEqual("b", result[1].Id);
    }

    [TestMethod]
    public void EmptyLists_ReturnsEmpty()
    {
        var result = RrfFusion.Fuse([], topK: 5);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void EmptyInnerList_HandledGracefully()
    {
        var list1 = new List<string> { "a" };
        var list2 = new List<string>();
        var result = RrfFusion.Fuse([list1, list2], topK: 5);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("a", result[0].Id);
    }

    [TestMethod]
    public void OverlappingLists_BoostsSharedItems()
    {
        var list1 = new List<string> { "a", "b", "c" };
        var list2 = new List<string> { "d", "a", "e" };
        var result = RrfFusion.Fuse([list1, list2], topK: 5);

        // "a" appears in both lists (rank 0 and rank 1) → boosted to top
        Assert.AreEqual("a", result[0].Id);
    }
}
