using LE1GalaxyMapEditor.Workflows.Editing;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Workflows;

public sealed class EditorSession
{
    private GalaxyMapWorkspace? _workspace;
    private GalaxyMapDocument? _referenceDocument;

    public EditorSession(GalaxyMapWorkspace? workspace = null)
    {
        Workspace = workspace;
    }

    public GalaxyMapWorkspace? Workspace
    {
        get => _workspace;
        internal set
        {
            _workspace = value;
            if (value is not null)
            {
                _referenceDocument = null;
            }
        }
    }

    public GalaxyMapDocument? Document => Workspace?.EffectiveDocument ?? _referenceDocument;
    public GalaxyMapModule? ActiveModule => Workspace?.ActiveModule;
    public EditChangeSet Changes { get; } = new();
    public EditHistory History { get; } = new();
    public long Revision { get; private set; }
    public event EventHandler<SessionChangedEventArgs>? Changed;

    internal void AttachReferenceDocument(GalaxyMapDocument document)
    {
        _workspace = null;
        _referenceDocument = document ?? throw new ArgumentNullException(nameof(document));
        Publish(ChangeImpact.StructuralAll);
    }

    internal void Publish(ChangeImpact impact)
    {
        Revision++;
        Changed?.Invoke(this, new SessionChangedEventArgs(Revision, impact));
    }
}
