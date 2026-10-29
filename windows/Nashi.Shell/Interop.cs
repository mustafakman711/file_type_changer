using System.Runtime.InteropServices;
namespace Nashi.Shell;

// Shell interfaces as declared in shobjidl_core.h. Every method keeps its
// HRESULT (PreserveSig) so nothing throws across the COM boundary into Explorer.

[ComImport,Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig]int GetTitle(IShellItemArray? items,out IntPtr name);
    [PreserveSig]int GetIcon(IShellItemArray? items,out IntPtr icon);
    [PreserveSig]int GetToolTip(IShellItemArray? items,out IntPtr tip);
    [PreserveSig]int GetCanonicalName(out Guid name);
    [PreserveSig]int GetState(IShellItemArray? items,[MarshalAs(UnmanagedType.Bool)]bool okToBeSlow,out uint state);
    [PreserveSig]int Invoke(IShellItemArray? items,IntPtr bindContext);
    [PreserveSig]int GetFlags(out uint flags);
    [PreserveSig]int EnumSubCommands(out IntPtr commands);
}

[ComImport,Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommand
{
    [PreserveSig]int Next(uint count,IntPtr commands,IntPtr fetched);
    [PreserveSig]int Skip(uint count);
    [PreserveSig]int Reset();
    [PreserveSig]int Clone(out IntPtr copy);
}

[ComImport,Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig]int BindToHandler(IntPtr bindContext,ref Guid handler,ref Guid iid,out IntPtr result);
    [PreserveSig]int GetPropertyStore(int flags,ref Guid iid,out IntPtr result);
    [PreserveSig]int GetPropertyDescriptionList(IntPtr keyType,ref Guid iid,out IntPtr result);
    [PreserveSig]int GetAttributes(int flags,uint mask,out uint attributes);
    [PreserveSig]int GetCount(out uint count);
    [PreserveSig]int GetItemAt(uint index,out IShellItem? item);
    [PreserveSig]int EnumItems(out IntPtr items);
}

[ComImport,Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig]int BindToHandler(IntPtr bindContext,ref Guid handler,ref Guid iid,out IntPtr result);
    [PreserveSig]int GetParent(out IShellItem? parent);
    [PreserveSig]int GetDisplayName(uint form,out IntPtr name);
    [PreserveSig]int GetAttributes(uint mask,out uint attributes);
    [PreserveSig]int Compare(IShellItem other,uint hint,out int order);
}

[ComImport,Guid("00000001-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]int CreateInstance(IntPtr outer,ref Guid iid,out IntPtr result);
    [PreserveSig]int LockServer([MarshalAs(UnmanagedType.Bool)]bool lockServer);
}

internal static class Native
{
    internal const int S_OK=0,S_FALSE=1,E_NOTIMPL=unchecked((int)0x80004001),E_FAIL=unchecked((int)0x80004005),E_INVALIDARG=unchecked((int)0x80070057),CLASS_E_NOAGGREGATION=unchecked((int)0x80040110);
    internal const uint ECS_ENABLED=0,ECS_HIDDEN=2,ECF_DEFAULT=0,ECF_HASSUBCOMMANDS=1;
    internal const uint SIGDN_FILESYSPATH=0x80058000,SFGAO_FOLDER=0x20000000,SFGAO_STREAM=0x00400000,SFGAO_FILESYSTEM=0x40000000;
    [DllImport("ole32.dll")]internal static extern int CoRegisterClassObject(ref Guid clsid,[MarshalAs(UnmanagedType.IUnknown)]object factory,uint context,uint flags,out uint cookie);
    [DllImport("ole32.dll")]internal static extern int CoRevokeClassObject(uint cookie);
    [DllImport("ole32.dll")]internal static extern int CoResumeClassObjects();
    [DllImport("user32.dll")]internal static extern bool AllowSetForegroundWindow(int process);
}
