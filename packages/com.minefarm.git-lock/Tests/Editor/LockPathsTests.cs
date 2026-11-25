using NUnit.Framework;

namespace Minefarm.GitLock.Tests
{
    public sealed class LockPathsTests
    {
        [TestCase("Assets/A.prefab", "assets/a.prefab.meta", true)]
        [TestCase("Assets/A", "Assets/AB", false)]
        [TestCase("Assets/A", "Assets/A/file.cs", false)]
        public void FileMatching(string a, string b, bool expected) =>
            Assert.AreEqual(expected, LockPaths.Overlaps(a, false, b, false));

        [Test]
        public void FolderMatching()
        {
            Assert.IsTrue(LockPaths.Overlaps("Assets/Art", true, "Assets/Art/New/file.png", false));
            Assert.IsTrue(LockPaths.Overlaps("Assets/Art", true, "Assets/Art.meta", false));
            Assert.IsTrue(LockPaths.Overlaps("Assets/Art/Models", true, "Assets/Art", true));
            Assert.IsFalse(LockPaths.Overlaps("Assets/Art", true, "Assets/Artwork/a.png", false));
        }

        [TestCase("../Assets/A")]
        [TestCase("/Assets/A")]
        [TestCase("C:/A")]
        [TestCase("Assets//A")]
        [TestCase("Assets/../A")]
        [TestCase(".git/config")]
        public void RejectInvalidPaths(string path) => Assert.IsFalse(LockPaths.IsValid(path));

        [TestCase("README.md")]
        [TestCase("Assets/새 파일.cs")]
        [TestCase("ProjectSettings/TagManager.asset")]
        public void AcceptOrdinaryFiles(string path) => Assert.IsTrue(LockPaths.IsValid(path));
    }
}
