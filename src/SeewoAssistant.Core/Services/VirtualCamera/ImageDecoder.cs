using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace SeewoAssistant.Core.Services.VirtualCamera;

/// <summary>
/// Decodes an image file to BGRA using the Windows Imaging Component.
/// </summary>
/// <remarks>
/// WIC is used rather than System.Drawing because it is part of Windows itself:
/// no extra package, no GDI+ dependency, and it handles PNG/JPEG/BMP/GIF/TIFF and
/// camera RAW (when the codec is installed) through the same code path.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ImageDecoder
{
    private static readonly Guid ClsidWicImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");

    /// <summary>
    /// Decodes the first frame of <paramref name="path"/> into a BGRA buffer.
    /// Returns null when the file cannot be decoded.
    /// </summary>
    internal static FrameBuffer? DecodeToBgra(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Keeps the Core assembly loadable in a non-Windows test host.
            return null;
        }

        IWICImagingFactory? factory = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICFormatConverter? converter = null;

        try
        {
            var factoryType = Type.GetTypeFromCLSID(ClsidWicImagingFactory);
            if (factoryType is null)
            {
                return null;
            }

            factory = (IWICImagingFactory?)Activator.CreateInstance(factoryType);
            if (factory is null)
            {
                return null;
            }

            var hr = factory.CreateDecoderFromFilename(
                path,
                nint.Zero,
                GenericAccessRights.GENERIC_READ,
                WICDecodeOptions.WICDecodeMetadataCacheOnDemand,
                out decoder);
            Marshal.ThrowExceptionForHR(hr);

            hr = decoder.GetFrame(0, out frame);
            Marshal.ThrowExceptionForHR(hr);

            hr = frame.GetSize(out var width, out var height);
            Marshal.ThrowExceptionForHR(hr);

            if (width == 0 || height == 0)
            {
                return null;
            }

            hr = factory.CreateFormatConverter(out converter);
            Marshal.ThrowExceptionForHR(hr);

            // GUID_WICPixelFormat32bppBGRA - the layout the media source expects.
            var targetFormat = new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90F");
            hr = converter.Initialize(frame, ref targetFormat, WICBitmapDitherType.WICBitmapDitherTypeNone, nint.Zero, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
            Marshal.ThrowExceptionForHR(hr);

            var stride = checked((int)width * 4);
            var buffer = new byte[checked(stride * (int)height)];
            hr = converter.CopyPixels(nint.Zero, (uint)stride, (uint)buffer.Length, buffer);
            Marshal.ThrowExceptionForHR(hr);

            return new FrameBuffer((int)width, (int)height, buffer, stride);
        }
        catch (COMException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            Release(converter);
            Release(frame);
            Release(decoder);
            Release(factory);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    private enum WICDecodeOptions
    {
        WICDecodeMetadataCacheOnDemand = 0,
        WICDecodeMetadataCacheOnLoad = 1,
    }

    private enum WICBitmapDitherType
    {
        WICBitmapDitherTypeNone = 0,
    }

    private enum WICBitmapPaletteType
    {
        WICBitmapPaletteTypeCustom = 0,
    }

    [Flags]
    private enum GenericAccessRights : uint
    {
        GENERIC_READ = 0x80000000,
    }

    [ComImport]
    [Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        // The vtable order matters for COM interop; only the members we call are
        // declared, in their true slots, using PreserveSig so HRESULTs surface.
        [PreserveSig] int CreateDecoderFromFilename(
            [MarshalAs(UnmanagedType.LPWStr)] string wzFilename,
            nint pguidVendor,
            GenericAccessRights dwDesiredAccess,
            WICDecodeOptions metadataOptions,
            out IWICBitmapDecoder ppIDecoder);

        [PreserveSig] int CreateDecoderFromStream(nint pIStream, nint pguidVendor, WICDecodeOptions metadataOptions, out nint ppIDecoder);
        [PreserveSig] int CreateDecoderFromFileHandle(nint hFile, nint pguidVendor, WICDecodeOptions metadataOptions, out nint ppIDecoder);
        [PreserveSig] int CreateComponentInfo(nint clsidComponent, out nint ppIInfo);
        [PreserveSig] int CreateDecoder(nint guidContainerFormat, nint pguidVendor, out nint ppIDecoder);
        [PreserveSig] int CreateEncoder(nint guidContainerFormat, nint pguidVendor, out nint ppIEncoder);
        [PreserveSig] int CreatePalette(out nint ppIPalette);
        [PreserveSig] int CreateFormatConverter(out IWICFormatConverter ppIFormatConverter);
    }

    [ComImport]
    [Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(nint pIStream, out uint pdwCapabilities);
        [PreserveSig] int Initialize(nint pIStream, WICDecodeOptions cacheOptions);
        [PreserveSig] int GetContainerFormat(out Guid pguidContainerFormat);
        [PreserveSig] int GetDecoderInfo(out nint ppIDecoderInfo);
        [PreserveSig] int CopyPalette(nint pIPalette);
        [PreserveSig] int GetMetadataQueryReader(out nint ppIMetadataQueryReader);
        [PreserveSig] int GetPreview(out nint ppIBitmapSource);
        [PreserveSig] int GetColorContexts(uint cCount, nint ppIColorContexts, out uint pcActualCount);
        [PreserveSig] int GetThumbnail(out nint ppIThumbnail);
        [PreserveSig] int GetFrameCount(out uint pCount);
        [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode ppIBitmapFrame);
    }

    [ComImport]
    [Guid("3B16811B-6A43-4EC9-A813-3D930C13B940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode
    {
        [PreserveSig] int GetSize(out uint puiWidth, out uint puiHeight);
    }

    [ComImport]
    [Guid("00000301-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        // IWICBitmapSource members, in vtable order, so Initialize/CopyPixels land
        // on the right slots.
        [PreserveSig] int GetSize(out uint puiWidth, out uint puiHeight);
        [PreserveSig] int GetPixelFormat(out Guid pPixelFormat);
        [PreserveSig] int GetResolution(out double pDpiX, out double pDpiY);
        [PreserveSig] int CopyPalette(nint pIPalette);
        [PreserveSig] int CopyPixels(nint prc, uint cbStride, uint cbBufferSize, [Out] byte[] pbBuffer);

        [PreserveSig] int Initialize(
            IWICBitmapFrameDecode pISource,
            ref Guid dstFormat,
            WICBitmapDitherType dither,
            nint pIPalette,
            double alphaThresholdPercent,
            WICBitmapPaletteType paletteTranslate);
    }
}
