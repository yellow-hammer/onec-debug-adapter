using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Onec.DebugAdapter.DebugServer
{
    public record DebugTargetEventArgs(DebugTargetId TargetId, bool Started);

    public record CallStackFormedEventArgs(DbguiExtCmdInfoCallStackFormed Info);

    public record ExpressionEvaluatedEventArgs(DbguiExtCmdInfoExprEvaluated Info);

    public record RuntimeExceptionArgs(DbguiExtCmdInfoRte Info);

    public record CorrectedBreakpointsArgs(DbguiExtCmdInfoCorrectedBp Info);

    public record SetForegroundHelperArgs(DbguiExtCmdInfoForegroundHelperSet Info);

    public record ForegroundHelperRequestArgs(DbguiExtCmdInfoForegroundHelperRequest Info);

    public record ProcessForegroundHelperArgs(DbguiExtCmdInfoForegroundHelperProcess Info);

    public record ShowMetadataObjectArgs(DbguiExtCmdShowMetadataObject Info);

    public record MeasureResultsEventArgs(DbguiExtCmdInfoMeasure Info);
}
