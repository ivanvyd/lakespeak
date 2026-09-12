using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LakeSpeak.QuestionPacks;

/// <summary>An output path that was checked before a pack spends warehouse compute.</summary>
internal sealed partial class PackOutputDestination : IDisposable
{
    private readonly string _safetyRoot;
    private readonly bool _overwrite;
    private readonly IReadOnlyList<DirectoryFingerprint> _parents;
    private readonly IReadOnlyList<SafeFileHandle> _directoryLocks;

    private PackOutputDestination(
        string fullPath,
        string safetyRoot,
        bool overwrite,
        IReadOnlyList<DirectoryFingerprint> parents,
        IReadOnlyList<SafeFileHandle> directoryLocks)
    {
        FullPath = fullPath;
        _safetyRoot = safetyRoot;
        _overwrite = overwrite;
        _parents = parents;
        _directoryLocks = directoryLocks;
    }

    public string FullPath { get; }

    /// <summary>
    /// Validates and prepares a report destination before any remote work starts.
    /// </summary>
    /// <param name="baseDirectory">Directory containing the pack.</param>
    /// <param name="path">Pack path or explicit command-line override.</param>
    /// <param name="isPackPath">
    /// True for an untrusted path read from the pack. Explicit user overrides may name a path
    /// outside the pack directory, but still cannot traverse links while LakeSpeak writes it.
    /// </param>
    /// <param name="overwrite">Whether an existing ordinary file may be atomically replaced.</param>
    public static PackOutputDestination Preflight(
        string baseDirectory,
        string path,
        bool isPackPath,
        bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new PackOutputException("The pack directory is missing.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new PackOutputException("The output path is missing.");
        }

        string packRoot;
        string target;
        try
        {
            packRoot = Path.GetFullPath(baseDirectory);
            if (isPackPath && Path.IsPathRooted(path))
            {
                throw new PackOutputException($"Pack output path '{path}' must be relative to the pack file.");
            }

            target = Path.GetFullPath(Path.Combine(packRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PackOutputException($"Output path '{path}' is invalid: {ex.Message}", ex);
        }

        if (isPackPath && !QuestionPackLoader.IsInside(target, packRoot))
        {
            throw new PackOutputException($"Pack output path '{path}' resolves outside the pack directory.");
        }

        var parent = Path.GetDirectoryName(target)
            ?? throw new PackOutputException($"Output path '{path}' has no parent directory.");
        var safetyRoot = isPackPath
            ? packRoot
            : FindExistingDirectory(parent, path);

        EnsureDirectories(safetyRoot, parent);
        RejectLinks(target, safetyRoot);

        if (Directory.Exists(target))
        {
            throw new PackOutputException($"{target} is a directory, not a report file.");
        }

        if (!overwrite && File.Exists(target))
        {
            throw new PackOutputException($"{target} already exists. Pass --force to overwrite it.");
        }

        var parents = CaptureParents(safetyRoot, parent);
        var directoryLocks = AcquireDirectoryLocks(parents);
        try
        {
            // On Windows the handles now prevent component replacement. Check again to catch a
            // swap that won between the managed preflight and handle acquisition.
            RejectLinks(target, safetyRoot);
            EnsureParentsMatch(parents);
            if (!overwrite && File.Exists(target))
            {
                throw new PackOutputException($"{target} already exists. Pass --force to overwrite it.");
            }

            ProbeWritable(parent);

            return new PackOutputDestination(
                target,
                safetyRoot,
                overwrite,
                parents,
                directoryLocks);
        }
        catch
        {
            DisposeHandles(directoryLocks);
            throw;
        }
    }

    /// <summary>
    /// Writes a complete temporary file and atomically installs it at the verified destination.
    /// </summary>
    /// <remarks>
    /// A failed or cancelled write never truncates a previous report. The parent chain is checked
    /// again before staging and before the atomic move, closing observable directory/link swaps.
    /// Filesystems do not offer a portable managed no-follow rename rooted at a directory handle;
    /// a same-user attacker that swaps an ordinary directory during the final system call remains
    /// outside this managed guarantee. Reparse points seen at either boundary fail closed.
    /// </remarks>
    public async Task WriteAtomicAsync(
        Func<TextWriter, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        EnsureUnchanged();

        var parent = Path.GetDirectoryName(FullPath)!;
        var temporary = Path.Combine(parent, $".lakespeak-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 65536,
                    leaveOpen: true);
                await write(writer, cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnchanged();
            File.Move(temporary, FullPath, _overwrite);
        }
        finally
        {
            if (CanCleanUpTemporary(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // The destination remains complete. A locked staging file is safe to remove later.
                }
                catch (UnauthorizedAccessException)
                {
                    // As above: never risk the installed report while cleaning an uncommitted sibling.
                }
            }
        }
    }

    private static void EnsureDirectories(string safetyRoot, string parent)
    {
        if (!Directory.Exists(safetyRoot))
        {
            throw new PackOutputException($"Output filesystem root '{safetyRoot}' does not exist.");
        }

        var relative = Path.GetRelativePath(safetyRoot, parent);
        var current = safetyRoot;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RejectLinks(current, safetyRoot);
            try
            {
                Directory.CreateDirectory(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackOutputException($"Cannot create output directory '{current}': {ex.Message}", ex);
            }

            RejectLinks(current, safetyRoot);
        }
    }

    private static string FindExistingDirectory(string path, string originalPath)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
        }

        throw new PackOutputException($"Output path '{originalPath}' has no existing parent directory.");
    }

