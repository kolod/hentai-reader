using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace HentaiReader.Host.Engine;

/// <summary>Contract limits per source call (host-api.d.ts execution model).</summary>
public sealed record EngineLimits(TimeSpan CallTimeout, long MemoryBytes)
{
    public static readonly EngineLimits Default = new(TimeSpan.FromSeconds(60), 128L * 1024 * 1024);
}

/// <summary>Outcome of a source call, classified as in the runner (contracts/host-bridge.md).</summary>
public sealed record CallResult(bool Ok, JsonNode? Value, CallError? Error)
{
    public static CallResult Success(JsonNode? value) => new(true, value, null);
    public static CallResult Failure(CallError error) => new(false, null, error);
}

public sealed record CallError(
    string Type,
    string? Kind,
    string? Name,
    string Message,
    double? RetryAfterSeconds = null,
    JsonObject? Details = null);

public sealed record SourceDescription(JsonObject Manifest, IReadOnlyList<string> Functions);

public sealed class LoadException(string message) : Exception(message);

/// <summary>
/// One Jint engine running one source module: contract prelude, the shared host glue
/// (contract/host-glue.js) on top of the Jint bridge adapter, and the source. All engine access
/// happens on its own thread; calls are serialised.
/// </summary>
public sealed partial class SourceEngine : IDisposable
{
    private static readonly string Prelude = ReadResource("contract/prelude.js");
    private static readonly string Glue = ReadResource("contract/host-glue.js");
    private static readonly string Adapter = ReadResource("engine/jint-bridge.js");

    private readonly EngineThread _thread;
    private readonly IHostBridge _bridge;
    private readonly EngineLimits _limits;
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly CancellationTokenSource _kill = new();
    private readonly Dictionary<int, string> _finished = new();
    private Jint.Engine _engine = null!;
    private JsValue _invoker = JsValue.Undefined;
    private JsValue _settle = JsValue.Undefined;
    private JsValue _settleHttp = JsValue.Undefined;
    private JsValue _watch = JsValue.Undefined;
    private int _nextCallId;

    /// <summary>Set after a limit hit or cancellation; the session replaces a broken engine.</summary>
    public string? Broken { get; private set; }

    private SourceEngine(IHostBridge bridge, EngineLimits limits, string name)
    {
        _bridge = bridge;
        _limits = limits;
        _thread = new EngineThread($"source-engine:{name}");
    }

    public static async Task<SourceEngine> CreateAsync(string source, string sourceName, IHostBridge bridge, long? frozenNow = null, EngineLimits? limits = null)
    {
        var engine = new SourceEngine(bridge, limits ?? EngineLimits.Default, sourceName);
        try
        {
            await engine._thread.InvokeAsync(() =>
            {
                engine.Initialise(source, frozenNow);
                return true;
            });
        }
        catch (Exception e) when (e is JavaScriptException or Acornima.SyntaxErrorException or Acornima.ParseErrorException)
        {
            engine.Dispose();
            throw new LoadException($"{sourceName}: {e.Message}");
        }
        return engine;
    }

    private void Initialise(string source, long? frozenNow)
    {
        _engine = new Jint.Engine(options =>
        {
            options.LimitMemory(_limits.MemoryBytes);
            options.TimeoutInterval(_limits.CallTimeout);
            options.CancellationToken(_kill.Token);
            options.Constraints.RegexTimeout = TimeSpan.FromSeconds(5);
            options.Strict = false;
        });

        var native = new JsObject(_engine);
        Define(native, "http", (_, a) =>
        {
            var id = (int)a[0].AsNumber();
            var meta = a[1].AsString();
            byte[]? body = a.Length > 2 && a[2] is JsArray arr ? arr.Select(v => (byte)v.AsNumber()).ToArray() : null;
            Start(id, () => _bridge.HttpAsync(meta, body), r => _engine.Invoke(_settleHttp, id, r.Env, r.Bytes is null ? JsValue.Undefined : Convert.ToBase64String(r.Bytes)));
            return JsValue.Undefined;
        });
        Define(native, "htmlSelect", (_, a) => _bridge.HtmlSelect(a[0].AsString(), (int)a[1].AsNumber(), a[2].AsString()));
        Define(native, "htmlCall", (_, a) => _bridge.HtmlCall(a[0].AsString(), (int)a[1].AsNumber(), a[2].AsString()));
        Define(native, "storageGet", (_, a) => StartString((int)a[0].AsNumber(), () => _bridge.StorageGetAsync(a[1].AsString())));
        Define(native, "storageSet", (_, a) => StartString((int)a[0].AsNumber(), () => _bridge.StorageSetAsync(a[1].AsString(), a[2].AsString())));
        Define(native, "sleep", (_, a) => StartString((int)a[0].AsNumber(), () => _bridge.SleepAsync(a[1].AsNumber())));
        Define(native, "log", (_, a) =>
        {
            _bridge.Log(a[0].AsString(), a[1].AsString());
            return JsValue.Undefined;
        });
        Define(native, "done", (_, a) =>
        {
            _finished[(int)a[0].AsNumber()] = a[1].AsString();
            return JsValue.Undefined;
        });
        _engine.SetValue("__native", native);
        _engine.SetValue("__frozenNow", frozenNow is null ? JsValue.Undefined : new JsNumber(frozenNow.Value));

        var adapter = _engine.Evaluate(Adapter, "jint-bridge.js").AsObject();
        _settle = adapter.Get("settle");
        _settleHttp = adapter.Get("settleHttp");
        _watch = adapter.Get("watch");
        _engine.Evaluate(Prelude, "prelude.js");
        var bind = _engine.Evaluate(Glue, "host-glue.js");

        _engine.Modules.Add("source", source);
        var exports = _engine.Modules.Import("source");
        _invoker = _engine.Invoke(bind, exports);
    }

