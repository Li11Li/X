﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NewLife;
using NewLife.Http;
using NewLife.Log;
using NewLife.Remoting;
using NewLife.Security;

namespace Test;

public class ApiHttpClientTests : DisposeBase
{
    private readonly ApiServer _Server;
    private readonly String _Address;
    private readonly IApiClient _Client;

    public ApiHttpClientTests()
    {
        _Server = new ApiServer(12347)
        {
            Log = XTrace.Log,
            EncoderLog = XTrace.Log,
        };
        _Server.Handler = new TokenApiHandler { Host = _Server };
        _Server.Start();

        _Address = "http://127.0.0.1:12347";

        //_Client = new ApiHttpClient();
        //_Client.Add("addr1", new Uri("http://127.0.0.1:12347"));
        _Client = new ApiHttpClient(_Address)
        {
            Log = XTrace.Log
        };
    }

    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _Server.TryDispose();
    }

    public async Task BasicTest()
    {
        var apis = await _Client.InvokeAsync<String[]>("api/all");
        Assert.NotNull(apis);
        Assert.Equal(2, apis.Length);
        Assert.Equal("String[] Api/All()", apis[0]);
        Assert.Equal("Object Api/Info(String state)", apis[1]);
        //Assert.Equal("Packet Api/Info2(Packet state)", apis[2]);
    }

    public async Task InfoTest()
    {
        var state = Rand.NextString(8);
        var state2 = Rand.NextString(8);

        var infs = await _Client.InvokeAsync<IDictionary<String, Object>>("api/info", new { state, state2 });
        Assert.NotNull(infs);
        Assert.Equal(Environment.MachineName, infs["MachineName"]);
        //Assert.Equal(Environment.UserName, infs["UserName"]);

        Assert.Equal(state, infs["state"]);
        Assert.Null(infs["state2"]);
    }

    //[Fact(DisplayName = "二进制测试")]
    //public async void Info2Test()
    //{
    //    var buf = Rand.NextBytes(32);

    //    var pk = await _Client.InvokeAsync<Packet>("api/info2", buf);
    //    Assert.NotNull(pk);
    //    Assert.True(pk.Total > buf.Length);
    //    Assert.Equal(buf, pk.Slice(pk.Total - buf.Length, -1).ToArray());
    //}

    public async Task ErrorTest()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _Client.InvokeAsync<Object>("api/info3"));

        Assert.NotNull(ex);
        Assert.Equal(404, ex.Code);
        //Assert.True(ex.Message.EndsWith("无法找到名为[api/info3]的服务！"));
        Assert.EndsWith("无法找到名为[api/info3]的服务！", ex.Message);
    }

    public async Task TokenTest(String token, String state)
    {
        var client = new ApiHttpClient(_Address) { Token = token };
        var ac = client as IApiClient;

        var infs = await ac.InvokeAsync<IDictionary<String, Object>>("api/info", new { state });
        Assert.NotNull(infs);
        Assert.Equal(token, infs["token"]);

        // 另一个客户端，共用令牌，应该可以拿到上一次状态数据
        var client2 = new ApiHttpClient(_Address) { Token = token };

        infs = await client2.GetAsync<IDictionary<String, Object>>("api/info");
        Assert.NotNull(infs);
        //Assert.Equal(state, infs["LastState"]);
    }

    public void SlaveTest()
    {
        var client = new ApiHttpClient("http://127.0.0.1:11000,http://127.0.0.1:20000," + _Address)
        {
            Log = XTrace.Log,
            Timeout = 1_000
        };
        var ac = client as IApiClient;

        var infs = ac.Invoke<IDictionary<String, Object>>("api/info");
        Assert.NotNull(infs);
    }

    public async Task SlaveAsyncTest()
    {
        var filter = new TokenHttpFilter
        {
            UserName = "test",
            Password = "",
        };
        var client = new ApiHttpClient("http://127.0.0.1:11001,http://127.0.0.1:20001,http://star.newlifex.com:6600")
        {
            Filter = filter,
            Log = XTrace.Log,
            Timeout = 1_000
        };

        var rs = await client.PostAsync<Object>("config/getall", new { appid = "test" });
        Assert.NotNull(rs);

        var ss = client.Services;
        Assert.Equal(3, ss.Count);
        Assert.Equal(1, ss[0].Times);
        Assert.Equal(1, ss[0].Errors);
        Assert.Equal(1, ss[1].Times);
        Assert.Equal(1, ss[1].Errors);
        Assert.Equal(1, ss[2].Times);
        Assert.Equal(0, ss[2].Errors);
    }

    public async Task RoundRobinTest()
    {
        var client = new ApiHttpClient("test1=3*http://127.0.0.1:11000,test2=7*http://127.0.0.1:20000,")
        {
            RoundRobin = true,
            Timeout = 3_000,
            Log = XTrace.Log,
        };

        Assert.Equal(2, client.Services.Count);

        // 再加两个
        client.Add("test3", "2*" + _Address);
        client.Add("test4", "1*" + _Address);

        Assert.Equal(4, client.Services.Count);

        {
            var svc = client.Services[0];
            Assert.Equal("test1", svc.Name);
            Assert.Equal(3, svc.Weight);
            Assert.Equal("http://127.0.0.1:11000/", svc.Address + "");

            svc = client.Services[1];
            Assert.Equal("test2", svc.Name);
            Assert.Equal(7, svc.Weight);
            Assert.Equal("http://127.0.0.1:20000/", svc.Address + "");

            svc = client.Services[2];
            Assert.Equal("test3", svc.Name);
            Assert.Equal(2, svc.Weight);
            Assert.Equal(_Address + "/", svc.Address + "");
        }

        var ac = client as IApiClient;

        {
            var infs = await ac.InvokeAsync<IDictionary<String, Object>>("api/info");
            Assert.NotNull(infs);
        }
        {
            var infs = await ac.InvokeAsync<IDictionary<String, Object>>("api/info");
            Assert.NotNull(infs);
        }
        {
            var infs = await ac.InvokeAsync<IDictionary<String, Object>>("api/info");
            Assert.NotNull(infs);
        }
        {
            var infs = await ac.InvokeAsync<IDictionary<String, Object>>("api/info");
            Assert.NotNull(infs);
        }

        // 判断结果
        {
            var svc = client.Services[0];
            Assert.Null(svc.Client);
            //Assert.True(svc.NextTime > DateTime.Now.AddSeconds(55));
            Assert.Equal(1, svc.Times);
        }
        {
            var svc = client.Services[1];
            Assert.Null(svc.Client);
            //Assert.True(svc.NextTime > DateTime.Now.AddSeconds(55));
            Assert.Equal(1, svc.Times);
        }
        {
            var svc = client.Services[2];
            Assert.NotNull(svc.Client);
            Assert.True(svc.NextTime.Year < 2000);
            Assert.Equal(3, svc.Times);
        }
        {
            var svc = client.Services[3];
            Assert.NotNull(svc.Client);
            Assert.True(svc.NextTime.Year < 2000);
            Assert.Equal(1, svc.Times);
        }
    }

    public async Task FilterTest()
    {
        var filter = new TokenHttpFilter
        {
            UserName = "test",
            Password = "",
        };

        var client = new ApiHttpClient("http://star.newlifex.com:6600")
        {
            Filter = filter,

            Log = XTrace.Log,
        };

        var rs = await client.PostAsync<Object>("config/getall", new { appid = "test" });

        Assert.NotNull(rs);
        Assert.NotNull(filter.Token);
    }
}