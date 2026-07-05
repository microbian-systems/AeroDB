using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx.Codegen;

/// <summary>
/// Codegen frame that calls <see cref="MessageContext.FlushOutgoingMessagesAsync"/>
/// to dispatch outgoing messages after the AeroDB session commits.
/// </summary>
internal sealed class FlushAeroDBOutgoingMessagesFrame : MethodCall
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects MessageContext.GetMethod(nameof(MessageContext.FlushOutgoingMessagesAsync)) at codegen time. The target method is statically referenced via nameof.")]
    public FlushAeroDBOutgoingMessagesFrame()
        : base(typeof(MessageContext), nameof(MessageContext.FlushOutgoingMessagesAsync))
    {
        CommentText = "Flush outgoing messages after AeroDB transaction commits";
    }
}
