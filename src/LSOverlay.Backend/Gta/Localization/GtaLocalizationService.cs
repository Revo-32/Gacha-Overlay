using System.Text.Json;
using System.Threading.Channels;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Gta.Localization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Gta.Localization;

internal sealed record GtaLocalizationDiagnostics(long Requests, long CacheHits, long Joins, long RejectedSources,
    long Failures, int InFlight, int Cached, string Model);

internal sealed class GtaLocalizationService : BackgroundService
{
    public const int MaximumEntries = 64;
    private const int MaximumStoreBytes = 4 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly IGtaLocalizationProvider _provider;
    private readonly GtaLocalizationGlossary _glossary;
    private readonly LocalizationOverrides _overrides;
    private readonly ILogger<GtaLocalizationService> _logger;
    private readonly string _path;
    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(8) { SingleReader = true });
    private readonly Dictionary<string, TaskCompletionSource<bool>> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stored> _memory = new(StringComparer.Ordinal);
    // Retired identities remain bounded on disk, but can never serve current translations.
    private readonly Dictionary<string, Stored> _retired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _views = new(StringComparer.Ordinal);
    private long _requests, _hits, _joins, _rejected, _failures;
    private bool _stopping;
    private sealed record Job(string Identity, PreparedLocalization Prepared, TaskCompletionSource<bool> Completion);
    private sealed record Stored(string Identity, PublicGtaLocalizationInput Input, string Response);
    public event Action? Changed;

    public GtaLocalizationService(IGtaLocalizationProvider provider, string stateDirectory, ILogger<GtaLocalizationService> logger,
        GtaLocalizationGlossary? glossary = null, LocalizationOverrides? overrides = null)
    {
        _provider = provider;
        _glossary = glossary ?? GtaLocalizationGlossary.Default;
        _overrides = overrides ?? LocalizationOverrides.Default;
        _path = Path.Combine(stateDirectory, "gta-localization-memory.json");
        _logger = logger;
        Load();
    }
    public GtaLocalizationDiagnostics Diagnostics()
    {
        lock (_sync) return new(_requests, _hits, _joins, _rejected, _failures, _inFlight.Count, _memory.Count, _provider.Model);
    }
    public string? Find(string? source)
    {
        if (source is null) return null;
        lock (_sync)
        {
            foreach (var view in _views.Values.Reverse()) if (view.TryGetValue(source, out var value)) return value;
            return null;
        }
    }
    public Task<bool> Submit(CanonicalEventDocument document)
    {
        var input = TrustedGtaLocalizationSourcePolicy.Extract(document);
        if (input is null) { lock (_sync) _rejected++; return Task.FromResult(false); }
        PreparedLocalization prepared;
        try { prepared = LocalizationValidation.Prepare(input, _glossary); }
        catch (InvalidDataException) { lock (_sync) _rejected++; return Task.FromResult(false); }
        var identity = Identity(input);
        lock (_sync)
        {
            if (_stopping) return Task.FromResult(false);
            if (_inFlight.TryGetValue(identity, out var existing)) { _joins++; return existing.Task; }
            if (_memory.ContainsKey(identity))
            {
                _hits++;
                // One structural proof per process, not a per-request cache log.
                if (_hits == 1) _logger.LogInformation("GTA localization first cache hit revision={Revision} generation_requests={Requests}.", input.SourceRevision, _requests);
                return Task.FromResult(true);
            }
            if (_failed.TryGetValue(identity, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(10)) return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.Writer.TryWrite(new(identity, prepared, completion))) return Task.FromResult(false);
            _inFlight.Add(identity, completion);
            return completion.Task;
        }
    }
    private string Identity(PublicGtaLocalizationInput input) => GtaLocalizationGlossary.Digest(string.Join('|',
        TrustedGtaLocalizationSourcePolicy.Version, _glossary.Hash, _overrides.Hash, GeminiGtaLocalizationProvider.PromptVersion,
        GeminiGtaLocalizationProvider.SchemaVersion, KoreanLocalizationSurface.Version, LocalizationProtection.Version, LocalizationRepairFeedback.Version,
        _provider.Model, JsonSerializer.Serialize(input, GeminiGtaLocalizationProvider.JsonOptions)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var success = false;
                var reason = "Unavailable";
                var correlation = Guid.NewGuid().ToString("N");
                LocalizationRepairFeedback? feedback = null;
                try
                {
                    var overrides = _overrides.Resolve(job.Prepared);
                    if (overrides.Count > 0)
                    {
                        lock (_sync) { TrimViews(); _views[job.Identity] = overrides; }
                        Notify();
                    }
                    if (overrides.Count == job.Prepared.Fields.Count) { success = true; continue; }
                    for (var attempt = 0; attempt < 2 && !stoppingToken.IsCancellationRequested; attempt++)
                    {
                        var response = await _provider.TranslateAsync(job.Prepared.ProtectedInput, attempt == 1, stoppingToken, feedback).ConfigureAwait(false);
                        lock (_sync) if (response.Category != "MissingCredential") _requests++;
                        reason = response.Category;
                        _logger.LogInformation("GTA localization model={Model} revision={Revision} result={Result} elapsed_ms={Elapsed} attempt={Attempt}.",
                            _provider.Model, job.Prepared.Input.SourceRevision, reason, response.ElapsedMilliseconds, attempt + 1);
                        if (response.Json is null) break;
                        if (!LocalizationValidation.TryValidate(job.Prepared, response.Json, out var view, out reason, out var diagnostics))
                        {
                            feedback = LocalizationRepairFeedback.Create(diagnostics, response.Json);
                            _logger.LogInformation("GTA localization validation rejected correlation={Correlation} revision={Revision} category={Category} attempt={Attempt} model={Model} prompt={Prompt} protection={Protection} repair={Repair} diagnostics={Diagnostics}.",
                                correlation, job.Prepared.Input.SourceRevision, reason, attempt + 1, _provider.Model,
                                GeminiGtaLocalizationProvider.PromptVersion, LocalizationProtection.Version, LocalizationRepairFeedback.Version,
                                JsonSerializer.Serialize(feedback.Fields, LocalizationRepairFeedback.DiagnosticJson));
                            continue;
                        }
                        if (stoppingToken.IsCancellationRequested) break;
                        var merged = new Dictionary<string, string>(view, StringComparer.Ordinal);
                        foreach (var pair in overrides) merged[pair.Key] = pair.Value;
                        lock (_sync)
                        {
                            while (_memory.Count >= MaximumEntries) Remove(_memory.Keys.First());
                            TrimViews();
                            _memory[job.Identity] = new(job.Identity, job.Prepared.Input, response.Json);
                            _views[job.Identity] = merged;
                            _failed.Remove(job.Identity);
                        }
                        Save();
                        success = true;
                        Notify();
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    reason = "ProcessingFailure";
                    // Provider exceptions may contain credentials. Never log the exception.
                }
                finally
                {
                    lock (_sync)
                    {
                        _inFlight.Remove(job.Identity);
                        if (!success)
                        {
                            _failures++;
                            if (_failed.Count >= MaximumEntries) _failed.Remove(_failed.Keys.First());
                            _failed[job.Identity] = DateTimeOffset.UtcNow;
                        }
                    }
                    if (!success) _logger.LogWarning("GTA localization rejected category={Category}; source-compatible translation or existing formatter retained.", reason);
                    job.Completion.TrySetResult(success);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_sync)
            {
                _stopping = true;
                _queue.Writer.TryComplete();
                foreach (var pending in _inFlight.Values) pending.TrySetResult(false);
                _inFlight.Clear();
                while (_queue.Reader.TryRead(out _)) { }
            }
        }
    }
    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _logger.LogWarning("GTA localization subscriber failed."); }
    }
    private void TrimViews()
    {
        while (_views.Count >= MaximumEntries) Remove(_views.Keys.First());
    }
    private void Remove(string identity) { _views.Remove(identity); _memory.Remove(identity); }
    private void Load()
    {
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumStoreBytes) continue;
                var entries = JsonSerializer.Deserialize<Stored[]>(File.ReadAllText(path), GeminiGtaLocalizationProvider.JsonOptions);
                if (entries is null || entries.Length > MaximumEntries) continue;
                foreach (var entry in entries)
                {
                    if (entry is null || entry.Input is null || entry.Input.Items is null ||
                        entry.Identity is null || entry.Identity.Length != 64 || entry.Response is null || entry.Response.Length > 128 * 1024) continue;
                    var prepared = LocalizationValidation.Prepare(entry.Input, _glossary);
                    if (entry.Identity != Identity(entry.Input)) { _retired[entry.Identity] = entry; continue; }
                    if (!LocalizationValidation.TryValidate(prepared, entry.Response, out var view, out _)) continue;
                    var merged = new Dictionary<string, string>(view, StringComparer.Ordinal);
                    foreach (var pair in _overrides.Resolve(prepared)) merged[pair.Key] = pair.Value;
                    _memory[entry.Identity] = entry;
                    _views[entry.Identity] = merged;
                }
                if (_memory.Count > 0 || _retired.Count > 0 || entries.Length == 0)
                {
                    _logger.LogInformation("GTA translation memory loaded current={Current} retired={Retired} policy={Policy}.",
                        _memory.Count, _retired.Count, LocalizationProtection.Version);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
            { _logger.LogWarning("GTA translation memory invalid; checking Last-Good backup."); }
        }
    }
    private void Save()
    {
        var temporary = _path + ".tmp";
        try
        {
            string json;
            lock (_sync)
            {
                string Serialize() => JsonSerializer.Serialize(_retired.Values.Concat(_memory.Values).ToArray(), GeminiGtaLocalizationProvider.JsonOptions);
                json = Serialize();
                while ((_retired.Count + _memory.Count > MaximumEntries || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumStoreBytes) &&
                    _retired.Count + _memory.Count > 1)
                {
                    if (_retired.Count > 0) _retired.Remove(_retired.Keys.First());
                    else Remove(_memory.Keys.First());
                    json = Serialize();
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, json);
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning("GTA translation memory save failed; previous persistent state retained."); }
    }
}
