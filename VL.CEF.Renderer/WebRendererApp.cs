using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xilium.CefGlue;

namespace VL.CEF
{
    public class WebRendererApp : CefApp
    {
        // Main entry point when called by CEF
        [STAThread]
        public static int Main(string[] args)
        {
            CefRuntime.Load();

            var app = new WebRendererApp();
            var mainArgs = new CefMainArgs(args);
            var exitCode = CefRuntime.ExecuteProcess(mainArgs, app, IntPtr.Zero);
            if (exitCode != -1)
                return exitCode;

            CefRuntime.Shutdown();
            return 0;
        }

        private static readonly Dictionary<CefThreadId, CefTaskScheduler> TaskSchedulers = new Dictionary<CefThreadId, CefTaskScheduler>();
        private RenderProcessHandler renderHandler;
        private BrowserProcessHandler browserHandler;

        public static TaskScheduler GetTaskScheduler(CefThreadId threadId)
        {
            CefTaskScheduler result;
            if (!TaskSchedulers.TryGetValue(threadId, out result))
            {
                result = new CefTaskScheduler(threadId);
                TaskSchedulers.Add(threadId, result);
            }
            return result;
        }

        class CefTaskScheduler : TaskScheduler
        {
            class TaskWrapper : CefTask
            {
                public readonly CefTaskScheduler Scheduler;
                public readonly Task Task;
                private bool Removed;

                public TaskWrapper(CefTaskScheduler scheduler, Task task)
                {
                    Scheduler = scheduler;
                    Task = task;
                }

                protected override void Execute()
                {
                    Scheduler.TryExecuteTask(Task);
                    lock (Scheduler.Tasks)
                        Removed = Scheduler.Tasks.Remove(this);
                }

                protected override void Dispose(bool disposing)
                {
                    if (!Removed)
                    {
                        lock (Scheduler.Tasks)
                            Scheduler.Tasks.Remove(this);
                    }
                    base.Dispose(disposing);
                }
            }

            private readonly CefThreadId ThreadId;
            private readonly LinkedList<TaskWrapper> Tasks = new LinkedList<TaskWrapper>();

            public CefTaskScheduler(CefThreadId threadId)
            {
                ThreadId = threadId;
            }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                lock (Tasks)
                {
                    foreach (var taskWrapper in Tasks)
                        yield return taskWrapper.Task;
                }
            }

            protected override void QueueTask(Task task)
            {
                lock (Tasks)
                {
                    var t = new TaskWrapper(this, task);
                    if (CefRuntime.PostTask(ThreadId, t))
                        Tasks.AddLast(t);
                }
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }
        }

        class BrowserProcessHandler : CefBrowserProcessHandler
        {
            protected override void OnBeforeChildProcessLaunch(CefCommandLine commandLine)
            {
                base.OnBeforeChildProcessLaunch(commandLine);
            }
        }

        class RenderProcessHandler : CefRenderProcessHandler
        {
            sealed class CustomCallbackHandler : CefV8Handler
            {
                public const string ReportDocumentSize = "vvvvReportDocumentSize";

                private readonly CefBrowser Browser;
                private readonly CefFrame Frame;
                public CustomCallbackHandler(CefBrowser browser, CefFrame frame)
                {
                    Browser = browser;
                    Frame = frame;
                }

                protected override bool Execute(string name, CefV8Value obj, CefV8Value[] arguments, out CefV8Value returnValue, out string exception)
                {
                    var message = default(CefProcessMessage);
                    try
                    {
                        CefListValue args;
                        switch (name)
                        {
                            case ReportDocumentSize:
                                message = CefProcessMessage.Create("document-size-response");
                                using (args = message.Arguments)
                                {
                                    args.SetInt(0, arguments[0].GetIntValue());
                                    args.SetInt(1, arguments[1].GetIntValue());
                                    Frame.SendProcessMessage(CefProcessId.Browser, message);
                                }
                                returnValue = null;
                                exception = null;
                                return true;
                            default:
                                returnValue = null;
                                exception = null;
                                return false;
                        }
                    }
                    finally
                    {
                        message?.Dispose();
                    }
                }
            }

            sealed class DelegatingHandler : CefV8Handler
            {
                private readonly Func<string, CefV8Value, CefV8Value[], CefV8Value> handler;

                public DelegatingHandler(Func<string, CefV8Value, CefV8Value[], CefV8Value> handler)
                {
                    this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
                }

