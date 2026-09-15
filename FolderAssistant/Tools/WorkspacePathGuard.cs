namespace FolderAssistant.Tools;

/// <summary>Which rule of the containment guard refused a path.</summary>
internal enum ContainmentRefusal
{
	/// <summary>The path was empty or whitespace, which would have named the root by omission.</summary>
	EmptyPath,

	/// <summary>The path used Windows device syntax (<c>\\?\</c> or <c>\\.\</c>), which skips normalisation.</summary>
	DevicePath,

	/// <summary>The full path is not the root and not beneath it.</summary>
	OutsideRoot,

	/// <summary>The path names the metadata folder or something inside it.</summary>
	MetadataFolder,

	/// <summary>A segment below the root is a symbolic link or junction.</summary>
	ReparsePoint,

	/// <summary>The file has a hard-link name outside the root.</summary>
	HardLinkOutsideRoot,
}

/// <summary>
/// A path the guard accepted.
/// </summary>
/// <param name="FullPath">The full path as the caller named it; what a tool opens.</param>
/// <param name="RelativePath">
/// The path under the root, with no leading separator and no drive letter; what a result shows. Empty
/// for the root itself.
/// </param>
/// <param name="Note">
/// Set when the physical-identity check could not be completed and the textual rule decided alone. A
/// tool passes it on rather than dropping it.
/// </param>
internal sealed record GuardedPath(String FullPath, String RelativePath, String? Note);

/// <summary>
/// Raised when a caller-supplied path is refused. Carries which rule refused it and the path as given,
/// for the layer that turns a tool failure into something the model must report.
/// </summary>
internal sealed class WorkspaceContainmentException : InvalidOperationException
{
	public WorkspaceContainmentException(ContainmentRefusal refusal, String path, String message)
		: base(message)
	{
		this.Refusal = refusal;
		this.Path = path;
	}

	public ContainmentRefusal Refusal { get; }

	public String Path { get; }
}

/// <summary>
/// Resolves every caller-supplied path against one workspace root and refuses anything that leaves it.
/// The rule and each way it could be defeated are SPEC-101.
///
/// <para>
/// Containment is decided on the full, normalised path — so <c>..</c> is refused when its collapsed
/// result leaves the root, not as a token — and then the ways a textual check can be fooled are closed in
/// turn: a redirecting reparse point at any segment below the root, a hard link whose other name is
/// outside the root, and a <c>subst</c> drive letter standing for a path that is. The last two are
/// Windows-only checks; elsewhere the textual rule and the link check are all there is.
/// </para>
/// </summary>
internal sealed class WorkspacePathGuard
{
	/// <summary>
	/// Windows paths are compared without case. Elsewhere they are compared ordinally even on a
	/// case-insensitive volume, because refusing a case-only variant is the safe direction to be wrong in.
	/// </summary>
	private static readonly StringComparison PathComparison =
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private readonly String _root;
	private readonly String _physicalRoot;
	private readonly String? _rootNote;
	private readonly String _metadataFolderName;

	/// <param name="workspaceRoot">The analyzed folder. Made absolute once; a guard is not re-pointed.</param>
	/// <param name="metadataFolderName">The name of the folder the index lives in, directly under the root.</param>
	public WorkspacePathGuard(String workspaceRoot, String metadataFolderName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(metadataFolderName);

		this._root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		(this._physicalRoot, this._rootNote) = ToPhysical(this._root);
		this._metadataFolderName = metadataFolderName;
	}

	/// <summary>The workspace root as a full path, without a trailing separator unless it is a drive root.</summary>
	public String Root => this._root;

