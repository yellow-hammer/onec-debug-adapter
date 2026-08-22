using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.V8;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Onec.DebugAdapter.Services
{
    public class StoppingManager : IStoppingManager
    {
        private CancellationToken _cancellation;

        private readonly IDebugConfiguration _configuration;
        private readonly IMetadataProvider _metadataProvider;
        private readonly IDebugServerClient _debugServerClient;
        private readonly IDebugServerListener _listener;
        private readonly IDebugTargetsManager _targetsManager;
        private DebugProtocolClient _client = null!;

        private readonly ConcurrentDictionary<int, TaskCompletionSource> _callStackRequestTasks = new();
        private readonly ConcurrentDictionary<int, List<StackItemViewInfoData>> _threadsCallStack = new();
        private readonly References<(int ThreadId, int FrameId)> _frameIdentifiers = new();

        private readonly ConcurrentDictionary<string, TaskCompletionSource<List<CalculationResultBaseData>>> _evaluationTasks = new();
        private readonly References<(int ThreadId, int FrameId, List<SourceCalculationDataItem> Path, ViewInterface Interface)> _variableIdentifiers = new();

        // Ключ — путь исходника: у копий внешних обработок тройка идентификаторов общая,
        // и точки второй копии вытесняли бы точки первой.
        private readonly ConcurrentDictionary<string, SetBreakpointsArguments> _moduleSetBreakpointsArguments = new(StringComparer.OrdinalIgnoreCase);
        // Запросы точек идут подряд (правка точки в редакторе порождает быструю пару запросов) —
        // конкурентные setBreakpoints сервер отвергает.
        private readonly SemaphoreSlim _setBreakpointsLock = new(1, 1);
        // Идентификаторы DAP-точек по исходнику (в порядке BpInfo) — для адресного применения correctedBP.
        private readonly ConcurrentDictionary<string, List<int>> _moduleBreakpointIds = new(StringComparer.OrdinalIgnoreCase);
        private int _nextBreakpointId = 1;

        public StoppingManager(
            IDebugConfiguration debugConfiguration,
            IMetadataProvider metadataProvider,
            IDebugServerClient debugServerClient,
            IDebugServerListener debugServerListener,
            IDebugTargetsManager debugTargetsManager)
        {
            _configuration = debugConfiguration;
            _metadataProvider = metadataProvider;
            _debugServerClient = debugServerClient;
            _listener = debugServerListener;
            _targetsManager = debugTargetsManager;
        }

        public void Run(DebugProtocolClient client, CancellationToken cancellationToken)
        {
            _cancellation = cancellationToken;
            _client = client;

            _listener.CallStackFormed += CallStackFormedHandler;
            _listener.RuntimeException += RuntimeExceptionHandler;
            _listener.ExpressionEvaluated += ExpressionEvaluatedHandler;
            _listener.CorrectedBreakpoints += CorrectedBreakpointsHandler;
        }

        // Сервер скорректировал строки точек останова на исполняемые — двигаем маркеры в VS Code.
        private void CorrectedBreakpointsHandler(object? sender, CorrectedBreakpointsArgs e)
        {
            foreach (var module in e.Info.BpWorkspace)
            {
                var correctedPath = CallStackMapper.ResolveSourcePath(_metadataProvider, module.Id);
                Log.Debug($"коррекция точек: url=\"{module.Id?.Url}\" obj={module.Id?.ObjectId} prop={module.Id?.PropertyId} исходник={(correctedPath == null ? "не сопоставлен" : Path.GetFileName(correctedPath))}");
                if (correctedPath == null)
                    continue;

                var key = SourcePath.Canonical(correctedPath);
                if (!_moduleSetBreakpointsArguments.TryGetValue(key, out var args))
                    continue;
                if (!_moduleBreakpointIds.TryGetValue(key, out var ids))
                    continue;
                // Сопоставление по порядку; при несовпадении количества коррекцию не применяем.
                if (module.BpInfo.Count != ids.Count)
                    continue;

                for (var i = 0; i < ids.Count; i++)
                {
                    var bp = module.BpInfo[i];
                    _client.SendEvent(new BreakpointEvent()
                    {
                        Reason = BreakpointEvent.ReasonValue.Changed,
                        Breakpoint = new Breakpoint()
                        {
                            Id = ids[i],
                            Line = (int)bp.Line,
                            Source = args!.Source,
                            // IsActive=false — строка неисполняемая, маркер показываем неподтверждённым.
                            Verified = bp.IsActive
                        }
                    });
                }
            }
        }

        public async Task<SetBreakpointsResponse> SetBreakpoints(SetBreakpointsArguments args)
        {
            await _setBreakpointsLock.WaitAsync(_cancellation);
            try
            {
                return await SetBreakpointsCore(args);
            }
            finally
            {
                _setBreakpointsLock.Release();
            }
        }

        private async Task<SetBreakpointsResponse> SetBreakpointsCore(SetBreakpointsArguments args)
        {
            var debuggerResponse = new SetBreakpointsResponse();

            var requestedKey = SourcePath.Canonical(args.Source.Path.CapitalizeFirstChar());
            _moduleSetBreakpointsArguments[requestedKey] = args;

            var request = _configuration.CreateRequest<RdbgSetBreakpointsRequest>();
            // Внешние модули различаются URL собранного файла: тройка идентификаторов
            // у копий обработок совпадает, и в запрос уходила бы одна из них.
            var modules = new Dictionary<(string Extension, string ObjectId, string PropertyId, string Url), ModuleBpInfoInternal>();

            foreach (var (moduleKey, cArgs) in _moduleSetBreakpointsArguments.ToList())
            {
                var sourcePath = cArgs.Source.Path.CapitalizeFirstChar();
                var (extension, objectId, propertyId) = _metadataProvider.ModuleInfoByPath(sourcePath);

                var moduleInfo = new ModuleBpInfoInternal()
                {
                    Id = new BslModuleIdInternal()
                    {
                        ExtensionName = extension,
                        ObjectId = objectId,
                        PropertyId = propertyId
                    }
                };
                var includeInRequest = true;
                var isExternal = _metadataProvider.IsExternalModule((extension, objectId, propertyId));
                if (isExternal)
                {
                    // Сервер адресует внешние модули по URL собранного файла; без него запрос
                    // отвергается целиком, поэтому такой модуль не отправляем вовсе.
                    // У копий обработок uuid общий, значит URL уточняем по пути исходника.
                    // Путь может не сопоставиться (другая машина, Git-URI), тогда остаётся
                    // поиск по тройке идентификаторов.
                    var url = _metadataProvider.ExternalModuleUrlByPath(sourcePath);
                    if (url.Length == 0)
                        url = _metadataProvider.ExternalModuleUrl((extension, objectId, propertyId));
                    moduleInfo.Id.Type = BslModuleType.ExtMdModule;
                    moduleInfo.Id.Url = url;
                    includeInRequest = url.Length > 0;
                }
                else if (!string.IsNullOrEmpty(extension))
                    moduleInfo.Id.Type = BslModuleType.ExtensionModule;
                Log.Debug($"точки: модуль type={moduleInfo.Id.Type} url=\"{moduleInfo.Id.Url}\" obj={objectId} prop={propertyId} строки=[{string.Join(",", cArgs.Breakpoints.Select(b => b.Line))}]{(includeInRequest ? "" : ", пропущен: нет собранного файла")}");

                // Точка на неисполняемой строке (комментарий, директива, препроцессор) молча не работает —
                // сдвигаем до исполняемой; VS Code подвинет маркер по строке из ответа.
                string? moduleSource = null;
                // У копий обработок обратный поиск по тройке идентификаторов даёт файл соседней,
                // поэтому исходник внешнего модуля читаем по пути из запроса.
                var localPath = _metadataProvider.LocalModulePath((extension, objectId, propertyId));
                if (isExternal && File.Exists(sourcePath))
                    localPath = sourcePath;
                if (localPath != null)
                    try { moduleSource = File.ReadAllText(localPath); } catch { /* без исходника — без сдвига */ }

                var requestedLines = new List<int>();
                cArgs.Breakpoints.ForEach(bp =>
                {
                    var line = moduleSource == null ? bp.Line : BslModuleAnalyzer.AdjustBreakpointLine(moduleSource, bp.Line);
                    if (line != bp.Line)
                        Log.Debug($"точка {Path.GetFileName(localPath)}:{bp.Line} сдвинута на исполняемую строку {line}");
                    requestedLines.Add(line);

                    // Исполняемой строки ниже не нашлось (хвост модуля) — серверу такую точку не шлём.
                    if (moduleSource != null && !BslModuleAnalyzer.IsLineBreakable(moduleSource, line))
                    {
                        Log.Debug($"точка {Path.GetFileName(localPath)}:{bp.Line} без исполняемой строки ниже — не отправлена");
                        return;
                    }

                    // Точки, схлопнувшиеся в одну строку, серверу отправляем один раз.
                    if (moduleInfo.BpInfo.Any(x => (int)x.Line == line))
                        return;

                    moduleInfo.BpInfo.Add(new BreakpointInfo()
                    {
                        Line = line,
                        HitCount = int.TryParse(bp.HitCondition, out var hit) ? hit : 1,
                        BreakOnHitCount = !string.IsNullOrEmpty(bp.HitCondition),
                        IsActive = true,
                        PutExpressionResult = BuildLogExpression(bp.LogMessage),
                        ShowOutputMessage = !string.IsNullOrEmpty(bp.LogMessage),
                        ContinueExecution = !string.IsNullOrEmpty(bp.LogMessage),
                        Condition = bp.Condition ?? "",
                        BreakOnCondition = !string.IsNullOrEmpty(bp.Condition)
                    });
                });

                if (includeInRequest)
                    modules[(extension, objectId, propertyId, moduleInfo.Id.Url ?? "")] = moduleInfo;

                if (string.Equals(moduleKey, requestedKey, StringComparison.OrdinalIgnoreCase))
                {
                    var ids = moduleInfo.BpInfo.Select(_ => System.Threading.Interlocked.Increment(ref _nextBreakpointId)).ToList();
                    _moduleBreakpointIds[moduleKey] = ids;

                    var idByLine = moduleInfo.BpInfo
                        .Select((c, i) => (Line: (int)c.Line, Id: ids[i]))
                        .ToDictionary(x => x.Line, x => x.Id);

                    debuggerResponse.Breakpoints = requestedLines.Select(line => idByLine.TryGetValue(line, out var id)
                        ? new Breakpoint() { Id = id, Line = line, Source = args.Source, Verified = includeInRequest }
                        : new Breakpoint() { Line = line, Source = args.Source, Verified = false }).ToList();
                }
            }

            MirrorBreakpointsToExtensions(modules);

            foreach (var moduleInfo in modules.Values)
                request.BpWorkspace.Add(moduleInfo);

            await _debugServerClient.SetBreakpoints(request);

            return debuggerResponse;
        }

        /// <summary>
        /// Дублирует точки базовой конфигурации в процедуры-заместители расширений: при «Вместо»/
        /// «ИзменениеИКонтроль» базовый код не выполняется и точка без зеркала молча не срабатывает.
        /// </summary>
        private void MirrorBreakpointsToExtensions(Dictionary<(string Extension, string ObjectId, string PropertyId, string Url), ModuleBpInfoInternal> modules)
        {
            foreach (var (key, moduleInfo) in modules.ToList())
            {
                var info = (key.Extension, key.ObjectId, key.PropertyId);
                if (info.Extension.Length > 0 || _metadataProvider.IsExternalModule(info))
                    continue;

                var basePath = _metadataProvider.LocalModulePath(info);
                if (basePath == null)
                    continue;
                string baseContent;
                try { baseContent = File.ReadAllText(basePath); }
                catch { continue; }

                foreach (var counterpart in _metadataProvider.ExtensionCounterparts(info))
                {
                    var extensionPath = _metadataProvider.LocalModulePath(counterpart);
                    if (extensionPath == null)
                        continue;
                    string extensionContent;
                    try { extensionContent = File.ReadAllText(extensionPath); }
                    catch { continue; }

                    var counterpartKey = (counterpart.Extension, counterpart.ObjectId, counterpart.PropertyId, "");

                    foreach (var bp in moduleInfo.BpInfo.ToList())
                    {
                        var mappedLines = BslModuleAnalyzer.MapBaseLineToExtensionLines(baseContent, (int)bp.Line, extensionContent);
                        if (mappedLines.Count == 0)
                            continue;

                        if (!modules.TryGetValue(counterpartKey, out var extensionModule))
                        {
                            extensionModule = new ModuleBpInfoInternal()
                            {
                                Id = new BslModuleIdInternal()
                                {
                                    ExtensionName = counterpart.Extension,
                                    ObjectId = counterpart.ObjectId,
                                    PropertyId = counterpart.PropertyId,
                                    Type = BslModuleType.ExtensionModule
                                }
                            };
                            modules[counterpartKey] = extensionModule;
                        }

                        foreach (var line in mappedLines.Where(l => extensionModule.BpInfo.All(x => (int)x.Line != l)))
                        {
                            extensionModule.BpInfo.Add(new BreakpointInfo()
                            {
                                Line = line,
                                HitCount = bp.HitCount,
                                BreakOnHitCount = bp.BreakOnHitCount,
                                IsActive = true,
                                PutExpressionResult = bp.PutExpressionResult,
                                ShowOutputMessage = bp.ShowOutputMessage,
                                ContinueExecution = bp.ContinueExecution,
                                Condition = bp.Condition,
                                BreakOnCondition = bp.BreakOnCondition
                            });
                            Log.Debug($"зеркало точки: {Path.GetFileName(basePath)}:{bp.Line} → расширение «{counterpart.Extension}»:{line}");
                        }
                    }
                }
            }
        }

        public async Task<SetExceptionBreakpointsResponse> SetExceptionBreakpoints(SetExceptionBreakpointsArguments args)
        {
            var request = _configuration.CreateRequest<RdbgSetRunTimeErrorProcessingRequest>();
            request.State = new RteFilterStorage()
            {
                StopOnErrors = args.FilterOptions switch
                {
                    null => false,
                    _ => args.FilterOptions.Count > 0
                }
            };
            var filter = args.FilterOptions?.FirstOrDefault();
            if (filter != null && !string.IsNullOrEmpty(filter.Condition))
            {
                request.State.AnalyzeErrorStr = true;
                request.State.StrTemplate.Add(new RteFilterItem()
                {
                    Include = true,
                    Str = filter.Condition
                });
            }

            await _debugServerClient.SetBreakOnRTE(request, _cancellation);

            return new SetExceptionBreakpointsResponse()
            {
                Breakpoints = new() { new() { Verified = true } }
            };
        }

        public async Task<StackTraceResponse> GetCallStack(StackTraceArguments args)
        {
            if (!_threadsCallStack.ContainsKey(args.ThreadId))
            {
                _callStackRequestTasks.TryAdd(args.ThreadId, new TaskCompletionSource());
                await _callStackRequestTasks[args.ThreadId].Task;
            }

            if (!_threadsCallStack.TryGetValue(args.ThreadId, out var callStack))
            {
                return new StackTraceResponse()
                {
                    TotalFrames = 0,
                    StackFrames = new()
                };
            }

            return new StackTraceResponse()
            {
                TotalFrames = callStack.Count,
                StackFrames = callStack.Select((c, index) =>
                    CallStackMapper.ToDapFrame(
                        _frameIdentifiers.Add((args.ThreadId, index)),
                        c,
                        CallStackMapper.ResolveSourcePath(_metadataProvider, c.ModuleId)
                    )).ToList()
            };
        }

        public void ClearStackFrameInfo(int threadId)
        {
            _callStackRequestTasks.TryRemove(threadId, out _);
            _threadsCallStack.TryRemove(threadId, out _);
            _frameIdentifiers.Clear(item => item.ThreadId == threadId);
            _variableIdentifiers.Clear(item => item.ThreadId == threadId);
        }

        public ScopesResponse GetScopes(ScopesArguments args)
        {
            var (ThreadId, FrameId) = _frameIdentifiers.Get(args.FrameId);

            return new ScopesResponse()
            {
                Scopes = new List<Scope>()
                {
                    new()
                    {
                        Name = "Локальные",
                        VariablesReference = _variableIdentifiers.Add((ThreadId, FrameId, new(), ViewInterface.Context))
                    }
                }
            };
        }

        public async Task<VariablesResponse> GetVariables(VariablesArguments args)
        {
            (int ThreadId, int FrameId, List<SourceCalculationDataItem> Path, ViewInterface ViewInterface) = _variableIdentifiers.Get(args.VariablesReference);

            var debuggeeResponse = (Path.Count > 0) switch
            {
                true => await Eval(ThreadId, FrameId, Path, ViewInterface),
                _ => await EvalLocalVariables(ThreadId, FrameId)
            };
            var result = debuggeeResponse.FirstOrDefault();
            var response = new VariablesResponse();
            if (result == null)
                return response;

            // Сервер нередко отдаёт пустой список локальных переменных сразу после останова —
            // повторяем с паузами, затем строим список по исходнику процедуры.
            if (Path.Count == 0 && LocalsEmpty(result))
            {
                foreach (var delay in _configuration.VariablesRetryDelaysMs)
                {
                    await Task.Delay(delay, _cancellation);
                    result = (await EvalLocalVariables(ThreadId, FrameId)).FirstOrDefault() ?? result;
                    if (!LocalsEmpty(result))
                        break;
                }

                if (LocalsEmpty(result))
                {
                    response.Variables = await VariablesFromSource(ThreadId, FrameId);
                    return response;
                }
            }

            if (result.ErrorOccurred)
                return response;

            switch(result.CalculationResult.ViewInterface)
            {
                case ViewInterface.Enum:
                    // Перечислимые коллекции (Соответствие и т.п.): элемент несёт пару Ключ/Значение
                    // во вложенных свойствах — показываем «ключ = значение» с возможностью раскрытия.
                    response.Variables = result.CalculationResult.ValueOfEnumInfo.Select((c, index) =>
                    {
                        var props = c.ValueOfContextPropInfoSpecified ? c.ValueOfContextPropInfo : null;
                        var key = props?.FirstOrDefault(p => p.PropInfo?.PropName is "Ключ" or "Key");
                        var value = props?.FirstOrDefault(p => p.PropInfo?.PropName is "Значение" or "Value");

                        var itemPath = new List<SourceCalculationDataItem>(Path)
                        {
                            new() { Index = index, IndexSpecified = true, ItemType = SourceCalculationDataItemType.Index }
                        };
                        var valueInfo = value?.ValueInfo ?? c.ValueInfo;

                        return new Variable()
                        {
                            Name = key?.ValueInfo.Pres.GetUTF8String() ?? c.ValueInfo.Pres.GetUTF8String(),
                            Value = value != null ? value.ValueInfo.Pres.GetUTF8String() : (key != null ? "" : c.ValueInfo.Pres.GetUTF8String()),
                            Type = valueInfo.TypeName,
                            VariablesReference = GetVariableReference(ThreadId, FrameId, itemPath, valueInfo)
                        };
                    }).ToList();
                    break;
                case ViewInterface.Collection:
                    response.Variables = GetCollectionVariables(ThreadId, FrameId, Path, result);
                    break;
                case ViewInterface.Context:
                    response.Variables = result.CalculationResult.ValueOfContextPropInfo.Select(c =>
                    {
                        if (c.PropInfo.IsReaded)
                        {
                            var itemPath = new List<SourceCalculationDataItem>();
                            itemPath.AddRange(Path);
                            if (itemPath.Count == 0)
                                itemPath.Add(new()
                                {
                                    Expression = c.PropInfo.PropName,
                                    ItemType = SourceCalculationDataItemType.Expression
                                });
                            else
                                itemPath.Add(new()
                                {
                                    Property = c.PropInfo.PropName,
                                    ItemType = SourceCalculationDataItemType.Property
                                });

                            var itemReference = GetVariableReference(ThreadId, FrameId, itemPath, c.ValueInfo);

                            return new Variable()
                            {
                                Name = $"{c.PropInfo.PropName} ({c.ValueInfo.TypeName})",
                                Type = c.ValueInfo.TypeName,
                                Value = c.ValueInfo.Pres.GetUTF8String(),
                                VariablesReference = itemReference
                            };
                        }
                        else
                            return new Variable()
                            {
                                Name = c.PropInfo.PropName,
                                Value = c.PropInfo.ErrorStr.GetUTF8String()
                            };
                    }).ToList();
                    break;
                default:
                    break;
            };

            if (result.CalculationResult.ViewInterface == ViewInterface.Context && 
                result.ResultValueInfo.IsIIndexedCollectionRo &&
                result.ResultValueInfo.CollectionSizeSpecified)
            {
                var itemsEvalResult = await Eval(ThreadId, FrameId, Path, ViewInterface.Collection);
                var items = itemsEvalResult.First();

                response.Variables.AddRange(GetCollectionVariables(ThreadId, FrameId, Path, items));
            }

            return response;
        }

        private List<Variable> GetCollectionVariables(int threadId, int frameId, List<SourceCalculationDataItem> path, CalculationResultBaseData result)
        {
            return result.CalculationResult.ValueOfCollectionInfo.Select((c, index) =>
            {
                var itemPath = new List<SourceCalculationDataItem>();
                itemPath.AddRange(path);
                itemPath.Add(new SourceCalculationDataItem()
                {
                    Index = index,
                    IndexSpecified = true,
                    ItemType = SourceCalculationDataItemType.Index
                });

                var itemReference = GetVariableReference(threadId, frameId, itemPath, c.ValueInfo);

                return new Variable()
                {
                    Name = $"{index} ({c.ValueInfo.TypeName})",
                    Type = c.ValueInfo.TypeName,
                    Value = c.ValueInfo.Pres.GetUTF8String(),
                    VariablesReference = itemReference
                };
            }).ToList();
        }

        private static bool LocalsEmpty(CalculationResultBaseData result)
            => result.ErrorOccurred
               || result.CalculationResult == null
               || result.CalculationResult.ValueOfContextPropInfo.Count == 0;

        /// <summary>
        /// Запасной список переменных по исходнику текущей процедуры (параметры, «Перем», присваивания):
        /// каждое имя вычисляется отдельным запросом.
        /// </summary>
        private async Task<List<Variable>> VariablesFromSource(int threadId, int frameId)
        {
            var variables = new List<Variable>();
            if (!_threadsCallStack.TryGetValue(threadId, out var frames) || frameId < 0 || frameId >= frames.Count)
                return variables;

            var frame = frames[frameId];
            var url = frame.ModuleId.Url ?? "";
            var modulePath = (url.Length > 0 ? _metadataProvider.TryModulePathByExternalUrl(url, frame.ModuleId.PropertyId) : null)
                ?? _metadataProvider.LocalModulePath((frame.ModuleId.ExtensionName ?? "", frame.ModuleId.ObjectId, frame.ModuleId.PropertyId));
            if (modulePath == null)
                return variables;

            string content;
            try { content = await File.ReadAllTextAsync(modulePath, _cancellation); }
            catch { return variables; }

            var names = BslModuleAnalyzer.VariableNamesAtLine(content, (int)frame.LineNo);
            Log.Debug($"переменные из исходника: {names.Count} имён ({Path.GetFileName(modulePath)}:{frame.LineNo})");

            foreach (var name in names.Take(40))
            {
                var path = new List<SourceCalculationDataItem>
                {
                    new() { Expression = name, ItemType = SourceCalculationDataItemType.Expression }
                };
                var item = (await Eval(threadId, frameId, path, ViewInterface.Context)).FirstOrDefault();
                if (item == null || item.ErrorOccurred)
                    continue;

                variables.Add(new Variable()
                {
                    Name = name,
                    Value = item.ResultValueInfo.Pres.GetUTF8String(),
                    Type = item.ResultValueInfo.TypeName,
                    VariablesReference = GetVariableReference(threadId, frameId, path, item.ResultValueInfo)
                });
            }

            return variables;
        }

        public async Task<EvaluateResponse> Evaluate(EvaluateArguments args)
        {
            // Вычисление в 1С возможно только в контексте кадра стека.
            if (args.FrameId == null)
                return new EvaluateResponse { Result = "Вычисление выражения доступно только при остановке на точке (в контексте кадра стека)." };

            var path = new List<SourceCalculationDataItem>()
            {
                new()
                {
                    Expression = args.Expression,
                    ItemType = SourceCalculationDataItemType.Expression
                }
            };

            var (ThreadId, FrameId) = _frameIdentifiers.Get((int)args.FrameId!);

            var debuggeeResponse = await Eval(ThreadId, FrameId, path, ViewInterface.Context);
            var debuggeeResponseItem = debuggeeResponse.FirstOrDefault();

            var response = new EvaluateResponse();
            if (debuggeeResponseItem == null)
                response.Result = "Ошибка получения результата вычисления выражения";
            else if (debuggeeResponseItem.ErrorOccurred)
                response.Result = debuggeeResponseItem.ExceptionStr.GetUTF8String();
            else
            {
                response.Result = debuggeeResponseItem.ResultValueInfo.Pres.GetUTF8String();
                response.Type = debuggeeResponseItem.ResultValueInfo.TypeName;

                response.VariablesReference = GetVariableReference(ThreadId, FrameId, path, debuggeeResponseItem.ResultValueInfo);
            }

            return response;
        }

        public async Task<SetVariableResponse> SetVariable(SetVariableArguments args)
        {
            var (threadId, frameId, containerPath, _) = _variableIdentifiers.Get(args.VariablesReference);

            // Имя в панели содержит тип («Отказ (Булево)») — для пути нужен чистый идентификатор.
            var name = args.Name;
            var typeSuffix = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (typeSuffix > 0 && name.EndsWith(')'))
                name = name[..typeSuffix];

            // Путь к изменяемому значению: локальная переменная кадра, элемент коллекции или свойство.
            var itemPath = new List<SourceCalculationDataItem>(containerPath);
            if (itemPath.Count == 0)
                itemPath.Add(new() { Expression = name, ItemType = SourceCalculationDataItemType.Expression });
            else if (int.TryParse(name, out var index))
                itemPath.Add(new() { Index = index, IndexSpecified = true, ItemType = SourceCalculationDataItemType.Index });
            else
                itemPath.Add(new() { Property = name, ItemType = SourceCalculationDataItemType.Property });

            var srcCalcInfo = new SourceCalculationDataInfo() { ExpressionResultId = Guid.NewGuid().ToString() };
            itemPath.ForEach(c => srcCalcInfo.CalcItem.Add(c));

            var request = _configuration.CreateRequest<RdbgModifyValueRequest>();
            request.TargetId = _targetsManager.GetTargetId(threadId).ToLight();
            request.ModifyDataPath = new CalculationSourceDataStorage()
            {
                StackLevel = frameId > 0 ? frameId : 0,
                SrcCalcInfo = srcCalcInfo,
                PresOptions = new DbgPresentationOptionsOfStringValue() { MaxTextSize = 307200 }
            };
            // Новое значение — выражение 1С: покрывает и литералы, и конструкторы значений.
            request.NewValueInfo = new NewValueInfo()
            {
                Variant = NewValueVariant.Expr,
                ValueExpression = args.Value
            };

            Log.Debug($"setVariable: {name} ← {args.Value}");
            await _debugServerClient.ModifyValue(request, _cancellation);

            // Фактическое значение перечитываем с сервера — оно и уходит в панель «Переменные».
            var result = (await Eval(threadId, frameId, itemPath, ViewInterface.Context)).FirstOrDefault();
            if (result == null)
                return new SetVariableResponse() { Value = args.Value };
            if (result.ErrorOccurred)
                throw new InvalidOperationException(result.ExceptionStr.GetUTF8String());

            return new SetVariableResponse()
            {
                Value = result.ResultValueInfo.Pres.GetUTF8String(),
                Type = result.ResultValueInfo.TypeName
            };
        }

        /// <summary>
        /// Транслирует DAP-сообщение logpoint (`текст {выражение}`) в выражение 1С:
        /// литералы — строковые константы, вставки — конкатенация с приведением к строке.
        /// </summary>
        internal static string? BuildLogExpression(string? logMessage)
        {
            if (string.IsNullOrEmpty(logMessage))
                return logMessage;

            var parts = new List<string>();
            var literal = new StringBuilder();
            var i = 0;
            while (i < logMessage.Length)
            {
                var open = logMessage.IndexOf('{', i);
                if (open < 0)
                {
                    literal.Append(logMessage[i..]);
                    break;
                }
                var close = logMessage.IndexOf('}', open + 1);
                if (close < 0)
                {
                    literal.Append(logMessage[i..]);
                    break;
                }

                literal.Append(logMessage[i..open]);
                if (literal.Length > 0)
                {
                    parts.Add("\"" + literal.Replace("\"", "\"\"") + "\"");
                    literal.Clear();
                }

                var expression = logMessage[(open + 1)..close].Trim();
                if (expression.Length > 0)
                    parts.Add("(" + expression + ")");

                i = close + 1;
            }
            if (literal.Length > 0)
                parts.Add("\"" + literal.Replace("\"", "\"\"") + "\"");

            if (parts.Count == 0)
                return "\"\"";
            // Первый операнд — строка: тогда 1С приводит остальные операнды конкатенации к строке.
            if (!parts[0].StartsWith('"'))
                parts.Insert(0, "\"\"");

            return string.Join(" + ", parts);
        }

        private async Task<List<CalculationResultBaseData>> EvalLocalVariables(int threadId, int frameId)
        {
            var resultId = Guid.NewGuid().ToString();
            _evaluationTasks.TryAdd(resultId, new());

            var presentationOptions = new DbgPresentationOptionsOfStringValue()
            {
                MaxTextSize = 307200
            };

            var sourceCalculationDataInfo = new SourceCalculationDataInfo()
            {
                ExpressionResultId = resultId
            };
            sourceCalculationDataInfo.Interfaces.Add(ViewInterface.Context);

            var calculationSource = new CalculationSourceDataStorage
            {
                StackLevel = frameId > 0 ? frameId : 0,
                SrcCalcInfo = sourceCalculationDataInfo,
                PresOptions = presentationOptions
            };

            var request = _configuration.CreateRequest<RdbgEvalLocalVariablesRequest>();
            request.TargetId = _targetsManager.GetTargetId(threadId).ToLight();
            request.CalcWaitingTime = _configuration.CalcWaitingTimeMs;
            request.Expr.Add(calculationSource);

            var response = await _debugServerClient.EvalLocalVariables(request, _cancellation);

            if (response?.Result == null)
                await _evaluationTasks[resultId].Task;
            else
                _evaluationTasks[resultId].SetResult(new() { response!.Result });

            var result = _evaluationTasks[resultId].Task.Result;
            _evaluationTasks.Remove(resultId, out _);
            return result;
        }

        private async Task<List<CalculationResultBaseData>> Eval(int threadId, int frameId, List<SourceCalculationDataItem> path, ViewInterface viewInterface)
        {
            var resultId = Guid.NewGuid().ToString();
            _evaluationTasks.TryAdd(resultId, new());

            var presentationOptions = new DbgPresentationOptionsOfStringValue()
            {
                MaxTextSize = 307200
            };

            var sourceCalculationDataInfo = new SourceCalculationDataInfo()
            {
                ExpressionResultId = resultId
            };
            sourceCalculationDataInfo.Interfaces.Add(viewInterface);
            path.ForEach(c => sourceCalculationDataInfo.CalcItem.Add(c));

            var calculationSource = new CalculationSourceDataStorage
            {
                StackLevel = frameId > 0 ? frameId : 0,
                SrcCalcInfo = sourceCalculationDataInfo,
                PresOptions = presentationOptions
            };

            var request = _configuration.CreateRequest<RdbgEvalExprRequest>();
            request.TargetId = _targetsManager.GetTargetId(threadId).ToLight();
            request.CalcWaitingTime = _configuration.CalcWaitingTimeMs;
            request.Expr.Add(calculationSource);

            var response = await _debugServerClient.EvalExpr(request, _cancellation);

            if (response?.Result == null || !response.ResultSpecified)
                await _evaluationTasks[resultId].Task;
            else
                _evaluationTasks[resultId].SetResult(response!.Result.ToList());

            var result = _evaluationTasks[resultId].Task.Result;
            _evaluationTasks.Remove(resultId, out _);
            return result;
        }

        private async void CallStackFormedHandler(object? sender, CallStackFormedEventArgs e)
        {
            // async void: незамеченное исключение уронило бы процесс адаптера.
            try
            {
                await OnCallStackFormed(e);
            }
            catch (System.Exception ex)
            {
                _client.SendError(ex);
            }
        }

        private async Task OnCallStackFormed(CallStackFormedEventArgs e)
        {
            var threadId = _targetsManager.GetThreadId(e.Info.TargetId);

            var frames = e.Info.CallStack?.Reverse().ToList() ?? [];
            if (frames.Count > 0)
            {
                var stopPoint = frames[0];
                if (stopPoint.ModuleId != null)
                    Log.Debug($"останов: модуль type={stopPoint.ModuleId.Type} url=\"{stopPoint.ModuleId.Url}\" obj={stopPoint.ModuleId.ObjectId} prop={stopPoint.ModuleId.PropertyId} строка={stopPoint.LineNo}");
                else
                    Log.Debug($"останов: кадр без модуля «{stopPoint.Presentation.GetUTF8String()}» строка={stopPoint.LineNo}");
            }

            if (e.Info.CallStackSpecified && frames.Count > 0)
            {
                _threadsCallStack.TryRemove(threadId, out _);
                _threadsCallStack.TryAdd(threadId, frames);

                if (_callStackRequestTasks.TryGetValue(threadId, out var task))
                {
                    _callStackRequestTasks.TryRemove(threadId, out _);
                    task.SetResult();
                }
            }

            if (e.Info.HasMessage == true)
                _client.SendOutput(e.Info.Message);

            if (e.Info.SendMessageOnly == true)
            {
                await ContinueDebugTarget(e.Info.TargetId);
                return;
            }

            if (e.Info.SendHitCounterOnly)
                return;

            if (e.Info.StopByBp == true || e.Info.SuspendedByOther)
                _client.SendEvent(new StoppedEvent()
                {
                    Reason = StoppedEvent.ReasonValue.Breakpoint,
                    AllThreadsStopped = false,
                    ThreadId = threadId
                });
            else
            {
                _client.SendEvent(new StoppedEvent()
                {
                    Reason = StoppedEvent.ReasonValue.Step,
                    AllThreadsStopped = false,
                    ThreadId = threadId
                });
            }
        }

        private async Task ContinueDebugTarget(DebugTargetId target)
        {
            var request = _configuration.CreateRequest<RdbgStepRequest>();
            request.TargetId = target.ToLight();
            request.Action = DebugStepAction.Continue;

            await _debugServerClient.Step(request, _cancellation);
        }

        private void RuntimeExceptionHandler(object? sender, RuntimeExceptionArgs e)
        {
            var threadId = _targetsManager.GetThreadId(e.Info.TargetId);

            if (e.Info.CallStackSpecified)
            {
                _threadsCallStack.TryRemove(threadId, out _);
                _threadsCallStack.TryAdd(threadId, e.Info.CallStack.Reverse().ToList());

                if (_callStackRequestTasks.TryGetValue(threadId, out var task))
                {
                    _callStackRequestTasks.TryRemove(threadId, out _);
                    task.SetResult();
                }
            }

            _client.SendEvent(new StoppedEvent()
            {
                Reason = StoppedEvent.ReasonValue.Exception,
                AllThreadsStopped = false,
                ThreadId = threadId
            });

            _client.SendError(e.Info.Exception.Descr);
        }

        private void ExpressionEvaluatedHandler(object? sender, ExpressionEvaluatedEventArgs e)
        {
            if (_evaluationTasks.TryGetValue(e.Info.EvalExprResBaseData.ExpressionResultId, out var task))
                task.SetResult(new() { e.Info.EvalExprResBaseData });
        }

        private int GetVariableReference(int threadId, int frameId, List<SourceCalculationDataItem> path, BaseValueInfoData data)
        {
            var reference = 0;

            if (data.IsExpandable)
                reference = _variableIdentifiers.Add((threadId, frameId, path, ViewInterface.Context));
            else if (data.IsIIndexedCollectionRo)
                reference = _variableIdentifiers.Add((threadId, frameId, path, ViewInterface.Collection));
            else if (data.IsSupportIEnumValue)
                // Перечислимые значения (Соответствие и т.п.) раскрываются интерфейсом Enum.
                reference = _variableIdentifiers.Add((threadId, frameId, path, ViewInterface.Enum));

            return reference;
        }
    }
}