                protected override bool Execute(string name, CefV8Value obj, CefV8Value[] arguments, out CefV8Value returnValue, out string exception)
                {
                    try
                    {
                        returnValue = handler(name, obj, arguments);
                        exception = null;
                    }
                    catch (Exception e)
                    {
                        returnValue = null;
                        exception = e.ToString();
                    }

                    return true;
                }
            }

            private readonly ConcurrentDictionary<(string request, int id), (CefV8Context context, CefV8Value onSuccess, CefV8Value onError)> queries = new ConcurrentDictionary<(string request, int id), (CefV8Context context, CefV8Value onSuccess, CefV8Value onError)>();
            private int queryCount;
            // Store global variables for guaranteed injection into every new context (before page scripts run)
            private readonly Dictionary<string, string> globalVariables = new Dictionary<string, string>();

            protected override bool OnProcessMessageReceived(CefBrowser browser, CefFrame frame, CefProcessId sourceProcess, CefProcessMessage message)
            {
                switch (message.Name)
                {
                    case "document-size-request":
                        HandleDocumentSizeRequest(browser, sourceProcess, frame);
                        return true;
                    case "query-response":
                        HandleQueryResponse(browser, frame, message);
                        return true;
                    case "set-global":
                        HandleSetGlobal(browser, frame, message);
                        return true;
                    default:
                        return base.OnProcessMessageReceived(browser, frame, sourceProcess, message);
                }
            }

            protected override void OnContextCreated(CefBrowser browser, CefFrame frame, CefV8Context context)
            {
                if (frame.IsMain)
                {
                    // Retrieve the context's window object and install the "vvvvReportDocumentSize" function
                    // used to tell the node about the document size as well as the "vvvvSend" function
                    // used to tell the node about variables computed inside the frame.
                    using (var window = context.GetGlobal())
                    {
                        var handler = new CustomCallbackHandler(browser, frame);
                        var reportDocumentSizeFunc = CefV8Value.CreateFunction(CustomCallbackHandler.ReportDocumentSize, handler);
                        window.SetValue(CustomCallbackHandler.ReportDocumentSize, reportDocumentSizeFunc);


                        window.SetValue("vvvvQuery", CefV8Value.CreateFunction("vvvvQuery", new DelegatingHandler((name, obj, args) =>
                        {
                            const string argForm = "{ request: 'NAME', arguments: {}, onSuccess: function(response) {}, onError: function(error_message) {} }";
                            string invalidArg = $"vvvvQuery expects one argument of the form {argForm}";

                            if (args.Length != 1)
                                throw new Exception($"vvvvQuery expects one argument");

                            var x = args[0];
                            if (x is null || !x.IsObject)
                                throw new Exception($"The argument must be of the form {argForm}");

                            var requestValue = x.GetValue("request");
                            if (requestValue.IsUndefined)
                                throw new Exception($"The request entry is missing");
                            if (!requestValue.IsString)
                                throw new Exception($"The request must be a string");

                            var onSuccessValue = x.GetValue("onSuccess");
                            if (!onSuccessValue.IsUndefined && !onSuccessValue.IsFunction)
                                throw new Exception($"The onSuccess entry must be a function");

                            var onErrorValue = x.GetValue("onError");
                            if (!onErrorValue.IsUndefined && !onErrorValue.IsFunction)
                                throw new Exception($"The onError entry must be a function");

                            var request = requestValue.GetStringValue();
                            var id = queryCount++;

                            if (queries.TryAdd((request, id), (context, onSuccessValue, onErrorValue)))
                            {
                                using var message = CefProcessMessage.Create("query-request");
                                message.Arguments.SetString(0, request);
                                message.Arguments.SetInt(1, id);
                                message.Arguments.SetValue(2, ToValue(x.GetValue("arguments")));
                                frame.SendProcessMessage(CefProcessId.Browser, message);
                            }

                            return default;
                        })));

                        // Guaranteed injection: apply all global variables to the new context
                        // This happens SYNCHRONOUSLY before any page scripts run, ensuring data is available immediately
                        foreach (var kvp in globalVariables)
                        {
                            ApplyGlobalVariableToContext(window, frame, kvp.Key, kvp.Value);
                        }
                    }
                }
                base.OnContextCreated(browser, frame, context);

                static CefValue ToValue(CefV8Value v)
                {
                    var c = CefValue.Create();
                    if (v is null || v.IsNull)
                        c.SetNull();
                    else if (v.IsBool)
                        c.SetBool(v.GetBoolValue());
                    else if (v.IsInt)
                        c.SetInt(v.GetIntValue());
                    else if (v.IsUInt)
                        c.SetInt((int)v.GetUIntValue());
                    else if (v.IsDouble)
                        c.SetDouble(v.GetDoubleValue());
                    else if (v.IsString)
                        c.SetString(v.GetStringValue());
                    else if (v.IsArray)
                        c.SetList(ToList(v));
                    else if (v.IsObject)
                        c.SetDictionary(ToDict(v));
                    return c;
                }

                static CefListValue ToList(CefV8Value v)
                {
                    var x = CefListValue.Create();
                    for (int i = 0; i < v.GetArrayLength(); i++)
                        x.SetValue(i, ToValue(v.GetValue(i)));
                    return x;
                }

                static CefDictionaryValue ToDict(CefV8Value v)
                {
                    var x = CefDictionaryValue.Create();
                    foreach (var key in v.GetKeys())
                        x.SetValue(key, ToValue(v.GetValue(key)));
                    return x;
                }
            }

