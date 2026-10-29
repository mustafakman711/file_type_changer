namespace Nashi.Core;

public enum LaunchKind{Open,Background,Convert,Wheel}

/// One Nashi.exe invocation, or one message forwarded to the resident
/// instance. Plain launches keep the original "list of paths" message so an
/// older resident still understands them; commands carry a "nashi:" header,
/// which can never be an existing Windows path.
public sealed record LaunchCommand(LaunchKind Kind,string? Format,string[] Paths)
{
    public const int MessagePathLimit=250;
    private const string ConvertHeader="nashi:convert",WheelHeader="nashi:wheel",BackgroundHeader="nashi:background";
    public static LaunchCommand Open(params string[] paths)=>new(LaunchKind.Open,null,paths);

    /// `--convert FORMAT [--files-from LIST] PATH...`, `--wheel PATH...`,
    /// `--background` or plain paths. Missing paths are dropped like before.
    public static LaunchCommand Parse(IReadOnlyList<string> args,Func<string,bool>? exists=null,Func<string,IEnumerable<string>>? readList=null)
    {
        exists??=p=>File.Exists(p)||Directory.Exists(p);readList??=File.ReadLines;
        var convert=IndexOf(args,"--convert");var wheel=IndexOf(args,"--wheel");
        if(convert>=0&&wheel>=0)throw new ArgumentException("Use either --convert or --wheel.");
        if(convert>=0)
        {
            if(convert+1>=args.Count||args[convert+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("Use --convert FORMAT FILE...");
            var format=NormalizeFormat(args[convert+1])??throw new ArgumentException("Unknown output format: "+args[convert+1]);
            var paths=ExistingPaths(args.Where((_,n)=>n!=convert&&n!=convert+1).ToArray(),exists,readList);
            if(paths.Length==0)throw new ArgumentException("Choose at least one existing file to convert.");
            return new(LaunchKind.Convert,format,paths);
        }
        if(wheel>=0)return new(LaunchKind.Wheel,null,ExistingPaths(args.Where((_,n)=>n!=wheel).ToArray(),exists,readList));
        var background=IndexOf(args,"--background")>=0;var open=ExistingPaths(args,exists,readList);
        return background&&open.Length==0?new(LaunchKind.Background,null,[]):new(LaunchKind.Open,null,open);
    }
    private static int IndexOf(IReadOnlyList<string> args,string option){for(var n=0;n<args.Count;n++)if(string.Equals(args[n],option,StringComparison.OrdinalIgnoreCase))return n;return -1;}
    private static string[] ExistingPaths(IReadOnlyList<string> args,Func<string,bool> exists,Func<string,IEnumerable<string>> readList)
    {
        var result=new List<string>();
        for(var n=0;n<args.Count;n++)
        {
            if(string.Equals(args[n],"--files-from",StringComparison.OrdinalIgnoreCase))
            {
                if(n+1>=args.Count)throw new ArgumentException("Use --files-from LIST.");
                try{result.AddRange(readList(args[++n]).Select(line=>line.Trim()).Where(line=>line.Length>0));}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){throw new ArgumentException("Choose at least one existing file to convert.",error);}
                continue;
            }
            if(!args[n].StartsWith("--",StringComparison.Ordinal))result.Add(args[n]);
        }
        return result.Where(p=>p.Length<=16384&&exists(p)).Select(Full).Where(p=>p.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string Full(string path){try{return Path.GetFullPath(path);}catch(Exception error)when(error is ArgumentException or NotSupportedException or PathTooLongException){return "";}}

    /// Lower-case output format known to the shared conversion matrix.
    public static string? NormalizeFormat(string? value)
    {
        var format=value?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(format)||!FeatureCatalog.Conversions.Values.Any(targets=>targets.Contains(format))?null:format;
    }

    /// Pipe messages, chunked so a large Explorer selection never exceeds the
    /// single-instance message limits.
    public IReadOnlyList<string[]> ToMessages(int limit=MessagePathLimit)
    {
        string[] header=Kind switch{LaunchKind.Convert=>[ConvertHeader,Format!],LaunchKind.Wheel=>[WheelHeader],LaunchKind.Background=>[BackgroundHeader],_=>[]};
        if(Kind==LaunchKind.Background)return [header];
        if(Kind==LaunchKind.Open)return [Paths.Take(256).ToArray()];
        if(Kind==LaunchKind.Wheel)return [header.Concat(Paths.Take(limit)).ToArray()];
        return Paths.Chunk(Math.Max(1,limit)).Select(chunk=>header.Concat(chunk).ToArray()).DefaultIfEmpty(header).ToArray();
    }
    public static LaunchCommand FromMessage(IReadOnlyList<string> message,Func<string,bool>? exists=null)
    {
        exists??=p=>File.Exists(p)||Directory.Exists(p);
        string[] Existing(IEnumerable<string> paths)=>paths.Where(p=>!string.IsNullOrEmpty(p)&&Path.IsPathFullyQualified(p)&&exists(p)).ToArray();
        if(message.Count>0&&message[0]==BackgroundHeader)return new(LaunchKind.Background,null,[]);
        if(message.Count>0&&message[0]==WheelHeader)return new(LaunchKind.Wheel,null,Existing(message.Skip(1)));
        if(message.Count>1&&message[0]==ConvertHeader&&NormalizeFormat(message[1]) is {} format)return new(LaunchKind.Convert,format,Existing(message.Skip(2)));
        if(message.Count>0&&message[0].StartsWith("nashi:",StringComparison.Ordinal))return new(LaunchKind.Background,null,[]);
        return new(LaunchKind.Open,null,message.Where(p=>!string.IsNullOrEmpty(p)&&exists(p)).ToArray());
    }
}
