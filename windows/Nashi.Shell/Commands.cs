using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nashi.Core;
namespace Nashi.Shell;

/// The files and folders Explorer handed to a command.
internal sealed record Selection(string[] Paths,HashSet<string> Directories)
{
    private static readonly ConditionalWeakTable<IShellItemArray,Selection> cache=new();
    internal string[] Formats=>formats??=FeatureCatalog.Formats(Paths,Directories.Contains);private string[]? formats;
    /// Null when an item has no file-system path (Recycle Bin, phones, search results without files).
    internal static Selection? Read(IShellItemArray? items)
    {
        if(items is null)return null;if(cache.TryGetValue(items,out var known))return known;
        if(items.GetCount(out var count)!=Native.S_OK||count==0)return null;
        var paths=new List<string>((int)Math.Min(count,4096));var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(uint n=0;n<count;n++)
        {
            if(items.GetItemAt(n,out var item)!=Native.S_OK||item is null)return null;
            try
            {
                if(item.GetDisplayName(Native.SIGDN_FILESYSPATH,out var name)!=Native.S_OK||name==IntPtr.Zero)return null;
                string path;try{path=Marshal.PtrToStringUni(name)??"";}finally{Marshal.FreeCoTaskMem(name);}
                if(path.Length==0)return null;
                // Explorer reports ZIP files as folders too; those also carry SFGAO_STREAM.
                if(item.GetAttributes(Native.SFGAO_FOLDER|Native.SFGAO_STREAM,out var attributes)==Native.S_OK&&(attributes&Native.SFGAO_FOLDER)!=0&&(attributes&Native.SFGAO_STREAM)==0)directories.Add(path);
                paths.Add(path);
            }
            finally{Marshal.ReleaseComObject(item);}
        }
        var selection=new Selection(paths.ToArray(),directories);cache.AddOrUpdate(items,selection);return selection;
    }
}

internal static class Labels
{
    private static readonly bool turkish=ReadLanguage()=="tr";
    internal static string Convert=>turkish?"Nashi ile dönüştür":"Convert with Nashi";
    internal static string Tools=>turkish?"Nashi araçları…":"Nashi tools…";
    internal static string Icon=>Path.Combine(AppContext.BaseDirectory,"NashiMenu.ico");
    private static string? ReadLanguage()
    {
        try
        {
            var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nashi","preferences.json");
            if(!File.Exists(file)||new FileInfo(file).Length>1_000_000)return null;
            using var document=JsonDocument.Parse(File.ReadAllBytes(file));return document.RootElement.TryGetProperty("Language",out var value)?value.GetString():null;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){return null;}
    }
    internal static int Text(string value,out IntPtr result){result=Marshal.StringToCoTaskMemUni(value);return Native.S_OK;}
    internal static Guid Name(string value)=>new(MD5.HashData(Encoding.UTF8.GetBytes("Nashi.ExplorerMenu."+value)));
}

/// "Convert with Nashi" / "Nashi ile dönüştür": a submenu of the formats every
/// selected item can become (FeatureCatalog.Formats), hidden when there are none.
[ComVisible(true),ClassInterface(ClassInterfaceType.None),Guid(Server.ConvertClass)]
public sealed class ConvertMenu:IExplorerCommand
{
    private string[]? formats;
    public int GetTitle(IShellItemArray? items,out IntPtr name){Server.Touch();return Labels.Text(Labels.Convert,out name);}
    public int GetIcon(IShellItemArray? items,out IntPtr icon){Server.Touch();return Labels.Text(Labels.Icon,out icon);}
    public int GetToolTip(IShellItemArray? items,out IntPtr tip){tip=IntPtr.Zero;return Native.E_NOTIMPL;}
    public int GetCanonicalName(out Guid name){name=new(Server.ConvertClass);return Native.S_OK;}
    public int GetState(IShellItemArray? items,bool okToBeSlow,out uint state)
    {
        Server.Touch();formats=Selection.Read(items)?.Formats??[];state=formats.Length>0?Native.ECS_ENABLED:Native.ECS_HIDDEN;return Native.S_OK;
    }
    public int Invoke(IShellItemArray? items,IntPtr bindContext)=>Native.E_NOTIMPL;
    public int GetFlags(out uint flags){flags=Native.ECF_HASSUBCOMMANDS;return Native.S_OK;}
    public int EnumSubCommands(out IntPtr commands)
    {
        Server.Touch();
        // GetState normally runs first; otherwise each format hides itself when it does not apply.
        var list=(formats??FeatureCatalog.Conversions.Values.SelectMany(f=>f).Distinct().Order().ToArray()).Select(f=>(IExplorerCommand)new FormatCommand(f)).ToArray();
        commands=Marshal.GetComInterfaceForObject<CommandList,IEnumExplorerCommand>(new CommandList(list));return Native.S_OK;
    }
}

