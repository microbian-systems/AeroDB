using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using AeroDB.Sable;

namespace AeroDB.WolverineFx.Codegen;

/// <summary>
/// Codegen frame that calls <see cref="IDocumentSession.SaveChangesAsync"/>
/// to persist pending changes within the handler pipeline.
/// </summary>
internal sealed class AeroDBSessionSaveChangesFrame : MethodCall
{
    public AeroDBSessionSaveChangesFrame()
        : base(typeof(IDocumentSession), ReflectionHelper.GetMethod<IDocumentSession>(x => x.SaveChangesAsync(default))!)
    {
        CommentText = "Save all pending changes to this AeroDB.Sable session";
    }
}
