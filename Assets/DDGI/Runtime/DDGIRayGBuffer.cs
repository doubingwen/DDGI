using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Dou.DDGI
{
    public sealed class DDGIRayGBuffer : IDisposable
    {
        public const int DefaultRaysPerProbe = 64;

        public static readonly int PositionTextureId = Shader.PropertyToID("_DDGI_RayPositionTexture");
        public static readonly int NormalTextureId = Shader.PropertyToID("_DDGI_RayNormalTexture");
        public static readonly int AlbedoTextureId = Shader.PropertyToID("_DDGI_RayAlbedoTexture");
        public static readonly int DistanceMomentsTextureId = Shader.PropertyToID("_DDGI_RayDistanceMomentsTexture");

        public int RayCount { get; private set; }
        public int ProbeCount { get; private set; }
        public RenderTexture PositionTexture { get; private set; }
        public RenderTexture NormalTexture { get; private set; }
        public RenderTexture AlbedoTexture { get; private set; }
        public RenderTexture DistanceMomentsTexture { get; private set; }

        public bool IsCreated =>
            PositionTexture != null && PositionTexture.IsCreated() &&
            NormalTexture != null && NormalTexture.IsCreated() &&
            AlbedoTexture != null && AlbedoTexture.IsCreated() &&
            DistanceMomentsTexture != null && DistanceMomentsTexture.IsCreated();
        //确保三张 Ray G-Buffer 已按照指定的 Probe 数量和射线数量正确创建。
        public bool EnsureCreated(int probeCount, int rayCount = DefaultRaysPerProbe)
        {
            ValidateDimensions(probeCount, rayCount);

            if (IsCreated && ProbeCount == probeCount && RayCount == rayCount)
                return false;

            Release();

            ProbeCount = probeCount;
            RayCount = rayCount;
            PositionTexture = CreateTexture(
                "DDGI Ray GBuffer - Position",
                rayCount,
                probeCount,
                GraphicsFormat.R32G32B32A32_SFloat);
            NormalTexture = CreateTexture(
                "DDGI Ray GBuffer - Normal",
                rayCount,
                probeCount,
                GraphicsFormat.R16G16B16A16_SFloat);
            AlbedoTexture = CreateTexture(
                "DDGI Ray GBuffer - Albedo",
                rayCount,
                probeCount,
                GraphicsFormat.R8G8B8A8_UNorm);
            DistanceMomentsTexture = CreateTexture(
                "DDGI Ray GBuffer - Distance Moments",
                rayCount,
                probeCount,
                GraphicsFormat.R32G32_SFloat);

            if (!IsCreated)
            {
                Release();
                throw new InvalidOperationException("Failed to create the DDGI ray G-Buffer textures.");
            }

            return true;
        }
        //纹理清除数据
        public void Clear(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));
            if (!IsCreated)
                return;

            ClearTexture(commandBuffer, PositionTexture);
            ClearTexture(commandBuffer, NormalTexture);
            ClearTexture(commandBuffer, AlbedoTexture);
            ClearTexture(commandBuffer, DistanceMomentsTexture);
        }
        //shader传递纹理
        public void BindGlobals(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));
            if (!IsCreated)
                return;

            commandBuffer.SetGlobalTexture(PositionTextureId, PositionTexture);
            commandBuffer.SetGlobalTexture(NormalTextureId, NormalTexture);
            commandBuffer.SetGlobalTexture(AlbedoTextureId, AlbedoTexture);
            commandBuffer.SetGlobalTexture(DistanceMomentsTextureId, DistanceMomentsTexture);
        }
        //释放纹理内存
        public void Release()
        {
            ReleaseTexture(PositionTexture);
            ReleaseTexture(NormalTexture);
            ReleaseTexture(AlbedoTexture);
            ReleaseTexture(DistanceMomentsTexture);
            PositionTexture = null;
            NormalTexture = null;
            AlbedoTexture = null;
            DistanceMomentsTexture = null;
            RayCount = 0;
            ProbeCount = 0;
        }

        public void Dispose()
        {
            Release();
        }

        static RenderTexture CreateTexture(
            string textureName,
            int width,
            int height,
            GraphicsFormat format)
        {
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore))
                throw new NotSupportedException($"The graphics device does not support UAV writes to {format}.");

            var descriptor = new RenderTextureDescriptor(
                width,
                height,
                format,
                GraphicsFormat.None)
            {
                dimension = TextureDimension.Tex2D,
                volumeDepth = 1,
                msaaSamples = 1,
                mipCount = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = true
            };

            var texture = new RenderTexture(descriptor)
            {
                name = textureName,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            texture.Create();
            return texture;
        }

        static void ClearTexture(CommandBuffer commandBuffer, RenderTexture texture)
        {
            commandBuffer.SetRenderTarget(texture);
            commandBuffer.ClearRenderTarget(false, true, Color.clear);
        }

        static void ReleaseTexture(RenderTexture texture)
        {
            if (texture == null)
                return;

            texture.Release();
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(texture);
            else
                UnityEngine.Object.DestroyImmediate(texture);
        }
        //验证是否正确
        static void ValidateDimensions(int probeCount, int rayCount)
        {
            if (probeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(probeCount));
            if (rayCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(rayCount));
            if (probeCount > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(probeCount), "Probe count exceeds the maximum texture height.");
            if (rayCount > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(rayCount), "Ray count exceeds the maximum texture width.");
        }
    }
}
