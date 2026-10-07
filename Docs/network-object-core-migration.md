# Network-object Core extraction (2026-10-06)

Source baseline: Unity package commit `dcb7b513a425737ab91692ed15e0b41dc037d95f`.
This document describes a source migration, not a package-release announcement or a Unity acceptance report.

## Ownership and layering

`BITKit.Multiplayer.NetRpc.NetworkObjectService`, in the engine-neutral source package,
owns the object protocol, snapshots, IDs, generations, revisions, authority, owner changes,
pending loads, late scene retry, stale-message rejection, entity registration and teardown.
It uses the existing `RpcContextService` and `IEntitiesService`; it is not a second transport,
component registry or engine-specific replication system.

`UnityNetworkObjects` is now a facade. Its remaining responsibilities are:

- Convert Unity prefab/pose inputs into Core spawn templates
- Instantiate via Unity or `INetworkPrefabLoader`, and release through that loader
- Resolve loaded `SceneIdentity.NameKey` objects and reject duplicate authored keys
- Bind/clear Unity identity components, apply the initial transform and active state
- Return engine work to the owning session's main thread
- Present `NetworkGameObjectHandle` and forward lifecycle events

There are no Unity-owned object RPC methods, ID/revision counters, network-state dictionaries
or load/tombstone protocol implementations. The scene lookup dictionary is only an engine
asset lookup. The weak handle-wrapper cache is only a presentation cache; Core owns all leases.
The existing `SceneIdentity`/`NetworkIdentity` scripts and their `.meta` GUIDs are unchanged.

## Source compatibility

The following public entrypoints remain available:

- `UnityNetRpcAdapter.AttachNetworkObjects(worldGeneration, localPeerId, prefabLoader)`
- `UnityNetworkObjects.SpawnAsync(GameObject, Vector3, Quaternion, ownerPeerId, token)`
- `INetworkPrefabLoader.InstantiateAsync` and `Release`
- `NetworkGameObjectHandle` identity/owner/GameObject/scene properties
- `Initializing`, `Spawned`, `Despawning`, `Faulted`
- `RegisterLoadedSceneObjects`, `RegisterSceneObject`, `SynchronizeAsync`, `SetOwner`,
  `DespawnAsync`, `TryGet`, `Owns`, `Objects` and disposal

These are source-level facades, not a promise of binary or behavioral equivalence:

1. The old three-argument attach uses `Runtime.Entities` and fails explicitly if no entity
   service is attached. It never invents a competing registry. The new overload accepts the
   same registry explicitly and an optional `registerLoadedSceneObjects` flag.
2. `Initializing` now runs for scene objects as well as prefabs. Both paths use the same
   Core entity-registration/state-ready gate before `Spawned`.
3. The synchronous `RegisterSceneObject` can return null while async initialization is pending
   (as well as while client scene state has not arrived). Await `RegisterSceneObjectAsync`
   or observe `Spawned` when completion matters. Do not block Unity's main thread waiting.
4. `RegisterLoadedSceneObjects` starts registration and reports asynchronous failures through
   `Faulted`. Use `RegisterLoadedSceneObjectsAsync` for awaited completion/error propagation.
5. `Objects` is a snapshot collection of Core-owned handles, not the old live dictionary view.
6. Core enforces Host peer ID 1 and distinct nonzero Client peer IDs. Generation must be nonzero.
7. Public mutating Unity entrypoints, attach, detach and dispose require the session main thread.
8. The old `BITKit.Multiplayer.Unity.NetworkObjectKind`, `NetworkObjectState` and
   `NetworkObjectSnapshot` remain obsolete source-only compatibility DTOs. They do not
   automatically convert to Core DTOs and are not registered as the new wire contract.
   New shared code uses the `BITKit.Multiplayer.NetRpc` types. When importing both namespaces,
   qualify these names or use a C# type alias to avoid ambiguity.

New surfaces: `Core`, `RetryPendingAsync`, async scene-registration methods,
`NetworkGameObjectHandle.Entity` and `AttachEntity(NetEntity)`.
Existing consuming assemblies must be rebuilt; do not copy the new Unity source beside an
old Core DLL or keep a second compiled copy of the source package.

## Explicit entity initialization bridge

Use the application's existing entity service, already attached by the normal DI/runtime
setup. Disable automatic scene scanning if initialization hooks must be installed first:

```csharp
var objects = adapter.AttachNetworkObjects(
    worldGeneration, localPeerId, prefabLoader, entities,
    registerLoadedSceneObjects: false);

objects.Initializing += (handle, token) =>
{
    // Application-owned factory: build its normal scope and components. That scope must
    // resolve INetworkIdentity to handle.Identity (or the same assigned EntityId).
    NetEntity entity = CreateApplicationEntity(handle, token);
    handle.AttachEntity(entity);
    return UniTask.CompletedTask;
};

await objects.RegisterLoadedSceneObjectsAsync(token);
if (!objects.IsAuthority)
    await objects.SynchronizeAsync(token);
```