            private void HandleDocumentSizeRequest(CefBrowser browser, CefProcessId sourceProcess, CefFrame frame)
            {
                var scheduler = GetTaskScheduler(CefThreadId.Renderer);
                Task.Factory.StartNew(
                    () =>
                    {
                        var js = new StringBuilder();
                        js.AppendLine("var body = document.body;");
                        js.AppendLine("var width = body.scrollWidth;");
                        js.AppendLine("var height = body.scrollHeight;");
                        js.AppendLine(string.Format("window.{0}(width, height);", CustomCallbackHandler.ReportDocumentSize));
                        frame.ExecuteJavaScript(js.ToString(), string.Empty, 0);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    scheduler);
            }

            private void HandleSetGlobal(CefBrowser browser, CefFrame frame, CefProcessMessage message)
            {
                var name = message.Arguments.GetString(0);
                var jsonOrText = message.Arguments.GetString(1);

                // Validate inputs to prevent crashes
                if (string.IsNullOrEmpty(name) || jsonOrText == null)
                    return;

                // Store the value for guaranteed injection into future contexts (OnContextCreated)
                // This ensures data survives navigation and is available before page scripts run
                globalVariables[name] = jsonOrText;

                // Also apply to current context if it exists (immediate effect for already-loaded pages)
                if (frame != null && frame.IsMain)
                {
                    var context = frame.V8Context;
                    if (context != null && context.Enter())
                    {
                        try
                        {
                            using (var window = context.GetGlobal())
                            {
                                ApplyGlobalVariableToContext(window, frame, name, jsonOrText);
                            }
                        }
                        finally
                        {
                            context.Exit();
                        }
                    }
                    else
                    {
                        // Fallback to ExecuteJavaScript if context is not available yet
                        ApplyGlobalVariable(frame, name, jsonOrText);
                    }
                }
            }

            private void ApplyGlobalVariable(CefFrame frame, string name, string jsonOrText)
            {
                // Escape the name and value for safe insertion into JavaScript
                var escapedName = EscapeJavaScriptString(name);
                var escapedValue = EscapeJavaScriptString(jsonOrText);

                // Create JavaScript code that safely parses JSON or falls back to string
                var js = "(function() {" +
                    "try {" +
                    "window[" + escapedName + "] = JSON.parse(" + escapedValue + ");" +
                    "} catch (e) {" +
                    "window[" + escapedName + "] = " + escapedValue + ";" +
                    "}" +
                    "})();";

                frame.ExecuteJavaScript(js, string.Empty, 0);
            }

            private void ApplyGlobalVariableToContext(CefV8Value window, CefFrame frame, string name, string jsonOrText)
            {
                // Apply directly to V8 context using window.SetValue (works immediately for data URLs)
                // This is more reliable than ExecuteJavaScript in OnContextCreated
                try
                {
                    CefV8Value value;
                    // Try to parse as JSON first
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<JsonElement>(jsonOrText);
                        value = JsonElementToV8Value(parsed);
                    }
                    catch
                    {
                        // If parsing fails, use as string
                        value = CefV8Value.CreateString(jsonOrText);
                    }
                    window.SetValue(name, value);
                }
                catch
                {
                    // Fallback to ExecuteJavaScript if SetValue fails (shouldn't happen normally)
                    // This can happen if window is not fully initialized
                    if (frame != null)
                        ApplyGlobalVariable(frame, name, jsonOrText);
                }
            }

