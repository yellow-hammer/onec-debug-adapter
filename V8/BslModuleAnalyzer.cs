using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Onec.DebugAdapter.V8
{
    /// <summary>
    /// Лёгкий разбор BSL-модуля: границы процедур/функций, директивы расширений, имена переменных.
    /// Не полноценный парсер — достаточен для подсказок панели переменных и зеркалирования точек останова.
    /// </summary>
    internal static class BslModuleAnalyzer
    {
        internal enum ExtensionDirective { None, Replacement, After, Before }

        internal sealed record BslProcedure(string Name, int StartLine, int EndLine, ExtensionDirective Directive, string? BaseProcName);

        private static readonly Regex ProcStart = new(@"^\s*(Процедура|Функция)\s+([А-Яа-яёЁA-Za-z_][А-Яа-яёЁ\w]*)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProcEnd = new(@"^\s*Конец(Процедуры|Функции)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Directive = new(@"&(ИзменениеИКонтроль|Вместо|После|Перед)\s*\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VarAssign = new(@"^\s*([А-Яа-яёЁA-Za-z_][А-Яа-яёЁ\w]*)\s*=[^=]", RegexOptions.Compiled);
        private static readonly Regex VarDeclare = new(@"^\s*Перем\s+(.+?);", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Сдвигает строку точки останова (1-based) вниз до ближайшей исполняемой: пустые строки,
        /// комментарии, препроцессор (#), директивы (&), «Перем», заголовки процедур и продолжения
        /// строковых литералов (|) точку не принимают. «КонецПроцедуры» — валидная цель (останов на
        /// выходе). Если исполняемой строки ниже нет — возвращается исходная.
        /// </summary>
        internal static int AdjustBreakpointLine(string content, int line)
        {
            var lines = content.Split('\n');
            for (var i = Math.Max(0, line - 1); i < lines.Length; i++)
            {
                if (IsExecutableLine(lines[i]))
                    return i + 1;
            }
            return line;
        }

        /// <summary>Может ли строка принять точку останова.</summary>
        internal static bool IsLineBreakable(string content, int line)
        {
            var lines = content.Split('\n');
            return line >= 1 && line <= lines.Length && IsExecutableLine(lines[line - 1]);
        }

        private static bool IsExecutableLine(string rawLine)
        {
            var trimmed = rawLine.TrimEnd('\r').TrimStart();
            return trimmed.Length > 0
                   && !trimmed.StartsWith("//", StringComparison.Ordinal)
                   && !trimmed.StartsWith('#')
                   && !trimmed.StartsWith('&')
                   && !trimmed.StartsWith('|')
                   && !trimmed.StartsWith("Перем ", StringComparison.OrdinalIgnoreCase)
                   && !ProcStart.IsMatch(trimmed);
        }

        /// <summary>Процедуры/функции модуля с границами строк (1-based) и директивами расширения.</summary>
        internal static List<BslProcedure> ParseProcedures(string content)
        {
            var result = new List<BslProcedure>();
            var lines = content.Split('\n');
            (ExtensionDirective Type, string BaseName)? pendingDirective = null;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');

                var directive = Directive.Match(line);
                if (directive.Success)
                {
                    pendingDirective = (directive.Groups[1].Value.ToLowerInvariant() switch
                    {
                        "изменениеиконтроль" or "вместо" => ExtensionDirective.Replacement,
                        "после" => ExtensionDirective.After,
                        _ => ExtensionDirective.Before
                    }, directive.Groups[2].Value.Trim());
                    continue;
                }

                var start = ProcStart.Match(line);
                if (!start.Success)
                    continue;

                var endLine = -1;
                for (var j = i + 1; j < lines.Length; j++)
                    if (ProcEnd.IsMatch(lines[j].TrimEnd('\r')))
                    {
                        endLine = j + 1;
                        break;
                    }

                if (endLine > 0)
                    result.Add(new BslProcedure(
                        start.Groups[2].Value, i + 1, endLine,
                        pendingDirective?.Type ?? ExtensionDirective.None,
                        pendingDirective?.BaseName));
                pendingDirective = null;
            }

            return result;
        }

        /// <summary>
        /// Имена переменных процедуры, в которой находится строка (1-based): параметры, «Перем», присваивания.
        /// Подсказка для панели переменных, когда сервер отладки вернул пустой список.
        /// </summary>
        internal static List<string> VariableNamesAtLine(string content, int lineNo)
        {
            var lines = content.Split('\n');
            var proc = ParseProcedures(content).Find(p => lineNo >= p.StartLine && lineNo <= p.EndLine);
            if (proc == null)
                return new List<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            void Add(string name)
            {
                if (seen.Add(name))
                    names.Add(name);
            }

            // Параметры из заголовка (он может занимать несколько строк до «)»).
            var header = "";
            for (var i = proc.StartLine - 1; i < lines.Length; i++)
            {
                header += lines[i];
                if (lines[i].Contains(')'))
                    break;
            }
            var paramsStart = header.IndexOf('(');
            var paramsEnd = header.IndexOf(')');
            if (paramsStart >= 0 && paramsEnd > paramsStart)
                foreach (var raw in header[(paramsStart + 1)..paramsEnd].Split(','))
                {
                    var name = raw.Replace("Знач ", "", StringComparison.OrdinalIgnoreCase).Split('=')[0].Trim();
                    if (name.Length > 0)
                        Add(name);
                }

            for (var i = proc.StartLine; i < proc.EndLine && i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var declare = VarDeclare.Match(line);
                if (declare.Success)
                {
                    foreach (var raw in declare.Groups[1].Value.Split(','))
                        Add(raw.Replace("Экспорт", "", StringComparison.OrdinalIgnoreCase).Trim());
                    continue;
                }
                var assign = VarAssign.Match(line);
                if (assign.Success)
                    Add(assign.Groups[1].Value);
            }

            return names;
        }

        /// <summary>
        /// Строки модуля расширения, на которые надо продублировать точку из базового модуля.
        /// «Вместо»/«ИзменениеИКонтроль»: база не выполняется — точка по относительной позиции в заместителе.
        /// «После»/«Перед»: дополнительная точка на первую строку тела процедуры расширения.
        /// </summary>
        internal static List<int> MapBaseLineToExtensionLines(string baseContent, int baseLine, string extensionContent)
        {
            var result = new List<int>();
            var baseProc = ParseProcedures(baseContent).Find(p => baseLine >= p.StartLine && baseLine <= p.EndLine);
            if (baseProc == null)
                return result;

            foreach (var extProc in ParseProcedures(extensionContent))
            {
                if (extProc.Directive == ExtensionDirective.None
                    || !string.Equals(extProc.BaseProcName, baseProc.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (extProc.Directive == ExtensionDirective.Replacement)
                {
                    var baseHeight = baseProc.EndLine - baseProc.StartLine;
                    var extHeight = extProc.EndLine - extProc.StartLine;
                    var line = baseHeight <= 0 || extHeight <= 0
                        ? extProc.StartLine + 1
                        : extProc.StartLine + (int)Math.Round((baseLine - baseProc.StartLine) / (double)baseHeight * extHeight);
                    result.Add(Math.Clamp(line, extProc.StartLine + 1, extProc.EndLine - 1));
                }
                else
                    result.Add(extProc.StartLine + 1);
            }

            return result;
        }
    }
}
