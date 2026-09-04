using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ComputerUse.Native;

internal sealed class WindowsGraphicsCaptureBackend : ICaptureBackend
{
    private const uint D3D11CreateDeviceBgraSupport = 0x00000020;
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x00020000;
    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const int D3D11CreateTexture2DVtableIndex = 5;
    private const int D3D11DeviceContextMapVtableIndex = 14;
    private const int D3D11DeviceContextUnmapVtableIndex = 15;
    private const int D3D11DeviceContextCopyResourceVtableIndex = 47;
    private const int D3D11Texture2DGetDescVtableIndex = 10;
    private const int FrameWaitMilliseconds = 1_500;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static readonly D3DFeatureLevel[] PreferredFeatureLevels =
    [
        D3DFeatureLevel.Level11_1,
        D3DFeatureLevel.Level11_0,
        D3DFeatureLevel.Level10_1,
        D3DFeatureLevel.Level10_0
    ];

    public string Name => "windows_graphics_capture";

    public CaptureAttempt Capture(IntPtr hwnd, WindowRectData rect)
    {
        var validationError = CaptureDimensions.Validate(rect);
        if (validationError is not null)
        {
            return CaptureAttempt.Failure(Name, validationError);
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            return CaptureAttempt.Failure(Name, "Windows Graphics Capture is not supported on this device.");
        }

        NativeD3DDevice? nativeDevice = null;
        IDirect3DDevice? graphicsDevice = null;
        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            nativeDevice = CreateD3DDevice();
            graphicsDevice = CreateGraphicsDevice(nativeDevice.Device);
            item = CreateCaptureItemForWindow(hwnd);

            var itemSize = item.Size;
            if (itemSize.Width <= 0 || itemSize.Height <= 0)
            {
                return CaptureAttempt.Failure(Name, "Windows Graphics Capture returned an empty item size.");
            }

            if (itemSize.Width > CaptureDimensions.MaximumDimension
                || itemSize.Height > CaptureDimensions.MaximumDimension)
            {
                return CaptureAttempt.Failure(
                    Name,
                    $"Windows Graphics Capture returned an item larger than the capture limit ({CaptureDimensions.MaximumDimension}x{CaptureDimensions.MaximumDimension}).");
            }

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                graphicsDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                itemSize);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();

            using var frame = WaitForFrame(framePool);
            if (frame is null)
            {
                return CaptureAttempt.Failure(
                    Name,
                    $"Windows Graphics Capture did not deliver a frame within {FrameWaitMilliseconds} ms.");
            }

