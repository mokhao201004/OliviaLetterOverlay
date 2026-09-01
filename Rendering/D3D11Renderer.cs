using System.Runtime.InteropServices;
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OliviaLetterOverlay.Rendering;

/// <summary>
/// WallpaperRenderWindow 的唯一视频输出路径。
/// 视频纹理、黑场和 FadeFactor 都在此 D3D11 Renderer 内完成，不创建独立视频 HWND。
/// </summary>
internal sealed class D3D11Renderer : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11Texture2D? _videoTexture;
    private ID3D11ShaderResourceView? _videoView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _fadeBuffer;
    private int _videoWidth;
    private int _videoHeight;
    private bool _initialized;
    private bool _disposed;
    private float _fadeFactor;
    private uint _swapWidth;
    private uint _swapHeight;
    private int _presentCount;
    private long _renderCallCount;
    private long _presentCallCount;
    private double _lastPresentMilliseconds;
    private double _maxPresentMilliseconds;

    public float FadeFactor
    {
        get => _fadeFactor;
        set => _fadeFactor = Math.Clamp(value, 0, 1);
    }

    public bool IsInitialized => _initialized;
    public double LastPresentMilliseconds => _lastPresentMilliseconds;
    public double MaxPresentMilliseconds => _maxPresentMilliseconds;
    public long RenderCallCount => Interlocked.Read(ref _renderCallCount);
    public long PresentCallCount => Interlocked.Read(ref _presentCallCount);
    public int RenderWidth => checked((int)_swapWidth);
    public int RenderHeight => checked((int)_swapHeight);

    public void Initialize(IntPtr hwnd, int width, int height)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            Resize(width, height);
            return;
        }

        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels);
        _context = _device.ImmediateContext;
        var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        try
        {
            var description = new SwapChainDescription1(
                (uint)Math.Max(1, width), (uint)Math.Max(1, height), Format.B8G8R8A8_UNorm,
                false, Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.Discard,
                AlphaMode.Ignore, SwapChainFlags.None);
            _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, description, null, null);
            _swapWidth = description.Width;
            _swapHeight = description.Height;
        }
        finally
        {
            factory.Dispose();
        }

        CreateRenderTarget();
        CreateShaders();
        _initialized = true;
        DiagnosticLog.Write("renderer", $"D3D11 initialized hwnd=0x{hwnd.ToInt64():X} size={width}x{height} renderer=D3D11 swapchain=Format.B8G8R8A8_UNorm texture=Format.B8G8R8A8_UNorm shader=direct-rgb fade-in-shader");
        Render();
    }

    public void Resize(int width, int height)
    {
        if (!_initialized || _swapChain is null || _context is null)
        {
            return;
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_swapWidth == width && _swapHeight == height)
        {
            return;
        }

        _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        _renderTarget?.Dispose();
        _renderTarget = null;
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        _swapWidth = (uint)width;
        _swapHeight = (uint)height;
        CreateRenderTarget();
        Render();
    }

    public void PresentFrame(byte[] pixels, int width, int height)
    {
        ThrowIfDisposed();
        if (!_initialized || _context is null || _swapChain is null)
        {
            return;
        }

        EnsureVideoTexture(width, height);
        if (_videoTexture is not null)
        {
            var mapped = _context.Map(_videoTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            var sourceStride = checked(width * 4);
            var rowBytes = Math.Min(sourceStride, checked((int)mapped.RowPitch));
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * sourceStride, IntPtr.Add(mapped.DataPointer, checked((int)(y * mapped.RowPitch))), rowBytes);
            }

            _context.Unmap(_videoTexture, 0);
        }

        Render();
    }

    public void RenderBlack()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            Render();
        }
    }

    private void Render()
    {
        if (!_initialized || _context is null || _renderTarget is null || _swapChain is null)
        {
            return;
        }

        Interlocked.Increment(ref _renderCallCount);

        var clear = new Color4(0, 0, 0, 1);
        _context.ClearRenderTargetView(_renderTarget, clear);
        _context.OMSetRenderTargets(_renderTarget, null);
        _context.RSSetViewport(0, 0, _swapWidth, _swapHeight, 0, 1);

        if (_videoView is not null && _vertexShader is not null && _pixelShader is not null && _sampler is not null && _fadeBuffer is not null && _fadeFactor > 0.001f)
        {
            var mapped = _context.Map(_fadeBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            var constants = new[]
            {
                BitConverter.SingleToInt32Bits(_fadeFactor),
                BitConverter.SingleToInt32Bits(_videoWidth),
                BitConverter.SingleToInt32Bits(_videoHeight),
                BitConverter.SingleToInt32Bits(_swapWidth),
                BitConverter.SingleToInt32Bits(_swapHeight),
                0,
                0,
                0,
            };
            Marshal.Copy(constants, 0, mapped.DataPointer, constants.Length);
            _context.Unmap(_fadeBuffer, 0);
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetShaderResources(0, new[] { _videoView });
            _context.PSSetSamplers(0, new[] { _sampler });
            _context.PSSetConstantBuffers(0, new[] { _fadeBuffer });
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(3, 0);
        }

        var presentStart = Stopwatch.GetTimestamp();
        // The scheduler already paces frames against the audio clock.  Avoid
        // blocking the UI/render callback on a second VSync wait; DWM will
        // compose this child swap chain with the desktop normally.
        var presentResult = _swapChain.Present(0, PresentFlags.None);
        Interlocked.Increment(ref _presentCallCount);
        _lastPresentMilliseconds = Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds;
        _maxPresentMilliseconds = Math.Max(_maxPresentMilliseconds, _lastPresentMilliseconds);
        if (_presentCount++ == 0 || presentResult.Failure || _lastPresentMilliseconds >= 8)
        {
            DiagnosticLog.Write("renderer", $"Present result={presentResult} hwnd-parented=true fade={_fadeFactor:0.###} PresentTimeMs={_lastPresentMilliseconds:0.###}");
        }
    }

    private void CreateRenderTarget()
    {
        if (_swapChain is null || _device is null)
        {
            return;
        }

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = _device.CreateRenderTargetView(backBuffer, null);
    }

    private void CreateShaders()
    {
        if (_device is null)
        {
            return;
        }

        const string vertexSource = """
            struct VSOut { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            VSOut main(uint id : SV_VertexID) {
                float2 positions[3] = { float2(-1,-1), float2(-1,3), float2(3,-1) };
                float2 uvs[3] = { float2(0,1), float2(0,-1), float2(2,1) };
                VSOut o; o.position = float4(positions[id],0,1); o.uv = uvs[id]; return o;
            }
            """;
        const string pixelSource = """
            Texture2D videoTexture : register(t0);
            SamplerState linearSampler : register(s0);
            cbuffer FadeParams : register(b0) {
                float fadeFactor;
                float videoWidth;
                float videoHeight;
                float targetWidth;
                float targetHeight;
                float3 padding;
            };
            float4 main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
                float videoAspect = videoWidth / max(videoHeight, 1.0);
                float targetAspect = targetWidth / max(targetHeight, 1.0);
                if (targetAspect > videoAspect) {
                    float visibleHeight = videoAspect / targetAspect;
                    uv.y = (uv.y - 0.5) * visibleHeight + 0.5;
                } else {
                    float visibleWidth = targetAspect / videoAspect;
                    uv.x = (uv.x - 0.5) * visibleWidth + 0.5;
                }
                float4 color = videoTexture.Sample(linearSampler, uv);
                return float4(color.rgb * fadeFactor, 1.0);
            }
            """;

        var vertexBytecode = Compiler.Compile(vertexSource, "main", "wallpaper.hlsl", "vs_5_0", ShaderFlags.None, EffectFlags.None);
        var pixelBytecode = Compiler.Compile(pixelSource, "main", "wallpaper.hlsl", "ps_5_0", ShaderFlags.None, EffectFlags.None);
        _vertexShader = _device.CreateVertexShader(vertexBytecode.Span, null);
        _pixelShader = _device.CreatePixelShader(pixelBytecode.Span, null);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        var bufferDescription = new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        _fadeBuffer = _device.CreateBuffer(bufferDescription);
    }

    private void EnsureVideoTexture(int width, int height)
    {
        if (_device is null || (_videoTexture is not null && width == _videoWidth && height == _videoHeight))
        {
            return;
        }

        _videoView?.Dispose();
        _videoTexture?.Dispose();
        _videoView = null;
        _videoTexture = null;
        var description = new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write, 1, 0, ResourceOptionFlags.None);
        _videoTexture = _device.CreateTexture2D(in description);
        _videoView = _device.CreateShaderResourceView(_videoTexture, null);
        _videoWidth = width;
        _videoHeight = height;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fadeBuffer?.Dispose();
        _sampler?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _videoView?.Dispose();
        _videoTexture?.Dispose();
        _renderTarget?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11Renderer));
        }
    }
}
