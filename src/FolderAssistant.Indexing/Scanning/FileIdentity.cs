using System.Security.Cryptography;
using System.Text;

namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// The document id a file is delivered under: <c>sha256("file::" + relative key)</c>, lowercase hex.
///
/// <para>
/// It must be exactly the id the application's corpus scanner derives for the same file, because the
/// embedding side keys its rows on it. Delivered one file at a time under any other id, a file would
/// land on different rows from the ones a whole-folder scan wrote — indexed twice, and removable only
/// under the id it was not delivered with. The two derivations live in two assemblies by agreement, so
/// the agreement is asserted by a test that runs the application's scanner, not by this comment.
/// </para>
///
/// <para>
/// A cryptographic hash here, unlike change detection's: this value is an identity stored in rows and
/// compared across processes and machines, so it has to be stable everywhere, not merely fast.
/// </para>
/// </summary>
public static class FileIdentity
{
    /// <param name="relativeKey">The file's key as <see cref="IndexKey"/> derives it.</param>
    public static string For(string relativeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeKey);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("file::" + relativeKey))).ToLowerInvariant();
    }
}
