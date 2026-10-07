# BITKit Multiplayer Unity

- Independent Unity extension; runtime/transport authority lives in the sibling Net.BITKit.Multiplayer repository.
- Read the main library's current architecture and Docs/unity-integration-plan.md before integration changes.
- Ordinary Rpc objects use AddSingleton. The single NetRpc ILPP processes Rpc automatically; no backend selector or legacy runtime.
- Keep runtime code in Src/NetRpc, Editor-only code in Src/Editor and probe code in Src/Probes, with existing asmdef isolation.
- Maintain main-thread Adapter.Pump and safe disposal/connection ownership. Network-object lifecycle and continuous component state are separate.
- Preserve Unity GUIDs and unrelated work. Use apply_patch; commit/push only on explicit request.
- For Unity validation, follow the target host's instructions and MCP import/compile/readback. Do not infer Editor/Player success from .NET tests.
