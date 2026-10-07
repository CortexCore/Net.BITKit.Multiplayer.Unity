# BITKit Multiplayer Unity

Unity 对象与主线程适配扩展，依赖 [主网络库](https://github.com/CortexCore/Net.BITKit.Multiplayer)。

- `UnityNetRpcAdapter` 包装已接入房间的连接，通过主线程 `Pump` 处理 RPC 和发布状态。
- `NetworkIdentity` / `SceneIdentity` 提供 Prefab 和场景对象身份。
- `UnityNetworkObjects` 管理初始对象快照、生成、Owner 和销毁；持续业务状态使用主库 NetEntity/NetComponent。
- 普通 RPC 对象按标准 AddSingleton 注册，只标记 Rpc；编织由主库的唯一 NetRpc ILPP 自动完成。

包根为 `Src/`，Git UPM 地址：

```text
https://github.com/CortexCore/Net.BITKit.Multiplayer.Unity.git?path=/Src
```

宿主需要安装主库、UniTask、MessagePack 和 DI 依赖。实际 API／示例与验证范围见主库 README、
Docs/unity-integration-plan.md 和 Docs/legacy-backend-removal.md。
