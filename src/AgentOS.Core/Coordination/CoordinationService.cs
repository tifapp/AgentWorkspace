namespace AgentOS.Core.Coordination;

/// <summary>
/// First-slice, trusted in-process coordination API. A future process transport must
/// authenticate callers before it issues actors or invokes these methods.
/// </summary>
public sealed class CoordinationService : IDisposable
{
    public const int ProtocolVersion = 1;
    private readonly CoordinationStore _store;
    private readonly CoordinationIdentity _identity;
    private readonly CoordinationActions _actions;
    private readonly CoordinationMessaging _messaging;
    private readonly CoordinationEvidence _evidence;
    private readonly CoordinationPresentation _presentation;
    private readonly CoordinationAdmission _admission;

    /// <summary>Opens a fresh root for a trusted in-process host.</summary>
    public CoordinationService(string root) : this(root, null, null) { }

    /// <summary>Internal test seam for process liveness and transaction/export faults.</summary>
    internal CoordinationService(string root, Func<int,long,bool>? processLiveness, Func<FaultPoint,bool>? fault)
    {
        _store = new CoordinationStore(root, processLiveness, fault);
        try
        {
            _identity = new CoordinationIdentity(_store, this);
            _actions = new CoordinationActions(_store, _identity);
            _messaging = new CoordinationMessaging(_store, _identity);
            _evidence = new CoordinationEvidence(_store);
            _admission = new CoordinationAdmission(_store, _identity);
            _presentation = new CoordinationPresentation(_store);
            _store.AfterCommit = () => _presentation.RebuildRegistry();
            _store.AfterCommitFailed = error => _presentation.RecordFailure(error);
            _presentation.Snapshot();
            _presentation.RebuildRegistry();
        }
        catch
        {
            _store.Dispose();
            throw;
        }
    }
    public string Root => _store.Root;
    /// <summary>Reports the last projection attempt; committed database work remains authoritative when export fails.</summary>
    public ExportHealth ExportStatus => _presentation.ExportStatus;
    public long EvidenceCount => _evidence.EvidenceCount;
    /// <summary>Issues user authority only to a trusted in-process host; no external caller authentication is performed here.</summary>
    public Actor TrustedUser() => _identity.TrustedUser();
    /// <summary>Issues system authority only to a trusted in-process host; no external caller authentication is performed here.</summary>
    public Actor TrustedSystem() => _identity.TrustedSystem();
    /// <summary>Registers the current process. This first slice has no process transport or remote admission.</summary>
    public ParticipantSession Register(string key,string? stableIdentity=null,string? group=null) => _identity.Register(key,stableIdentity,group);
    public Actor Authenticate(ParticipantSession session) => _identity.Authenticate(session);
    public long Heartbeat(Actor actor,long expectedRevision,string key) => _identity.Heartbeat(actor,expectedRevision,key);
    public ActionDetail CreateAction(Actor requester,string text,string key) => _actions.CreateAction(requester,text,key);
    public ActionDetail ReadAction(string id) => _actions.ReadAction(id);
    public ActionDetail AssignAction(Actor actor,string id,string? owner,long expectedRevision,string key) => _actions.AssignAction(actor,id,owner,expectedRevision,key);
    public ActionDetail ClaimAction(Actor actor,string id,long expectedRevision,string key) => _actions.ClaimAction(actor,id,expectedRevision,key);
    public ActionDetail ReleaseAction(Actor actor,string id,long expectedRevision,string key) => _actions.ReleaseAction(actor,id,expectedRevision,key);
    public ActionDetail CloseAction(Actor actor,string id,ActionOutcome outcome,string explanation,long expectedRevision,string key,IReadOnlyList<string>? artifacts=null) => _actions.CloseAction(actor,id,outcome,explanation,expectedRevision,key,artifacts);
    public MessageDetail SendMessage(Actor sender,string to,string text,string key,string? replyTo=null) => _messaging.SendMessage(sender,to,text,key,replyTo);
    public MessageDetail ReadMessage(string id) => _messaging.ReadMessage(id);
    public IReadOnlyList<(string MessageId,string Recipient,string State)> InspectOutbox() => _messaging.InspectOutbox();
    public Registry Snapshot() => _presentation.Snapshot();
    public AdmissionDetail RequestResources(Actor actor, IReadOnlyList<ResourceRequest> resources, string key) => _admission.Request(actor, resources, key);
    public AdmissionDetail ReadAdmission(Actor actor, string id) => _admission.Read(actor, id);
    public IReadOnlyList<AdmissionDetail> ListAdmissions(Actor actor) => _admission.List(actor);
    public AdmissionDetail ReleaseResources(Actor actor, string id, long expectedRevision, string key) => _admission.Finish(actor, id, expectedRevision, key, false);
    public AdmissionDetail CancelAdmission(Actor actor, string id, long expectedRevision, string key) => _admission.Finish(actor, id, expectedRevision, key, true);
    /// <summary>Retries the disposable registry projection after an export failure.</summary>
    public ExportHealth RebuildRegistry() => _presentation.RebuildRegistry();
    public void Dispose() => _store.Dispose();
}






