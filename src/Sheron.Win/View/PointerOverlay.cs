using System.Runtime.InteropServices;
using System.Text;
using Sheron.Core.Capture;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Sheron.Win.View;

/// <summary>
/// Draws the pointer on top of the video (TRD › Viewer): first its colour pixels (premultiplied alpha), then the
/// pixels that invert the screen. Two tiny shaders, compiled once with the Windows shader compiler. The pointer is
/// drawn into a viewport the size of its rectangle, so no vertex buffer is needed. Used by the viewer thread only.
/// </summary>
internal sealed class PointerOverlay : IDisposable
{
    /// <summary>
    /// <c>Corners</c> makes the 4 corners of a strip that fills the viewport, from the vertex number alone; <c>Image</c>
    /// reads the pointer texture there.
    /// </summary>
    private const string Shaders = """
        Texture2D pointerImage : register(t0);
        SamplerState pointerSampler : register(s0);

        struct Corner
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
        };

        Corner Corners(uint id : SV_VertexID)
        {
            Corner corner;
            corner.uv = float2(id & 1, id >> 1);
            corner.position = float4(corner.uv.x * 2 - 1, 1 - corner.uv.y * 2, 0, 1);
            return corner;
        }

        float4 Image(Corner corner) : SV_Target
        {
            return pointerImage.Sample(pointerSampler, corner.uv);
        }
        """;

    private const uint OptimizationLevel3 = 1 << 15;   // D3DCOMPILE_OPTIMIZATION_LEVEL3 (d3dcompiler.h)

    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11BlendState _invert;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;
    private PointerImage? _image;
    private ID3D11Texture2D? _colorTexture;
    private ID3D11ShaderResourceView? _colorView;
    private ID3D11Texture2D? _invertTexture;
    private ID3D11ShaderResourceView? _invertView;

    public PointerOverlay(ID3D11Device device)
    {
        _device = device;
        _vertexShader = device.CreateVertexShader(Compile("Corners", "vs_4_0"));
        _pixelShader = device.CreatePixelShader(Compile("Image", "ps_4_0"));
        // Premultiplied colour over the video.
        _blend = device.CreateBlendState(BlendState(Blend.One, Blend.InverseSourceAlpha));
        // White inverts what is under it (1 − screen); black leaves it as it is.
        _invert = device.CreateBlendState(BlendState(Blend.InverseDestinationColor, Blend.InverseSourceColor));
        _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        // Both triangles drawn whichever way they face; cut off at the edge of the picture.
        var rasterizer = RasterizerDescription.CullNone;
        rasterizer.ScissorEnable = true;
        _rasterizer = device.CreateRasterizerState(rasterizer);
    }

    /// <summary>
    /// Draws <paramref name="image"/> with its top-left corner at (<paramref name="left"/>, <paramref name="top"/>) in
    /// the back buffer, scaled like the video, and only inside <paramref name="picture"/>.
    /// </summary>
    public void Draw(ID3D11DeviceContext context, ID3D11RenderTargetView target, PointerImage image, double left, double top,
        double scale, ScreenRect picture)
    {
        SetImage(image);
        context.OMSetRenderTargets(target);
        context.RSSetViewport((float)Math.Round(left), (float)Math.Round(top),
            (float)(image.Width * scale), (float)(image.Height * scale));
        context.RSSetScissorRect(picture.Left, picture.Top, picture.Width, picture.Height);
        context.RSSetState(_rasterizer);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetSampler(0, _sampler);

        context.PSSetShaderResource(0, _colorView!);
        context.OMSetBlendState(_blend);
        context.Draw(4, 0);
        if (image.HasInvert)
        {
            context.PSSetShaderResource(0, _invertView!);
            context.OMSetBlendState(_invert);
            context.Draw(4, 0);
        }

        context.PSSetShaderResource(0, null!);
        context.UnsetRenderTargets();
    }

    /// <summary>New textures only when the pointer changes shape.</summary>
    private void SetImage(PointerImage image)
    {
        if (ReferenceEquals(image, _image)) return;
        DisposeTextures();
        (_colorTexture, _colorView) = Upload(image.Color, image.Width, image.Height);
        (_invertTexture, _invertView) = Upload(image.Invert, image.Width, image.Height);
        _image = image;
    }

    private unsafe (ID3D11Texture2D, ID3D11ShaderResourceView) Upload(byte[] pixels, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };
        fixed (byte* data = pixels)
        {
            var texture = _device.CreateTexture2D(description,
                new SubresourceData { DataPointer = (IntPtr)data, RowPitch = (uint)width * 4 });
            return (texture, _device.CreateShaderResourceView(texture));
        }
    }

    private static BlendDescription BlendState(Blend source, Blend destination)
    {
        var description = new BlendDescription();
        description.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = source,
            DestinationBlend = destination,
            BlendOperation = BlendOperation.Add,
            // The back buffer's alpha is not shown; keep it as it is.
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return description;
    }

    /// <summary>Compiles one entry point of <see cref="Shaders"/> with d3dcompiler_47.dll, which comes with Windows.</summary>
    private static byte[] Compile(string entryPoint, string profile)
    {
        byte[] source = Encoding.ASCII.GetBytes(Shaders);
        int result = D3DCompile(source, (nuint)source.Length, "pointer", IntPtr.Zero, IntPtr.Zero, entryPoint, profile,
            OptimizationLevel3, 0, out IntPtr code, out IntPtr errors);
        using var codeBlob = code != IntPtr.Zero ? new Blob(code) : null;
        using var errorBlob = errors != IntPtr.Zero ? new Blob(errors) : null;
        if (result < 0 || codeBlob is null)
            throw new InvalidOperationException($"The pointer shader {entryPoint} did not compile: "
                + (errorBlob?.AsString() ?? $"0x{result:X8}"));
        return codeBlob.AsBytes();
    }

    private void DisposeTextures()
    {
        _colorView?.Dispose();
        _colorTexture?.Dispose();
        _invertView?.Dispose();
        _invertTexture?.Dispose();
    }

    public void Dispose()
    {
        DisposeTextures();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _invert.Dispose();
        _blend.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile(byte[] sourceData, nuint sourceSize, [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errorMessages);
}
