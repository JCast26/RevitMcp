using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Preprocesador de fallos de Revit para suprimir avisos no críticos y resolver advertencias
/// durante el modelado automatizado de armaduras en muros y elementos estructurales.
/// Evita la aparición de diálogos modales bloqueantes como 'The Rebar's hooks do not match its Rebar Shape'.
/// </summary>
public class RebarFailuresPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        IList<FailureMessageAccessor> failureMessages = failuresAccessor.GetFailureMessages();

        foreach (var fmsg in failureMessages)
        {
            var severity = fmsg.GetSeverity();

            if (severity == FailureSeverity.Warning)
            {
                failuresAccessor.DeleteWarning(fmsg);
            }
            else if (severity == FailureSeverity.Error)
            {
                // Si el error tiene resolución automática (p.ej. eliminar gancho en conflicto), resolverlo
                if (fmsg.HasResolutions())
                {
                    failuresAccessor.ResolveFailure(fmsg);
                    return FailureProcessingResult.ProceedWithCommit;
                }
            }
        }

        return FailureProcessingResult.Continue;
    }
}
