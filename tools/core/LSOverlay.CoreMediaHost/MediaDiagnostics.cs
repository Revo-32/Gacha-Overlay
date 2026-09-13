using LSOverlay.CoreMedia;
internal sealed class MediaDiagnostics
{
    private readonly object _gate=new();
    private long _ready,_animated,_hits,_failures;
    private object? _lastFailure;
    private int _maximumWidth,_maximumHeight,_maximumFrames;
    private double _queueMs,_prepareMs,_maxPrepareMs;
    private readonly Dictionary<string,(long Count,double Total,double Maximum)> _timings=new();
    private readonly Dictionary<string,long> _providers=new();
    public void ProviderReady(Uri source) {var provider=source.Host switch {"static.klipy.com" or "static1.klipy.com" or "static2.klipy.com"=>"klipy","media.giphy.com" or "media0.giphy.com" or "media1.giphy.com" or "media2.giphy.com" or "media3.giphy.com" or "media4.giphy.com"=>"giphy","media.tenor.com"=>"tenor",_=>"discord"};lock(_gate)_providers[provider]=_providers.GetValueOrDefault(provider)+1;}
    public void Timing(string stage,double milliseconds) {lock(_gate){var old=_timings.GetValueOrDefault(stage);_timings[stage]=(old.Count+1,old.Total+milliseconds,Math.Max(old.Maximum,milliseconds));}}
    public void Ready(MediaPackage package,bool hit,double queueMs,double prepareMs)
    {
        lock (_gate) {
            ++_ready;if (package.Frames.Count>1) ++_animated;if (hit) ++_hits;
            _queueMs+=queueMs;_prepareMs+=prepareMs;_maxPrepareMs=Math.Max(_maxPrepareMs,prepareMs);
            _maximumWidth=Math.Max(_maximumWidth,package.Width);_maximumHeight=Math.Max(_maximumHeight,package.Height);_maximumFrames=Math.Max(_maximumFrames,package.Frames.Count);
        }
    }
    public void Failed(string stage,Exception error)
    {
        // Code-owned categories only: no exception body, URL, path, identity or credentials.
        var reason=error switch {
            MediaSourceResponseException response=>response.Reason,
            OperationCanceledException=>"cancelled",HttpRequestException=>"network",
            InvalidDataException when error.Message=="Unsupported image."=>"image-format",
            InvalidDataException when error.Message=="Incomplete or malformed image frame."=>"decode-frame",
            InvalidDataException when error.Message=="Derivative byte budget exceeded; quality was not reduced."=>"derivative-bytes",
            InvalidDataException=>"invalid-data",IOException=>"io",_=>"invalid-request"
        };
        lock (_gate) {++_failures;_lastFailure=new {stage,reason,status=(error as MediaSourceResponseException)?.Status,at=DateTimeOffset.UtcNow};}
    }
    public object Snapshot() {lock (_gate) return new {ready=_ready,animated=_animated,hits=_hits,failures=_failures,maximumWidth=_maximumWidth,maximumHeight=_maximumHeight,maximumFrames=_maximumFrames,queueMs=_queueMs,prepareMs=_prepareMs,maxPrepareMs=_maxPrepareMs,providers=new Dictionary<string,long>(_providers),stages=_timings.ToDictionary(p=>p.Key,p=>new{count=p.Value.Count,totalMs=p.Value.Total,maxMs=p.Value.Maximum}),lastFailure=_lastFailure};}
}
