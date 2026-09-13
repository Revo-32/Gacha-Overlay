using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LSOverlay.CoreMedia;

public sealed record CacheBudget(long Hard = 1L << 30, long High = 900L << 20, long Low = 768L << 20, int MaximumFiles = 16_384)
{
    public void Validate() { if (Low <= 0 || Low >= High || High >= Hard || MaximumFiles is < 8 or > 16_384) throw new ArgumentException("Invalid cache watermarks."); }
}

// A single owner worker holds this directory. No database, no original archive.
// Cache files + current temp input/output are accounted together. No symlinks.
public sealed class DerivativeCache : IDisposable
{
    private readonly string _root;
    private readonly CacheBudget _budget;
    private readonly FileStream _owner;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly SemaphoreSlim _slots = new(4);
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private bool _disposed;
    public int Conversions { get; private set; }
    public int Hits { get; private set; }
    public int Evictions { get; private set; }
    public string Root => _root;
    private static bool KeyValid(string key) => Regex.IsMatch(key, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);

    public DerivativeCache(string root, CacheBudget? budget = null)
    {
        _budget = budget ?? new(); _budget.Validate();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Absolute dedicated cache directory required.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (_root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_root)!)) throw new ArgumentException("Drive/root cannot be a cache.");
        CheckAncestors(_root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any() && !File.Exists(Path.Combine(_root, ".core-media-cache-v1")))
            throw new InvalidDataException("Refusing a non-cache directory.");
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = Path.Combine(_root, ".core-media-cache-v1");
        CheckAncestors(marker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "Disposable LS Overlay Core derivative cache v1\n");
        else if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != "Disposable LS Overlay Core derivative cache v1\n") throw new InvalidDataException("Unknown cache owner marker.");
        CheckAncestors(Path.Combine(_root, ".owner.lock"));
        _owner = new FileStream(Path.Combine(_root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var files = Directory.GetFiles(_root);
            if (files.Length > _budget.MaximumFiles || Directory.EnumerateDirectories(_root).Any()) throw new InvalidDataException("Unexpected cache structure.");
            var orphans = new List<string>();
            foreach (var path in files)
            {
                CheckAncestors(path);
                var file = Path.GetFileName(path);
                if (file is ".owner.lock" or ".core-media-cache-v1") continue;
                if (Regex.IsMatch(file, "\\A[0-9a-f]{32}\\.(source|pending)\\z")) orphans.Add(path);
                else if (!file.EndsWith(".lscm", StringComparison.Ordinal) || !KeyValid(file[..^5])) throw new InvalidDataException("Unknown cache file; not deleting it.");
            }
            foreach (var path in orphans) File.Delete(path); // only after the entire owned directory is validated
            MakeRoom(0);
        }
        catch { _owner.Dispose(); throw; }
    }

    public static void CheckAncestors(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cache/source symlink or reparse point rejected.");
    }

    private string Entry(string key) => KeyValid(key) ? Path.Combine(_root, key + ".lscm") : throw new ArgumentException("Invalid derivative key.");
    private IEnumerable<FileInfo> Entries() => new DirectoryInfo(_root).EnumerateFiles("*.lscm").OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal);
    private long Used() => new DirectoryInfo(_root).EnumerateFiles().Where(file => !file.Name.StartsWith('.')).Sum(file => file.Length);

    private void MakeRoom(long additional)
    {
        lock (_sync) MakeRoomLocked(additional);
    }
    private void ReserveTemporarySlots()
    {
        lock (_sync)
        {
            var count = Directory.EnumerateFiles(_root).Count();
            if (count + 2 <= _budget.MaximumFiles) return;
            foreach (var entry in Entries())
            {
                if (_leases.ContainsKey(entry.Name[..^5])) continue;
                try { File.Delete(entry.FullName); count--; Evictions++; }
                catch (IOException) { }
                if (count + 2 <= _budget.MaximumFiles) return;
            }
            throw new IOException("Cache file-count budget is busy/full.");
        }
    }
    private void MakeRoomLocked(long additional)
    {
        if (additional < 0 || additional > _budget.Hard) throw new IOException("Cache reservation exceeds budget.");
        var used = Used();
        if (used + additional <= _budget.High) return;
        foreach (var entry in Entries())
        {
            if (used + additional <= _budget.Low) break;
            if (_leases.ContainsKey(entry.Name[..^5])) continue; // required on Linux too: open files can otherwise be unlinked
            var length = entry.Length;
            try { File.Delete(entry.FullName); used -= length; Evictions++; }
            catch (IOException) { } // leased file may be held on Windows; never force it
        }
        if (used + additional > _budget.Hard) throw new IOException("Cache is busy/full; quality is unchanged.");
    }

    public async Task<string> GetOrCreateAsync(Stream source, MediaProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_slots.Wait(0)) throw new IOException("Media worker queue is full.");
        }
        // This gate also coalesces content-identical work. Callers independently
        // stream/hash new URLs, then the already committed derivative is reused.
        try { await _serial.WaitAsync(cancellationToken); }
        catch { _slots.Release(); throw; }
        string? input = null, pending = null;
        try
        {
            ReserveTemporarySlots();
            var nonce = Guid.NewGuid().ToString("N");
            input = Path.Combine(_root, nonce + ".source"); pending = Path.Combine(_root, nonce + ".pending");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > MediaConverter.MaximumSourceBytes) throw new InvalidDataException("Source byte budget exceeded.");
                    MakeRoom(read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); await output.FlushAsync(cancellationToken);
                    hash.AppendData(buffer, 0, read);
                }
            }
            var key = profile.Key(Convert.ToHexString(hash.GetHashAndReset())); var target = Entry(key);
            if (File.Exists(target))
            {
                try
                {
                    using var existing = Open(key); var package = MediaPackage.Read(existing);
                    for (var i = 0; i < package.Frames.Count; i++) { cancellationToken.ThrowIfCancellationRequested(); package.ReadFrame(existing, i); }
                    File.SetLastWriteTimeUtc(target, DateTime.UtcNow); Hits++; return key;
                }
                catch (InvalidDataException) { RemoveCorrupt(key); }
                catch (EndOfStreamException) { RemoveCorrupt(key); }
            }
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var bounded = new BudgetStream(output, additional => MakeRoom(additional)))
            {
                MediaConverter.Convert(input, bounded, profile, cancellationToken); output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, target); pending = null; Conversions++; return key;
        }
        finally
        {
            try
            {
                try { if (input is not null) File.Delete(input); }
                finally { if (pending is not null) File.Delete(pending); }
            }
            finally { _serial.Release(); _slots.Release(); }
        }
    }

    // Callers must serve this only after live message/channel authorization.
    public Stream Open(string key)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = Entry(key); CheckAncestors(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _leases[key] = _leases.GetValueOrDefault(key) + 1;
            return new LeaseStream(stream, () => { lock (_sync) { if (--_leases[key] == 0) _leases.Remove(key); } });
        }
    }
    private void RemoveCorrupt(string key)
    {
        lock (_sync)
        {
            if (_leases.ContainsKey(key)) throw new IOException("Corrupt derivative is still leased.");
            File.Delete(Entry(key));
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_slots.CurrentCount != 4 || _leases.Count != 0) throw new InvalidOperationException("Drain jobs and leases before disposing the worker.");
            _disposed = true; _owner.Dispose(); _serial.Dispose(); _slots.Dispose();
        }
    }

    private sealed class LeaseStream(FileStream inner, Action release) : Stream
    {
        private int _closed;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0) { try { inner.Dispose(); } finally { release(); } }
            base.Dispose(disposing);
        }
    }

    private sealed class BudgetStream(FileStream inner, Action<long> reserve) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => true; public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var extra = Math.Max(0, inner.Position + buffer.Length - inner.Length);
            if (inner.Position > MediaPackage.MaximumBytes - buffer.Length) throw new IOException("Derivative budget exceeded.");
            reserve(extra); inner.Write(buffer); inner.Flush();
        }
    }
}