	/// <summary>
	/// Resolves <paramref name="path"/> — relative to the root, or absolute — and returns it if it is inside
	/// the root; otherwise throws <see cref="WorkspaceContainmentException"/> naming the rule that refused it.
	/// </summary>
	public GuardedPath Resolve(String path)
	{
		if (String.IsNullOrWhiteSpace(path))
		{
			throw Refuse(ContainmentRefusal.EmptyPath, path ?? String.Empty, "an empty path names nothing");
		}

		if (IsDevicePath(path))
		{
			throw Refuse(ContainmentRefusal.DevicePath, path, "device-path syntax skips normalisation");
		}

		String full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, this._root));
		(String physical, String? note) = ToPhysical(full);

		String? relative = RelativeUnder(physical, this._physicalRoot);
		if (relative is null)
		{
			throw Refuse(ContainmentRefusal.OutsideRoot, path, $"it resolves to '{full}', outside '{this._root}'");
		}

		if (this.FirstSegmentIsMetadataFolder(relative))
		{
			throw Refuse(ContainmentRefusal.MetadataFolder, path, $"'{this._metadataFolderName}' is the index's own folder");
		}

		this.RefuseRedirectingSegments(path, relative);

		if (OperatingSystem.IsWindows())
		{
			note = this.RefuseHardLinksOutsideRoot(path, physical, note);
		}

		return new GuardedPath(full, relative, this._rootNote ?? note);
	}

	/// <summary>
	/// Whether <paramref name="fullPath"/> is the metadata folder or inside it — the one rule that both
	/// refuses a path and hides one from a listing.
	/// </summary>
	public Boolean IsMetadataFolder(String fullPath)
	{
		String candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath, this._root));
		String? relative = RelativeUnder(candidate, this._root);

		return relative is not null && this.FirstSegmentIsMetadataFolder(relative);
	}

	private static Boolean IsDevicePath(String path)
		=> path.StartsWith(@"\\?\", StringComparison.Ordinal)
			|| path.StartsWith(@"\\.\", StringComparison.Ordinal)
			|| path.StartsWith("//?/", StringComparison.Ordinal)
			|| path.StartsWith("//./", StringComparison.Ordinal);

	/// <summary>
	/// The part of <paramref name="candidate"/> under <paramref name="root"/>, empty when they are the same
	/// path, or null when the candidate is outside. Built on a root-plus-separator prefix so that a sibling
	/// sharing the root's prefix is outside and a drive root, which already ends in its separator, still works.
	/// </summary>
	private static String? RelativeUnder(String candidate, String root)
	{
		if (candidate.Equals(root, PathComparison))
		{
			return String.Empty;
		}

		String rootWithSeparator = Path.EndsInDirectorySeparator(root)
			? root
			: root + Path.DirectorySeparatorChar;

		return candidate.StartsWith(rootWithSeparator, PathComparison)
			? candidate[rootWithSeparator.Length..]
			: null;
	}

	private static (String Physical, String? Note) ToPhysical(String fullPath)
	{
		if (!OperatingSystem.IsWindows())
		{
			return (fullPath, null);
		}

		return WindowsPathProbe.ResolveSubstitution(fullPath);
	}

	private Boolean FirstSegmentIsMetadataFolder(String relative)
	{
		if (relative.Length == 0)
		{
			return false;
		}

		Int32 separator = relative.IndexOf(Path.DirectorySeparatorChar);
		ReadOnlySpan<Char> first = separator < 0 ? relative : relative.AsSpan(0, separator);

		return first.Equals(this._metadataFolderName, PathComparison);
	}

	/// <summary>
	/// Walks every segment of <paramref name="relative"/> below the root and refuses the first that is a
	/// symbolic link or junction. Only existing segments can redirect, so the walk stops at the first that
	/// does not exist. Reparse points with no link target — cloud placeholders, deduplicated files — are
	/// not refused: they redirect nothing.
	/// </summary>
	private void RefuseRedirectingSegments(String path, String relative)
	{
		if (relative.Length == 0)
		{
			return;
		}

		String current = this._root;
		foreach (String segment in relative.Split(Path.DirectorySeparatorChar))
		{
			current = Path.Join(current, segment);

			FileSystemInfo info = new FileInfo(current);
			if (!info.Exists)
			{
				info = new DirectoryInfo(current);
				if (!info.Exists)
				{
					return;
				}
			}

			if (info.LinkTarget is not null)
			{
				throw Refuse(ContainmentRefusal.ReparsePoint, path, $"'{current}' is a link to '{info.LinkTarget}'");
			}
		}
	}

	/// <summary>
	/// Refuses an existing file that the volume also knows by a name outside the physical root. Names are
	/// reported relative to the volume, so they are composed onto the physical path's root, not the textual
	/// one. A volume that cannot enumerate names is noted, not refused.
	/// </summary>
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private String? RefuseHardLinksOutsideRoot(String path, String physical, String? note)
	{
		if (!File.Exists(physical))
		{
			return note;
		}

		(IReadOnlyList<String> names, String? enumerationNote) = WindowsPathProbe.HardLinkNames(physical);
		String? outside = names.FirstOrDefault(name => RelativeUnder(name, this._physicalRoot) is null);
		if (outside is not null)
		{
			throw Refuse(ContainmentRefusal.HardLinkOutsideRoot, path, $"the file is also '{outside}'");
		}

		return note ?? enumerationNote;
	}

	private static WorkspaceContainmentException Refuse(ContainmentRefusal refusal, String path, String reason)
		=> new(refusal, path, $"Path '{path}' refused ({refusal}): {reason}.");
}