[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class FormatCommand(string format):IExplorerCommand
{
    public int GetTitle(IShellItemArray? items,out IntPtr name){Server.Touch();return Labels.Text(format.ToUpperInvariant(),out name);}
    public int GetIcon(IShellItemArray? items,out IntPtr icon){icon=IntPtr.Zero;return Native.E_NOTIMPL;}
    public int GetToolTip(IShellItemArray? items,out IntPtr tip){tip=IntPtr.Zero;return Native.E_NOTIMPL;}
    public int GetCanonicalName(out Guid name){name=Labels.Name("convert."+format);return Native.S_OK;}
    public int GetState(IShellItemArray? items,bool okToBeSlow,out uint state)
    {
        Server.Touch();var selection=Selection.Read(items);state=items is null||selection?.Formats.Contains(format)==true?Native.ECS_ENABLED:Native.ECS_HIDDEN;return Native.S_OK;
    }
    public int Invoke(IShellItemArray? items,IntPtr bindContext)
    {
        Server.Touch();var selection=Selection.Read(items);if(selection is null||!selection.Formats.Contains(format))return Native.E_INVALIDARG;
        Router.Send(new LaunchCommand(LaunchKind.Convert,format,selection.Paths));return Native.S_OK;
    }
    public int GetFlags(out uint flags){flags=Native.ECF_DEFAULT;return Native.S_OK;}
    public int EnumSubCommands(out IntPtr commands){commands=IntPtr.Zero;return Native.E_NOTIMPL;}
}

/// "Nashi tools…" / "Nashi araçları…": opens the tools wheel at the pointer.
[ComVisible(true),ClassInterface(ClassInterfaceType.None),Guid(Server.ToolsClass)]
public sealed class ToolsCommand:IExplorerCommand
{
    public int GetTitle(IShellItemArray? items,out IntPtr name){Server.Touch();return Labels.Text(Labels.Tools,out name);}
    public int GetIcon(IShellItemArray? items,out IntPtr icon){Server.Touch();return Labels.Text(Labels.Icon,out icon);}
    public int GetToolTip(IShellItemArray? items,out IntPtr tip){tip=IntPtr.Zero;return Native.E_NOTIMPL;}
    public int GetCanonicalName(out Guid name){name=new(Server.ToolsClass);return Native.S_OK;}
    public int GetState(IShellItemArray? items,bool okToBeSlow,out uint state){Server.Touch();state=Selection.Read(items) is null?Native.ECS_HIDDEN:Native.ECS_ENABLED;return Native.S_OK;}
    public int Invoke(IShellItemArray? items,IntPtr bindContext)
    {
        Server.Touch();var selection=Selection.Read(items);if(selection is null)return Native.E_INVALIDARG;
        Router.Send(new LaunchCommand(LaunchKind.Wheel,null,selection.Paths));return Native.S_OK;
    }
    public int GetFlags(out uint flags){flags=Native.ECF_DEFAULT;return Native.S_OK;}
    public int EnumSubCommands(out IntPtr commands){commands=IntPtr.Zero;return Native.E_NOTIMPL;}
}

[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class CommandList(IExplorerCommand[] commands,int position=0):IEnumExplorerCommand
{
    private int position=position;
    public int Next(uint count,IntPtr output,IntPtr fetched)
    {
        Server.Touch();var written=0;
        for(;written<count&&position<commands.Length;written++,position++)Marshal.WriteIntPtr(output,written*IntPtr.Size,Marshal.GetComInterfaceForObject(commands[position],typeof(IExplorerCommand)));
        if(fetched!=IntPtr.Zero)Marshal.WriteInt32(fetched,written);return written==count?Native.S_OK:Native.S_FALSE;
    }
    public int Skip(uint count){position=(int)Math.Min(commands.Length,position+(long)count);return position<commands.Length?Native.S_OK:Native.S_FALSE;}
    public int Reset(){position=0;return Native.S_OK;}
    public int Clone(out IntPtr copy){copy=Marshal.GetComInterfaceForObject<CommandList,IEnumExplorerCommand>(new CommandList(commands,position));return Native.S_OK;}
}

[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class ClassFactory(Func<object> create):IClassFactory
{
    public int CreateInstance(IntPtr outer,ref Guid iid,out IntPtr result)
    {
        result=IntPtr.Zero;if(outer!=IntPtr.Zero)return Native.CLASS_E_NOAGGREGATION;Server.Touch();
        var unknown=Marshal.GetIUnknownForObject(create());try{return Marshal.QueryInterface(unknown,in iid,out result);}finally{Marshal.Release(unknown);}
    }
    public int LockServer(bool lockServer){Server.Touch();return Native.S_OK;}
}

/// Hands a request to the running Nashi (named pipe, no new process) or
/// starts Nashi.exe for a one-shot conversion. Never blocks Explorer.
internal static class Router
{
    private static string Executable=>Path.Combine(AppContext.BaseDirectory,"Nashi.exe");
    private static string InstanceName=>"Nashi.Desktop.v1."+Process.GetCurrentProcess().SessionId;
    internal static void Send(LaunchCommand command)
    {
        // Explorer allowed this server to take the foreground; let Nashi have it.
        Native.AllowSetForegroundWindow(-1);Server.Busy(+1);
        Task.Run(()=>{try{if(!TryNotify(command))Launch(command);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException){}finally{Server.Busy(-1);}});
    }
    private static bool TryNotify(LaunchCommand command)
    {
        if(!Mutex.TryOpenExisting("Local\\"+InstanceName,out var mutex))return false;mutex.Dispose();
        try
        {
            foreach(var message in command.ToMessages())
            {
                using var client=new NamedPipeClientStream(".",InstanceName,PipeDirection.InOut,PipeOptions.CurrentUserOnly);client.Connect(3000);
                var data=JsonSerializer.SerializeToUtf8Bytes(message);var prefix=BitConverter.GetBytes(data.Length);if(!BitConverter.IsLittleEndian)Array.Reverse(prefix);
                client.Write(prefix);client.Write(data);client.Flush();var ack=new byte[1];client.ReadExactly(ack);
            }
            return true;
        }
        catch(Exception error)when(error is IOException or TimeoutException or UnauthorizedAccessException or EndOfStreamException){return false;}
    }
    private static void Launch(LaunchCommand command)
    {
        var start=new ProcessStartInfo(Executable){UseShellExecute=false};
        start.ArgumentList.Add(command.Kind==LaunchKind.Convert?"--convert":"--wheel");if(command.Kind==LaunchKind.Convert)start.ArgumentList.Add(command.Format!);
        if(command.Paths.Sum(p=>p.Length+3)>24_000)
        {
            var list=Path.Combine(Path.GetTempPath(),"Nashi-files-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllLines(list,command.Paths,new UTF8Encoding(false));
            start.ArgumentList.Add("--files-from");start.ArgumentList.Add(list);
        }
        else foreach(var path in command.Paths)start.ArgumentList.Add(path);
        using var _=Process.Start(start);
    }
}