    private static void RejectLinks(string target, string safetyRoot)
    {
        if (QuestionPackLoader.ContainsLink(target, safetyRoot))
        {
            throw new PackOutputException(
                $"Output path '{target}' contains a symbolic link or junction and cannot be written safely.");
        }
    }

    private static List<DirectoryFingerprint> CaptureParents(string safetyRoot, string parent)
    {
        var parents = new List<DirectoryFingerprint>();
        for (var current = parent;
             current.Length >= safetyRoot.Length;
             current = Path.GetDirectoryName(current)!)
        {
            var info = new DirectoryInfo(current);
            parents.Add(new DirectoryFingerprint(
                current,
                OperatingSystem.IsWindows() ? info.CreationTimeUtc : null));
            if (PathsEqual(current, safetyRoot))
            {
                break;
            }
        }

        return parents;
    }

    private static List<SafeFileHandle> AcquireDirectoryLocks(
        IReadOnlyList<DirectoryFingerprint> parents)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var handles = new List<SafeFileHandle>(parents.Count);
        try
        {
            foreach (var parent in parents)
            {
                // Omitting FILE_SHARE_DELETE keeps each verified component in place until the
                // final atomic move. OPEN_REPARSE_POINT prevents this call from following a link
                // introduced between the managed check and acquiring the handle.
                var handle = CreateFile(
                    parent.Path,
                    desiredAccess: 0,
                    FileShare.Read | FileShare.Write,
                    securityAttributes: 0,
                    creationDisposition: OpenExisting,
                    FlagsAndAttributes,
                    templateFile: 0);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new PackOutputException(
                        $"Cannot secure output directory '{parent.Path}': {new Win32Exception(error).Message}");
                }

                handles.Add(handle);
            }

            return handles;
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static void ProbeWritable(string parent)
    {
        var probe = Path.Combine(parent, $".lakespeak-{Guid.NewGuid():N}.probe");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackOutputException(
                $"Output directory '{parent}' is not writable: {ex.Message}", ex);
        }
    }

    private void EnsureUnchanged()
    {
        RejectLinks(FullPath, _safetyRoot);
        EnsureParentsMatch(_parents);

        if (!_overwrite && File.Exists(FullPath))
        {
            // This pre-write check avoids needless staging. The atomic no-overwrite move below is
            // authoritative when another writer wins after this point.
            throw new IOException($"{FullPath} was created after output validation.");
        }
    }

    private bool CanCleanUpTemporary(string temporary)
    {
        try
        {
            RejectLinks(temporary, _safetyRoot);
            EnsureParentsMatch(_parents);
            return true;
        }
        catch (PackOutputException)
        {
            // Never follow a changed path merely to clean up. The random staging entry may remain
            // in the original directory, but no different path is touched.
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record DirectoryFingerprint(string Path, DateTime? CreationTimeUtc);

    private static void EnsureParentsMatch(IReadOnlyList<DirectoryFingerprint> parents)
    {
        foreach (var expected in parents)
        {
            var current = new DirectoryInfo(expected.Path);
            if (!current.Exists
                || (expected.CreationTimeUtc is { } expectedCreationTime
                    && current.CreationTimeUtc != expectedCreationTime))
            {
                throw new PackOutputException(
                    $"Output directory '{expected.Path}' changed after validation; the report was not written.");
            }
        }
    }

    private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    public void Dispose() => DisposeHandles(_directoryLocks);

    private const uint OpenExisting = 3;
    private const uint FlagsAndAttributes = 0x02000000 | 0x00200000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}

internal sealed class PackOutputException : IOException
{
    public PackOutputException(string message)
        : base(message)
    {
    }

    public PackOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