`CreateApplicationEntity` is application code, not a new SDK DI container or discovery rule.
Attach only during `Initializing`. Core freezes the lease after initialization, registers its
entity exactly once, and performs the explicit initial-state request/application before
exposing a Client object through `Spawned`. If no entity is attached, the object has a Unity
identity and roster lifecycle only; the SDK does not guess its application components.

Remove old initialization code that manually registers the same entity in `IEntitiesService`.
Do not bind a second object RPC target or maintain a parallel entity-ID allocation table.
Component state remains handled by the existing runtime/registry.

At despawn, Core emits `Despawning`, unregisters the attached entity, then calls the engine
release boundary. Application-specific subscriptions/scopes remain application-owned. Make
their cleanup idempotent and cover initialization failures too; Core does not dispose arbitrary
`IServiceProvider` instances. Prefabs are returned through `INetworkPrefabLoader.Release`;
scene objects have identities cleared and are deactivated rather than destroyed.
An old asynchronous lease may finish after the same authored object is respawned under a
new ID. Release checks the currently bound identity and never clears/deactivates a different
nonzero ID; state application also rejects a lease whose ID no longer matches. Core still
unregisters the old lease's own entity, independently of the new binding.

Register newly loaded additive scene identities, then await `RetryPendingAsync` (or use
`RegisterLoadedSceneObjectsAsync`). A missing scene is a pending engine resolution, not a new
entity. End the world before unloading its objects; the facade does not install automatic
SceneManager event handlers or silently recover externally destroyed GameObjects.

## Visibility and main-thread behavior

Bind assigns identity and initial pose while deactivating the object. Core's state-ready
completion invokes `ApplyState`, which updates ownership and restores the authoritative
active flag before `Spawned`. Ownership updates do not reset the transform to the spawn pose.

Unity can invoke `Awake`/initial `OnEnable` during an active prefab's instantiation before the
SDK receives that instance. Those callbacks must not assume networking is initialized.
Use `Initializing`/`Spawned` for network-dependent initialization, or have the loader return
an inactive instance. This change is not a guarantee that Unity never invokes pre-bind callbacks.

The Unity transport continues to deliver through `UnityNetRpcDispatcher.Pump`.
The adapter implements Core's optional `INetworkObjectDispatcher`; loader completions and
application hooks also explicitly return to the session thread, including failure paths.
All synchronous engine actions assert that thread. Continue pumping during pending work;
never synchronously wait on asynchronous registration from the main thread.
Keep the session/Pump owner outside network-managed GameObjects that can be deactivated
during initialization or despawn, so the object lifecycle cannot suspend its own dispatcher.

Cancellation is not permission to destroy engine objects from a worker thread. A late
loader result is returned to the main thread and released. If the session queue has closed
or is full, the dispatcher uses the Unity synchronization context captured when it was
created. If no usable context exists, it fails explicitly rather than executing engine work
off-thread. This fallback and domain-reload behavior still require actual Unity validation.
Loader implementations must themselves observe Unity's threading rules and honor cancellation.

## Wire compatibility and rollout

This is a wire-breaking object-protocol migration. The old Unity-owned RPC target and DTO
identities have been replaced by Core's built-in `INetworkObjectProtocol` descriptors and
`BITKit.Multiplayer.NetRpc.NetworkObjectState`/`NetworkObjectSnapshot` types. The new protocol
also requests an initial entity-state manifest before Client `Spawned`.

Retained field names/values and obsolete Unity DTOs do not make old and new peers compatible.
No compatibility receiver or dual-protocol shim is installed. Upgrade and rebuild Core,
Unity adapters, other engine adapters, Host and all Clients together; reconnect into a new
world/runtime scope. Do not perform an old/new rolling peer deployment or restore the old
Unity target registration alongside the Core service. The existing pre-release package
version alone does not establish wire compatibility; pin matching source revisions.

## Verification and remaining acceptance

Performed here: static contract review against the new Core source, engine-boundary/thread
review, verification that the Unity facade has no object RPC/revision/ID/roster implementation,
and `git diff --check`. Core's separate .NET tests are evidence about Core only.

Not run here: Unity import/compilation, actual Unity ILPP, Edit Mode tests, Play Mode,
domain reload, two Unity processes, Player/IL2CPP/AOT or engine GC measurement. No fake
UnityEngine-stub build is offered as Unity evidence. The Core constructor self-registers the
built-in protocol; the Unity facade no longer needs object-protocol weaving.

Next actual Unity acceptance should cover prefab spawn/despawn and late join, owner change
without pose reset, scene-before-roster and roster-before-scene, async initialization failures,
cancellation/late loader release, state-ready-before-Spawned, realm teardown, main-thread
assertions, queue saturation and Editor/domain-reload cleanup. Run the target Unity project's
own AGENTS/overlay/MCP workflow and report its precise Editor/Player/backend separately.
