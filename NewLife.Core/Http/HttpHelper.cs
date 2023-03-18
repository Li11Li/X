﻿using System.Net;
using System.Net.Http;
using System.Text;
using NewLife.Collections;
using NewLife.Data;
using NewLife.Http.Headers;
using NewLife.Log;
using NewLife.Serialization;
using NewLife.Xml;
#if !NET40
using TaskEx = System.Threading.Tasks.Task;
#endif

namespace NewLife.Http;

/// <summary>Http帮助类</summary>
public static class HttpHelper
{
    /// <summary>性能跟踪器</summary>
    public static ITracer Tracer { get; set; } = DefaultTracer.Instance;

    /// <summary>Http过滤器</summary>
    public static IHttpFilter Filter { get; set; }

    #region Http封包解包
    /// <summary>创建请求包</summary>
    /// <param name="method"></param>
    /// <param name="uri"></param>
    /// <param name="headers"></param>
    /// <param name="pk"></param>
    /// <returns></returns>
    public static Packet MakeRequest(String method, Uri uri, IDictionary<String, Object> headers, Packet pk)
    {
        if (method.IsNullOrEmpty()) method = pk?.Count > 0 ? "POST" : "GET";

        // 分解主机和资源
        var host = "";
        if (uri == null) uri = new Uri("/");

        if (uri.Scheme.EqualIgnoreCase("http", "ws"))
        {
            if (uri.Port == 80)
                host = uri.Host;
            else
                host = $"{uri.Host}:{uri.Port}";
        }
        else if (uri.Scheme.EqualIgnoreCase("https"))
        {
            if (uri.Port == 443)
                host = uri.Host;
            else
                host = $"{uri.Host}:{uri.Port}";
        }

        // 构建头部
        var sb = Pool.StringBuilder.Get();
        sb.AppendFormat("{0} {1} HTTP/1.1\r\n", method, uri.PathAndQuery);
        sb.AppendFormat("Host:{0}\r\n", host);

        //if (Compressed) sb.AppendLine("Accept-Encoding:gzip, deflate");
        //if (KeepAlive) sb.AppendLine("Connection:keep-alive");
        //if (!UserAgent.IsNullOrEmpty()) sb.AppendFormat("User-Agent:{0}\r\n", UserAgent);

        // 内容长度
        if (pk?.Count > 0) sb.AppendFormat("Content-Length:{0}\r\n", pk.Count);

        foreach (var item in headers)
        {
            sb.AppendFormat("{0}:{1}\r\n", item.Key, item.Value);
        }

        sb.Append("\r\n");

        //return sb.ToString();
        var rs = new Packet(sb.Put(true).GetBytes())
        {
            Next = pk
        };
        return rs;
    }

    /// <summary>创建响应包</summary>
    /// <param name="code"></param>
    /// <param name="headers"></param>
    /// <param name="pk"></param>
    /// <returns></returns>
    public static Packet MakeResponse(HttpStatusCode code, IDictionary<String, Object> headers, Packet pk)
    {
        // 构建头部
        var sb = Pool.StringBuilder.Get();
        sb.AppendFormat("HTTP/1.1 {0} {1}\r\n", (Int32)code, code);

        // 内容长度
        if (pk?.Count > 0) sb.AppendFormat("Content-Length:{0}\r\n", pk.Count);

        foreach (var item in headers)
        {
            sb.AppendFormat("{0}:{1}\r\n", item.Key, item.Value);
        }

        sb.Append("\r\n");

        //return sb.ToString();
        var rs = new Packet(sb.Put(true).GetBytes())
        {
            Next = pk
        };
        return rs;
    }

    private static readonly Byte[] NewLine = new[] { (Byte)'\r', (Byte)'\n', (Byte)'\r', (Byte)'\n' };
    /// <summary>分析头部</summary>
    /// <param name="pk"></param>
    /// <returns></returns>
    public static IDictionary<String, Object> ParseHeader(Packet pk)
    {
        // 客户端收到响应，服务端收到请求
        var headers = new Dictionary<String, Object>(StringComparer.OrdinalIgnoreCase);

        var p = pk.IndexOf(NewLine);
        if (p < 0) return headers;

#if DEBUG
        //WriteLog(pk.ToStr());
#endif

        // 截取
        var lines = pk.ReadBytes(0, p).ToStr().Split("\r\n");
        // 重构
        p += 4;
        pk.Set(pk.Data, pk.Offset + p, pk.Count - p);

        // 分析头部
        headers.Clear();
        var line = lines[0];
        for (var i = 1; i < lines.Length; i++)
        {
            line = lines[i];
            p = line.IndexOf(':');
            if (p > 0) headers[line.Substring(0, p)] = line.Substring(p + 1).Trim();
        }

        line = lines[0];
        var ss = line.Split(" ");
        // 分析请求方法 GET / HTTP/1.1
        if (ss.Length >= 3 && ss[2].StartsWithIgnoreCase("HTTP/"))
        {
            headers["Method"] = ss[0];

            // 构造资源路径
            var host = headers.TryGetValue("Host", out var s) ? s : "";
            var uri = $"http://{host}";
            //var uri = "{0}://{1}".F(IsSSL ? "https" : "http", host);
            //if (host.IsNullOrEmpty() || !host.Contains(":"))
            //{
            //    var port = Local.Port;
            //    if (IsSSL && port != 443 || !IsSSL && port != 80) uri += ":" + port;
            //}
            uri += ss[1];
            headers["Url"] = new Uri(uri);
        }
        else
        {
            // 分析响应码
            var code = ss[1].ToInt();
            if (code > 0) headers["StatusCode"] = (HttpStatusCode)code;
        }

        return headers;
    }
    #endregion

