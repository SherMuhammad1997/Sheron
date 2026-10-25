using Sheron.Core.Video;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Sheron.Win.Encode;

/// <summary>
/// Turns the captured BGRA image into NV12 for the encoder, on the GPU with the Direct3D 11 video processor
/// (TRD › Video path: BT.709, 16–235). Results go round a pool of textures, so the encoder can still be reading
/// one while the next is written. Not thread-safe: the capture thread owns it.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private const int PoolSize = 3;

    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D[] _outputs = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[PoolSize];
    private IntPtr _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;
    private int _next;

    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context, int screenWidth, int screenHeight, VideoSettings settings)
    {
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();

        var rate = new Rational((uint)settings.FramesPerSecond, 1);
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = rate,
            InputWidth = (uint)screenWidth,
            InputHeight = (uint)screenHeight,
            OutputFrameRate = rate,
            OutputWidth = (uint)settings.Width,
            OutputHeight = (uint)settings.Height,
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        // Screen pixels are full-range RGB; the video is BT.709 YUV in the 16–235 range.
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        // Same size in and out for now; an odd last column or row of the screen is left out.
        var area = new RawRect(0, 0, settings.Width, settings.Height);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, area);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, area);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, area);

        var nv12 = new Texture2DDescription
        {
            Width = (uint)settings.Width,
            Height = (uint)settings.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        };
        var view = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        for (int i = 0; i < PoolSize; i++)
        {
            _outputs[i] = device.CreateTexture2D(nv12);
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_outputs[i], _enumerator, view);
        }
    }

    /// <summary>Queues the conversion on the GPU and returns the NV12 texture it writes to.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D bgra)
    {
        if (_inputView is null || _inputTexture != bgra.NativePointer)
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(bgra, _enumerator,
                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            _inputTexture = bgra.NativePointer;
        }

        int index = _next;
        _next = (_next + 1) % PoolSize;
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
        _videoContext.VideoProcessorBlt(_processor, _outputViews[index], 0, 1, [stream]).CheckError();
        return _outputs[index];
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        for (int i = 0; i < PoolSize; i++)
        {
            _outputViews[i]?.Dispose();
            _outputs[i]?.Dispose();
        }
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