            var image = ReadbackFrame(frame, nativeDevice);
            var pngBytes = CaptureImageEncoder.EncodeBgra32(
                image.Pixels,
                image.Width,
                image.Height,
                image.Stride);
            return CaptureAttempt.Success(Name, pngBytes, image.Width, image.Height);
        }
        catch (Exception exception)
        {
            return CaptureAttempt.Failure(Name, FormatFailure(exception));
        }
        finally
        {
            session?.Dispose();
            framePool?.Dispose();
            if (graphicsDevice is IDisposable disposableGraphicsDevice)
            {
                disposableGraphicsDevice.Dispose();
            }

            nativeDevice?.Dispose();
        }
    }

    private static Direct3D11CaptureFrame? WaitForFrame(Direct3D11CaptureFramePool framePool)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)(FrameWaitMilliseconds * (double)Stopwatch.Frequency / 1_000.0);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            var frame = framePool.TryGetNextFrame();
            if (frame is not null)
            {
                return frame;
            }

            Thread.Sleep(16);
        }

        return null;
    }

    private static FrameReadback ReadbackFrame(
        Direct3D11CaptureFrame frame,
        NativeD3DDevice nativeDevice)
    {
        var contentSize = frame.ContentSize;
        if (contentSize.Width <= 0 || contentSize.Height <= 0)
        {
            throw new InvalidOperationException("Windows Graphics Capture returned an empty frame.");
        }

        if (contentSize.Width > CaptureDimensions.MaximumDimension
            || contentSize.Height > CaptureDimensions.MaximumDimension)
        {
            throw new InvalidOperationException(
                $"Windows Graphics Capture returned a frame larger than the capture limit ({CaptureDimensions.MaximumDimension}x{CaptureDimensions.MaximumDimension}).");
        }

        if (!ComWrappersSupport.TryUnwrapObject(frame.Surface, out var surfaceReference)
            || surfaceReference is null)
        {
            throw new InvalidOperationException("Unable to unwrap the WGC frame surface.");
        }

        try
        {
            var accessIid = Direct3DDxgiInterfaceAccessIid;
            var accessReference = surfaceReference.As(accessIid);
            try
            {
                var accessObject = Marshal.GetObjectForIUnknown(accessReference.ThisPtr);
                try
                {
                    var access = (IDirect3DDxgiInterfaceAccess)accessObject;
                    var textureIid = D3D11Texture2DIid;
                    ThrowIfFailed(
                        access.GetInterface(
                            ref textureIid,
                            out var sourceTexture),
                        "IDirect3DDxgiInterfaceAccess.GetInterface(ID3D11Texture2D)");

                    if (sourceTexture == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "IDirect3DDxgiInterfaceAccess.GetInterface(ID3D11Texture2D) returned a null texture.");
                    }

                    try
                    {
                        return CopyTextureToCpu(
                            sourceTexture,
                            nativeDevice,
                            contentSize.Width,
                            contentSize.Height);
                    }
                    finally
                    {
                        Marshal.Release(sourceTexture);
                    }
                }
                finally
                {
                    if (Marshal.IsComObject(accessObject))
                    {
                        Marshal.ReleaseComObject(accessObject);
                    }
                }
            }
            finally
            {
                accessReference.Dispose();
            }
        }
        finally
        {
            surfaceReference.Dispose();
        }
    }

    private static FrameReadback CopyTextureToCpu(
        IntPtr sourceTexture,
        NativeD3DDevice nativeDevice,
        int width,
        int height)
    {
        var getDescription = GetComMethod<GetTextureDescriptionDelegate>(
            sourceTexture,
            D3D11Texture2DGetDescVtableIndex);
        getDescription(sourceTexture, out var sourceDescription);

        if (sourceDescription.Format != DxgiFormatB8G8R8A8Unorm)
        {
            throw new InvalidOperationException(
                $"Windows Graphics Capture returned unsupported DXGI format {sourceDescription.Format}; expected B8G8R8A8_UNORM (87).");
        }

        if (sourceDescription.Width < width || sourceDescription.Height < height)
        {
            throw new InvalidOperationException("The capture frame is smaller than its reported content size.");
        }

        if (sourceDescription.SampleDescription.Count != 1)
        {
            throw new InvalidOperationException("Multisampled Windows Graphics Capture frames are not supported by this readback path.");
        }

        var stagingDescription = sourceDescription with
        {
            MipLevels = 1,
            ArraySize = 1,
            Usage = D3D11UsageStaging,
            BindFlags = 0,
            CpuAccessFlags = D3D11CpuAccessRead,
            MiscFlags = 0
        };

        var createTexture = GetComMethod<CreateTexture2DDelegate>(
            nativeDevice.Device,
            D3D11CreateTexture2DVtableIndex);
        ThrowIfFailed(
            createTexture(
                nativeDevice.Device,
                ref stagingDescription,
                IntPtr.Zero,
                out var stagingTexture),
            "ID3D11Device.CreateTexture2D(staging)");

        try
        {
            var copyResource = GetComMethod<CopyResourceDelegate>(
                nativeDevice.Context,
                D3D11DeviceContextCopyResourceVtableIndex);
            copyResource(nativeDevice.Context, stagingTexture, sourceTexture);

            var map = GetComMethod<MapDelegate>(
                nativeDevice.Context,
                D3D11DeviceContextMapVtableIndex);
            ThrowIfFailed(
                map(
                    nativeDevice.Context,
                    stagingTexture,
                    0,
                    D3D11Map.Read,
                    0,
                    out var mapped),
                "ID3D11DeviceContext.Map(staging)");

            var mappedSuccessfully = true;
            try
            {
                var stride = checked(width * 4);
                if (mapped.RowPitch < stride)
                {
                    throw new InvalidOperationException("The mapped capture row pitch is smaller than the frame width.");
                }

                var pixels = new byte[checked(stride * height)];
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.Data, checked(row * (int)mapped.RowPitch)),
                        pixels,
                        row * stride,
                        stride);
                }

                return new FrameReadback(pixels, width, height, stride);
            }
            finally
            {
                if (mappedSuccessfully)
                {
                    var unmap = GetComMethod<UnmapDelegate>(
                        nativeDevice.Context,
                        D3D11DeviceContextUnmapVtableIndex);
                    unmap(nativeDevice.Context, stagingTexture, 0);
                }
            }
        }
        finally
        {
            Marshal.Release(stagingTexture);
        }
    }

    private static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr hwnd)
    {
        var factory = GetGraphicsCaptureItemActivationFactory();
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var itemIid = GraphicsCaptureItemIid;
            var itemPointer = interop.CreateForWindow(hwnd, ref itemIid);
            if (itemPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("GraphicsCaptureItem interop returned a null item.");
            }

            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static unsafe IntPtr GetGraphicsCaptureItemActivationFactory()
    {
        var className = new MarshalString("Windows.Graphics.Capture.GraphicsCaptureItem");
        try
        {
            var classNameAbi = className.GetAbi();
            var factoryIid = GraphicsCaptureItemInteropIid;
            ThrowIfFailed(
                RoGetActivationFactory(
                    classNameAbi,
                    ref factoryIid,
                    out var factory),
                "RoGetActivationFactory(GraphicsCaptureItem)");

            return factory;
        }
        finally
        {
            className.Dispose();
        }
    }

    private static IDirect3DDevice CreateGraphicsDevice(IntPtr d3dDevice)
    {
        var dxgiIid = DxgiDeviceIid;
        ThrowIfFailed(
            Marshal.QueryInterface(d3dDevice, ref dxgiIid, out var dxgiDevice),
            "QueryInterface(IDXGIDevice)");

        try
        {
            ThrowIfFailed(
                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDevice),
                "CreateDirect3D11DeviceFromDXGIDevice");

            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                Marshal.Release(graphicsDevice);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    private static NativeD3DDevice CreateD3DDevice()
    {
        var result = CreateD3DDevice(PreferredFeatureLevels, out var device, out var context);
        if (result < 0)
        {
            ReleaseIfPresent(device);
            ReleaseIfPresent(context);
            result = CreateD3DDevice(
                PreferredFeatureLevels[1..],
                out device,
                out context);
        }

        ThrowIfFailed(result, "D3D11CreateDevice");
        return new NativeD3DDevice(device, context);
    }

    private static int CreateD3DDevice(
        D3DFeatureLevel[] featureLevels,
        out IntPtr device,
        out IntPtr context)
    {
        return D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            featureLevels,
            (uint)featureLevels.Length,
            D3D11SdkVersion,
            out device,
            out _,
            out context);
    }

    private static string FormatFailure(Exception exception)
    {
        if (exception is COMException comException)
        {
            return $"{exception.Message} (HRESULT 0x{comException.HResult:X8}).";
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new COMException(
                $"{operation} failed (HRESULT 0x{hresult:X8}).",
                hresult);
        }
    }

    private static T GetComMethod<T>(IntPtr instance, int index)
        where T : Delegate
    {
        if (instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("A native COM instance pointer was null.");
        }

        var vtable = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtable, checked(index * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static void ReleaseIfPresent(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        int GetInterface([In] ref Guid iid, out IntPtr result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTextureDescriptionDelegate(
        IntPtr instance,
        out D3D11Texture2DDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(
        IntPtr instance,
        ref D3D11Texture2DDescription description,
        IntPtr initialData,
        out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(
        IntPtr instance,
        IntPtr destination,
        IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(
        IntPtr instance,
        IntPtr resource,
        uint subresource,
        D3D11Map mapType,
        uint mapFlags,
        out D3D11MappedSubresource mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(
        IntPtr instance,
        IntPtr resource,
        uint subresource);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        [In] D3DFeatureLevel[] featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out D3DFeatureLevel featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    private enum D3DDriverType : uint
    {
        Hardware = 1
    }

    private enum D3DFeatureLevel : uint
    {
        Level10_0 = 0xA000,
        Level10_1 = 0xA100,
        Level11_0 = 0xB000,
        Level11_1 = 0xB100
    }

    private enum D3D11Map : uint
    {
        Read = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11SampleDescription
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct D3D11Texture2DDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public D3D11SampleDescription SampleDescription;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    private readonly record struct FrameReadback(byte[] Pixels, int Width, int Height, int Stride);

    private sealed class NativeD3DDevice : IDisposable
    {
        public NativeD3DDevice(IntPtr device, IntPtr context)
        {
            Device = device;
            Context = context;
        }

        public IntPtr Device { get; }

        public IntPtr Context { get; }

        public void Dispose()
        {
            ReleaseIfPresent(Context);
            ReleaseIfPresent(Device);
        }
    }
}
