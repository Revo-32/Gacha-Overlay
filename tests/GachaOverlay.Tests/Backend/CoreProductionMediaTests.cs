using System.Net;
using System.Net.Http.Headers;
using LSOverlay.Backend.CoreClient;
using Microsoft.AspNetCore.Http;

namespace GachaOverlay.Tests.Backend;

public sealed class CoreProductionMediaTests
{
    private const string Source="https://cdn.discordapp.com/attachments/123/456/test.png";
    [Fact]
    public async Task UserScopeAndChannelRevocationAreIndependent()
    {
        var a=new CoreMediaScope("1",_=>"10",(_,_)=>Task.FromResult(true),CancellationToken.None);
        var b=new CoreMediaScope("2",_=>"10",(_,_)=>Task.FromResult(true),CancellationToken.None);
        var aid=a.RegisterCanonical("20","image","image",Source);var bid=b.RegisterCanonical("20","image","image",Source);
        Assert.NotEqual(aid,bid);
        Assert.Equal(Source,await a.Resolve(aid,CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>b.Resolve(aid,CancellationToken.None));
        a.RevokeChannel("10");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>a.Resolve(aid,CancellationToken.None));
        Assert.Equal(Source,await b.Resolve(bid,CancellationToken.None));
    }
    [Fact]
    public async Task RevocationDuringAuthorizationCannotDeliverBytes()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope=new CoreMediaScope("1",_=>"10",async (_,_)=>{entered.TrySetResult();await resume.Task;return true;},CancellationToken.None);
        var id=scope.RegisterCanonical("20","image","image",Source);
        var resolve=scope.Resolve(id,CancellationToken.None);await entered.Task;scope.Revoke("20");resume.SetResult();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>resolve);
    }
    [Theory]
    [InlineData(true,200)]
    [InlineData(false,403)]
    public async Task ConversionRequiresPermissionBeforeAndAfter(bool afterAllowed,int expected)
    {
        var permissions=0;var handler=new Worker();
        var scope=new CoreMediaScope("1",_=>"10",(_,_)=>Task.FromResult(++permissions==1 || afterAllowed),CancellationToken.None);
        var id=scope.RegisterCanonical("20","image","image",Source);
        using var gateway=new CoreMediaGateway(handler,new string('a',64));
        var context=new DefaultHttpContext();context.Response.Body=new MemoryStream();
        await gateway.Serve(context,scope,id,64,64);
        Assert.Equal(expected,context.Response.StatusCode);Assert.Equal(2,permissions);Assert.Equal(1,handler.Calls);
        Assert.Equal(afterAllowed?24:0,context.Response.Body.Length);
    }
    [Fact]
    public async Task PerSessionConcurrencyBudgetDoesNotConsumeOtherSessionBudget()
    {
        var scope=new CoreMediaScope("1",_=>"10",(_,_)=>Task.FromResult(true),CancellationToken.None);
        for(var i=0;i<4;i++)Assert.True(scope.TryEnter());Assert.False(scope.TryEnter());
        using var gateway=new CoreMediaGateway(new Worker(),new string('a',64));
        var context=new DefaultHttpContext();
        await gateway.Serve(context,scope,new string('a',48),64,64);Assert.Equal(429,context.Response.StatusCode);
        scope.Leave();Assert.True(scope.TryEnter());
        var other=new CoreMediaScope("2",_=>"10",(_,_)=>Task.FromResult(true),CancellationToken.None);Assert.True(other.TryEnter());
    }
    [Fact]
    public async Task WorkerRedirectAndUntrustedPackageHeadersFailClosed()
    {
        foreach(var status in new[]{HttpStatusCode.Redirect,HttpStatusCode.OK})
        {
            var scope=new CoreMediaScope("1",_=>"10",(_,_)=>Task.FromResult(true),CancellationToken.None);
            var id=scope.RegisterCanonical("20","image","image",Source);
            using var gateway=new CoreMediaGateway(new Worker{Status=status,BadHeader=true},new string('a',64));
            var context=new DefaultHttpContext();context.Response.Body=new MemoryStream();
            await gateway.Serve(context,scope,id,64,64);Assert.Equal(422,context.Response.StatusCode);Assert.Equal(0,context.Response.Body.Length);
        }
    }
    private sealed class Worker:HttpMessageHandler
    {
        public int Calls;public HttpStatusCode Status=HttpStatusCode.OK;public bool BadHeader;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;Assert.Equal("http://127.0.0.1:15191/internal/media",request.RequestUri!.AbsoluteUri);
            var response=new HttpResponseMessage(Status){Content=new ByteArrayContent(new byte[24])};
            response.Content.Headers.ContentType=new MediaTypeHeaderValue("application/vnd.lsoverlay.media-v1");
            response.Headers.Add("X-Core-Media-Key",BadHeader?"invalid":new string('b',64));
            response.Headers.Add("X-Core-Media-Width","64");response.Headers.Add("X-Core-Media-Height","64");response.Headers.Add("X-Core-Cache-Hit","1");
            return Task.FromResult(response);
        }
    }
}