    #region 高级功能扩展
    /// <summary>异步提交Json</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="data">数据</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static async Task<String> PostJsonAsync(this HttpClient client, String requestUri, Object data, IDictionary<String, String> headers = null)
    {
        HttpContent content = null;
        if (data != null)
        {
            content = data is String str
                ? new StringContent(str, Encoding.UTF8, "application/json")
                : new StringContent(data.ToJson(), Encoding.UTF8, "application/json");
        }

        if (headers == null && client.DefaultRequestHeaders.Accept == null) client.DefaultRequestHeaders.Accept = "application/json";
        client.AddHeaders(headers);

        return await PostAsync(client, requestUri, content);
    }

    /// <summary>同步提交Json</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="data">数据</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static String PostJson(this HttpClient client, String requestUri, Object data, IDictionary<String, String> headers = null) => TaskEx.Run(() => client.PostJsonAsync(requestUri, data, headers)).Result;

    /// <summary>异步提交Xml</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="data">数据</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static async Task<String> PostXmlAsync(this HttpClient client, String requestUri, Object data, IDictionary<String, String> headers = null)
    {
        HttpContent content = null;
        if (data != null)
        {
            content = data is String str
                ? new StringContent(str, Encoding.UTF8, "application/xml")
                : new StringContent(data.ToXml(), Encoding.UTF8, "application/xml");
        }

        if (headers == null && client.DefaultRequestHeaders.Accept == null) client.DefaultRequestHeaders.Accept = "application/xml";
        client.AddHeaders(headers);

        return await PostAsync(client, requestUri, content);
    }

    /// <summary>同步提交Xml</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="data">数据</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static String PostXml(this HttpClient client, String requestUri, Object data, IDictionary<String, String> headers = null) => TaskEx.Run(() => client.PostXmlAsync(requestUri, data, headers)).Result;

    /// <summary>异步提交表单</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="data">数据</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static async Task<String> PostFormAsync(this HttpClient client, String requestUri, Object data, IDictionary<String, String> headers = null)
    {
        HttpContent content = null;
        if (data != null)
        {
            content = data is String str
                ? new StringContent(str, Encoding.UTF8, "application/x-www-form-urlencoded")
                : (
                    data is IDictionary<String, String> dic
                    ? new FormUrlEncodedContent(dic)
                    : new FormUrlEncodedContent(data.ToDictionary().ToDictionary(e => e.Key, e => e.Value + ""))
                );
        }

        client.AddHeaders(headers);

        return await PostAsync(client, requestUri, content);
    }

    /// <summary>同步提交表单</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="data">数据</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static String PostForm(this HttpClient client, String requestUri, Object data, IDictionary<String, String> headers = null) => TaskEx.Run(() => client.PostFormAsync(requestUri, data, headers)).Result;

    /// <summary>同步获取字符串</summary>
    /// <param name="client">Http客户端</param>
    /// <param name="requestUri">请求资源地址</param>
    /// <param name="headers">附加头部</param>
    /// <returns></returns>
    public static String GetString(this HttpClient client, String requestUri, IDictionary<String, String> headers = null)
    {
        client.AddHeaders(headers);
        return TaskEx.Run(() => client.GetStringAsync(requestUri)).Result;
    }

    private static async Task<String> PostAsync(HttpClient client, String requestUri, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };

        // 设置接受 mediaType
        //if (content.Headers.TryGetValues("Content-Type", out var vs))
        var type = content.Headers.ContentType;
        if (!type.IsNullOrEmpty())
        {
            // application/json; charset=utf-8
            //var type = vs.FirstOrDefault()?.Split(";").FirstOrDefault();
            if (type.EqualIgnoreCase("application/json", "application/xml")) request.Headers["Accept"] = type;
        }

        // 开始跟踪，注入TraceId
        using var span = Tracer?.NewSpan(request);
        //if (span != null) span.SetTag(content.ReadAsStringAsync().Result);
        var filter = Filter;
        try
        {
            if (filter != null) await filter.OnRequest(client, request, null);

            var response = await client.SendAsync(request);

            if (filter != null) await filter.OnResponse(client, response, request);

            var result = await response.Content.ReadAsStringAsync();

            // 增加埋点数据
            span?.AppendTag(result);

            return result;
        }
        catch (Exception ex)
        {
            // 跟踪异常
            span?.SetError(ex, null);

            if (filter != null) await filter.OnError(client, ex, request);

            throw;
        }
    }

    private static HttpClient AddHeaders(this HttpClient client, IDictionary<String, String> headers)
    {
        if (client == null) return null;
        if (headers == null || headers.Count == 0) return client;

        //foreach (var item in headers)
        //{
        //    //判断请求头中是否已存在，存在先删除，再添加
        //    if (client.DefaultRequestHeaders.Contains(item.Key))
        //        client.DefaultRequestHeaders.Remove(item.Key);
        //    client.DefaultRequestHeaders.Add(item.Key, item.Value);
        //}

        return client;
    }

    /// <summary>下载文件</summary>
    /// <param name="client"></param>
    /// <param name="address"></param>
    /// <param name="fileName"></param>
    public static async Task DownloadFileAsync(this HttpClient client, String address, String fileName)
    {
        var rs = await client.GetStreamAsync(address);
        fileName.EnsureDirectory(true);
        using var fs = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
#if NET40
        rs.CopyTo(fs);
        fs.Flush();
#else
        await rs.CopyToAsync(fs);
        await fs.FlushAsync();
#endif
    }
    #endregion
}