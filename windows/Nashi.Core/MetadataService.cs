using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Nashi.Core;
public sealed class MetadataService(string root)
{
    public static readonly string[] EditableTags=["EXIF:Artist","EXIF:Copyright","EXIF:ImageDescription","EXIF:Make","EXIF:Model","EXIF:Software","EXIF:DateTimeOriginal","EXIF:CreateDate","EXIF:ModifyDate","EXIF:UserComment","XMP:Title","XMP:Description","XMP:Creator","XMP:Rights","PDF:Title","PDF:Author","PDF:Subject","PDF:Keywords","QuickTime:Title","QuickTime:Artist","QuickTime:Comment","ID3:Title","ID3:Artist","ID3:Album","ID3:Year","ID3:Genre","ID3:Comment"];
    public string? Executable=>File.Exists(Path.Combine(root,"metadata-13.59","exiftool.exe"))?Path.Combine(root,"metadata-13.59","exiftool.exe"):null;
    public async Task InstallAsync(IProgress<OperationProgress>? progress,CancellationToken token)
    {
        if(Executable is not null)return;Directory.CreateDirectory(root);var stage=Path.Combine(root,".metadata-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            var zip=Path.Combine(stage,"exiftool.zip");await VerifiedDownload.FetchAsync(new("https://downloads.sourceforge.net/project/exiftool/exiftool-13.59_64.zip"),zip,30_000_000,p=>progress?.Report(new(p*.8,"Downloading ExifTool…")),token);
            await using(var file=File.OpenRead(zip))if(Convert.ToHexString(await SHA256.HashDataAsync(file,token)).ToLowerInvariant()!="44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec")throw new InvalidDataException("Metadata tools checksum mismatch.");
            var unpack=ArchiveEngine.Extract(zip,stage,null,null,token)[0];var exe=Directory.GetFiles(unpack,"exiftool(-k).exe",SearchOption.AllDirectories).Single();var folder=Path.GetDirectoryName(exe)!;File.Move(exe,Path.Combine(folder,"exiftool.exe"));token.ThrowIfCancellationRequested();Directory.Move(folder,Path.Combine(root,"metadata-13.59"));progress?.Report(new(1,"Metadata tools ready"));
        }finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    public async Task<IReadOnlyDictionary<string,string>> ReadAsync(string input,CancellationToken token=default)
    {
        var result=await ProcessRunner.RunAsync(Executable??throw new InvalidOperationException("Set up metadata tools once."),["-json","-G","-s","-charset","filename=UTF8","--",Path.GetFullPath(input)],token);using var json=JsonDocument.Parse(result.Output);return json.RootElement[0].EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.ToString());
    }
    public Task CopyImageTagsAsync(string source,string output,CancellationToken token)=>CopyImageTagsAsync([(source,output)],token);
    /// Copies tags for a whole batch in one ExifTool process (one command per
    /// file, separated by -execute): ExifTool's start-up dominates per file.
    public async Task CopyImageTagsAsync(IReadOnlyList<(string Source,string Output)> files,CancellationToken token)
    {
        if(files.Count==0)return;var executable=Executable??throw new InvalidOperationException("Metadata tools are not ready.");
        // Pixels are already oriented and converted to sRGB. Do not reattach a
        // stale orientation, source ICC profile, preview or image dimensions.
        static string[] Arguments(string source,string output,bool listed=false)=>["-overwrite_original","-charset","filename=UTF8","-tagsFromFile",Path.GetFullPath(source),"-all:all","--ICC_Profile","--Orientation","--ThumbnailImage","--PreviewImage","--JpgFromRaw","--ImageWidth","--ImageHeight","--ExifImageWidth","--ExifImageHeight","-Orientation=1","-ColorSpace=sRGB",..(listed?Array.Empty<string>():["--"]),Path.GetFullPath(output)];
        // An argument file holds one argument per line.
        if(files.Any(f=>(Path.GetFullPath(f.Source)+Path.GetFullPath(f.Output)).Any(char.IsControl))){foreach(var file in files)await ProcessRunner.RunAsync(executable,Arguments(file.Source,file.Output),token);return;}
        // In an argument file "--" would end options for every later command;
        // full paths never start with "-", so it is not needed there.
        var list=Path.Combine(Path.GetTempPath(),"Nashi-exif-"+Guid.NewGuid().ToString("N")+".args");
        try
        {
            await File.WriteAllLinesAsync(list,files.SelectMany((f,i)=>i==0?Arguments(f.Source,f.Output,true):["-execute",..Arguments(f.Source,f.Output,true)]),new System.Text.UTF8Encoding(false),token);
            // Like one process per file, any failed file fails the batch.
            var result=await ProcessRunner.RunAsync(executable,["-charset","filename=UTF8","-@",list],token);
            if(result.Error.Contains("Error:",StringComparison.Ordinal))throw new IOException(result.Error.Trim());
        }
        finally{if(File.Exists(list))File.Delete(list);}
    }
    public async Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<string> inputs,string destination,bool removeAll,IReadOnlyDictionary<string,string>? updates=null,IReadOnlyList<string>? removals=null,IProgress<OperationProgress>? progress=null,CancellationToken token=default)
    {
        if(updates?.Keys.Any(k=>!EditableTags.Contains(k))==true||removals?.Any(k=>!EditableTags.Contains(k))==true)throw new ArgumentException("Choose one of the editable metadata fields.");
        using var tx=new ExportTransaction(destination);var outputs=new List<string>();
        foreach(var input in inputs)
        {
            token.ThrowIfCancellationRequested();var output=SafePaths.Unique(tx.StagingDirectory,Path.GetFileNameWithoutExtension(input)+" Metadata"+Path.GetExtension(input));
            if(FeatureCatalog.Extension(input)=="pdf"&&removeAll){var fresh=DocumentEngine.Pdf("removeMetadata",[input],tx.StagingDirectory,token:token)[0];File.Move(fresh,output);}else File.Copy(input,output);
            var args=new List<string>{"-overwrite_original","-P","-charset","filename=UTF8"};if(removeAll){args.Add("-all=");if(FeatureCatalog.IsImage(input)||FeatureCatalog.Extension(input)=="gif")args.AddRange(["-tagsfromfile","@","-Orientation","-ICC_Profile"]);}if(removals is not null)args.AddRange(removals.Select(k=>"-"+k+"="));if(updates is not null)args.AddRange(updates.Select(p=>"-"+p.Key+"="+p.Value));args.AddRange(["--",output]);
            await ProcessRunner.RunAsync(Executable??throw new InvalidOperationException("Set up metadata tools once."),args,token);outputs.Add(output);progress?.Report(new(outputs.Count/(double)inputs.Count*.98,"Saving metadata…"));
        }
        return tx.Publish(outputs,token);
    }
}
