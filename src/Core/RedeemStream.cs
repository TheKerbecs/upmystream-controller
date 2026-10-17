using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GameController;

public enum StreamState { Connecting, Connected, Reconnecting, BadKey, Stopped }

/// <summary>Holds GET /api/v1/redeems/stream open and reconnects with backoff. The server ends each stream after a
/// while on purpose, so a deleted key stops working; that end is not an error.</summary>
public sealed class RedeemStream
{
    public const string DefaultServer = "https://upmy.stream";
    const string UserAgent = "upmystream-game-controller/0.3";

    // The server pings every 15 s; three missed pings means the connection is dead.
    public TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);
    public TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    public TimeSpan RateLimitedWait = TimeSpan.FromMinutes(5);

    readonly string _server, _apiKey;
    readonly bool _chat;
    readonly Action<StreamEvent> _onEvent;
    readonly Action<StreamState, string> _onState;
    readonly HttpClient _http;

    /// <param name="chat">Also receive chat lines (?chat=1); off, the server sends redeems only.</param>
    public RedeemStream(string server, string apiKey, bool chat, Action<StreamEvent> onEvent, Action<StreamState, string> onState,
        HttpMessageHandler handler = null)
    {
        _chat = chat;
        _server = (string.IsNullOrWhiteSpace(server) ? DefaultServer : server).TrimEnd('/');
        _apiKey = apiKey;
        _onEvent = onEvent;
        _onState = onState;
        _http = Client(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    static HttpClient Client(HttpMessageHandler handler)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    public async Task Run(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        _onState(StreamState.Connecting, null);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var result = await Once(ct).ConfigureAwait(false);
                if (result == HttpStatusCode.Unauthorized || result == HttpStatusCode.Forbidden)
                {
                    _onState(StreamState.BadKey, "API key rejected");
                    return;
                }
                if (result == HttpStatusCode.OK)
                {
                    backoff = TimeSpan.FromSeconds(1); // the server closed a healthy stream: reconnect at once
                    wait = backoff;
                    _onState(StreamState.Reconnecting, null);
                }
                else if ((int)result == 429)
                {
                    wait = RateLimitedWait;
                    _onState(StreamState.Reconnecting, $"rate limited, retrying in {wait.TotalMinutes:0} min");
                }
                else
                {
                    wait = backoff;
                    _onState(StreamState.Reconnecting, result == HttpStatusCode.NotFound
                        ? "the server doesn't support this app yet (HTTP 404)"
                        : $"HTTP {(int)result}");
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                wait = backoff;
                _onState(StreamState.Reconnecting, e is TimeoutException ? "no response for 45 s" : Unwrap(e).Message);
            }
            catch (Exception) // cancelled
            {
                break;
            }
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
        _onState(StreamState.Stopped, null);
    }

    /// <summary>One connection. Returns the status code; OK means it streamed and then ended.</summary>
    async Task<HttpStatusCode> Once(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _server + "/api/v1/redeems/stream" + (_chat ? "?chat=1" : ""));
        req.Headers.Add("X-API-Key", _apiKey);
        req.Headers.Accept.ParseAdd("text/event-stream");
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("couldn't connect within 15 s");
            }
            using (resp)
            {
                if (resp.StatusCode != HttpStatusCode.OK) return resp.StatusCode;
                _onState(StreamState.Connected, null);
                using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(body))
                using (ct.Register(() => body.Dispose())) // ReadLineAsync takes no token on net48
                {
                    var parser = new SseParser();
                    while (true)
                    {
                        var read = reader.ReadLineAsync();
                        if (await Task.WhenAny(read, Task.Delay(ReadTimeout, ct)).ConfigureAwait(false) != read)
                        {
                            ct.ThrowIfCancellationRequested();
                            throw new TimeoutException();
                        }
                        var line = await read.ConfigureAwait(false);
                        if (line == null) return HttpStatusCode.OK;
                        var ev = parser.Line(line);
                        if (ev == null || (ev.Name != "message" && ev.Name != "redeem")) continue;
                        StreamEvent item;
                        try
                        {
                            item = Json.Read<StreamEvent>(ev.Data);
                        }
                        catch (Exception)
                        {
                            continue; // not ours; never let one odd event drop the stream
                        }
                        if (item == null) continue;
                        if (item.IsChat ? !string.IsNullOrEmpty(item.Message) : !string.IsNullOrEmpty(item.RedeemName)) _onEvent(item);
                    }
                }
            }
        }
    }

    /// <summary>GET /api/v1/redeems: the tenant's redeems. Throws with a readable message.</summary>
    public static async Task<List<RedeemInfo>> Redeems(string server, string apiKey, HttpMessageHandler handler = null)
    {
        server = (string.IsNullOrWhiteSpace(server) ? DefaultServer : server).TrimEnd('/');
        using (var http = Client(handler))
        {
            http.Timeout = TimeSpan.FromSeconds(15);
            var req = new HttpRequestMessage(HttpMethod.Get, server + "/api/v1/redeems");
            req.Headers.Add("X-API-Key", apiKey);
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new Exception("couldn't reach " + server + ": " + Unwrap(e).Message);
            }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new Exception("API key rejected");
                if ((int)resp.StatusCode == 429) throw new Exception("too many requests, try again later");
                if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}");
                return Json.Read<List<RedeemInfo>>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
    }

    static Exception Unwrap(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return e;
    }
}