    private void Define(ObjectInstance target, string name, Func<JsValue, JsValue[], JsValue> fn)
    {
        target.Set(name, new ClrFunction(_engine, name, fn));
    }

    /// <summary>Starts .NET work; its result is settled on the engine thread.</summary>
    private void Start<T>(int id, Func<Task<T>> work, Action<T> settle)
    {
        Task<T> task;
        try { task = work(); }
        catch (Exception e) { task = Task.FromException<T>(e); }
        task.ContinueWith(t => _thread.Post(() =>
        {
            if (t.IsCompletedSuccessfully) settle(t.Result);
            else _engine.Invoke(_settle, id, Envelope.Error(SourceErrorKind.Network, t.Exception?.GetBaseException().Message ?? "cancelled"));
            _engine.Advanced.ProcessTasks();
        }), TaskScheduler.Default);
    }

    private JsValue StartString(int id, Func<Task<string>> work)
    {
        Start(id, work, s => _engine.Invoke(_settle, id, s));
        return JsValue.Undefined;
    }

    public async Task<SourceDescription> DescribeAsync()
    {
        var res = await CallAsync("__describe", "[]", CancellationToken.None);
        if (!res.Ok) throw new LoadException($"cannot read source exports: {res.Error!.Message}");
        var value = res.Value!.AsObject();
        var manifest = value["manifest"] as JsonObject ?? throw new LoadException("source does not export `manifest`");
        var functions = value["functions"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        return new SourceDescription(manifest, functions);
    }

    /// <summary>Calls an exported function with JSON arguments. Never throws for script errors.</summary>
    public async Task<CallResult> CallAsync(string fn, string argsJson, CancellationToken token)
    {
        if (Broken is not null) return Limit("EngineBroken", Broken, "time");
        await _callLock.WaitAsync(token);
        try
        {
            return await _thread.InvokeAsync(() => RunCall(fn, argsJson, token));
        }
        finally
        {
            _callLock.Release();
        }
    }

    private CallResult RunCall(string fn, string argsJson, CancellationToken token)
    {
        var id = ++_nextCallId;
        try
        {
            var promise = _engine.Invoke(_invoker, fn, argsJson);
            _engine.Invoke(_watch, promise, id);
            _engine.Advanced.ProcessTasks();
            _thread.PumpUntil(() => _finished.ContainsKey(id) || _bridge.Fatal is not null, DateTime.UtcNow + _limits.CallTimeout, token);
            if (_bridge.Fatal is { } fatal) throw fatal;
            var json = _finished[id];
            _finished.Remove(id);
            return Parse(json);
        }
        catch (Exception e) when (e is TimeoutException or Jint.Runtime.ExecutionCanceledException or OperationCanceledException)
        {
            Broken = token.IsCancellationRequested ? "call cancelled" : "call exceeded the time limit";
            _kill.Cancel();
            return token.IsCancellationRequested
                ? Limit("Cancelled", "call cancelled", "cancelled")
                : Limit("InternalError", "call exceeded the time limit", "time");
        }
        catch (MemoryLimitExceededException)
        {
            Broken = "engine memory limit exceeded";
            _kill.Cancel();
            return Limit("InternalError", "engine memory limit exceeded", "memory");
        }
        catch (JavaScriptException e)
        {
            return Classify(new JsonObject { ["name"] = "Error", ["message"] = e.Message });
        }
        finally
        {
            _bridge.Idle();
        }
    }

    private static CallResult Limit(string name, string message, string limit) =>
        CallResult.Failure(new CallError("ScriptError", null, name, message, null, new JsonObject { ["limit"] = limit }));

    private static CallResult Parse(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        if (node["ok"]!.GetValue<bool>()) return CallResult.Success(node["value"]?.DeepClone());
        return Classify(node["error"]!.AsObject());
    }

    [GeneratedRegex(@"^'?([\w$]+)'? is not defined$")]
    private static partial Regex NotDefined();

    /// <summary>Maps errors described by the glue to contract-level classifications.</summary>
    private static CallResult Classify(JsonObject err)
    {
        var type = err["type"]?.GetValue<string>();
        var message = err["message"]?.GetValue<string>() ?? "";
        if (type == "SourceError" && err["kind"] is { } kind)
        {
            return CallResult.Failure(new CallError(
                "SourceError",
                kind.GetValue<string>(),
                null,
                message,
                err["retryAfterSeconds"]?.GetValue<double>(),
                err["details"]?.DeepClone() as JsonObject));
        }
        var name = err["name"]?.GetValue<string>() ?? "Error";
        var missing = name == "ReferenceError" ? NotDefined().Match(message) : null;
        if (missing is { Success: true })
        {
            var capability = missing.Groups[1].Value;
            return CallResult.Failure(new CallError("ScriptError", null, name, $"forbidden capability: {capability}", null,
                new JsonObject { ["capability"] = capability }));
        }
        return CallResult.Failure(new CallError("ScriptError", null, name, message));
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _kill.Cancel();
        _thread.Dispose();
    }

    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}
