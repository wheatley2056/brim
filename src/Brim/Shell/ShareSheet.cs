using System.Runtime.InteropServices;
using Brim.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace Brim.Shell;

/// <summary>
/// Windows-Teilen-Dialog für Desktop-Fenster. Unverpackte WinUI-3-Apps haben kein CoreWindow;
/// Microsoft dokumentiert dafür <c>IDataTransferManagerInterop</c> mit dem Fensterhandle
/// („Display WinRT UI objects that depend on CoreWindow“).
/// </summary>
internal static class ShareSheet
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(nint appWindow);
    }

    private static readonly Guid DataTransferManagerId = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static void Show(nint window, IReadOnlyList<IStorageItem> items, string title)
    {
        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerId;
            var manager = MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(window, ref iid));

            void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
            {
                // Pro Fenster gibt es nur einen Manager; den Handler nach dem Teilen wieder abmelden.
                sender.DataRequested -= OnDataRequested;
                args.Request.Data.Properties.Title = title;
                args.Request.Data.SetStorageItems(items, readOnly: true);
            }

            manager.DataRequested += OnDataRequested;
            interop.ShowShareUIForWindow(window);
        }
        catch (Exception ex)
        {
            Log.Warn("Teilen-Dialog ließ sich nicht öffnen", ex);
        }
    }
}
