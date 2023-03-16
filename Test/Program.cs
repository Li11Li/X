﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using NewLife.Http;
using NewLife.Log;
using NewLife.Net;
using NewLife.Security;
using NewLife.Serialization;
using NewLife;

namespace Test;

public class Program
{
    private static void Main(string[] args)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

        //XTrace.Log = new NetworkLog();
        XTrace.UseConsole();
#if DEBUG
        XTrace.Debug = true;
#endif
        while (true)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
#if !DEBUG
            try
            {
#endif
            Test2();
#if !DEBUG
            }
            catch (Exception ex)
            {
                XTrace.WriteException(ex);
            }
#endif

            sw.Stop();
            Console.WriteLine("OK! 耗时 {0}", sw.Elapsed);
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.Key != ConsoleKey.C) break;
        }
    }

    static async void Test1()
    {
        var client = new TinyHttpClient("http://star.newlifex.com:6600");

        var html = client.GetString("http://newlifex.com");
        //XTrace.WriteLine(html);

        var rs = await client.GetAsync<Object>("api", new { state = 1234 });
        XTrace.WriteLine(rs.ToJson(true));

        var rs2 = await client.PostAsync<Object>("node/ping", new { state = 1234 });
        //var rs2 = await client.InvokeAsync<Object>("option", "api", new { state = 1234 });
        XTrace.WriteLine(rs2.ToJson(true));
    }

    static void Test2()
    {
        var server = new NetServer();
        server.Port = 88;
        server.NewSession += server_NewSession;
        //server.Received += server_Received;
        server.SocketLog = null;
        server.SessionLog = null;
        server.Start();

        var html = "新生命开发团队";

        var sb = new StringBuilder();
        sb.AppendLine("HTTP/1.1 200 OK");
        sb.AppendLine("Server: NewLife.WebServer");
        sb.AppendLine("Connection: keep-alive");
        sb.AppendLine("Content-Type: text/html; charset=UTF-8");
        sb.AppendFormat("Content-Length: {0}", Encoding.UTF8.GetByteCount(html));
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(html);

        response = sb.ToString().GetBytes();

        while (true)
        {
            Console.Title = String.Format("会话：{0:n0} 请求：{1:n0} 错误：{2:n0}", server.SessionCount, Request, Error);
            Thread.Sleep(500);
        }
    }

    static void server_NewSession(object sender, NetSessionEventArgs e)
    {
        var session = e.Session;
        session.Received += session_Received;
        session.Session.Error += (s, e2) => Error++;
    }

    static Int32 Request;
    static Int32 Error;

    static Byte[] response;
    static void session_Received(object sender, ReceivedEventArgs e)
    {
        Request++;

        var session = sender as INetSession;
        //XTrace.WriteLine("客户端 {0} 收到：{1}", session, e.Stream.ToStr());

        //XTrace.WriteLine(response.ToStr());
        session.Send(response);

        //session.Dispose();
    }

    static void Test3()
    {
    }

    static UdpServer _udpServer;
    static void Test5()
    {
        if (_udpServer != null) return;

        _udpServer = new UdpServer();
        _udpServer.Port = 888;
        //_udpServer.Received += _udpServer_Received;
        _udpServer.SessionTimeout = 5000;
        _udpServer.Open();

        var session = _udpServer.CreateSession(new IPEndPoint(IPAddress.Any, 0));
        for (int i = 0; i < 5; i++)
        {
            var buf = session.Receive();
            Console.WriteLine(buf.ToHex());
            session.Send("Hello");
        }

        //Console.ReadKey();
        _udpServer.Dispose();
        _udpServer = null;
    }

    static void _udpServer_Received(object sender, ReceivedEventArgs e)
    {
        var session = sender as ISocketSession;
        var pk = e.Packet;
        XTrace.WriteLine("{0} [{1}]：{2}", session.Remote, pk.Total, pk.ToHex());
    }

    static void Test4()
    {
    }

    static Int32 success = 0;
    static Int32 total = 0;
    static void GetMac(Object state)
    {
        var ip = IPAddress.Parse("192.168.0." + state);
        var mac = ip.GetMac();
        if (mac != null)
        {
            success++;
            Console.WriteLine("{0}\t{1}", ip, mac.ToHex("-"));
        }
        total++;
    }

    static void Test6()
    {
        // UDP没有客户端服务器之分。推荐使用NetUri指定服务器地址
        var udp = new UdpServer();
        udp.Remote = new NetUri("udp://smart.peacemoon.cn:7");
        udp.Received += (s, e) =>
        {
            XTrace.WriteLine("收到：{0}", e.Packet.ToStr());
        };
        udp.Open();
        udp.Send("新生命团队");
        udp.Send("学无先后达者为师！");

        // Tcp客户端会话。改用传统方式指定服务器地址
        var tcp = new TcpSession();
        tcp.Remote.Host = "smart.peacemoon.cn";
        tcp.Remote.Port = 13;
        tcp.Open();
        var str = tcp.ReceiveString();
        XTrace.WriteLine(str);

        // 产品级客户端用法。直接根据NetUri创建相应客户端
        var client = new NetUri("tcp://smart.peacemoon.cn:17").CreateRemote();
        client.Received += (s, e) =>
        {
            XTrace.WriteLine("收到：{0}", e.Packet.ToStr());
        };
        client.Open();

        Thread.Sleep(1000);
    }

    static void Test7()
    {
        //TestNewLife_Net test = new TestNewLife_Net();
        //test.StartTest();
        //test.StopTest();
    }

    private static void Test8()
    {
        XTrace.WriteLine("启动两个服务端");

        // 不管是哪一种服务器用法，都具有相同的数据接收处理事件
        var onReceive = new EventHandler<ReceivedEventArgs>((s, e) =>
        {
            // ReceivedEventArgs中标准使用Data+Length或Stream表示收到的数据，测试时使用ToStr/ToHex直接输出
            // UserState表示来源地址IPEndPoint
            XTrace.WriteLine("收到 {0}：{1}", e.UserState, e.Packet.ToStr());

            // 拿到会话，原样发回去。
            // 不管是TCP/UDP，都会有一个唯一的ISocketSession对象表示一个客户端会话
            var session = s as ISocketSession;
            session.Send(e.Packet);
        });

        // 入门级UDP服务器，直接收数据
        var udp = new UdpServer(3388);
        udp.Received += onReceive;
        udp.Open();

        // 入门级TCP服务器，先接收会话连接，然后每个连接再分开接收数据
        var tcp = new TcpServer(3388);
        tcp.NewSession += (s, e) =>
        {
            XTrace.WriteLine("新连接 {0}", e.Session);
            e.Session.Received += onReceive;
        };
        tcp.Start();

        // 轻量级应用服务器（不建议作为产品级使用），同时在TCP/TCPv6/UDP/UDPv6监听指定端口，统一事件接收数据
        var svr = new NetServer();
        svr.Port = 3377;
        svr.Received += onReceive;
        svr.Start();

        Console.WriteLine();

        // 构造多个客户端连接上面的服务端
        var uri1 = new NetUri(NetType.Udp, IPAddress.Loopback, 3388);
        var uri2 = new NetUri(NetType.Tcp, IPAddress.Loopback, 3388);
        var uri3 = new NetUri(NetType.Tcp, IPAddress.IPv6Loopback, 3377);
        var clients = new ISocketClient[] { uri1.CreateRemote(), uri2.CreateRemote(), uri3.CreateRemote() };

        // 打开每个客户端，如果是TCP，此时连接服务器。
        // 这一步也可以省略，首次收发数据时也会自动打开连接
        // TCP客户端设置AutoReconnect指定断线自动重连次数，默认3次。
        foreach (var item in clients)
        {
            item.Open();
        }

        Thread.Sleep(1000);
        Console.WriteLine();
        XTrace.WriteLine("以下灰色日志为客户端日志，其它颜色为服务端日志，可通过线程ID区分");

        // 循环发送几次数据
        for (int i = 0; i < 3; i++)
        {
            foreach (var item in clients)
            {
                item.Send($"第{i + 1}次{item.Remote.Type}发送");
                var str = item.ReceiveString();
                Trace.Assert(!str.IsNullOrEmpty());
            }
            Thread.Sleep(500);
        }

        XTrace.WriteLine("不用担心断开连接等日志，因为离开当前函数后，客户端连接将会被销毁");

        // 为了统一TCP/UDP架构，网络库底层（UdpServer/TcpServer）是重量级封装为ISocketServer
        // 实际产品级项目不关心底层，而是继承中间层（位于NewLife.Net）的NetServer/NetSession，直接操作ISocketSession
        // 平台级项目一般在中间层之上封装消息序列化，转化为消息收发或者RPC调用，无视网络层的存在
        // 以太网接口之上还有一层传输接口ITransport，它定义包括以太网和其它工业网络接口的基本数据收发能力
    }
}