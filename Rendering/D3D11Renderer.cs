using System.Runtime.InteropServices;
using System.Diagnostics;
using SharpGen.Runtime;
using Vortice;
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
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _videoProcessorEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView? _videoProcessorOutputView;
    private ID3D11Texture2D? _videoProcessorInputCopyTexture;
    private int _videoProcessorInputCopyWidth;
    private int _videoProcessorInputCopyHeight;
    private int _videoProcessorInputWidth;
    private int _videoProcessorInputHeight;
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
    private bool _presentSlowLogged;
    private bool _decoderTextureAdapterLogged;
    private bool _videoProcessorInputViewFailureLogged;
    private bool _videoProcessorInputViewReadyLogged;
    private bool _videoProcessorCopyLogged;
    private bool _videoProcessorFrameLogged;
    private int _immediateContextThreadId;

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
    internal ID3D11Device? Device => _device;

    public void Initialize(IntPtr hwnd, int width, int height)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            Resize(width, height);
            return;
        }

        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, featureLevels);
        _context = _device.ImmediateContext;
        _immediateContextThreadId = Environment.CurrentManagedThreadId;
        TryInitializeVideoProcessorInterfaces();
        LogDeviceDiagnostics();
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

    private void LogDeviceDiagnostics()
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var description = adapter.Description;
            var videoSupportRequested = (_device.CreationFlags & DeviceCreationFlags.VideoSupport) != 0;
            DiagnosticLog.Write("VIDEO_DECODER", $"RenderAdapter={description.Description} AdapterVendor=0x{description.VendorId:X4} AdapterDevice=0x{description.DeviceId:X4} AdapterLuid={description.Luid} FeatureLevel={_device.FeatureLevel} VideoSupport={videoSupportRequested} D3D11CreateFlags={_device.CreationFlags}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("VIDEO_DECODER", $"RenderAdapterProbe=failed type={exception.GetType().Name} message={exception.Message}");
        }
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

        VerifyImmediateContextThread("Resize");
        _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        _renderTarget?.Dispose();
        _renderTarget = null;
        _videoProcessorOutputView?.Dispose();
        _videoProcessorOutputView = null;
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

        VerifyImmediateContextThread("PresentFrame");
        ThreadCpuDiagnostics.MarkWakeup("Olivia.Render");
        GpuPipelineDiagnostics.MarkProgress("Olivia.Render", "PresentFrame");
        using var renderActivity = ThreadCpuDiagnostics.StartActivity("Olivia.Render");
        EnsureVideoTexture(width, height);
        if (_videoTexture is not null)
        {
            var mapped = _context.Map(_videoTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            var sourceStride = checked(width * 4);
            var destinationStride = checked((int)mapped.RowPitch);
            if (destinationStride == sourceStride)
            {
                // The dynamic texture is normally tightly packed for the
                // negotiated RGB32 size.  Use one native copy in that case;
                // keep the row-by-row fallback for drivers that add padding.
                Marshal.Copy(pixels, 0, mapped.DataPointer, checked(sourceStride * height));
            }
            else
            {
                var rowBytes = Math.Min(sourceStride, destinationStride);
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * sourceStride, IntPtr.Add(mapped.DataPointer, checked(y * destinationStride)), rowBytes);
                }
            }

            _context.Unmap(_videoTexture, 0);
        }

        Render();
    }

    /// <summary>
    /// Present a decoder-owned GPU surface through the D3D11 video processor.
    /// The decoder texture remains owned by the caller until this call returns.
    /// </summary>
    public bool PresentGpuFrame(ID3D11Texture2D texture, uint subresourceIndex, int width, int height)
    {
        ThrowIfDisposed();
        if (!_initialized || _device is null || _context is null || _swapChain is null
            || _renderTarget is null || texture is null || width <= 0 || height <= 0)
        {
            return false;
        }

        VerifyImmediateContextThread("PresentGpuFrame");
        ThreadCpuDiagnostics.MarkWakeup("Olivia.Render");
        GpuPipelineDiagnostics.MarkProgress("Olivia.Render", "PresentGpuFrame");
        using var renderActivity = ThreadCpuDiagnostics.StartActivity("Olivia.Render");
        var frameId = GpuPipelineDiagnostics.NextFrameId();
        GpuPipelineDiagnostics.MarkProgress("Olivia.Render", "PresentGpuFrame", frameId);
        if (!EnsureVideoProcessor(width, height))
        {
            return false;
        }

        try
        {
            try
            {
                using var textureDevice = texture.Device;
                if (textureDevice is not null)
                {
                    using var textureDxgiDevice = textureDevice.QueryInterface<IDXGIDevice>();
                    using var textureAdapter = textureDxgiDevice.GetAdapter();
                    if (!_decoderTextureAdapterLogged)
                    {
                        _decoderTextureAdapterLogged = true;
                        DiagnosticLog.Write("VIDEO_PIPELINE", $"DecoderTextureAdapter={textureAdapter.Description.Description} Luid={textureAdapter.Description.Luid}");
                    }
                }
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("VIDEO_PIPELINE", $"DecoderTextureAdapterProbe=failed type={exception.GetType().Name} message={exception.Message}");
            }

            ID3D11VideoProcessorInputView? inputView = CreateVideoProcessorInputView(texture, subresourceIndex, frameId);
            ID3D11Texture2D? inputCopyTexture = null;
            try
            {
                if (inputView is null)
                {
                    inputCopyTexture = EnsureVideoProcessorInputCopy(width, height);
                    if (inputCopyTexture is not null && _context is not null)
                    {
                        using var sourceResource = texture.QueryInterface<ID3D11Resource>();
                        using var destinationResource = inputCopyTexture.QueryInterface<ID3D11Resource>();
                        using (GpuPipelineDiagnostics.Begin("Olivia.Render", "CopySubresourceRegion", frameId))
                        {
                            _context.CopySubresourceRegion(destinationResource, 0, 0, 0, 0, sourceResource, subresourceIndex, null);
                        }
                        if (!_videoProcessorCopyLogged)
                        {
                            _videoProcessorCopyLogged = true;
                            DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessorInputCopy=GPU subresource={subresourceIndex}");
                        }
                        inputView = CreateVideoProcessorInputView(inputCopyTexture, 0, frameId);
                    }
                }

                if (inputView is null || _videoProcessor is null || _videoProcessorOutputView is null || _videoContext is null)
                {
                    DiagnosticLog.Write("renderer", $"VideoProcessor input setup failed inputView={(inputView is not null)} processor={(_videoProcessor is not null)} outputView={(_videoProcessorOutputView is not null)} context={(_videoContext is not null)}");
                    return false;
                }

                _context!.ClearRenderTargetView(_renderTarget!, new Color4(0, 0, 0, 1));
                _context.OMSetRenderTargets(_renderTarget!, null);
                _context.RSSetViewport(0, 0, _swapWidth, _swapHeight, 0, 1);

                var sourceRect = new RawRect(0, 0, width, height);
                var targetRect = CalculateAspectFitRect(width, height, (int)_swapWidth, (int)_swapHeight);
                var firstVideoProcessorFrame = !_videoProcessorFrameLogged;
                if (firstVideoProcessorFrame)
                {
                    _videoProcessorFrameLogged = true;
                    DiagnosticLog.Write("renderer", $"VideoProcessor configure begin source={sourceRect} target={targetRect} fade={_fadeFactor:0.###}");
                }
                _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);
                _videoContext.VideoProcessorSetStreamSourceRect(_videoProcessor, 0, true, sourceRect);
                _videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, 0, true, targetRect);
                _videoContext.VideoProcessorSetStreamAlpha(_videoProcessor, 0, true, _fadeFactor);
                _videoContext.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, new VideoProcessorColorSpace
                {
                    Usage = 0,
                    RGB_Range = 0,
                    YCbCr_Matrix = 1,
                    YCbCr_xvYCC = 0,
                    Nominal_Range = 1,
                    Reserved = 0,
                });
                _videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace
                {
                    Usage = 0,
                    RGB_Range = 0,
                    YCbCr_Matrix = 0,
                    YCbCr_xvYCC = 0,
                    Nominal_Range = 0,
                    Reserved = 0,
                });

                var stream = new VideoProcessorStream
                {
                    Enable = true,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    InputSurface = inputView,
                };
                Result result;
                using (GpuPipelineDiagnostics.Begin("Olivia.Render", "VideoProcessorBlt", frameId))
                {
                    result = _videoContext.VideoProcessorBlt(_videoProcessor, _videoProcessorOutputView, 0, new[] { stream });
                }
                if (result.Failure || firstVideoProcessorFrame)
                {
                    DiagnosticLog.Write("renderer", $"VideoProcessorBlt result={result}");
                }
                if (result.Failure)
                {
                    DiagnosticLog.Write("renderer", $"VideoProcessorBlt failed result={result} input={width}x{height} format={texture.Description.Format}");
                    return false;
                }

                PresentSwapChain(frameId);
                return true;
            }
            finally
            {
                if (inputView is not null)
                {
                    GpuPipelineDiagnostics.InputViewReleased();
                    using var releaseViewStage = GpuPipelineDiagnostics.Begin("Olivia.Render", "ReleaseVideoProcessorInputView", frameId);
                    inputView.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("renderer", $"VideoProcessor frame failed type={exception.GetType().Name} message={exception.Message}");
            return false;
        }
    }

    public void RenderBlack()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            ThreadCpuDiagnostics.MarkWakeup("Olivia.Render");
            using var renderActivity = ThreadCpuDiagnostics.StartActivity("Olivia.Render");
            Render();
        }
    }

    private void Render()
    {
        if (!_initialized || _context is null || _renderTarget is null || _swapChain is null)
        {
            return;
        }

        VerifyImmediateContextThread("Render");
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

        PresentSwapChain();
    }

    private void VerifyImmediateContextThread(string stage)
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        var ownerThreadId = Volatile.Read(ref _immediateContextThreadId);
        if (ownerThreadId == 0)
        {
            Interlocked.CompareExchange(ref _immediateContextThreadId, currentThreadId, 0);
            ownerThreadId = Volatile.Read(ref _immediateContextThreadId);
        }

        if (ownerThreadId != currentThreadId)
        {
            DiagnosticLog.Write("VIDEO_PIPELINE", $"IMMEDIATE_CONTEXT_CROSS_THREAD stage={stage} ownerThreadId={ownerThreadId} currentThreadId={currentThreadId}");
        }
    }

    private void PresentSwapChain(long frameId = 0)
    {
        if (_swapChain is null)
        {
            return;
        }

        if (frameId == 0)
        {
            frameId = GpuPipelineDiagnostics.NextFrameId();
        }
        GpuPipelineDiagnostics.MarkProgress("Olivia.Render", "Present", frameId);
        var presentStart = Stopwatch.GetTimestamp();
        // The scheduler already paces frames against the audio clock.  Avoid
        // blocking the UI/render callback on a second VSync wait; DWM will
        // compose this child swap chain with the desktop normally.
        Result presentResult;
        using (GpuPipelineDiagnostics.Begin("Olivia.Render", "Present", frameId))
        {
            presentResult = _swapChain.Present(0, PresentFlags.None);
        }
        Interlocked.Increment(ref _presentCallCount);
        _lastPresentMilliseconds = Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds;
        _maxPresentMilliseconds = Math.Max(_maxPresentMilliseconds, _lastPresentMilliseconds);
        var presentSlow = _lastPresentMilliseconds >= 8;
        if (!presentSlow)
        {
            _presentSlowLogged = false;
        }
        if (_presentCount++ == 0 || presentResult.Failure || (presentSlow && !_presentSlowLogged))
        {
            _presentSlowLogged = presentSlow;
            DiagnosticLog.Write("renderer", $"Present result={presentResult} hwnd-parented=true fade={_fadeFactor:0.###} PresentTimeMs={_lastPresentMilliseconds:0.###}");
        }
    }

    private void TryInitializeVideoProcessorInterfaces()
    {
        if (_device is null || _context is null)
        {
            return;
        }

        try
        {
            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _context.QueryInterface<ID3D11VideoContext>();
            DiagnosticLog.Write("VIDEO_DECODER", "D3D11VideoInterfaces=true");
        }
        catch (Exception exception)
        {
            _videoDevice?.Dispose();
            _videoDevice = null;
            _videoContext?.Dispose();
            _videoContext = null;
            DiagnosticLog.Write("VIDEO_DECODER", $"D3D11VideoInterfaces=false reason={exception.GetType().Name}:{exception.Message}");
        }
    }

    private bool EnsureVideoProcessor(int width, int height)
    {
        if (_videoDevice is null || _videoContext is null || _device is null || _swapChain is null)
        {
            return false;
        }

        if (_videoProcessor is not null && _videoProcessorEnumerator is not null
            && _videoProcessorOutputView is not null
            && _videoProcessorInputWidth == width && _videoProcessorInputHeight == height)
        {
            return true;
        }

        DisposeVideoProcessorResources();
        try
        {
            var frameRate = new Rational(60, 1);
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = frameRate,
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputFrameRate = frameRate,
                OutputWidth = Math.Max(1u, _swapWidth),
                OutputHeight = Math.Max(1u, _swapHeight),
                Usage = VideoUsage.PlaybackNormal,
            };
            _videoProcessorEnumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
            var nv12Support = (VideoProcessorFormatSupport)0;
            var bgraSupport = (VideoProcessorFormatSupport)0;
            var nv12Result = _videoProcessorEnumerator.CheckVideoProcessorFormat(Format.NV12, out nv12Support);
            var bgraResult = _videoProcessorEnumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm, out bgraSupport);
            DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessorFormatSupport NV12={nv12Result}/{nv12Support} BGRA={bgraResult}/{bgraSupport}");
            _videoProcessor = _videoDevice.CreateVideoProcessor(_videoProcessorEnumerator, 0);
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var outputDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
            using (GpuPipelineDiagnostics.Begin("Olivia.Render", "CreateVideoProcessorOutputView", 0))
            {
                _videoProcessorOutputView = _videoDevice.CreateVideoProcessorOutputView(backBuffer, _videoProcessorEnumerator, outputDesc);
            }
            _videoProcessorInputWidth = width;
            _videoProcessorInputHeight = height;
            DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessor=ready input={width}x{height} output={(int)_swapWidth}x{(int)_swapHeight}");
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessor=failed type={exception.GetType().Name} message={exception.Message}");
            DisposeVideoProcessorResources();
            return false;
        }
    }

    private ID3D11VideoProcessorInputView? CreateVideoProcessorInputView(ID3D11Texture2D texture, uint subresourceIndex, long frameId)
    {
        if (_videoDevice is null || _videoProcessorEnumerator is null)
        {
            return null;
        }

        var description = texture.Description;
        var fourCcCandidates = description.Format == Format.NV12
            ? new[] { 0u, 0x3231564Eu }
            : new[] { 0u };
        var arraySliceCandidates = new[] { subresourceIndex, 0u }.Distinct();
        foreach (var fourCc in fourCcCandidates)
        {
            foreach (var arraySlice in arraySliceCandidates)
            {
                if (arraySlice >= description.ArraySize)
                {
                    continue;
                }

                var inputDesc = new VideoProcessorInputViewDescription
                {
                    FourCC = fourCc,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView
                    {
                        MipSlice = 0,
                        ArraySlice = arraySlice,
                    },
                };
                try
                {
                    ID3D11VideoProcessorInputView view;
                    using (GpuPipelineDiagnostics.Begin("Olivia.Render", "CreateVideoProcessorInputView", frameId))
                    {
                        using var resource = texture.QueryInterface<ID3D11Resource>();
                        view = _videoDevice.CreateVideoProcessorInputView(resource, _videoProcessorEnumerator, inputDesc);
                    }
                    GpuPipelineDiagnostics.InputViewCreated();
                    if (!_videoProcessorInputViewReadyLogged)
                    {
                        _videoProcessorInputViewReadyLogged = true;
                        DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessorInputView=ready FourCC=0x{fourCc:X8} ArraySlice={arraySlice}");
                    }
                    return view;
                }
                catch (Exception exception)
                {
                    if (!_videoProcessorInputViewFailureLogged)
                    {
                        _videoProcessorInputViewFailureLogged = true;
                        DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessorInputView=failed FourCC=0x{fourCc:X8} ArraySlice={arraySlice} type={exception.GetType().Name} message={exception.Message}");
                    }
                }
            }
        }

        return null;
    }

    private ID3D11Texture2D? EnsureVideoProcessorInputCopy(int width, int height)
    {
        if (_device is null)
        {
            return null;
        }

        if (_videoProcessorInputCopyTexture is not null
            && _videoProcessorInputCopyWidth == width
            && _videoProcessorInputCopyHeight == height)
        {
            return _videoProcessorInputCopyTexture;
        }

        _videoProcessorInputCopyTexture?.Dispose();
        _videoProcessorInputCopyTexture = null;
        _videoProcessorInputCopyWidth = 0;
        _videoProcessorInputCopyHeight = 0;
        try
        {
            var description = new Texture2DDescription(
                Format.NV12, (uint)width, (uint)height, 1, 1,
                BindFlags.Decoder, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            _videoProcessorInputCopyTexture = _device.CreateTexture2D(in description);
            _videoProcessorInputCopyWidth = width;
            _videoProcessorInputCopyHeight = height;
            DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessorInputCopyTexture=ready format=NV12 size={width}x{height} bind={description.BindFlags}");
            return _videoProcessorInputCopyTexture;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("VIDEO_PIPELINE", $"VideoProcessorInputCopyTexture=failed type={exception.GetType().Name} message={exception.Message}");
            _videoProcessorInputCopyTexture?.Dispose();
            _videoProcessorInputCopyTexture = null;
            return null;
        }
    }

    private static RawRect CalculateAspectFitRect(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var sourceAspect = sourceWidth / (double)Math.Max(1, sourceHeight);
        var targetAspect = targetWidth / (double)Math.Max(1, targetHeight);
        if (targetAspect > sourceAspect)
        {
            var width = (int)Math.Round(targetHeight * sourceAspect);
            var left = (targetWidth - width) / 2;
            return new RawRect(left, 0, left + width, targetHeight);
        }

        var height = (int)Math.Round(targetWidth / sourceAspect);
        var top = (targetHeight - height) / 2;
        return new RawRect(0, top, targetWidth, top + height);
    }

    private void DisposeVideoProcessorResources()
    {
        _videoProcessorInputCopyTexture?.Dispose();
        _videoProcessorInputCopyTexture = null;
        _videoProcessorInputCopyWidth = 0;
        _videoProcessorInputCopyHeight = 0;
        _videoProcessorOutputView?.Dispose();
        _videoProcessorOutputView = null;
        _videoProcessor?.Dispose();
        _videoProcessor = null;
        _videoProcessorEnumerator?.Dispose();
        _videoProcessorEnumerator = null;
        _videoProcessorInputWidth = 0;
        _videoProcessorInputHeight = 0;
        _videoProcessorInputViewFailureLogged = false;
        _videoProcessorInputViewReadyLogged = false;
        _videoProcessorCopyLogged = false;
        _videoProcessorFrameLogged = false;
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
        DisposeVideoProcessorResources();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
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