            private CefV8Value JsonElementToV8Value(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Null:
                        return CefV8Value.CreateNull();
                    case JsonValueKind.True:
                        return CefV8Value.CreateBool(true);
                    case JsonValueKind.False:
                        return CefV8Value.CreateBool(false);
                    case JsonValueKind.Number:
                        if (element.TryGetInt32(out int intValue))
                            return CefV8Value.CreateInt(intValue);
                        if (element.TryGetDouble(out double doubleValue))
                            return CefV8Value.CreateDouble(doubleValue);
                        return CefV8Value.CreateString(element.GetRawText());
                    case JsonValueKind.String:
                        return CefV8Value.CreateString(element.GetString());
                    case JsonValueKind.Array:
                        var array = CefV8Value.CreateArray(element.GetArrayLength());
                        int index = 0;
                        foreach (var item in element.EnumerateArray())
                        {
                            array.SetValue(index++, JsonElementToV8Value(item));
                        }
                        return array;
                    case JsonValueKind.Object:
                        var obj = CefV8Value.CreateObject();
                        foreach (var prop in element.EnumerateObject())
                        {
                            obj.SetValue(prop.Name, JsonElementToV8Value(prop.Value));
                        }
                        return obj;
                    default:
                        return CefV8Value.CreateString(element.GetRawText());
                }
            }

            private string EscapeJavaScriptString(string value)
            {
                // Use JSON.stringify to safely escape the string for JavaScript
                // This handles quotes, newlines, and other special characters
                return JsonSerializer.Serialize(value);
            }

            private void HandleQueryResponse(CefBrowser browser, CefFrame frame, CefProcessMessage message)
            {
                var request = message.Arguments.GetString(0);
                var id = message.Arguments.GetInt(1);
                var error = message.Arguments.GetString(2);
                var args = message.Arguments.GetValue(3);

                if (!queries.TryRemove((request, id), out var x))
                    return;

                if (!x.context.Enter())
                    return;

                try
                {
                    if (error is null)
                        x.onSuccess.ExecuteFunctionWithContext(x.context, null, new[] { ToV8Value(args) });
                    else
                        x.onError.ExecuteFunctionWithContext(x.context, null, new[] { CefV8Value.CreateString(error) });
                }
                finally
                {
                    x.context.Exit();
                }

                static CefV8Value ToV8Value(CefValue o)
                {
                    if (o is null)
                        return CefV8Value.CreateNull();

                    switch (o.GetValueType())
                    {
                        case CefValueType.Null:
                            return CefV8Value.CreateNull();
                        case CefValueType.Bool:
                            return CefV8Value.CreateBool(o.GetBool());
                        case CefValueType.Int:
                            return CefV8Value.CreateInt(o.GetInt());
                        case CefValueType.Double:
                            return CefV8Value.CreateDouble(o.GetDouble());
                        case CefValueType.String:
                            return CefV8Value.CreateString(o.GetString());
                        case CefValueType.Binary:
                            throw new NotImplementedException();
                        case CefValueType.Dictionary:
                            return ToV8Dict(o.GetDictionary());
                        case CefValueType.List:
                            return ToV8List(o.GetList());
                        default:
                            throw new NotImplementedException();
                    }
                }

                static CefV8Value ToV8Dict(CefDictionaryValue dict)
                {
                    var v = CefV8Value.CreateObject();
                    foreach (var key in dict.GetKeys())
                        v.SetValue(key, ToV8Value(dict.GetValue(key)));
                    return v;
                }

                static CefV8Value ToV8List(CefListValue list)
                {
                    var v = CefV8Value.CreateArray(list.Count);
                    for (int i = 0; i < list.Count; i++)
                        v.SetValue(i, ToV8Value(list.GetValue(i)));
                    return v;
                }
            }
        }

        //protected override void OnRegisterCustomSchemes(CefSchemeRegistrar registrar)
        //{
        //    registrar.AddCustomScheme(SchemeHandlerFactory.SCHEME_NAME, CefSchemeOptions.Local | CefSchemeOptions.CspBypassing);
        //    base.OnRegisterCustomSchemes(registrar);
        //}

        protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
        {
            if (string.IsNullOrEmpty(processType))
            {
                commandLine.AppendSwitch("disable-smooth-scrolling");
                commandLine.AppendSwitch("enable-system-flash");
            }
            base.OnBeforeCommandLineProcessing(processType, commandLine);
        }

        protected override CefBrowserProcessHandler GetBrowserProcessHandler()
        {
            return browserHandler ??= new BrowserProcessHandler();
        }

        protected override CefRenderProcessHandler GetRenderProcessHandler()
        {
            return renderHandler ??= new RenderProcessHandler();
        }
    }
}
