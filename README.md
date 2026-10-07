# BITKit Multiplayer Unity

Unity 2022.3 的 NetRpc 主线程、身份组件与网络对象生命周期适配层。UPM 包目录为 `Src/`；使用与本仓库对象协议匹配的 Core 源码及 UniTask 2.5.10。

## Unity 对象同步怎么接

Core `NetworkObjectService` 管理对象 ID、生成/销毁、Owner、名册、迟加入及初始化状态；`UnityNetworkObjects` 负责 GameObject、Prefab 加载、场景身份和主线程接入。Host 是唯一权威端，不包含隐式的本地 Client。

1. 按已有 DI/会话流程建立 `RpcContextService` 和 `UnityNetRpcAdapter`，使用已附加到 `Runtime.Entities` 的同一个 `IEntitiesService`。每个世界使用一致的非零 `worldGeneration`；Host 的 `localPeerId` 为 1，Client 使用不同的非零、非 1 ID。
2. 动态 Prefab 挂 `NetworkIdentity` 并填写 `PrefabAddress`。Client 的 `INetworkPrefabLoader.InstantiateAsync` 按该地址创建对象；两端回收 Prefab 都经过 `Release`，因此 loader 也必须能释放 Host 直接实例化的对象。场景内已有对象挂 `SceneIdentity`，两端使用相同的已保存场景和唯一 `NameKey`。
3. 两端均附加对象服务，再安装初始化回调。需要同步业务组件时，在 `Initializing` 创建应用自己的 `NetEntity` 并 `AttachEntity`，不要再次手工注册同一实体。纯展示对象可在两端都省略实体。

```csharp
// adapter、entities、prefabLoader 和 token 来自应用已有的会话。
var objects = adapter.AttachNetworkObjects(
    worldGeneration, localPeerId, prefabLoader, entities,
    registerLoadedSceneObjects: false);

objects.Initializing += (handle, cancellationToken) =>
{
    // 应用自己的工厂，不是 SDK 方法。Host/Client 接入相同组件集；
    // 实体的 ServiceProvider 必须解析到 handle.Identity 对应的 INetworkIdentity。
    NetEntity entity = CreateApplicationEntity(handle, cancellationToken);
    handle.AttachEntity(entity);
    return UniTask.CompletedTask;
};

await objects.RegisterLoadedSceneObjectsAsync(token);
// 连接已就绪后，由 Client 主动拉取已有对象名册。
if (!objects.IsAuthority)
    await objects.SynchronizeAsync(token);
```

4. 只有 Host 调用以下接口；在业务的生成、转交和回收时分别调用，不需要 Client 重复执行：

```csharp
var handle = await objects.SpawnAsync(
    prefab, position, rotation, ownerPeerId, token);

objects.SetOwner(handle.EntityId, nextOwnerPeerId);

await objects.DespawnAsync(handle.EntityId, token);
```

Client 收到名册后自动加载/绑定对象，注册实体并请求可靠的完整初始组件状态，成功后才触发 `Spawned`。`Spawned` 表示本端该对象就绪；Host 的 `SpawnAsync` / `DespawnAsync` 完成不表示所有 Client 已确认。运行中加载的新场景需再次注册，并重试待解析对象；可调用 `RegisterLoadedSceneObjectsAsync`，或注册后调用 `RetryPendingAsync`。

### 状态、线程和退出边界

- 对象层设置初始位置/旋转、Owner 和激活状态，不自动逐帧同步 Transform。移动、旋转、血量等后续数据继续走已有 NetRpc/组件状态同步，并由业务代码应用到 Unity；Owner 变更不会把位置重置到出生点。
- `OwnerPeerId` / `Owns` 表示控制归属，不自动给 Client 写状态或执行输入 RPC 的权限。输入鉴权仍由 Host 业务处理。
- 在 Unity 主线程持续调用 `adapter.Pump(unscaledSeconds)`；对象修改、附加/分离和销毁也在该线程执行。不要阻塞主线程等待异步初始化。Pump 所在对象不能随网络对象一起被停用。
- 活跃 Prefab 实例化时 Unity 可能先调用 `Awake` / `OnEnable`；网络相关逻辑放在 `Initializing` / `Spawned`，或让 loader 返回未激活对象。
- 世界结束时在主线程 `DetachNetworkObjects()`，或随会话 `adapter.Dispose()` 清理；新世界需新 Runtime/传输 scope。应用负责自己的订阅与 DI scope 清理，也要覆盖初始化失败。取消后仍可能有迟到加载结果，须让主线程调度继续可用以完成释放；`adapter.Disposal` 是连接/出站任务屏障，不是所有资源加载的完成屏障。

## 迁移与验证范围

本次对象协议与旧 Unity-only 实现不兼容。Core、Unity/Godot 适配、Host 和所有 Client 必须一起更新并重新构建；固定匹配的源码修订，不要仅凭仍为 `0.1.0` 的包版本判断兼容。

本轮仅完成源码/API、主线程边界静态检查及 `git diff --check`，没有 Unity Editor 编译、Edit Mode、Player 或 IL2CPP 验证。保留接口、行为变化和后续验收清单见 [Core 对象协议迁移说明](Docs/network-object-core-migration.md)。
