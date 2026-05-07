using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Saivs.Graphics.Core.MDI
{
    // CommandBuffer.MultiDrawMeshIndirect — vertex data comes from the user's
    // Mesh through the input assembler, so the shader can use the standard
    // POSITION / NORMAL / TEXCOORD0 semantics. True MDI is currently routed
    // to the native plugin only on backends whose MDI.hlsl branch resolves
    // the global instance ID through SV_InstanceID — Metal, Vulkan and WebGPU.
    // On D3D11/D3D12/OpenGL the call falls back to a per-draw
    // DrawMeshInstancedIndirect loop (the user shader would otherwise need a
    // TEXCOORD7 stream which user meshes don't carry) and emits a one-time warning.
    public static partial class MultiDrawIndirect
    {
        // -----------------------------------------------------------------------
        // Mesh-only state
        // -----------------------------------------------------------------------

        // Mesh instance IDs whose `indexBufferTarget` we've already augmented
        // with Raw, so `mesh.GetIndexBuffer()` returns a buffer the native
        // plugin can address.
        private static readonly HashSet<int> _meshesPrepared = new HashSet<int>();
        private static bool _meshFallbackWarned;

        // True when the current backend's MDI.hlsl branch routes the global
        // instance ID through SV_InstanceID. These backends accept arbitrary
        // user meshes because the shader has no TEXCOORD7 input requirement.
        private static bool MeshApiSupportedNatively
        {
            get
            {
                var api = SystemInfo.graphicsDeviceType;
                return api == GraphicsDeviceType.Metal
                    || api == GraphicsDeviceType.Vulkan
                    || api == GraphicsDeviceType.WebGPU;
            }
        }

        static partial void DisposeMeshState()
        {
            _meshesPrepared.Clear();
            _meshFallbackWarned = false;
        }

        // Index format codes — must match the values consumed by native backends.
        // 0 = UInt16, 1 = UInt32.
        private static uint EncodeIndexFormat(IndexFormat fmt)
            => fmt == IndexFormat.UInt16 ? 0u : 1u;

        private static GraphicsBuffer EnsureMeshIndexBuffer(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (!_meshesPrepared.Contains(id))
            {
                mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
                _meshesPrepared.Add(id);
            }
            return mesh.GetIndexBuffer();
        }

        private static void WarnMeshFallbackOnce()
        {
            if (_meshFallbackWarned) return;
            _meshFallbackWarned = true;
            Debug.LogWarning(
                $"[MDI] MultiDrawMeshIndirect: true MDI is currently supported only on " +
                $"Metal, Vulkan and WebGPU. Current backend ({SystemInfo.graphicsDeviceType}) " +
                $"falls back to a per-draw DrawMeshInstancedIndirect loop, and the user shader " +
                $"must not require TEXCOORD7 in its vertex input layout (i.e. should not include " +
                $"the default MDI_INSTANCE_ID_PARAMETER macro from MDI.hlsl on these APIs).");
        }

        // -----------------------------------------------------------------------
        // CommandBuffer extension
        // -----------------------------------------------------------------------
        public static void MultiDrawMeshIndirect(
            this CommandBuffer cmd,
            Mesh mesh,
            Material material,
            MaterialPropertyBlock properties,
            int shaderPass,
            GraphicsBuffer bufferWithArgs,
            int argsStartIndex,
            int argsCount)
        {
            EnsureInitialized();

            if (_supported && argsCount > 1 && MeshApiSupportedNatively)
            {
                var meshIndexBuffer = EnsureMeshIndexBuffer(mesh);
                IntPtr dataPtr = WriteParams(
                    bufferWithArgs, meshIndexBuffer, argsStartIndex, argsCount,
                    mesh.GetTopology(0),
                    EncodeIndexFormat(mesh.indexFormat),
                    out int slot);

                cmd.DrawMeshInstancedIndirect(mesh, 0, material, shaderPass,
                    _dummyArgsBuffer, slot * INDIRECT_DRAW_INDEXED_ARGS_SIZE, properties);

                cmd.IssuePluginEventAndData(_renderEventAndDataFunc, _baseEventID + slot, dataPtr);
            }
            else
            {
                if (_supported && !MeshApiSupportedNatively) WarnMeshFallbackOnce();
                for (int i = 0; i < argsCount; i++)
                {
                    cmd.DrawMeshInstancedIndirect(mesh, 0, material, shaderPass,
                        bufferWithArgs, (argsStartIndex + i) * INDIRECT_DRAW_INDEXED_ARGS_SIZE, properties);
                }
            }
        }
    }
}
